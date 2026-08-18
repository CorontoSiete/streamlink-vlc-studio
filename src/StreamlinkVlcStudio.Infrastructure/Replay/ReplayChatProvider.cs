using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Core.Text;
using StreamlinkVlcStudio.Core.Time;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Limits;
using StreamlinkVlcStudio.Infrastructure.Replay.TwitchDownloader;
using StreamlinkVlcStudio.Infrastructure.Twitch;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Replay;

public sealed class ReplayChatProvider : IReplayChatProvider
{
    // Public Twitch web Client-ID used by the installed Twitch VOD Downloader extension as its fallback.
    private const string TwitchVodDownloaderClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";
    private const string TwitchLiveDvrReplayIdPrefix = "live-dvr-";
    private const string TwitchVideoCommentsOperationName = "VideoCommentsByOffsetOrCursor";
    private const string TwitchVideoCommentsPersistedQueryHash = "b70a3591ff0f4e0313d126c6a1502d79a1c02baebb288227c582044aa76adf6a";
    private const int TwitchReplayChatMaxPages = 60;
    private const int TwitchGraphQlChatMaxMessages = 5000;
    private const int MaximumTwitchVodIdLength = 32;
    private static readonly TimeSpan TwitchReplayChatBackfill = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TwitchReplayChatPrefetch = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan KickTimestampReplayChatBackfill = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan KickOfficialReplayChatBackfill = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan KickOfficialReplayChatPrefetch = TimeSpan.FromMinutes(4);
    private static readonly HttpClient SharedHttpClient = HttpClientFactory.Create(
        TimeSpan.FromSeconds(20),
        includeUserAgent: true);
    private static readonly string TwitchGraphQlDeviceId = CreateDeviceId();

    private readonly HttpClient httpClient;
    private readonly KickOfficialChatReplayStore kickOfficialChatReplayStore;
    private readonly KickChatHistoryBackfillService kickChatHistoryBackfillService;
    private readonly TwitchGraphQlTransport twitchGraphQlTransport;

    public ReplayChatProvider()
        : this(SharedHttpClient, new KickOfficialChatReplayStore(), NoOpAppLogger.Instance)
    {
    }

    public ReplayChatProvider(IAppLogger logger)
        : this(SharedHttpClient, new KickOfficialChatReplayStore(), logger)
    {
    }

    public ReplayChatProvider(KickOfficialChatReplayStore kickOfficialChatReplayStore)
        : this(SharedHttpClient, kickOfficialChatReplayStore, NoOpAppLogger.Instance)
    {
    }

    public ReplayChatProvider(KickOfficialChatReplayStore kickOfficialChatReplayStore, IAppLogger logger)
        : this(SharedHttpClient, kickOfficialChatReplayStore, logger)
    {
    }

    public ReplayChatProvider(HttpClient httpClient)
        : this(httpClient, new KickOfficialChatReplayStore(), NoOpAppLogger.Instance)
    {
    }

    public ReplayChatProvider(HttpClient httpClient, IAppLogger logger)
        : this(httpClient, new KickOfficialChatReplayStore(), logger)
    {
    }

    public ReplayChatProvider(HttpClient httpClient, KickOfficialChatReplayStore kickOfficialChatReplayStore)
        : this(httpClient, kickOfficialChatReplayStore, NoOpAppLogger.Instance)
    {
    }

    public ReplayChatProvider(
        HttpClient httpClient,
        KickOfficialChatReplayStore kickOfficialChatReplayStore,
        IAppLogger logger)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.kickOfficialChatReplayStore = kickOfficialChatReplayStore ?? throw new ArgumentNullException(nameof(kickOfficialChatReplayStore));
        KickHttpHeaders.Configure(this.httpClient);
        kickChatHistoryBackfillService = new KickChatHistoryBackfillService(this.httpClient, logger ?? NoOpAppLogger.Instance);
        twitchGraphQlTransport = new TwitchGraphQlTransport(this.httpClient);
    }

    public async Task<ReplayChatLoadResult> LoadChatAsync(
        ReplaySessionInfo replay,
        AppSettings settings,
        TimeSpan offset,
        CancellationToken cancellationToken = default)
    {
        return replay.Platform switch
        {
            PlatformKind.Twitch => await LoadTwitchChatAsync(replay, settings, offset, cancellationToken).ConfigureAwait(false),
            PlatformKind.Kick => await LoadKickChatAsync(replay, settings, offset, cancellationToken).ConfigureAwait(false),
            _ => ReplayChatLoadResult.Unavailable($"Replay chat is not supported for {replay.Platform}.")
        };
    }

    public static string GetDefaultReplayChatCacheDirectory(PlatformKind platform)
    {
        var platformDirectory = platform == PlatformKind.Kick ? "kick-official" : "twitch";
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StreamlinkVlcStudio",
            "replay-chat",
            platformDirectory);
    }

    private async Task<ReplayChatLoadResult> LoadKickChatAsync(
        ReplaySessionInfo replay,
        AppSettings settings,
        TimeSpan offset,
        CancellationToken cancellationToken)
    {
        var directResult = await LoadKickTimestampChatAsync(replay, settings, offset, cancellationToken).ConfigureAwait(false);
        if (directResult is { IsAvailable: true, Messages.Count: > 0 })
        {
            return directResult;
        }

        var webhookResult = await LoadKickOfficialWebhookChatAsync(replay, offset, cancellationToken).ConfigureAwait(false);
        if (webhookResult.IsAvailable)
        {
            if (!directResult.IsAvailable ||
                webhookResult.Messages.Count > 0)
            {
                return webhookResult;
            }
        }

        if (directResult.IsAvailable)
        {
            return directResult;
        }

        return ReplayChatLoadResult.Unavailable(CombineUnavailableReasons(
            directResult.UnavailableReason,
            $"Official Kick webhook cache fallback was also unavailable: {webhookResult.UnavailableReason}"));
    }

    private async Task<ReplayChatLoadResult> LoadKickTimestampChatAsync(
        ReplaySessionInfo replay,
        AppSettings settings,
        TimeSpan offset,
        CancellationToken cancellationToken)
    {
        if (replay.StreamStartedAtUtc is not { } startedAt)
        {
            return ReplayChatLoadResult.Unavailable(
                "Direct Kick VOD chat needs the VOD start time so messages can be aligned to playback.");
        }

        var replayMessagesChannelId = FirstNonEmpty(replay.ChatRoomId);
        var configuredChatroomId = GetConfiguredKickChatroomId(settings.Chat, replay.Channel);
        if (string.IsNullOrWhiteSpace(replayMessagesChannelId) &&
            string.IsNullOrWhiteSpace(configuredChatroomId))
        {
            return ReplayChatLoadResult.Unavailable(
                $"Direct Kick VOD chat needs Kick channel_id or chatroom_id metadata for {replay.Channel}.");
        }

        var requestedFrom = ClampOffset(offset - KickTimestampReplayChatBackfill, replay.Duration);
        var requestedThrough = ClampOffset(offset + KickOfficialReplayChatPrefetch, replay.Duration);
        if (requestedThrough < requestedFrom)
        {
            requestedThrough = requestedFrom;
        }

        try
        {
            var startedAtUtc = startedAt.ToUniversalTime();
            var requestedFromTimestampUtc = startedAtUtc + requestedFrom;
            var requestedThroughTimestampUtc = startedAtUtc + requestedThrough;
            var channelInfo = await kickChatHistoryBackfillService
                .ResolveChannelInfoAsync(replay.Channel, settings.Chat, cancellationToken)
                .ConfigureAwait(false);
            var channelId = FirstNonEmpty(channelInfo.ChannelId, replayMessagesChannelId);
            var chatroomId = FirstNonEmpty(channelInfo.ChatroomId, configuredChatroomId, replayMessagesChannelId);
            if (string.IsNullOrWhiteSpace(chatroomId))
            {
                return ReplayChatLoadResult.Unavailable(
                    $"Direct Kick VOD chat could not resolve Kick chatroom metadata for {replay.Channel}.");
            }

            var backfillResult = await kickChatHistoryBackfillService
                .BackfillRecentChatFromStartTimeAsync(
                    replay.Channel,
                    channelId,
                    chatroomId,
                    requestedFromTimestampUtc,
                    requestedThroughTimestampUtc,
                    cancellationToken)
                .ConfigureAwait(false);

            if (backfillResult.Messages.Count == 0 && !backfillResult.CoveredRequestedRange)
            {
                return ReplayChatLoadResult.Unavailable(
                    backfillResult.Attempted
                        ? "Direct Kick VOD chat could not be loaded from Kick timestamp messages."
                        : $"Direct Kick VOD chat needs Kick channel_id or chatroom_id metadata for {replay.Channel}.");
            }

            var messages = new List<ReplayChatMessage>(backfillResult.Messages.Count);
            var seenMessages = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chatMessage in backfillResult.Messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var messageOffset = chatMessage.Timestamp.ToUniversalTime() - startedAtUtc;
                if (messageOffset < requestedFrom ||
                    messageOffset > requestedThrough)
                {
                    continue;
                }

                var normalizedMessage = string.IsNullOrWhiteSpace(chatMessage.RoomId)
                    ? chatMessage with { RoomId = chatroomId }
                    : chatMessage;
                if (!seenMessages.Add(GetReplayMessageDeduplicationKey(normalizedMessage, messageOffset)))
                {
                    continue;
                }

                messages.Add(new ReplayChatMessage(messageOffset, normalizedMessage));
            }

            return ReplayChatLoadResult.Available(
                messages
                    .OrderBy(message => message.Offset)
                    .ThenBy(message => message.Message.MessageId, StringComparer.Ordinal)
                    .ToArray(),
                GetReplayOffsetFromTimestamp(backfillResult.CoveredFromTimestampUtc, startedAtUtc, replay.Duration) ?? requestedFrom,
                GetReplayOffsetFromTimestamp(backfillResult.CoveredThroughTimestampUtc, startedAtUtc, replay.Duration));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ReplayChatLoadResult.Unavailable($"Direct Kick VOD chat could not be loaded: {ex.Message}");
        }
    }

    public async Task<ReplayChatLoadResult> LoadKickOfficialWebhookChatAsync(
        ReplaySessionInfo replay,
        TimeSpan offset,
        CancellationToken cancellationToken = default)
    {
        if (replay.StreamStartedAtUtc is not { } startedAt)
        {
            return ReplayChatLoadResult.Unavailable(
                "Official Kick VOD chat needs the VOD start time so webhook messages can be aligned to playback.");
        }

        var requestedFrom = ClampOffset(offset - KickOfficialReplayChatBackfill, replay.Duration);
        var requestedThrough = ClampOffset(offset + KickOfficialReplayChatPrefetch, replay.Duration);
        if (requestedThrough < requestedFrom)
        {
            requestedThrough = requestedFrom;
        }

        var startedAtUtc = startedAt.ToUniversalTime();
        if (!TryAddOffset(startedAtUtc, requestedFrom, out var fromTimestampUtc) ||
            !TryAddOffset(startedAtUtc, requestedThrough, out var throughTimestampUtc))
        {
            return ReplayChatLoadResult.Unavailable(
                "Official Kick VOD chat timestamp range is outside the supported date range.");
        }

        var result = await kickOfficialChatReplayStore
            .ReadMessagesAsync(replay.Channel, fromTimestampUtc, throughTimestampUtc, cancellationToken)
            .ConfigureAwait(false);
        if (result.CacheFileCount == 0)
        {
            return ReplayChatLoadResult.Unavailable(
                $"No official Kick webhook chat cache was found for {replay.Channel}. Enable the Kick webhook listener, subscribe to chat.message.sent, and capture chat before opening the VOD.");
        }

        return ReplayChatLoadResult.Available(
            result.Messages
                .Select(message => new ReplayChatMessage(message.Timestamp.ToUniversalTime() - startedAtUtc, message))
                .Where(message => message.Offset >= TimeSpan.Zero)
                .OrderBy(message => message.Offset)
                .ToArray(),
            requestedFrom,
            requestedThrough);
    }

    public async Task<ReplayChatLoadResult> LoadTwitchChatAsync(
        ReplaySessionInfo replay,
        AppSettings settings,
        TimeSpan offset,
        CancellationToken cancellationToken = default)
    {
        if (IsTwitchLiveDvrReplay(replay))
        {
            return ReplayChatLoadResult.Unavailable(
                "Twitch replay chat is unavailable for current-live DVR playback because Twitch has not published a VOD comments ID yet.");
        }

        var requestedFrom = ClampOffset(offset - TwitchReplayChatBackfill, replay.Duration);
        var requestedThrough = ClampOffset(offset + TwitchReplayChatPrefetch, replay.Duration);
        if (requestedThrough < requestedFrom)
        {
            requestedThrough = requestedFrom;
        }

        var cacheResult = LoadTwitchChat(replay, cancellationToken);
        if (CoversRange(cacheResult, requestedFrom, requestedThrough))
        {
            return cacheResult;
        }

        var graphQlResult = await LoadTwitchGraphQlChatAsync(
                replay,
                offset,
                cacheResult.IsAvailable ? "" : cacheResult.UnavailableReason,
                cancellationToken)
            .ConfigureAwait(false);
        if (!cacheResult.IsAvailable)
        {
            return graphQlResult;
        }

        if (!graphQlResult.IsAvailable)
        {
            return cacheResult;
        }

        return MergeTwitchChatResults(cacheResult, graphQlResult, requestedFrom);
    }

    public static ReplayChatLoadResult LoadTwitchChat(ReplaySessionInfo replay, CancellationToken cancellationToken = default)
    {
        if (IsTwitchLiveDvrReplay(replay))
        {
            return ReplayChatLoadResult.Unavailable(
                "Twitch replay chat is unavailable for current-live DVR playback because Twitch has not published a VOD comments ID yet.");
        }

        var replayId = replay.ReplayId.Trim();
        if (!IsTwitchVodId(replayId))
        {
            return ReplayChatLoadResult.Unavailable("Twitch replay chat needs a valid numeric VOD ID.");
        }

        var cacheDirectory = GetDefaultReplayChatCacheDirectory(PlatformKind.Twitch);
        var candidates = new[]
        {
            Path.Combine(cacheDirectory, $"{replayId}.json"),
            Path.Combine(cacheDirectory, $"{replayId}_chat.json"),
            Path.Combine(cacheDirectory, $"v{replayId}.json"),
            Path.Combine(cacheDirectory, $"v{replayId}_chat.json")
        };

        var filePath = candidates.FirstOrDefault(File.Exists);
        if (filePath is null)
        {
            return ReplayChatLoadResult.Unavailable(
                $"Twitch replay chat cache was not found for VOD {replayId}. Expected a TwitchDownloader JSON file under {cacheDirectory}.");
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            if (stream.Length > PayloadLimits.ReplayChatCacheBytes)
            {
                return ReplayChatLoadResult.Unavailable(
                    $"Twitch replay chat cache exceeds the {PayloadLimits.ReplayChatCacheBytes:N0}-byte safety limit.");
            }

            using var document = JsonDocument.Parse(stream);
            var messages = ReadTwitchDownloaderMessages(document.RootElement, replay, cancellationToken);
            return messages.Count == 0
                ? ReplayChatLoadResult.Unavailable("The cached Twitch replay chat file did not contain usable messages.")
                : ReplayChatLoadResult.Available(messages, messages[0].Offset, messages[^1].Offset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return ReplayChatLoadResult.Unavailable($"Twitch replay chat cache could not be read: {ex.Message}");
        }
    }

    public static IReadOnlyList<ReplayChatMessage> ReadTwitchDownloaderMessages(
        JsonElement root,
        ReplaySessionInfo replay,
        CancellationToken cancellationToken = default)
    {
        var comments = TwitchDownloaderChatJson.ReadComments(root);
        var messages = new List<ReplayChatMessage>();
        foreach (var comment in comments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = ReadMessageBody(comment);
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            if (!TryCreateReplayOffset(comment.content_offset_seconds, out var offset))
            {
                continue;
            }

            var emotes = ReadTwitchDownloaderEmotes(comment.message, body);
            var timestamp = DateTimeOffset.UtcNow;
            if (replay.StreamStartedAtUtc is { } startedAt &&
                !TryAddOffset(startedAt, offset, out timestamp))
            {
                continue;
            }
            var badges = ReadTwitchDownloaderBadges(comment.message);
            messages.Add(new ReplayChatMessage(
                offset,
                new ChatMessage(
                    replay.Platform,
                    replay.Channel,
                    FirstNonEmpty(comment.commenter?.display_name, comment.commenter?.name, "viewer"),
                    body,
                    timestamp,
                    string.IsNullOrWhiteSpace(comment.message?.user_color) ? null : comment.message.user_color,
                    badges.Count > 0 ? badges : null,
                    emotes.Count > 0 ? emotes : null,
                    RoomId: FirstNonEmpty(comment.channel_id, replay.ChatRoomId),
                    MessageId: string.IsNullOrWhiteSpace(comment._id) ? null : comment._id)));
        }

        return messages
            .OrderBy(message => message.Offset)
            .ToArray();
    }

    public static TwitchReplayChatPage ReadTwitchGraphQlChatPage(JsonElement root, ReplaySessionInfo replay)
    {
        var messages = new List<ReplayChatMessage>();
        var hasNextPage = false;
        var endCursor = "";

        foreach (var comments in EnumerateTwitchGraphQlComments(root))
        {
            if (messages.Count >= TwitchGraphQlChatMaxMessages)
            {
                break;
            }

            if (comments.TryGetProperty("pageInfo", out var pageInfo))
            {
                hasNextPage = hasNextPage || TryGetBool(pageInfo, "hasNextPage") == true;
            }

            if (!comments.TryGetProperty("edges", out var edges) ||
                edges.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var edge in edges.EnumerateArray())
            {
                var cursor = GetOptionalString(edge, "cursor");
                if (!string.IsNullOrWhiteSpace(cursor))
                {
                    endCursor = cursor;
                }

                if (TryReadTwitchGraphQlMessage(edge, replay, out var message))
                {
                    messages.Add(message);
                    if (messages.Count >= TwitchGraphQlChatMaxMessages)
                    {
                        break;
                    }
                }
            }
        }

        return new TwitchReplayChatPage(
            messages
                .OrderBy(message => message.Offset)
                .ToArray(),
            hasNextPage,
            endCursor);
    }

    private async Task<ReplayChatLoadResult> LoadTwitchGraphQlChatAsync(
        ReplaySessionInfo replay,
        TimeSpan offset,
        string cacheUnavailableReason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(replay.ReplayId))
        {
            return ReplayChatLoadResult.Unavailable("Twitch replay chat needs a VOD ID.");
        }

        var requestedFrom = ClampOffset(offset - TwitchReplayChatBackfill, replay.Duration);
        var requestedThrough = ClampOffset(offset + TwitchReplayChatPrefetch, replay.Duration);
        if (requestedThrough < requestedFrom)
        {
            requestedThrough = requestedFrom;
        }

        try
        {
            var messages = new List<ReplayChatMessage>();
            var seenMessageIds = new HashSet<string>(StringComparer.Ordinal);
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            var nextOffset = requestedFrom;
            string? nextCursor = null;
            var loadedThrough = requestedFrom;
            var coveredRequestedThrough = false;

            for (var pageIndex = 0; pageIndex < TwitchReplayChatMaxPages; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await FetchTwitchGraphQlChatPageAsync(
                        replay,
                        TwitchVodDownloaderClientId,
                        nextOffset,
                        nextCursor,
                        cancellationToken)
                    .ConfigureAwait(false);

                foreach (var message in page.Messages)
                {
                    if (messages.Count >= TwitchGraphQlChatMaxMessages)
                    {
                        break;
                    }

                    var key = GetReplayMessageDeduplicationKey(message.Message, message.Offset);
                    if (seenMessageIds.Add(key))
                    {
                        messages.Add(message);
                    }
                }

                if (page.Messages.Count > 0)
                {
                    loadedThrough = Max(loadedThrough, page.Messages[^1].Offset);
                }

                if (loadedThrough >= requestedThrough)
                {
                    coveredRequestedThrough = true;
                    break;
                }

                if (messages.Count >= TwitchGraphQlChatMaxMessages)
                {
                    break;
                }

                if (!page.HasNextPage)
                {
                    coveredRequestedThrough = true;
                    break;
                }

                if (!string.IsNullOrWhiteSpace(page.EndCursor))
                {
                    if (!seenCursors.Add(page.EndCursor))
                    {
                        break;
                    }

                    nextCursor = page.EndCursor;
                    continue;
                }

                // Cursor pagination is authoritative and preserves messages sharing the same
                // contentOffsetSeconds. Offset paging remains only a compatibility fallback for
                // responses that advertise another page without returning an edge cursor.
                if (page.Messages.Count == 0)
                {
                    break;
                }
                var nextPageOffset = TimeSpan.FromSeconds(Math.Floor(page.Messages[^1].Offset.TotalSeconds) + 1);
                if (nextPageOffset <= nextOffset)
                {
                    break;
                }

                nextOffset = nextPageOffset;
                nextCursor = null;
            }

            return ReplayChatLoadResult.Available(
                messages
                    .OrderBy(message => message.Offset)
                    .ToArray(),
                requestedFrom,
                coveredRequestedThrough ? requestedThrough : loadedThrough);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ReplayChatLoadResult.Unavailable(
                CombineUnavailableReasons(
                    $"Direct Twitch replay chat could not be loaded: {ex.Message}",
                    string.IsNullOrWhiteSpace(cacheUnavailableReason)
                        ? ""
                        : $"Cached TwitchDownloader fallback was also unavailable: {cacheUnavailableReason}"));
        }
    }

    private static bool CoversRange(
        ReplayChatLoadResult result,
        TimeSpan requestedFrom,
        TimeSpan requestedThrough) =>
        result.IsAvailable &&
        result.LoadedFromOffset is { } loadedFrom &&
        result.LoadedThroughOffset is { } loadedThrough &&
        loadedFrom <= requestedFrom &&
        loadedThrough >= requestedThrough;

    private static ReplayChatLoadResult MergeTwitchChatResults(
        ReplayChatLoadResult cacheResult,
        ReplayChatLoadResult graphQlResult,
        TimeSpan requestedFrom)
    {
        var messages = new List<ReplayChatMessage>(cacheResult.Messages.Count + graphQlResult.Messages.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in cacheResult.Messages.Concat(graphQlResult.Messages))
        {
            if (seen.Add(GetReplayMessageDeduplicationKey(message.Message, message.Offset)))
            {
                messages.Add(message);
            }
        }

        var ranges = new[] { cacheResult, graphQlResult }
            .Where(result => result.LoadedFromOffset.HasValue && result.LoadedThroughOffset.HasValue)
            .Select(result => (
                From: result.LoadedFromOffset!.Value,
                Through: result.LoadedThroughOffset!.Value))
            .Where(range => range.Through >= range.From)
            .OrderBy(range => range.From)
            .ToArray();
        TimeSpan? loadedFrom = null;
        TimeSpan? loadedThrough = null;
        var containingIndex = Array.FindIndex(
            ranges,
            range => range.From <= requestedFrom && range.Through >= requestedFrom);
        if (containingIndex < 0 && ranges.Length > 0)
        {
            containingIndex = 0;
        }

        if (containingIndex >= 0)
        {
            loadedFrom = ranges[containingIndex].From;
            loadedThrough = ranges[containingIndex].Through;
            for (var index = containingIndex - 1;
                 index >= 0 && ranges[index].Through >= loadedFrom.Value;
                 index--)
            {
                loadedFrom = Min(loadedFrom.Value, ranges[index].From);
            }

            for (var index = containingIndex + 1;
                 index < ranges.Length && ranges[index].From <= loadedThrough.Value;
                 index++)
            {
                loadedThrough = Max(loadedThrough.Value, ranges[index].Through);
            }
        }

        return ReplayChatLoadResult.Available(
            messages
                .OrderBy(message => message.Offset)
                .ThenBy(message => message.Message.MessageId, StringComparer.Ordinal)
                .ToArray(),
            loadedFrom,
            loadedThrough);
    }

    private async Task<TwitchReplayChatPage> FetchTwitchGraphQlChatPageAsync(
        ReplaySessionInfo replay,
        string clientId,
        TimeSpan offset,
        string? cursor,
        CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            // VOD comments are public and this persisted query uses Twitch's public web Client-ID.
            // Attaching the user's token (issued for their configured Client-ID) makes Twitch reject
            // the otherwise valid request when the token is expired or belongs to a different app.
            document = await twitchGraphQlTransport.SendAsync(
                BuildTwitchVideoCommentsPayload(replay.ReplayId, offset, cursor),
                clientId,
                TwitchGraphQlDeviceId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TwitchGraphQlHttpException ex)
        {
            throw new InvalidOperationException(
                $"Twitch GraphQL returned {(int)ex.StatusCode} {ex.ReasonPhrase}. {GraphQlErrorReader.ExtractResponseMessage(ex.ResponseBody)}".Trim(),
                ex);
        }
        catch (TwitchGraphQlRejectedException ex)
        {
            throw new InvalidOperationException($"Twitch GraphQL rejected replay chat: {ex.GraphQlMessage}", ex);
        }

        using (document)
        {
            return ReadTwitchGraphQlChatPage(document.RootElement, replay);
        }
    }

    private static string BuildTwitchVideoCommentsPayload(string vodId, TimeSpan offset, string? cursor)
    {
        var variables = new Dictionary<string, object?>
        {
            ["videoID"] = vodId.Trim()
        };
        if (string.IsNullOrWhiteSpace(cursor))
        {
            variables["contentOffsetSeconds"] = (int)Math.Clamp(
                Math.Floor(offset.TotalSeconds),
                0,
                int.MaxValue);
        }
        else
        {
            variables["cursor"] = cursor;
        }

        var payload = new[]
        {
            new
            {
                operationName = TwitchVideoCommentsOperationName,
                variables,
                extensions = new
                {
                    persistedQuery = new
                    {
                        version = 1,
                        sha256Hash = TwitchVideoCommentsPersistedQueryHash
                    }
                }
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    private static IEnumerable<JsonElement> EnumerateTwitchGraphQlComments(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                foreach (var nestedComments in EnumerateTwitchGraphQlComments(item))
                {
                    yield return nestedComments;
                }
            }

            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("video", out var video) ||
            video.ValueKind != JsonValueKind.Object ||
            !video.TryGetProperty("comments", out var comments) ||
            comments.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        yield return comments;
    }

    private static bool TryReadTwitchGraphQlMessage(
        JsonElement edge,
        ReplaySessionInfo replay,
        out ReplayChatMessage message)
    {
        message = default!;
        if (!edge.TryGetProperty("node", out var node) ||
            node.ValueKind != JsonValueKind.Object ||
            !TryGetDouble(node, "contentOffsetSeconds", out var offsetSeconds))
        {
            return false;
        }

        var body = "";
        string? color = null;
        IReadOnlyList<ChatBadge>? badges = null;
        IReadOnlyList<ChatEmote>? emotes = null;
        if (node.TryGetProperty("message", out var messageElement) &&
            messageElement.ValueKind == JsonValueKind.Object)
        {
            body = ReadTwitchGraphQlMessageBody(messageElement);
            color = GetOptionalString(messageElement, "userColor");
            badges = ReadTwitchGraphQlBadges(messageElement);
            emotes = ReadTwitchGraphQlEmotes(messageElement, body);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        if (!TryCreateReplayOffset(offsetSeconds, out var offset))
        {
            return false;
        }

        DateTimeOffset timestamp;
        if (TryGetDateTimeOffset(node, "createdAt", out var createdAt))
        {
            timestamp = createdAt;
        }
        else if (replay.StreamStartedAtUtc is { } startedAt)
        {
            if (!TryAddOffset(startedAt, offset, out timestamp))
            {
                return false;
            }
        }
        else
        {
            timestamp = DateTimeOffset.UtcNow;
        }
        var username = "viewer";
        if (node.TryGetProperty("commenter", out var commenter) &&
            commenter.ValueKind == JsonValueKind.Object)
        {
            username = FirstNonEmpty(
                GetOptionalString(commenter, "displayName"),
                GetOptionalString(commenter, "login"),
                GetOptionalString(commenter, "name"),
                username);
        }

        message = new ReplayChatMessage(
            offset,
            new ChatMessage(
                replay.Platform,
                replay.Channel,
                username,
                body,
                timestamp,
                string.IsNullOrWhiteSpace(color) ? null : color,
                badges?.Count > 0 ? badges : null,
                emotes?.Count > 0 ? emotes : null,
                RoomId: replay.ChatRoomId,
                MessageId: FirstNonEmpty(GetOptionalString(node, "id"), GetOptionalString(edge, "cursor"))));
        return true;
    }

    private static IReadOnlyList<ChatEmote> ReadTwitchDownloaderEmotes(
        TwitchDownloaderMessage? message,
        string body)
    {
        if (message is null || string.IsNullOrEmpty(body))
        {
            return [];
        }

        var emotes = new List<ChatEmote>();
        var fragmentCursor = 0;
        if (message.fragments is { Count: > 0 } fragments)
        {
            foreach (var fragment in fragments)
            {
                var text = fragment.text ?? "";
                if (text.Length == 0)
                {
                    continue;
                }

                var startIndex = FindFragmentStart(body, fragmentCursor, text);
                if (startIndex < 0)
                {
                    continue;
                }

                fragmentCursor = startIndex + text.Length;
                AddTwitchEmote(
                    emotes,
                    body,
                    startIndex,
                    fragmentCursor,
                    FirstNonEmpty(
                        ReadTwitchDownloaderEmoticonId(fragment.emoticon),
                        ReadTwitchDownloaderEmoticonId(fragment.emote)));
            }
        }

        if (message.emoticons is { Count: > 0 } ranges)
        {
            foreach (var range in ranges)
            {
                if (string.IsNullOrWhiteSpace(range._id) ||
                    range.begin < 0 ||
                    range.end < range.begin)
                {
                    continue;
                }

                if (TryGetStringRangeFromCodepointRange(
                        body,
                        range.begin,
                        range.end,
                        out var startIndex,
                        out var endIndex) ||
                    (range.end < int.MaxValue &&
                        TryGetStringRangeFromStringIndexes(
                        body,
                        range.begin,
                        range.end + 1,
                        out startIndex,
                        out endIndex)))
                {
                    AddTwitchEmote(
                        emotes,
                        body,
                        startIndex,
                        endIndex,
                        range._id);
                }
            }
        }

        return emotes
            .OrderBy(emote => emote.StartIndex)
            .ThenBy(emote => emote.EndIndex)
            .ToArray();
    }

    private static IReadOnlyList<ChatEmote> ReadTwitchGraphQlEmotes(
        JsonElement message,
        string body)
    {
        if (string.IsNullOrEmpty(body) ||
            !message.TryGetProperty("fragments", out var fragments) ||
            fragments.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var emotes = new List<ChatEmote>();
        var fragmentCursor = 0;
        foreach (var fragment in fragments.EnumerateArray())
        {
            if (fragment.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var text = JsonElementReader.GetOptionalString(fragment, "text", trimStrings: false);
            if (text.Length == 0)
            {
                continue;
            }

            var startIndex = FindFragmentStart(body, fragmentCursor, text);
            if (startIndex < 0)
            {
                continue;
            }

            fragmentCursor = startIndex + text.Length;
            AddTwitchEmote(
                emotes,
                body,
                startIndex,
                fragmentCursor,
                ReadTwitchGraphQlEmoteId(fragment));
        }

        return emotes
            .OrderBy(emote => emote.StartIndex)
            .ThenBy(emote => emote.EndIndex)
            .ToArray();
    }

    private static string ReadTwitchDownloaderEmoticonId(
        TwitchDownloaderEmoticonFragment? fragment)
    {
        return fragment is null
            ? ""
            : FirstNonEmpty(fragment.emoticon_id, fragment.emoticonId, fragment.id);
    }

    private static string ReadTwitchGraphQlEmoteId(JsonElement fragment)
    {
        foreach (var propertyName in new[] { "emoticon", "emote" })
        {
            if (fragment.TryGetProperty(propertyName, out var emote) &&
                emote.ValueKind == JsonValueKind.Object)
            {
                var id = FirstNonEmpty(
                    GetOptionalString(emote, "emoticon_id"),
                    GetOptionalString(emote, "emoticonId"),
                    GetOptionalString(emote, "id"),
                    GetOptionalString(emote, "emote_id"),
                    GetOptionalString(emote, "emoteId"));
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return id;
                }
            }
        }

        return FirstNonEmpty(
            GetOptionalString(fragment, "emoticon_id"),
            GetOptionalString(fragment, "emoticonId"),
            GetOptionalString(fragment, "emote_id"),
            GetOptionalString(fragment, "emoteId"));
    }

    private static void AddTwitchEmote(
        List<ChatEmote> emotes,
        string body,
        int startIndex,
        int endIndex,
        string? id)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            startIndex < 0 ||
            endIndex <= startIndex ||
            endIndex > body.Length)
        {
            return;
        }

        var code = body[startIndex..endIndex];
        if (code.Length == 0 ||
            code.Length > 96 ||
            code.Any(char.IsWhiteSpace) ||
            emotes.Any(emote =>
                startIndex < emote.EndIndex &&
                endIndex > emote.StartIndex))
        {
            return;
        }

        emotes.Add(new ChatEmote(
            startIndex,
            endIndex,
            code,
            BuildTwitchEmoteImageUrl(id)));
    }

    private static int FindFragmentStart(string body, int searchStart, string fragmentText)
    {
        if (searchStart < 0 || searchStart > body.Length)
        {
            return -1;
        }

        if (searchStart + fragmentText.Length <= body.Length &&
            string.CompareOrdinal(
                body,
                searchStart,
                fragmentText,
                0,
                fragmentText.Length) == 0)
        {
            return searchStart;
        }

        return body.IndexOf(fragmentText, searchStart, StringComparison.Ordinal);
    }

    private static bool TryGetStringRangeFromCodepointRange(
        string text,
        int startCodepoint,
        int endCodepointInclusive,
        out int startIndex,
        out int endIndex)
    {
        startIndex = -1;
        endIndex = -1;
        if (startCodepoint < 0 || endCodepointInclusive < startCodepoint)
        {
            return false;
        }

        var codepointIndex = 0;
        var stringIndex = 0;
        while (stringIndex < text.Length)
        {
            if (codepointIndex == startCodepoint)
            {
                startIndex = stringIndex;
            }

            var codeUnit = text[stringIndex++];
            if (char.IsHighSurrogate(codeUnit) &&
                stringIndex < text.Length &&
                char.IsLowSurrogate(text[stringIndex]))
            {
                stringIndex++;
            }

            codepointIndex++;
            if (codepointIndex - 1 == endCodepointInclusive)
            {
                endIndex = stringIndex;
                return startIndex >= 0;
            }
        }

        return false;
    }

    private static bool TryGetStringRangeFromStringIndexes(
        string text,
        int startIndex,
        int endIndex,
        out int resolvedStartIndex,
        out int resolvedEndIndex)
    {
        resolvedStartIndex = startIndex;
        resolvedEndIndex = endIndex;
        return startIndex >= 0 &&
            endIndex > startIndex &&
            endIndex <= text.Length;
    }

    private static string BuildTwitchEmoteImageUrl(string id)
    {
        return $"https://static-cdn.jtvnw.net/emoticons/v2/{Uri.EscapeDataString(id.Trim())}/static/light/2.0";
    }

    private static string ReadTwitchGraphQlMessageBody(JsonElement message)
    {
        var body = GetOptionalString(message, "body");
        if (!string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        if (!message.TryGetProperty("fragments", out var fragments) ||
            fragments.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        return string.Concat(fragments
            .EnumerateArray()
            .Select(fragment => GetOptionalString(fragment, "text")));
    }

    private static IReadOnlyList<ChatBadge> ReadTwitchGraphQlBadges(JsonElement message)
    {
        if (!message.TryGetProperty("userBadges", out var badges) ||
            badges.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<ChatBadge>();
        foreach (var badge in badges.EnumerateArray())
        {
            if (badge.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadTwitchGraphQlBadgeSetId(badge);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            result.Add(new ChatBadge(
                id,
                GetOptionalString(badge, "version"),
                ChatTextNormalizer.NormalizeBadgeTitle(
                    FirstNonEmpty(
                        GetOptionalString(badge, "title"),
                        GetOptionalString(badge, "name")),
                    TwitchBadgeValues.ResolveTitle(id)),
                FirstNonEmpty(
                    GetOptionalString(badge, "imageURL"),
                    GetOptionalString(badge, "imageUrl"),
                    GetOptionalString(badge, "image_url"),
                    GetOptionalString(badge, "image_url_4x"),
                    GetOptionalString(badge, "image_url_2x"),
                    GetOptionalString(badge, "image_url_1x"))));
        }

        return result;
    }

    private static string ReadTwitchGraphQlBadgeSetId(JsonElement badge)
    {
        var setId = FirstNonEmpty(
            GetOptionalString(badge, "setID"),
            GetOptionalString(badge, "setId"),
            GetOptionalString(badge, "set_id"),
            GetOptionalString(badge, "badgeSetID"),
            GetOptionalString(badge, "badgeSetId"),
            GetOptionalString(badge, "badge_set_id"),
            GetOptionalString(badge, "name"),
            GetOptionalString(badge, "type"));
        if (!string.IsNullOrWhiteSpace(setId))
        {
            return setId;
        }

        var id = GetOptionalString(badge, "id");
        return LooksLikeOpaqueGraphQlId(id) ? "" : id;
    }

    private static IReadOnlyList<ChatBadge> ReadTwitchDownloaderBadges(TwitchDownloaderMessage? message)
    {
        var badges = message?.user_badges is { Count: > 0 } snakeCaseBadges
            ? snakeCaseBadges
            : message?.userBadges;
        if (badges is not { Count: > 0 })
        {
            return [];
        }

        var result = new List<ChatBadge>();
        foreach (var badge in badges)
        {
            var id = ReadTwitchDownloaderBadgeSetId(badge);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            result.Add(new ChatBadge(
                id,
                string.IsNullOrWhiteSpace(badge.version) ? null : badge.version.Trim(),
                ChatTextNormalizer.NormalizeBadgeTitle(
                    badge.title,
                    TwitchBadgeValues.ResolveTitle(id)),
                FirstNonEmpty(
                    badge.imageURL,
                    badge.image_url_4x,
                    badge.image_url_2x,
                    badge.image_url_1x,
                    badge.image_url)));
        }

        return result;
    }

    private static string ReadTwitchDownloaderBadgeSetId(TwitchDownloaderUserBadge badge)
    {
        var setId = FirstNonEmpty(
            badge.setID,
            badge.setId,
            badge.set_id,
            badge.badgeSetID,
            badge.badge_set_id,
            badge.name,
            badge.type);
        if (!string.IsNullOrWhiteSpace(setId))
        {
            return setId;
        }

        return LooksLikeOpaqueGraphQlId(badge._id) ? "" : FirstNonEmpty(badge._id);
    }

    private static string ReadMessageBody(TwitchDownloaderComment comment)
    {
        var body = comment.message?.body;
        if (!string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        return comment.message?.fragments is { Count: > 0 } fragments
            ? string.Concat(fragments.Select(fragment => fragment.text ?? ""))
            : "";
    }

    private static string GetReplayMessageDeduplicationKey(ChatMessage message, TimeSpan offset)
    {
        if (!string.IsNullOrWhiteSpace(message.MessageId))
        {
            return message.MessageId.Trim();
        }

        return string.Concat(
            offset.Ticks.ToString(CultureInfo.InvariantCulture),
            ":",
            message.Timestamp.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            ":",
            message.Username,
            ":",
            message.Message);
    }

    private static string CombineUnavailableReasons(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return second;
        }

        if (string.IsNullOrWhiteSpace(second))
        {
            return first;
        }

        return $"{first} {second}";
    }

    private static string GetConfiguredKickChatroomId(ChatSettings settings, string channel)
    {
        return settings.TryGetKickChatroomId(channel, out var configured)
            ? KickChannelInfoJson.NormalizeNumericId(configured) ?? ""
            : "";
    }

    private static TimeSpan ClampOffset(TimeSpan offset, TimeSpan duration)
    {
        if (offset < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return duration > TimeSpan.Zero && offset > duration ? duration : offset;
    }

    private static TimeSpan? GetReplayOffsetFromTimestamp(
        DateTimeOffset? timestampUtc,
        DateTimeOffset startedAtUtc,
        TimeSpan duration)
    {
        return timestampUtc is { } timestamp
            ? ClampOffset(timestamp.ToUniversalTime() - startedAtUtc, duration)
            : null;
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private static bool TryGetDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        var parsed = property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(
                property.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value),
            _ => false
        };
        return parsed && double.IsFinite(value);
    }

    private static bool TryCreateReplayOffset(double seconds, out TimeSpan offset)
    {
        if (seconds == 0)
        {
            offset = TimeSpan.Zero;
            return true;
        }

        return DurationValues.TryCreatePositive(seconds, TimeSpan.TicksPerSecond, out offset);
    }

    private static bool TryAddOffset(DateTimeOffset startedAt, TimeSpan offset, out DateTimeOffset timestamp)
    {
        try
        {
            timestamp = startedAt.Add(offset);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            timestamp = default;
            return false;
        }
    }

    private static bool IsTwitchVodId(string value) =>
        value.Length is > 0 and <= MaximumTwitchVodIdLength &&
        value.All(character => character is >= '0' and <= '9');

    // Twitch GraphQL replay chat messages are rebuilt by concatenating fragment text verbatim,
    // so this reader must NOT trim (trimming drops the spaces between fragments).
    private static string GetOptionalString(JsonElement element, string propertyName)
    {
        return JsonElementReader.GetOptionalString(element, propertyName, trimStrings: false);
    }

    private static string CreateDeviceId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static bool IsTwitchLiveDvrReplay(ReplaySessionInfo replay) =>
        replay.Platform == PlatformKind.Twitch &&
        (replay.MediaKind == ReplayMediaKind.CurrentLiveDvr ||
            replay.ReplayId.StartsWith(TwitchLiveDvrReplayIdPrefix, StringComparison.Ordinal));

    private sealed class NoOpAppLogger : IAppLogger
    {
        public static readonly NoOpAppLogger Instance = new();

        public event EventHandler<LogEntry>? EntryWritten
        {
            add { }
            remove { }
        }

        public void Write(AppLogLevel level, string source, string message, Exception? exception = null)
        {
        }
    }

    private static bool LooksLikeOpaqueGraphQlId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return normalized.Length >= 4 &&
            normalized.EndsWith('=') &&
            normalized.All(character =>
                char.IsLetterOrDigit(character) ||
                character is '+' or '/' or '-' or '_' or '=');
    }

}

public sealed record TwitchReplayChatPage(
    IReadOnlyList<ReplayChatMessage> Messages,
    bool HasNextPage,
    string EndCursor);

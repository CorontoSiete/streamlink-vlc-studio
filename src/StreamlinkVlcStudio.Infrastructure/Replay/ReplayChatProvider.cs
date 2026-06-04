using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Replay.TwitchDownloader;

namespace StreamlinkVlcStudio.Infrastructure.Replay;

public sealed class ReplayChatProvider : IReplayChatProvider
{
    private const string TwitchGraphQlEndpoint = "https://gql.twitch.tv/gql";
    // Public Twitch web Client-ID used by the installed Twitch VOD Downloader extension as its fallback.
    private const string TwitchVodDownloaderClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";
    private const string TwitchLiveDvrReplayIdPrefix = "live-dvr-";
    private const string TwitchVideoCommentsOperationName = "VideoCommentsByOffsetOrCursor";
    private const string TwitchVideoCommentsPersistedQueryHash = "b70a3591ff0f4e0313d126c6a1502d79a1c02baebb288227c582044aa76adf6a";
    private const int TwitchReplayChatMaxPages = 60;
    private const int TwitchReplayChatMaxMessages = 5000;
    private static readonly TimeSpan TwitchReplayChatBackfill = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TwitchReplayChatPrefetch = TimeSpan.FromMinutes(4);
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly string TwitchGraphQlDeviceId = CreateDeviceId();

    private readonly HttpClient httpClient;

    public ReplayChatProvider()
        : this(SharedHttpClient)
    {
    }

    public ReplayChatProvider(HttpClient httpClient)
    {
        this.httpClient = httpClient;
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
            PlatformKind.Kick => ReplayChatLoadResult.Unavailable(
                "Kick replay chat is unavailable because Kick does not expose a stable public replay-chat API."),
            _ => ReplayChatLoadResult.Unavailable($"Replay chat is not supported for {replay.Platform}.")
        };
    }

    public static string GetDefaultReplayChatCacheDirectory(PlatformKind platform)
    {
        var platformDirectory = platform == PlatformKind.Kick ? "kick" : "twitch";
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StreamlinkVlcStudio",
            "replay-chat",
            platformDirectory);
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

        var cacheResult = LoadTwitchChat(replay, cancellationToken);
        if (cacheResult.IsAvailable)
        {
            return cacheResult;
        }

        return await LoadTwitchGraphQlChatAsync(
                replay,
                offset,
                cacheResult.UnavailableReason,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static ReplayChatLoadResult LoadTwitchChat(ReplaySessionInfo replay, CancellationToken cancellationToken = default)
    {
        if (IsTwitchLiveDvrReplay(replay))
        {
            return ReplayChatLoadResult.Unavailable(
                "Twitch replay chat is unavailable for current-live DVR playback because Twitch has not published a VOD comments ID yet.");
        }

        if (string.IsNullOrWhiteSpace(replay.ReplayId))
        {
            return ReplayChatLoadResult.Unavailable("Twitch replay chat needs a VOD ID.");
        }

        var cacheDirectory = GetDefaultReplayChatCacheDirectory(PlatformKind.Twitch);
        var candidates = new[]
        {
            Path.Combine(cacheDirectory, $"{replay.ReplayId}.json"),
            Path.Combine(cacheDirectory, $"{replay.ReplayId}_chat.json"),
            Path.Combine(cacheDirectory, $"v{replay.ReplayId}.json"),
            Path.Combine(cacheDirectory, $"v{replay.ReplayId}_chat.json")
        };

        var filePath = candidates.FirstOrDefault(File.Exists);
        if (filePath is null)
        {
            return ReplayChatLoadResult.Unavailable(
                $"Twitch replay chat cache was not found for VOD {replay.ReplayId}. Expected a TwitchDownloader JSON file under {cacheDirectory}.");
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            using var document = JsonDocument.Parse(stream);
            var messages = ReadTwitchDownloaderMessages(document.RootElement, replay, cancellationToken);
            return messages.Count == 0
                ? ReplayChatLoadResult.Unavailable("The cached Twitch replay chat file did not contain usable messages.")
                : ReplayChatLoadResult.Available(messages, TimeSpan.Zero, replay.Duration);
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

            var offset = TimeSpan.FromSeconds(Math.Max(0, comment.content_offset_seconds));
            var timestamp = replay.StreamStartedAtUtc is { } startedAt
                ? startedAt + offset
                : DateTimeOffset.Now;
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
            var nextOffset = requestedFrom;
            var loadedThrough = requestedFrom;

            for (var pageIndex = 0; pageIndex < TwitchReplayChatMaxPages; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await FetchTwitchGraphQlChatPageAsync(
                        replay,
                        TwitchVodDownloaderClientId,
                        oauthToken: "",
                        nextOffset,
                        cursor: null,
                        cancellationToken)
                    .ConfigureAwait(false);

                foreach (var message in page.Messages)
                {
                    var key = string.IsNullOrWhiteSpace(message.Message.MessageId)
                        ? $"{message.Offset.TotalMilliseconds:0}:{message.Message.Username}:{message.Message.Message}"
                        : message.Message.MessageId;
                    if (seenMessageIds.Add(key))
                    {
                        messages.Add(message);
                    }
                }

                if (page.Messages.Count > 0)
                {
                    loadedThrough = Max(loadedThrough, page.Messages[^1].Offset);
                }

                if (loadedThrough >= requestedThrough ||
                    messages.Count >= TwitchReplayChatMaxMessages ||
                    !page.HasNextPage ||
                    page.Messages.Count == 0)
                {
                    break;
                }

                var nextPageOffset = TimeSpan.FromSeconds(Math.Floor(page.Messages[^1].Offset.TotalSeconds) + 1);
                if (nextPageOffset <= nextOffset)
                {
                    break;
                }

                nextOffset = nextPageOffset;
            }

            if (messages.Count == 0)
            {
                loadedThrough = requestedThrough;
            }

            return ReplayChatLoadResult.Available(
                messages
                    .OrderBy(message => message.Offset)
                    .ToArray(),
                requestedFrom,
                Max(loadedThrough, requestedThrough));
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

    private async Task<TwitchReplayChatPage> FetchTwitchGraphQlChatPageAsync(
        ReplaySessionInfo replay,
        string clientId,
        string oauthToken,
        TimeSpan offset,
        string? cursor,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TwitchGraphQlEndpoint);
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.AcceptLanguage.ParseAdd("en-US");
        request.Headers.Referrer = new Uri("https://www.twitch.tv/");
        request.Headers.TryAddWithoutValidation("Client-Id", clientId);
        request.Headers.TryAddWithoutValidation("X-Device-Id", TwitchGraphQlDeviceId);
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
        if (!string.IsNullOrWhiteSpace(oauthToken))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"OAuth {oauthToken}");
        }

        request.Content = new StringContent(
            BuildTwitchVideoCommentsPayload(replay.ReplayId, offset, cursor),
            Encoding.UTF8,
            "text/plain");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Twitch GraphQL returned {(int)response.StatusCode} {response.ReasonPhrase}. {ExtractGraphQlMessage(responseBody)}".Trim());
        }

        using var document = JsonDocument.Parse(responseBody);
        var error = ExtractGraphQlError(document.RootElement);
        if (!string.IsNullOrWhiteSpace(error))
        {
            throw new InvalidOperationException($"Twitch GraphQL rejected replay chat: {error}");
        }

        return ReadTwitchGraphQlChatPage(document.RootElement, replay);
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
        if (node.TryGetProperty("message", out var messageElement) &&
            messageElement.ValueKind == JsonValueKind.Object)
        {
            body = ReadTwitchGraphQlMessageBody(messageElement);
            color = GetOptionalString(messageElement, "userColor");
            badges = ReadTwitchGraphQlBadges(messageElement);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var offset = TimeSpan.FromSeconds(Math.Max(0, offsetSeconds));
        var timestamp = TryGetDateTimeOffset(node, "createdAt", out var createdAt)
            ? createdAt
            : replay.StreamStartedAtUtc is { } startedAt ? startedAt + offset : DateTimeOffset.Now;
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
                RoomId: replay.ChatRoomId,
                MessageId: FirstNonEmpty(GetOptionalString(node, "id"), GetOptionalString(edge, "cursor"))));
        return true;
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
                FirstNonEmpty(GetOptionalString(badge, "title"), GetOptionalString(badge, "name")),
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
                FirstNonEmpty(badge.title, ResolveTwitchBadgeTitle(id)),
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

    private static string ExtractGraphQlError(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var error = ExtractGraphQlError(item);
                if (!string.IsNullOrWhiteSpace(error))
                {
                    return error;
                }
            }

            return "";
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("errors", out var errors) ||
            errors.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        return errors
            .EnumerateArray()
            .Select(error => GetOptionalString(error, "message"))
            .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message)) ?? "";
    }

    private static string ExtractGraphQlMessage(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var graphQlError = ExtractGraphQlError(document.RootElement);
            if (!string.IsNullOrWhiteSpace(graphQlError))
            {
                return graphQlError;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
        }

        return responseBody.Length <= 240 ? responseBody : responseBody[..240];
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

    private static TimeSpan ClampOffset(TimeSpan offset, TimeSpan duration)
    {
        if (offset < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return duration > TimeSpan.Zero && offset > duration ? duration : offset;
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private static bool TryGetDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(property.GetString(), out value),
            _ => false
        };
    }

    private static bool? TryGetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static bool TryGetDateTimeOffset(JsonElement element, string propertyName, out DateTimeOffset value)
    {
        value = default;
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(property.GetString(), out value);
    }

    private static string GetOptionalString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return "";
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? "",
            JsonValueKind.Number => property.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamlinkVlcStudio/0.1");
        return client;
    }

    private static string CreateDeviceId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static bool IsTwitchLiveDvrReplay(ReplaySessionInfo replay) =>
        replay.Platform == PlatformKind.Twitch &&
        (replay.MediaKind == ReplayMediaKind.CurrentLiveDvr ||
            replay.ReplayId.StartsWith(TwitchLiveDvrReplayIdPrefix, StringComparison.Ordinal));

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private static string ResolveTwitchBadgeTitle(string? id)
    {
        return NormalizeBadgeId(id) switch
        {
            "admin" => "Admin",
            "artist-badge" => "Artist",
            "bits" => "Bits",
            "broadcaster" => "Broadcaster",
            "founder" => "Founder",
            "moderator" => "Moderator",
            "partner" => "Partner",
            "premium" => "Prime Gaming",
            "staff" => "Staff",
            "subscriber" => "Subscriber",
            "turbo" => "Turbo",
            "vip" => "VIP",
            var normalized when normalized.Length > 0 => ToTitle(normalized),
            _ => "Badge"
        };
    }

    private static string NormalizeBadgeId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? "" : id.Trim().ToLowerInvariant();

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

    private static string ToTitle(string value)
    {
        return string.Join(
            ' ',
            value
                .Replace('_', '-')
                .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
    }
}

public sealed record TwitchReplayChatPage(
    IReadOnlyList<ReplayChatMessage> Messages,
    bool HasNextPage,
    string EndCursor);

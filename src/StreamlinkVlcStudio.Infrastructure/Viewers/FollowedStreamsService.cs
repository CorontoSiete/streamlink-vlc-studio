using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

public sealed class FollowedStreamsService : IFollowedStreamsService
{
    private const int TwitchPageSize = 100;
    private const int MaxTwitchPages = 100;
    private const int KickSlugLimit = 50;
    private static readonly HttpClient SharedHttpClient = HttpClientFactory.Create(
        TimeSpan.FromSeconds(12),
        includeUserAgent: true,
        acceptJson: true);
    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly IKickTokenProvider kickTokenProvider;

    public FollowedStreamsService(IAppLogger logger)
        : this(logger, SharedHttpClient, KickTokenProvider.Shared)
    {
    }

    public FollowedStreamsService(IAppLogger logger, HttpClient httpClient)
        : this(logger, httpClient, KickTokenProvider.Shared)
    {
    }

    internal FollowedStreamsService(
        IAppLogger logger,
        HttpClient httpClient,
        IKickTokenProvider kickTokenProvider)
    {
        this.logger = logger;
        this.httpClient = httpClient;
        this.kickTokenProvider = kickTokenProvider;
    }

    public async Task<FollowedLiveStreamsResult> GetLiveFollowedStreamsAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var streams = new List<FollowedLiveStream>();
        var messages = new List<string>();
        var succeededPlatforms = new List<PlatformKind>();

        var twitchLoad = GetTwitchFollowedStreamsAsync(settings.Chat, cancellationToken);
        var kickLoad = GetKickFollowedStreamsAsync(settings, cancellationToken);
        var observedLoads = await Task.WhenAll(
            ObservePlatformResultAsync(PlatformKind.Twitch, twitchLoad, cancellationToken),
            ObservePlatformResultAsync(PlatformKind.Kick, kickLoad, cancellationToken))
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var observed in observedLoads)
        {
            if (observed.Result is not { } result)
            {
                messages.Add(observed.FailureMessage);
                continue;
            }

            streams.AddRange(result.Streams);
            messages.AddRange(result.Messages);
            if (result.Succeeded)
            {
                succeededPlatforms.Add(observed.Platform);
            }
        }

        var ordered = streams
            .DistinctBy(stream => stream.Target.StateKey, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(stream => stream.ViewerCount ?? -1)
            .ThenBy(stream => stream.Platform)
            .ThenBy(stream => stream.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new FollowedLiveStreamsResult(ordered, messages, succeededPlatforms);
    }

    private async Task<ObservedPlatformLoad> ObservePlatformResultAsync(
        PlatformKind platform,
        Task<PlatformFollowedStreamsResult> loadTask,
        CancellationToken cancellationToken)
    {
        try
        {
            return new ObservedPlatformLoad(
                platform,
                await loadTask.ConfigureAwait(false),
                "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.Write(AppLogLevel.Warning, "Followed", $"{platform} followed streams could not be loaded.", ex);
            return new ObservedPlatformLoad(platform, null, $"{platform}: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return new ObservedPlatformLoad(platform, null, "");
        }
    }

    private async Task<PlatformFollowedStreamsResult> GetTwitchFollowedStreamsAsync(
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        var streams = new List<FollowedLiveStream>();
        var token = TwitchOAuthService.NormalizeOAuthToken(settings.TwitchOAuthToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return PlatformFollowedStreamsResult.NotConfigured(
                "Twitch: connect a Twitch account with user:read:follows.");
        }

        TwitchTokenInfo tokenInfo;
        try
        {
            tokenInfo = await TwitchOAuthService.ValidateTokenAsync(httpClient, token, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Warning, "Followed", "Twitch token validation failed for followed streams.", ex);
            return PlatformFollowedStreamsResult.NotConfigured(
                "Twitch: saved OAuth token is invalid or expired.");
        }

        if (!tokenInfo.CanReadFollows)
        {
            return PlatformFollowedStreamsResult.NotConfigured(
                "Twitch: reconnect Twitch to grant user:read:follows.");
        }

        var clientId = tokenInfo.ClientId.Trim();
        TwitchClientIdResolver.WarnIfConfiguredMismatch(settings, clientId, logger, "Followed");
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return PlatformFollowedStreamsResult.NotConfigured(
                "Twitch: Client ID is required for followed streams.");
        }

        var after = "";
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var pageCount = 0;
        var malformed = false;
        do
        {
            if (++pageCount > MaxTwitchPages)
            {
                const string message = "Twitch: followed streams pagination exceeded the safety limit.";
                logger.Write(AppLogLevel.Warning, "Followed", message);
                return new PlatformFollowedStreamsResult(streams, [message]);
            }

            var url = BuildTwitchFollowedStreamsUrl(tokenInfo.UserId, after);
            using var request = TwitchApiRequest.Create(HttpMethod.Get, url, token, clientId);

            using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
            var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.Write(
                    AppLogLevel.Warning,
                    "Followed",
                    $"Twitch followed streams request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {ApiErrorMessage.Extract(responseBody)}");
                return new PlatformFollowedStreamsResult(
                    streams,
                    ["Twitch: followed streams unavailable. Check Twitch Client ID and OAuth token."]);
            }

            using var document = JsonDocument.Parse(responseBody);
            if (!TryGetArray(document.RootElement, "data", out var data))
            {
                malformed = true;
                break;
            }

            var page = ReadTwitchStreams(document.RootElement).ToArray();
            streams.AddRange(page);
            if (page.Length != data.GetArrayLength() ||
                (document.RootElement.TryGetProperty("pagination", out var pagination) &&
                 (pagination.ValueKind != JsonValueKind.Object ||
                  (pagination.TryGetProperty("cursor", out var cursor) && cursor.ValueKind != JsonValueKind.String))))
            {
                malformed = true;
                break;
            }

            var nextCursor = ReadPaginationCursor(document.RootElement);
            if (!string.IsNullOrWhiteSpace(nextCursor) && !seenCursors.Add(nextCursor))
            {
                const string message = "Twitch: followed streams pagination repeated a cursor.";
                logger.Write(AppLogLevel.Warning, "Followed", message);
                return new PlatformFollowedStreamsResult(streams, [message]);
            }

            after = nextCursor;
        }
        while (!string.IsNullOrWhiteSpace(after));

        await EnrichTwitchProfileImagesAsync(
            streams,
            token,
            clientId,
            cancellationToken).ConfigureAwait(false);

        return malformed
            ? PlatformFollowedStreamsResult.Malformed(PlatformKind.Twitch, streams)
            : new PlatformFollowedStreamsResult(streams, [], Succeeded: true);
    }

    private async Task<PlatformFollowedStreamsResult> GetKickFollowedStreamsAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var slugs = NormalizeKickSlugs(settings.FollowedChannels.KickChannelSlugs
            .Concat(settings.FollowedChannels.KickImportedChannelSlugs));
        if (slugs.Count == 0)
        {
            return settings.FollowedChannels.KickFollowsImportedAtUtc is not null
                ? new PlatformFollowedStreamsResult([], [], Succeeded: true)
                : PlatformFollowedStreamsResult.NotConfigured(
                    "Kick: use Detect Kick follows in Settings to import your followed channels.");
        }

        var accessToken = await kickTokenProvider
            .ResolveAsync(settings.Chat, logger, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return PlatformFollowedStreamsResult.NotConfigured(
                "Kick: configure Kick Client ID and Client Secret or a Kick user token.");
        }

        var streams = new List<FollowedLiveStream>();
        var broadcasterUserIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var malformed = false;
        for (var index = 0; index < slugs.Count; index += KickSlugLimit)
        {
            var chunk = slugs.Skip(index).Take(KickSlugLimit).ToArray();
            var url = BuildKickChannelsUrl(chunk);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
            var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.Write(
                    AppLogLevel.Warning,
                    "Followed",
                    $"Kick channels request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {ApiErrorMessage.Extract(responseBody)}");
                return new PlatformFollowedStreamsResult(
                    streams,
                    ["Kick: live followed channels unavailable. Check Kick API credentials."]);
            }

            using var document = JsonDocument.Parse(responseBody);
            var page = ReadKickChannelStreams(document.RootElement, chunk, broadcasterUserIds);
            streams.AddRange(page.Streams);
            malformed |= !page.Succeeded;
        }

        await EnrichKickProfileImagesAsync(streams, broadcasterUserIds, accessToken, cancellationToken)
            .ConfigureAwait(false);

        return malformed
            ? PlatformFollowedStreamsResult.Malformed(PlatformKind.Kick, streams)
            : new PlatformFollowedStreamsResult(streams, [], Succeeded: true);
    }

    private Task EnrichTwitchProfileImagesAsync(
        List<FollowedLiveStream> streams,
        string accessToken,
        string clientId,
        CancellationToken cancellationToken)
    {
        return ProfileImageLookup.EnrichTwitchAsync(
            httpClient,
            streams,
            stream => stream.Channel,
            // Followed cards can already carry an image from the followed-channel payload.
            (stream, profileImage) => stream with
            {
                ProfileImageUrl = FirstNonEmpty(stream.ProfileImageUrl, profileImage)
            },
            accessToken,
            clientId,
            logger,
            "Followed",
            cancellationToken);
    }

    private async Task EnrichKickProfileImagesAsync(
        List<FollowedLiveStream> streams,
        IReadOnlyDictionary<string, string> broadcasterUserIds,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var userIds = broadcasterUserIds.Values
            .Where(userId => !string.IsNullOrWhiteSpace(userId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (userIds.Length == 0)
        {
            return;
        }

        try
        {
            var profileImages = await ProfileImageLookup.GetKickAsync(
                httpClient,
                accessToken,
                userIds,
                cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < streams.Count; index++)
            {
                if (streams[index].ProfileImageUrl.Length > 0 ||
                    !broadcasterUserIds.TryGetValue(streams[index].Channel, out var userId) ||
                    !profileImages.TryGetValue(userId, out var profileImage))
                {
                    continue;
                }

                streams[index] = streams[index] with { ProfileImageUrl = profileImage };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Followed", "Kick profile images could not be loaded.", ex);
        }
    }

    private static IEnumerable<FollowedLiveStream> ReadTwitchStreams(JsonElement root)
    {
        foreach (var stream in BrowsePayloadMapper.ReadTwitchStreams(root))
        {
            yield return new FollowedLiveStream(
                stream.Platform,
                stream.Channel,
                stream.DisplayName,
                stream.Title,
                stream.CategoryName,
                stream.ViewerCount,
                stream.ThumbnailUrl,
                stream.StartedAtUtc,
                stream.IsMature,
                stream.Language,
                stream.Url,
                stream.ProfileImageUrl);
        }
    }

    private static (IReadOnlyList<FollowedLiveStream> Streams, bool Succeeded) ReadKickChannelStreams(
        JsonElement root,
        IReadOnlyList<string> requestedSlugs,
        IDictionary<string, string> broadcasterUserIds)
    {
        var streams = new List<FollowedLiveStream>();
        if (!TryGetArray(root, "data", out var data))
        {
            return (streams, false);
        }

        var requested = requestedSlugs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var succeeded = true;
        foreach (var item in data.EnumerateArray())
        {
            if (!TryGetNonEmptyString(item, "slug", out var slug) ||
                !StreamInputParser.TryFromChannel(PlatformKind.Kick, slug, out var target))
            {
                succeeded = false;
                continue;
            }

            if (!requested.Contains(target.Channel))
            {
                continue;
            }

            var payload = LiveChannelPayloadReader.ReadKickChannel(item);
            if (payload.State == LiveChannelState.Unavailable)
            {
                succeeded = false;
                continue;
            }
            if (payload.State == LiveChannelState.Offline)
            {
                continue;
            }

            var stream = payload.Stream;
            var broadcasterUserId = GetOptionalString(item, "broadcaster_user_id");
            if (!string.IsNullOrWhiteSpace(broadcasterUserId))
            {
                broadcasterUserIds[target.Channel] = broadcasterUserId;
            }

            var category = "";
            if (item.TryGetProperty("category", out var categoryElement) &&
                categoryElement.ValueKind == JsonValueKind.Object)
            {
                category = GetOptionalString(categoryElement, "name");
            }

            var thumbnail = GetKickThumbnailUrl(item, stream);

            streams.Add(new FollowedLiveStream(
                PlatformKind.Kick,
                target.Channel,
                target.Channel,
                FirstNonEmpty(GetOptionalString(item, "stream_title"), GetOptionalString(stream, "stream_title"), GetOptionalString(stream, "title")),
                category,
                TryGetInt32(stream, "viewer_count"),
                thumbnail,
                FirstDateTimeOffset(stream, "started_at", "start_time"),
                TryGetBool(stream, "is_mature"),
                GetOptionalString(stream, "language"),
                target.Url,
                NormalizeImageUrl(FirstNonEmpty(
                    GetOptionalString(item, "profile_picture"),
                    GetOptionalString(item, "profile_pic")))));
        }

        return (streams, succeeded);
    }

    private static string BuildTwitchFollowedStreamsUrl(string userId, string after)
    {
        var builder = new StringBuilder("https://api.twitch.tv/helix/streams/followed?");
        builder.Append("user_id=");
        builder.Append(Uri.EscapeDataString(userId));
        builder.Append("&first=");
        builder.Append(TwitchPageSize.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(after))
        {
            builder.Append("&after=");
            builder.Append(Uri.EscapeDataString(after));
        }

        return builder.ToString();
    }

    private static string BuildKickChannelsUrl(IReadOnlyList<string> slugs)
    {
        var query = string.Join("&", slugs.Select(slug => $"slug={Uri.EscapeDataString(slug)}"));
        return $"https://api.kick.com/public/v1/channels?{query}";
    }

    private static string GetKickThumbnailUrl(JsonElement channel, JsonElement stream)
    {
        return NormalizeImageUrl(FirstNonEmpty(
            GetOptionalString(stream, "thumbnail"),
            GetOptionalString(channel, "thumbnail")));
    }

    private static IReadOnlyList<string> NormalizeKickSlugs(IEnumerable<string> values)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var candidate = (value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            StreamTarget target;
            if (StreamInputParser.TryParsePlatformUrl(candidate, out var parsedTarget) && parsedTarget is not null)
            {
                if (parsedTarget.Platform != PlatformKind.Kick)
                {
                    continue;
                }

                target = parsedTarget;
            }
            else
            {
                if (!StreamInputParser.TryFromChannel(PlatformKind.Kick, candidate, out var channelTarget))
                {
                    continue;
                }

                target = channelTarget;
            }

            if (seen.Add(target.Channel))
            {
                normalized.Add(target.Channel);
            }
        }

        return normalized;
    }

    private static DateTimeOffset? FirstDateTimeOffset(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = TryGetDateTimeOffset(element, propertyName);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private sealed record PlatformFollowedStreamsResult(
        IReadOnlyList<FollowedLiveStream> Streams,
        IReadOnlyList<string> Messages,
        bool Succeeded = false)
    {
        internal static PlatformFollowedStreamsResult Malformed(
            PlatformKind platform, IReadOnlyList<FollowedLiveStream> streams) =>
            new(streams, [$"{platform}: followed streams returned malformed data; offline status could not be determined."]);

        public static PlatformFollowedStreamsResult NotConfigured(string message)
        {
            return new PlatformFollowedStreamsResult([], [message]);
        }
    }

    private sealed record ObservedPlatformLoad(
        PlatformKind Platform,
        PlatformFollowedStreamsResult? Result,
        string FailureMessage);
}

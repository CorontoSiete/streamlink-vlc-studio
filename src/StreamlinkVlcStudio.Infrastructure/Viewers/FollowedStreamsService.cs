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
        var offlineChannels = new List<FollowedChannel>();
        var messages = new List<string>();
        var offlineMessages = new List<string>();
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
                offlineMessages.Add(observed.FailureMessage);
                continue;
            }

            streams.AddRange(result.Streams);
            messages.AddRange(result.Messages);
            offlineChannels.AddRange(result.OfflineChannels ?? []);
            offlineMessages.AddRange(result.Messages);
            offlineMessages.AddRange(result.OfflineMessages ?? []);
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

        var liveKeys = ordered.Select(stream => stream.Target.StateKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orderedOfflineChannels = offlineChannels
            .Where(channel => !liveKeys.Contains(channel.Target.StateKey))
            .DistinctBy(channel => channel.Target.StateKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(channel => FirstNonEmpty(channel.DisplayName, channel.Channel), StringComparer.OrdinalIgnoreCase)
            .ThenBy(channel => channel.Platform)
            .ToArray();

        return new FollowedLiveStreamsResult(
            ordered, messages, succeededPlatforms, orderedOfflineChannels, offlineMessages);
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

            var url = BuildTwitchFollowedUrl("streams", tokenInfo.UserId, after);
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

        var offlineResult = (Channels: new List<FollowedChannel>(), Messages: new List<string>());
        if (!malformed)
        {
            offlineResult = await GetTwitchOfflineChannelsAsync(
                tokenInfo.UserId, token, clientId, streams, cancellationToken).ConfigureAwait(false);
        }

        await EnrichTwitchProfileImagesAsync(
            streams,
            offlineResult.Channels,
            token,
            clientId,
            cancellationToken).ConfigureAwait(false);

        return malformed
            ? PlatformFollowedStreamsResult.Malformed(PlatformKind.Twitch, streams)
            : new PlatformFollowedStreamsResult(streams, [], Succeeded: true,
                OfflineChannels: offlineResult.Channels, OfflineMessages: offlineResult.Messages);
    }

    private async Task<(List<FollowedChannel> Channels, List<string> Messages)> GetTwitchOfflineChannelsAsync(
        string userId,
        string accessToken,
        string clientId,
        IReadOnlyList<FollowedLiveStream> streams,
        CancellationToken cancellationToken)
    {
        try
        {
            var channels = new List<FollowedChannel>();
            var after = "";
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            var pageCount = 0;
            do
            {
                if (++pageCount > MaxTwitchPages)
                    throw new InvalidOperationException("followed channels pagination exceeded the safety limit.");

                var url = BuildTwitchFollowedUrl("channels", userId, after);
                using var request = TwitchApiRequest.Create(HttpMethod.Get, url, accessToken, clientId);
                using var response = await BoundedHttpResponseSender.SendAsync(
                    httpClient, request, cancellationToken).ConfigureAwait(false);
                var responseBody = await BoundedHttpContentReader.ReadJsonAsync(
                    response.Content, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"followed channels request failed: {(int)response.StatusCode} {response.ReasonPhrase}.");

                using var document = JsonDocument.Parse(responseBody);
                if (!TryGetArray(document.RootElement, "data", out var data))
                    throw new InvalidOperationException("followed channels returned malformed data.");

                foreach (var item in data.EnumerateArray())
                {
                    if (!TryGetNonEmptyString(item, "broadcaster_login", out var login) ||
                        !StreamInputParser.TryFromChannel(PlatformKind.Twitch, login, out var target))
                        throw new InvalidOperationException("followed channels returned malformed data.");

                    channels.Add(new FollowedChannel(PlatformKind.Twitch, target.Channel,
                        FirstNonEmpty(GetOptionalString(item, "broadcaster_name"), target.Channel), target.Url));
                }

                if (document.RootElement.TryGetProperty("pagination", out var pagination) &&
                    (pagination.ValueKind != JsonValueKind.Object ||
                     (pagination.TryGetProperty("cursor", out var cursor) && cursor.ValueKind != JsonValueKind.String)))
                    throw new InvalidOperationException("followed channels returned malformed pagination.");

                after = ReadPaginationCursor(document.RootElement);
                if (!string.IsNullOrWhiteSpace(after) && !seenCursors.Add(after))
                    throw new InvalidOperationException("followed channels pagination repeated a cursor.");
            }
            while (!string.IsNullOrWhiteSpace(after));

            var liveChannels = streams.Select(stream => stream.Channel)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (channels.Where(channel => !liveChannels.Contains(channel.Channel))
                .DistinctBy(channel => channel.Channel, StringComparer.OrdinalIgnoreCase).ToList(), []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Followed", "Twitch offline followed channels could not be loaded.", ex);
            return ([], [$"Twitch: offline followed channels unavailable; {ex.Message}"]);
        }
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
        var offlineChannels = new List<FollowedChannel>();
        var broadcasterUserIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var malformed = false;
        var missingChannelCount = 0;
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
                    ["Kick: live followed channels unavailable. Check Kick API credentials."],
                    OfflineChannels: offlineChannels);
            }

            using var document = JsonDocument.Parse(responseBody);
            var page = ReadKickChannelStreams(document.RootElement, chunk, broadcasterUserIds);
            streams.AddRange(page.Streams);
            offlineChannels.AddRange(page.OfflineChannels);
            malformed |= !page.Succeeded;
            missingChannelCount += page.MissingChannelCount;
        }

        await EnrichKickProfileImagesAsync(streams, offlineChannels, broadcasterUserIds, accessToken, cancellationToken)
            .ConfigureAwait(false);

        return malformed
            ? PlatformFollowedStreamsResult.Malformed(PlatformKind.Kick, streams, offlineChannels)
            : new PlatformFollowedStreamsResult(streams, [], Succeeded: true,
                OfflineChannels: offlineChannels, OfflineMessages: missingChannelCount switch
                {
                    0 => [],
                    1 => ["Kick: status could not be determined for 1 followed channel."],
                    _ => [$"Kick: status could not be determined for {missingChannelCount} followed channels."]
                });
    }

    private async Task EnrichTwitchProfileImagesAsync(
        List<FollowedLiveStream> streams,
        List<FollowedChannel> offlineChannels,
        string accessToken,
        string clientId,
        CancellationToken cancellationToken)
    {
        var channels = streams.Select(stream => stream.Channel)
            .Concat(offlineChannels.Select(channel => channel.Channel)).ToArray();
        if (channels.Length == 0) return;

        try
        {
            var profileImages = await ProfileImageLookup.GetTwitchAsync(
                httpClient, accessToken, clientId, channels, cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < streams.Count; index++)
            {
                if (profileImages.TryGetValue(streams[index].Channel, out var profileImage))
                    streams[index] = streams[index] with
                    {
                        ProfileImageUrl = FirstNonEmpty(streams[index].ProfileImageUrl, profileImage)
                    };
            }
            for (var index = 0; index < offlineChannels.Count; index++)
            {
                if (profileImages.TryGetValue(offlineChannels[index].Channel, out var profileImage))
                    offlineChannels[index] = offlineChannels[index] with { ProfileImageUrl = profileImage };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Followed", "Twitch profile images could not be loaded.", ex);
        }
    }

    private async Task EnrichKickProfileImagesAsync(
        List<FollowedLiveStream> streams,
        List<FollowedChannel> offlineChannels,
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
            for (var index = 0; index < offlineChannels.Count; index++)
            {
                if (offlineChannels[index].ProfileImageUrl.Length > 0 ||
                    !broadcasterUserIds.TryGetValue(offlineChannels[index].Channel, out var userId) ||
                    !profileImages.TryGetValue(userId, out var profileImage))
                    continue;

                offlineChannels[index] = offlineChannels[index] with { ProfileImageUrl = profileImage };
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

    private static (IReadOnlyList<FollowedLiveStream> Streams, IReadOnlyList<FollowedChannel> OfflineChannels,
        bool Succeeded, int MissingChannelCount) ReadKickChannelStreams(
        JsonElement root,
        IReadOnlyList<string> requestedSlugs,
        IDictionary<string, string> broadcasterUserIds)
    {
        var streams = new List<FollowedLiveStream>();
        var offlineChannels = new List<FollowedChannel>();
        if (!TryGetArray(root, "data", out var data))
        {
            return (streams, offlineChannels, false, 0);
        }

        var requested = requestedSlugs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var returned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            returned.Add(target.Channel);
            var payload = LiveChannelPayloadReader.ReadKickChannel(item);
            if (payload.State == LiveChannelState.Unavailable)
            {
                succeeded = false;
                continue;
            }
            var broadcasterUserId = GetOptionalString(item, "broadcaster_user_id");
            if (!string.IsNullOrWhiteSpace(broadcasterUserId))
            {
                broadcasterUserIds[target.Channel] = broadcasterUserId;
            }

            var profileImage = NormalizeImageUrl(FirstNonEmpty(
                GetOptionalString(item, "profile_picture"), GetOptionalString(item, "profile_pic")));
            if (payload.State == LiveChannelState.Offline)
            {
                offlineChannels.Add(new FollowedChannel(
                    PlatformKind.Kick, target.Channel, target.Channel, target.Url, profileImage));
                continue;
            }

            var stream = payload.Stream;
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
                profileImage));
        }

        return (streams, offlineChannels, succeeded, requested.Count - returned.Count);
    }

    private static string BuildTwitchFollowedUrl(string resource, string userId, string after)
    {
        var builder = new StringBuilder($"https://api.twitch.tv/helix/{resource}/followed?");
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
        bool Succeeded = false,
        IReadOnlyList<FollowedChannel>? OfflineChannels = null,
        IReadOnlyList<string>? OfflineMessages = null)
    {
        internal static PlatformFollowedStreamsResult Malformed(
            PlatformKind platform, IReadOnlyList<FollowedLiveStream> streams,
            IReadOnlyList<FollowedChannel>? offlineChannels = null) =>
            new(streams, [$"{platform}: followed streams returned malformed data; offline status could not be determined."],
                OfflineChannels: offlineChannels);

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

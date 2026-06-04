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

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

public sealed class FollowedStreamsService : IFollowedStreamsService
{
    private const int TwitchPageSize = 100;
    private const int KickSlugLimit = 50;
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;

    public FollowedStreamsService(IAppLogger logger)
        : this(logger, SharedHttpClient)
    {
    }

    public FollowedStreamsService(IAppLogger logger, HttpClient httpClient)
    {
        this.logger = logger;
        this.httpClient = httpClient;
    }

    public async Task<FollowedLiveStreamsResult> GetLiveFollowedStreamsAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var streams = new List<FollowedLiveStream>();
        var messages = new List<string>();

        await AppendPlatformResultAsync(
            streams,
            messages,
            () => GetTwitchFollowedStreamsAsync(settings.Chat, cancellationToken),
            "Twitch",
            cancellationToken).ConfigureAwait(false);

        await AppendPlatformResultAsync(
            streams,
            messages,
            () => GetKickFollowedStreamsAsync(settings, cancellationToken),
            "Kick",
            cancellationToken).ConfigureAwait(false);

        var ordered = streams
            .OrderByDescending(stream => stream.ViewerCount ?? -1)
            .ThenBy(stream => stream.Platform)
            .ThenBy(stream => stream.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new FollowedLiveStreamsResult(ordered, messages);
    }

    private async Task AppendPlatformResultAsync(
        List<FollowedLiveStream> streams,
        List<string> messages,
        Func<Task<PlatformFollowedStreamsResult>> loadAsync,
        string platformName,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await loadAsync().ConfigureAwait(false);
            streams.AddRange(result.Streams);
            messages.AddRange(result.Messages);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.Write(AppLogLevel.Warning, "Followed", $"{platformName} followed streams could not be loaded.", ex);
            messages.Add($"{platformName}: {ex.Message}");
        }
    }

    private async Task<PlatformFollowedStreamsResult> GetTwitchFollowedStreamsAsync(
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        var messages = new List<string>();
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

        var clientId = string.IsNullOrWhiteSpace(settings.TwitchClientId)
            ? tokenInfo.ClientId
            : settings.TwitchClientId.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return PlatformFollowedStreamsResult.NotConfigured(
                "Twitch: Client ID is required for followed streams.");
        }

        var after = "";
        do
        {
            var url = BuildTwitchFollowedStreamsUrl(tokenInfo.UserId, after);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("Client-Id", clientId);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.Write(
                    AppLogLevel.Warning,
                    "Followed",
                    $"Twitch followed streams request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {ExtractApiMessage(responseBody)}");
                return new PlatformFollowedStreamsResult(
                    streams,
                    ["Twitch: followed streams unavailable. Check Twitch Client ID and OAuth token."]);
            }

            using var document = JsonDocument.Parse(responseBody);
            streams.AddRange(ReadTwitchStreams(document.RootElement));
            after = ReadPaginationCursor(document.RootElement);
        }
        while (!string.IsNullOrWhiteSpace(after));

        return new PlatformFollowedStreamsResult(streams, messages);
    }

    private async Task<PlatformFollowedStreamsResult> GetKickFollowedStreamsAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var slugs = NormalizeKickSlugs(settings.FollowedChannels.KickChannelSlugs);
        if (slugs.Count == 0)
        {
            return PlatformFollowedStreamsResult.NotConfigured(
                "Kick: add followed channel slugs in Settings.");
        }

        var accessToken = await ResolveKickAccessTokenAsync(settings.Chat, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return PlatformFollowedStreamsResult.NotConfigured(
                "Kick: configure Kick Client ID and Client Secret or a Kick user token.");
        }

        var streams = new List<FollowedLiveStream>();
        for (var index = 0; index < slugs.Count; index += KickSlugLimit)
        {
            var chunk = slugs.Skip(index).Take(KickSlugLimit).ToArray();
            var url = BuildKickChannelsUrl(chunk);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.Write(
                    AppLogLevel.Warning,
                    "Followed",
                    $"Kick channels request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {ExtractApiMessage(responseBody)}");
                return new PlatformFollowedStreamsResult(
                    streams,
                    ["Kick: live followed channels unavailable. Check Kick API credentials."]);
            }

            using var document = JsonDocument.Parse(responseBody);
            streams.AddRange(ReadKickChannelStreams(document.RootElement));
        }

        return new PlatformFollowedStreamsResult(streams, []);
    }

    private async Task<string?> ResolveKickAccessTokenAsync(ChatSettings settings, CancellationToken cancellationToken)
    {
        var appToken = await KickOAuthService.TryGetAppAccessTokenAsync(settings, logger, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(appToken))
        {
            return appToken;
        }

        return await KickOAuthService.GetUsableAccessTokenAsync(settings, logger, cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<FollowedLiveStream> ReadTwitchStreams(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in data.EnumerateArray())
        {
            var login = GetOptionalString(item, "user_login").Trim();
            if (string.IsNullOrWhiteSpace(login))
            {
                continue;
            }

            if (!TryCreateTarget(PlatformKind.Twitch, login, out var target))
            {
                continue;
            }

            var title = GetOptionalString(item, "title");
            var category = GetOptionalString(item, "game_name");
            var thumbnail = NormalizeTwitchThumbnailUrl(GetOptionalString(item, "thumbnail_url"));

            yield return new FollowedLiveStream(
                PlatformKind.Twitch,
                target.Channel,
                GetOptionalString(item, "user_name") is { Length: > 0 } displayName ? displayName : target.Channel,
                title,
                category,
                TryGetInt32(item, "viewer_count"),
                thumbnail,
                TryGetDateTimeOffset(item, "started_at"),
                TryGetBool(item, "is_mature"),
                GetOptionalString(item, "language"),
                target.Url);
        }
    }

    private static IEnumerable<FollowedLiveStream> ReadKickChannelStreams(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in data.EnumerateArray())
        {
            var slug = GetOptionalString(item, "slug").Trim();
            if (string.IsNullOrWhiteSpace(slug) ||
                !item.TryGetProperty("stream", out var stream) ||
                stream.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                stream.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var isLive = TryGetBool(stream, "is_live");
            if (isLive == false)
            {
                continue;
            }

            if (!TryCreateTarget(PlatformKind.Kick, slug, out var target))
            {
                continue;
            }

            var category = "";
            if (item.TryGetProperty("category", out var categoryElement) &&
                categoryElement.ValueKind == JsonValueKind.Object)
            {
                category = GetOptionalString(categoryElement, "name");
            }

            var thumbnail = GetKickThumbnailUrl(item, stream);

            yield return new FollowedLiveStream(
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
                target.Url);
        }
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

    private static string ReadPaginationCursor(JsonElement root)
    {
        if (!root.TryGetProperty("pagination", out var pagination) ||
            pagination.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        return GetOptionalString(pagination, "cursor");
    }

    private static string NormalizeTwitchThumbnailUrl(string url)
    {
        return string.IsNullOrWhiteSpace(url)
            ? ""
            : url.Replace("{width}", "440", StringComparison.OrdinalIgnoreCase)
                .Replace("{height}", "248", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetKickThumbnailUrl(JsonElement channel, JsonElement stream)
    {
        return NormalizeImageUrl(FirstNonEmpty(
            GetOptionalString(stream, "thumbnail"),
            GetOptionalString(channel, "thumbnail")));
    }

    private static string NormalizeImageUrl(string url)
    {
        var trimmed = url.Trim();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            ? "https:" + trimmed
            : trimmed;
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
                if (!TryCreateTarget(PlatformKind.Kick, candidate, out target))
                {
                    continue;
                }
            }

            if (seen.Add(target.Channel))
            {
                normalized.Add(target.Channel);
            }
        }

        return normalized;
    }

    private static bool TryCreateTarget(PlatformKind platform, string channel, out StreamTarget target)
    {
        try
        {
            target = StreamInputParser.FromChannel(platform, channel);
            return true;
        }
        catch (ArgumentException)
        {
            target = null!;
            return false;
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return "";
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

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
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
            JsonValueKind.String when bool.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                property.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var value))
        {
            return null;
        }

        return value;
    }

    private static string GetOptionalString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";
    }

    private static string ExtractApiMessage(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? "";
            }

            if (document.RootElement.TryGetProperty("error_description", out var errorDescription))
            {
                return errorDescription.GetString() ?? "";
            }

            if (document.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
        }

        return responseBody.Length <= 240 ? responseBody : responseBody[..240];
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamlinkVlcStudio/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private sealed record PlatformFollowedStreamsResult(
        IReadOnlyList<FollowedLiveStream> Streams,
        IReadOnlyList<string> Messages)
    {
        public static PlatformFollowedStreamsResult NotConfigured(string message)
        {
            return new PlatformFollowedStreamsResult([], [message]);
        }
    }
}

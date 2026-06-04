using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

public sealed class TwitchVodService : ITwitchVodService
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;

    public TwitchVodService(IAppLogger logger)
        : this(logger, SharedHttpClient)
    {
    }

    public TwitchVodService(IAppLogger logger, HttpClient httpClient)
    {
        this.logger = logger;
        this.httpClient = httpClient;
    }

    public async Task<TwitchVodSearchResult> SearchAsync(
        TwitchVodSearchRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var login = NormalizeStreamerLogin(request.Streamer);
        if (string.IsNullOrWhiteSpace(login))
        {
            return new TwitchVodSearchResult(
                TwitchVodSearchStatus.NotFound,
                null,
                [],
                "",
                "Enter a Twitch streamer login.");
        }

        var token = TwitchOAuthService.NormalizeOAuthToken(settings.Chat.TwitchOAuthToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return new TwitchVodSearchResult(
                TwitchVodSearchStatus.NotConfigured,
                null,
                [],
                "",
                "Twitch VOD search requires a Twitch OAuth token.");
        }

        var clientId = await ResolveClientIdAsync(settings.Chat, token, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new TwitchVodSearchResult(
                TwitchVodSearchStatus.NotConfigured,
                null,
                [],
                "",
                "Twitch VOD search requires a Twitch Client ID that matches the OAuth token.");
        }

        var broadcaster = await ResolveBroadcasterAsync(login, token, clientId, cancellationToken).ConfigureAwait(false);
        if (broadcaster is null)
        {
            return new TwitchVodSearchResult(
                TwitchVodSearchStatus.NotFound,
                null,
                [],
                "",
                $"Twitch channel '{login}' was not found.");
        }

        return await LoadVideosAsync(
            request,
            broadcaster,
            token,
            clientId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ResolveClientIdAsync(
        ChatSettings settings,
        string token,
        CancellationToken cancellationToken)
    {
        var configured = settings.TwitchClientId.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        try
        {
            var tokenInfo = await TwitchOAuthService.ValidateTokenAsync(httpClient, token, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(tokenInfo.ClientId) ? null : tokenInfo.ClientId.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Warning, "VODs", "Twitch token validation failed for VOD search.", ex);
            return null;
        }
    }

    private async Task<TwitchVodBroadcaster?> ResolveBroadcasterAsync(
        string login,
        string token,
        string clientId,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.twitch.tv/helix/users?login={Uri.EscapeDataString(login)}";
        using var request = CreateTwitchRequest(url, token, clientId);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "VODs",
                $"Twitch user lookup failed for {login}: {(int)response.StatusCode} {response.ReasonPhrase}. {ExtractApiMessage(responseBody)}");
            throw new InvalidOperationException("Twitch user lookup failed. Check the Twitch Client ID and OAuth token.");
        }

        using var document = JsonDocument.Parse(responseBody);
        return ReadBroadcaster(document.RootElement);
    }

    private async Task<TwitchVodSearchResult> LoadVideosAsync(
        TwitchVodSearchRequest request,
        TwitchVodBroadcaster broadcaster,
        string token,
        string clientId,
        CancellationToken cancellationToken)
    {
        var url = BuildVideosUrl(broadcaster.Id, request);
        using var httpRequest = CreateTwitchRequest(url, token, clientId);
        using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "VODs",
                $"Twitch VOD search failed for {broadcaster.Login}: {(int)response.StatusCode} {response.ReasonPhrase}. {ExtractApiMessage(responseBody)}");
            return new TwitchVodSearchResult(
                TwitchVodSearchStatus.Unavailable,
                broadcaster,
                [],
                "",
                "Twitch VODs unavailable. Check the Twitch Client ID and OAuth token.");
        }

        using var document = JsonDocument.Parse(responseBody);
        var videos = ReadVideos(document.RootElement, broadcaster).ToArray();
        var nextCursor = ReadPaginationCursor(document.RootElement);
        var message = videos.Length switch
        {
            0 => $"No Twitch VODs found for {broadcaster.DisplayName}.",
            1 => $"1 Twitch VOD found for {broadcaster.DisplayName}.",
            _ => $"{videos.Length} Twitch VODs found for {broadcaster.DisplayName}."
        };
        return new TwitchVodSearchResult(
            TwitchVodSearchStatus.Available,
            broadcaster,
            videos,
            nextCursor,
            message);
    }

    private static HttpRequestMessage CreateTwitchRequest(string url, string token, string clientId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Client-Id", clientId);
        return request;
    }

    private static TwitchVodBroadcaster? ReadBroadcaster(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            var id = GetOptionalString(item, "id");
            var login = GetOptionalString(item, "login").Trim();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(login))
            {
                continue;
            }

            var displayName = GetOptionalString(item, "display_name");
            return new TwitchVodBroadcaster(
                id,
                login,
                string.IsNullOrWhiteSpace(displayName) ? login : displayName.Trim());
        }

        return null;
    }

    private static IEnumerable<TwitchVodItem> ReadVideos(JsonElement root, TwitchVodBroadcaster broadcaster)
    {
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in data.EnumerateArray())
        {
            var id = GetOptionalString(item, "id");
            var url = GetOptionalString(item, "url");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var login = FirstNonEmpty(GetOptionalString(item, "user_login"), broadcaster.Login);
            var displayName = FirstNonEmpty(GetOptionalString(item, "user_name"), broadcaster.DisplayName, login);
            _ = TryParseTwitchDuration(GetOptionalString(item, "duration"), out var duration);
            var type = ReadVideoType(GetOptionalString(item, "type"));

            yield return new TwitchVodItem(
                id,
                GetOptionalString(item, "stream_id"),
                FirstNonEmpty(GetOptionalString(item, "user_id"), broadcaster.Id),
                login,
                displayName,
                GetOptionalString(item, "title"),
                GetOptionalString(item, "description"),
                url,
                NormalizeTwitchVodThumbnailUrl(GetOptionalString(item, "thumbnail_url")),
                TryGetDateTimeOffset(item, "created_at"),
                TryGetDateTimeOffset(item, "published_at"),
                duration,
                TryGetInt32(item, "view_count"),
                type);
        }
    }

    private static string BuildVideosUrl(string userId, TwitchVodSearchRequest request)
    {
        var pageSize = Math.Clamp(request.PageSize <= 0 ? 100 : request.PageSize, 1, 100);
        var query = new List<KeyValuePair<string, string>>
        {
            new("user_id", userId),
            new("sort", "time"),
            new("first", pageSize.ToString(CultureInfo.InvariantCulture))
        };

        var type = ToApiType(request.Type);
        if (!string.IsNullOrWhiteSpace(type))
        {
            query.Add(new("type", type));
        }

        if (!string.IsNullOrWhiteSpace(request.Cursor))
        {
            query.Add(new("after", request.Cursor.Trim()));
        }

        var builder = new StringBuilder("https://api.twitch.tv/helix/videos?");
        builder.Append(string.Join('&', query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));
        return builder.ToString();
    }

    private static string ToApiType(TwitchVodTypeFilter type) => type switch
    {
        TwitchVodTypeFilter.Archive => "archive",
        TwitchVodTypeFilter.Highlight => "highlight",
        TwitchVodTypeFilter.Upload => "upload",
        _ => ""
    };

    private static TwitchVodTypeFilter ReadVideoType(string type) => type.Trim().ToLowerInvariant() switch
    {
        "highlight" => TwitchVodTypeFilter.Highlight,
        "upload" => TwitchVodTypeFilter.Upload,
        _ => TwitchVodTypeFilter.Archive
    };

    private static string NormalizeStreamerLogin(string value)
    {
        var trimmed = (value ?? "").Trim().TrimStart('@').Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "";
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal) &&
            (trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("twitch.tv/", StringComparison.OrdinalIgnoreCase)))
        {
            trimmed = "https://" + trimmed;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            uri.Host.EndsWith("twitch.tv", StringComparison.OrdinalIgnoreCase))
        {
            var path = uri.AbsolutePath.Trim('/');
            if (string.IsNullOrWhiteSpace(path))
            {
                return "";
            }

            var firstSegment = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[0].TrimStart('@');
            return IsKnownNonChannelSegment(firstSegment) ? "" : firstSegment.ToLowerInvariant();
        }

        return trimmed.Trim('/').ToLowerInvariant();
    }

    private static bool IsKnownNonChannelSegment(string segment)
    {
        return segment.Equals("videos", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("directory", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("downloads", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("jobs", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("p", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("settings", StringComparison.OrdinalIgnoreCase);
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

    private static string NormalizeTwitchVodThumbnailUrl(string url)
    {
        var normalized = (url ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "";
        }

        return normalized
            .Replace("%{width}", "320", StringComparison.OrdinalIgnoreCase)
            .Replace("%{height}", "180", StringComparison.OrdinalIgnoreCase)
            .Replace("{width}", "320", StringComparison.OrdinalIgnoreCase)
            .Replace("{height}", "180", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseTwitchDuration(string value, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        var text = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var index = 0;
        var total = TimeSpan.Zero;
        while (index < text.Length)
        {
            var start = index;
            while (index < text.Length && char.IsDigit(text[index]))
            {
                index++;
            }

            if (start == index ||
                index >= text.Length ||
                !int.TryParse(text[start..index], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return false;
            }

            var unit = text[index++];
            total += unit switch
            {
                'h' => TimeSpan.FromHours(number),
                'm' => TimeSpan.FromMinutes(number),
                's' => TimeSpan.FromSeconds(number),
                _ => TimeSpan.MinValue
            };

            if (total < TimeSpan.Zero)
            {
                return false;
            }
        }

        duration = total;
        return duration > TimeSpan.Zero;
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

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        var value = GetOptionalString(element, propertyName);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string GetOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return "";
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim() ?? "",
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

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

    private static string ExtractApiMessage(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var message = GetOptionalString(root, "message");
            return string.IsNullOrWhiteSpace(message) ? "" : message;
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }
}

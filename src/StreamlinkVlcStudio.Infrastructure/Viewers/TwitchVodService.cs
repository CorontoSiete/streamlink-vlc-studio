using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Core.Time;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

public sealed class TwitchVodService : ITwitchVodService
{
    private const string TwitchGraphQlEndpoint = "https://gql.twitch.tv/gql";
    private const string TwitchPublicClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";
    private static readonly HttpClient SharedHttpClient = HttpClientFactory.Create(TimeSpan.FromSeconds(20));
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

        var clientId = await TwitchClientIdResolver.ResolveAsync(
            settings.Chat,
            httpClient,
            token,
            logger,
            "VODs",
            "Twitch token validation failed for VOD search.",
            cancellationToken).ConfigureAwait(false);
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

    private async Task<TwitchVodBroadcaster?> ResolveBroadcasterAsync(
        string login,
        string token,
        string clientId,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.twitch.tv/helix/users?login={Uri.EscapeDataString(login)}";
        using var request = CreateTwitchRequest(url, token, clientId);
        using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
        var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "VODs",
                $"Twitch user lookup failed for {login}: {(int)response.StatusCode} {response.ReasonPhrase}. {ApiErrorMessage.Extract(responseBody, includeBodyFallback: false)}");
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
        using var response = await BoundedHttpResponseSender.SendAsync(httpClient, httpRequest, cancellationToken).ConfigureAwait(false);
        var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "VODs",
                $"Twitch VOD search failed for {broadcaster.Login}: {(int)response.StatusCode} {response.ReasonPhrase}. {ApiErrorMessage.Extract(responseBody, includeBodyFallback: false)}");
            return new TwitchVodSearchResult(
                TwitchVodSearchStatus.Unavailable,
                broadcaster,
                [],
                "",
                "Twitch VODs unavailable. Check the Twitch Client ID and OAuth token.");
        }

        using var document = JsonDocument.Parse(responseBody);
        var videos = ReadVideos(document.RootElement, broadcaster).ToArray();
        videos = await PopulateAccessKindsAsync(videos, cancellationToken).ConfigureAwait(false);
        var nextCursor = ReadPaginationCursor(document.RootElement);
        var message = videos.Length switch
        {
            0 => $"No Twitch VODs found for {broadcaster.DisplayName}.",
            1 => $"1 Twitch VOD found for {broadcaster.DisplayName}.",
            _ => $"{videos.Length} Twitch VODs found for {broadcaster.DisplayName}."
        };
        var unknownAccessCount = videos.Count(video => video.AccessKind == TwitchVodAccessKind.Unknown);
        if (unknownAccessCount > 0)
        {
            message += unknownAccessCount == 1
                ? " Access could not be checked for 1 VOD."
                : $" Access could not be checked for {unknownAccessCount} VODs.";
        }

        return new TwitchVodSearchResult(
            TwitchVodSearchStatus.Available,
            broadcaster,
            videos,
            nextCursor,
            message);
    }

    private async Task<TwitchVodItem[]> PopulateAccessKindsAsync(
        TwitchVodItem[] videos,
        CancellationToken cancellationToken)
    {
        if (videos.Length == 0)
        {
            return videos;
        }

        try
        {
            // Helix documents "viewable" as always "public", so it cannot identify
            // subscriber-only VODs. Request playback tokens anonymously: a non-empty
            // chansub.restricted_bitrates array is Twitch's actual subscriber gate.
            using var request = new HttpRequestMessage(HttpMethod.Post, TwitchGraphQlEndpoint);
            request.Headers.Accept.ParseAdd("*/*");
            request.Headers.TryAddWithoutValidation("Client-Id", TwitchPublicClientId);
            request.Headers.TryAddWithoutValidation("X-Device-Id", CreateDeviceId());
            request.Content = new StringContent(
                BuildVodAccessQueryPayload(videos),
                Encoding.UTF8,
                "application/json");

            using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
            var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.Write(
                    AppLogLevel.Warning,
                    "VODs",
                    $"Twitch VOD access lookup failed: {(int)response.StatusCode} {response.ReasonPhrase}. {ApiErrorMessage.Extract(responseBody, includeBodyFallback: false)}".Trim());
                return videos;
            }

            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                logger.Write(
                    AppLogLevel.Warning,
                    "VODs",
                    $"Twitch VOD access lookup returned no data. {GraphQlErrorReader.Extract(root)}".Trim());
                return videos;
            }

            var classified = new TwitchVodItem[videos.Length];
            var unknownCount = 0;
            for (var index = 0; index < videos.Length; index++)
            {
                var accessKind = ReadVodAccessKind(data, index, videos[index].Id);
                classified[index] = videos[index] with { AccessKind = accessKind };
                if (accessKind == TwitchVodAccessKind.Unknown)
                {
                    unknownCount++;
                }
            }

            if (unknownCount > 0)
            {
                logger.Write(
                    AppLogLevel.Warning,
                    "VODs",
                    $"Twitch did not return usable access metadata for {unknownCount} of {videos.Length} VODs. {GraphQlErrorReader.Extract(root)}".Trim());
            }

            return classified;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.Write(
                AppLogLevel.Warning,
                "VODs",
                $"Twitch VOD access labels are unavailable: {ex.Message}");
            return videos;
        }
    }

    private static string BuildVodAccessQueryPayload(IReadOnlyList<TwitchVodItem> videos)
    {
        var declarations = new string[videos.Count];
        var fields = new string[videos.Count];
        var variables = new Dictionary<string, string>(videos.Count, StringComparer.Ordinal);
        for (var index = 0; index < videos.Count; index++)
        {
            var variableName = $"vod{index}";
            declarations[index] = $"${variableName}: ID!";
            fields[index] = $"{variableName}: videoPlaybackAccessToken(id: ${variableName}, params: {{ platform: \"site\", playerType: \"embed\" }}) {{ value }}";
            variables[variableName] = videos[index].Id;
        }

        var payload = new
        {
            query = $"query VodAccess({string.Join(", ", declarations)}) {{ {string.Join(' ', fields)} }}",
            variables
        };
        return JsonSerializer.Serialize(payload);
    }

    private static TwitchVodAccessKind ReadVodAccessKind(JsonElement data, int index, string expectedVodId)
    {
        if (!data.TryGetProperty($"vod{index}", out var accessToken) ||
            accessToken.ValueKind != JsonValueKind.Object ||
            !accessToken.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            return TwitchVodAccessKind.Unknown;
        }

        try
        {
            using var tokenDocument = JsonDocument.Parse(value.GetString()!);
            var token = tokenDocument.RootElement;
            if (token.ValueKind != JsonValueKind.Object ||
                !token.TryGetProperty("vod_id", out var vodId) ||
                !string.Equals(ReadTokenVodId(vodId), expectedVodId, StringComparison.Ordinal) ||
                !token.TryGetProperty("authorization", out var authorization) ||
                authorization.ValueKind != JsonValueKind.Object ||
                !authorization.TryGetProperty("forbidden", out var forbidden) ||
                forbidden.ValueKind != JsonValueKind.False ||
                !token.TryGetProperty("chansub", out var channelSubscription) ||
                channelSubscription.ValueKind != JsonValueKind.Object ||
                !channelSubscription.TryGetProperty("restricted_bitrates", out var restrictedBitrates) ||
                restrictedBitrates.ValueKind != JsonValueKind.Array)
            {
                return TwitchVodAccessKind.Unknown;
            }

            foreach (var bitrate in restrictedBitrates.EnumerateArray())
            {
                if (bitrate.ValueKind != JsonValueKind.String)
                {
                    return TwitchVodAccessKind.Unknown;
                }

                if (!string.IsNullOrWhiteSpace(bitrate.GetString()))
                {
                    return TwitchVodAccessKind.SubscriberOnly;
                }
            }

            return TwitchVodAccessKind.Public;
        }
        catch (JsonException)
        {
            return TwitchVodAccessKind.Unknown;
        }
    }

    private static string ReadTokenVodId(JsonElement vodId) => vodId.ValueKind switch
    {
        JsonValueKind.String => vodId.GetString() ?? "",
        JsonValueKind.Number => vodId.GetRawText(),
        _ => ""
    };

    private static string CreateDeviceId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static HttpRequestMessage CreateTwitchRequest(string url, string token, string clientId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Client-Id", clientId);
        return request;
    }

    private static TwitchVodBroadcaster? ReadBroadcaster(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

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
                string.IsNullOrWhiteSpace(displayName) ? login : displayName.Trim(),
                GetOptionalString(item, "profile_image_url"));
        }

        return null;
    }

    private static IEnumerable<TwitchVodItem> ReadVideos(JsonElement root, TwitchVodBroadcaster broadcaster)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = GetOptionalString(item, "id");
            var url = GetOptionalString(item, "url");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var login = FirstNonEmpty(GetOptionalString(item, "user_login"), broadcaster.Login);
            var displayName = FirstNonEmpty(GetOptionalString(item, "user_name"), broadcaster.DisplayName, login);
            _ = DurationValues.TryParseHmsDuration(GetOptionalString(item, "duration"), out var duration);
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
                NormalizeImageUrl(GetOptionalString(item, "thumbnail_url"), "320", "180"),
                TryGetDateTimeOffset(item, "created_at"),
                TryGetDateTimeOffset(item, "published_at"),
                duration,
                TryGetInt32(item, "view_count"),
                type,
                ProfileImageUrl: broadcaster.ProfileImageUrl);
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
        var trimmed = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "";
        }

        try
        {
            return StreamInputParser.ParseCandidates(trimmed)
                .FirstOrDefault(candidate => candidate.Platform == PlatformKind.Twitch)
                ?.Channel
                .ToLowerInvariant() ?? "";
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

}

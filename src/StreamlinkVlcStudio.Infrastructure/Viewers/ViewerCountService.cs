using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

public sealed class ViewerCountService : IViewerCountService
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;

    public ViewerCountService(IAppLogger logger)
        : this(logger, SharedHttpClient)
    {
    }

    public ViewerCountService(IAppLogger logger, HttpClient httpClient)
    {
        this.logger = logger;
        this.httpClient = httpClient;
    }

    public Task<ViewerCountResult> GetViewerCountAsync(
        StreamTarget target,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        return target.Platform switch
        {
            PlatformKind.Twitch => GetTwitchViewerCountAsync(target, settings.Chat, cancellationToken),
            PlatformKind.Kick => GetKickViewerCountAsync(target, settings.Chat, cancellationToken),
            _ => Task.FromResult(new ViewerCountResult(ViewerCountState.Unavailable, null, $"Viewer counts are not supported for {target.Platform}."))
        };
    }

    private async Task<ViewerCountResult> GetTwitchViewerCountAsync(
        StreamTarget target,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        var token = TwitchOAuthService.NormalizeOAuthToken(settings.TwitchOAuthToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return new ViewerCountResult(
                ViewerCountState.NotConfigured,
                null,
                "Twitch viewer counts require a Twitch OAuth token.");
        }

        var clientId = await ResolveTwitchClientIdAsync(settings, token, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new ViewerCountResult(
                ViewerCountState.NotConfigured,
                null,
                "Twitch viewer counts require a Twitch Client ID that matches the OAuth token.");
        }

        var url = $"https://api.twitch.tv/helix/streams?user_login={Uri.EscapeDataString(target.Channel)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Client-Id", clientId);

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Viewers",
                $"Twitch viewer count request failed for {target.DisplayName}: {(int)response.StatusCode} {response.ReasonPhrase}. {ExtractApiMessage(responseBody)}");
            return new ViewerCountResult(
                ViewerCountState.Unavailable,
                null,
                "Twitch viewer count unavailable. Check the Twitch Client ID and OAuth token.");
        }

        using var document = JsonDocument.Parse(responseBody);
        return ReadTwitchViewerCount(target, document.RootElement);
    }

    private async Task<string?> ResolveTwitchClientIdAsync(
        ChatSettings settings,
        string token,
        CancellationToken cancellationToken)
    {
        var configured = settings.TwitchClientId.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return await TwitchClientIdCache.GetOrResolveAsync(
            httpClient,
            token,
            logger,
            "Viewers",
            "Could not resolve Twitch Client ID from the OAuth token.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ViewerCountResult> GetKickViewerCountAsync(
        StreamTarget target,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        var accessToken = await ResolveKickAccessTokenAsync(settings, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new ViewerCountResult(
                ViewerCountState.NotConfigured,
                null,
                "Kick viewer counts require a Kick user token or Kick Client ID and Client Secret.");
        }

        var url = $"https://api.kick.com/public/v1/channels?slug={Uri.EscapeDataString(target.Channel)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Viewers",
                $"Kick viewer count request failed for {target.DisplayName}: {(int)response.StatusCode} {response.ReasonPhrase}. {ExtractApiMessage(responseBody)}");
            return new ViewerCountResult(
                ViewerCountState.Unavailable,
                null,
                "Kick viewer count unavailable. Check Kick API credentials.");
        }

        using var document = JsonDocument.Parse(responseBody);
        return ReadKickViewerCount(target, document.RootElement);
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

    private static ViewerCountResult ReadTwitchViewerCount(StreamTarget target, JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return new ViewerCountResult(ViewerCountState.Unavailable, null, "Twitch viewer count response did not include stream data.");
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("user_login", out var login) &&
                login.ValueKind == JsonValueKind.String &&
                !string.Equals(login.GetString(), target.Channel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryGetInt32(item, "viewer_count", out var viewerCount))
            {
                return new ViewerCountResult(
                    ViewerCountState.Available,
                    viewerCount,
                    "Twitch viewer count updated.",
                    GetOptionalString(item, "game_name"),
                    GetOptionalString(item, "title"));
            }
        }

        return new ViewerCountResult(ViewerCountState.Offline, null, "Twitch stream is offline.");
    }

    private static ViewerCountResult ReadKickViewerCount(StreamTarget target, JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return new ViewerCountResult(ViewerCountState.Unavailable, null, "Kick viewer count response did not include channel data.");
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("slug", out var slug) &&
                slug.ValueKind == JsonValueKind.String &&
                !string.Equals(slug.GetString(), target.Channel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.TryGetProperty("stream", out var stream) ||
                stream.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return new ViewerCountResult(ViewerCountState.Offline, null, "Kick stream is offline.");
            }

            if (stream.ValueKind != JsonValueKind.Object)
            {
                return new ViewerCountResult(ViewerCountState.Unavailable, null, "Kick stream data had an unexpected shape.");
            }

            if (TryGetBool(stream, "is_live", out var isLive) && !isLive)
            {
                return new ViewerCountResult(ViewerCountState.Offline, null, "Kick stream is offline.");
            }

            if (TryGetInt32(stream, "viewer_count", out var viewerCount))
            {
                // Kick reports the category on the channel object, next to "stream", not inside it.
                return new ViewerCountResult(
                    ViewerCountState.Available,
                    viewerCount,
                    "Kick viewer count updated.",
                    TryReadNestedString(item, "category", "name"),
                    FirstNonEmpty(
                        GetOptionalString(item, "stream_title"),
                        GetOptionalString(stream, "stream_title"),
                        GetOptionalString(stream, "title")));
            }

            return new ViewerCountResult(ViewerCountState.Unavailable, null, "Kick stream data did not include viewer_count.");
        }

        return new ViewerCountResult(ViewerCountState.Offline, null, "Kick stream is offline.");
    }

    private static bool TryGetInt32(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.Number:
                return property.TryGetInt32(out value);
            case JsonValueKind.String:
                return int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            default:
                return false;
        }
    }

    private static bool TryGetBool(JsonElement element, string propertyName, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.String:
                return bool.TryParse(property.GetString(), out value);
            default:
                return false;
        }
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
            foreach (var propertyName in new[] { "message", "error_description", "error" })
            {
                var value = GetOptionalString(document.RootElement, propertyName);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
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

}

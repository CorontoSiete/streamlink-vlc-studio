using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

public sealed class StreamMetadataService : IStreamMetadataService
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly Dictionary<string, TwitchClientIdCacheEntry> TwitchClientIdCache = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim TwitchClientIdCacheGate = new(1, 1);
    private static readonly TimeSpan TwitchClientIdFallbackCacheLifetime = TimeSpan.FromHours(6);
    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;

    public StreamMetadataService(IAppLogger logger)
        : this(logger, SharedHttpClient)
    {
    }

    public StreamMetadataService(IAppLogger logger, HttpClient httpClient)
    {
        this.logger = logger;
        this.httpClient = httpClient;
    }

    public Task<StreamMetadataResult> GetLiveStreamMetadataAsync(
        StreamTarget target,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        return target.Platform switch
        {
            PlatformKind.Twitch => GetTwitchStreamMetadataAsync(target, settings.Chat, cancellationToken),
            PlatformKind.Kick => GetKickStreamMetadataAsync(target, settings.Chat, cancellationToken),
            _ => Task.FromResult(new StreamMetadataResult(
                StreamMetadataState.Unavailable,
                "",
                "",
                $"Stream metadata is not supported for {target.Platform}."))
        };
    }

    private async Task<StreamMetadataResult> GetTwitchStreamMetadataAsync(
        StreamTarget target,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        var token = TwitchOAuthService.NormalizeOAuthToken(settings.TwitchOAuthToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return new StreamMetadataResult(
                StreamMetadataState.NotConfigured,
                "",
                "",
                "Twitch stream thumbnails require a Twitch OAuth token.");
        }

        var clientId = await ResolveTwitchClientIdAsync(settings, token, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new StreamMetadataResult(
                StreamMetadataState.NotConfigured,
                "",
                "",
                "Twitch stream thumbnails require a Twitch Client ID that matches the OAuth token.");
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
                "Recent",
                $"Twitch stream metadata request failed for {target.DisplayName}: {(int)response.StatusCode} {response.ReasonPhrase}. {ExtractApiMessage(responseBody)}");
            return new StreamMetadataResult(
                StreamMetadataState.Unavailable,
                "",
                "",
                "Twitch stream metadata unavailable. Check the Twitch Client ID and OAuth token.");
        }

        using var document = JsonDocument.Parse(responseBody);
        return ReadTwitchMetadata(target, document.RootElement);
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

        var tokenKey = Fingerprint(token);
        await TwitchClientIdCacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TwitchClientIdCache.TryGetValue(tokenKey, out var cached) &&
                cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                return cached.ClientId;
            }

            TwitchTokenInfo tokenInfo;
            try
            {
                tokenInfo = await TwitchOAuthService.ValidateTokenAsync(httpClient, token, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Write(AppLogLevel.Warning, "Recent", "Could not resolve Twitch Client ID from the OAuth token.", ex);
                return null;
            }

            var expiresAt = tokenInfo.ExpiresAtUtc is { } tokenExpiry
                ? Min(tokenExpiry, DateTimeOffset.UtcNow.Add(TwitchClientIdFallbackCacheLifetime))
                : DateTimeOffset.UtcNow.Add(TwitchClientIdFallbackCacheLifetime);
            TwitchClientIdCache[tokenKey] = new TwitchClientIdCacheEntry(tokenInfo.ClientId, expiresAt);
            return tokenInfo.ClientId;
        }
        finally
        {
            TwitchClientIdCacheGate.Release();
        }
    }

    private async Task<StreamMetadataResult> GetKickStreamMetadataAsync(
        StreamTarget target,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        var accessToken = await ResolveKickAccessTokenAsync(settings, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new StreamMetadataResult(
                StreamMetadataState.NotConfigured,
                "",
                "",
                "Kick stream thumbnails require a Kick user token or Kick Client ID and Client Secret.");
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
                "Recent",
                $"Kick stream metadata request failed for {target.DisplayName}: {(int)response.StatusCode} {response.ReasonPhrase}. {ExtractApiMessage(responseBody)}");
            return new StreamMetadataResult(
                StreamMetadataState.Unavailable,
                "",
                "",
                "Kick stream metadata unavailable. Check Kick API credentials.");
        }

        using var document = JsonDocument.Parse(responseBody);
        return ReadKickMetadata(target, document.RootElement);
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

    private static StreamMetadataResult ReadTwitchMetadata(StreamTarget target, JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return new StreamMetadataResult(
                StreamMetadataState.Unavailable,
                "",
                "",
                "Twitch stream metadata response did not include stream data.");
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("user_login", out var login) &&
                !string.Equals(login.GetString(), target.Channel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new StreamMetadataResult(
                StreamMetadataState.Available,
                NormalizeTwitchThumbnailUrl(GetOptionalString(item, "thumbnail_url")),
                FirstNonEmpty(GetOptionalString(item, "user_name"), target.Channel),
                "Twitch stream metadata updated.",
                GetOptionalString(item, "game_name"));
        }

        return new StreamMetadataResult(StreamMetadataState.Offline, "", "", "Twitch stream is offline.");
    }

    private static StreamMetadataResult ReadKickMetadata(StreamTarget target, JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return new StreamMetadataResult(
                StreamMetadataState.Unavailable,
                "",
                "",
                "Kick stream metadata response did not include channel data.");
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("slug", out var slug) &&
                !string.Equals(slug.GetString(), target.Channel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.TryGetProperty("stream", out var stream) ||
                stream.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return new StreamMetadataResult(StreamMetadataState.Offline, "", "", "Kick stream is offline.");
            }

            if (stream.ValueKind != JsonValueKind.Object)
            {
                return new StreamMetadataResult(
                    StreamMetadataState.Unavailable,
                    "",
                    "",
                    "Kick stream data had an unexpected shape.");
            }

            if (TryGetBool(stream, "is_live") == false)
            {
                return new StreamMetadataResult(StreamMetadataState.Offline, "", "", "Kick stream is offline.");
            }

            var category = "";
            if (item.TryGetProperty("category", out var categoryElement) &&
                categoryElement.ValueKind == JsonValueKind.Object)
            {
                category = GetOptionalString(categoryElement, "name");
            }

            return new StreamMetadataResult(
                StreamMetadataState.Available,
                NormalizeImageUrl(FirstNonEmpty(
                    GetOptionalString(stream, "thumbnail"),
                    GetOptionalString(item, "thumbnail"))),
                FirstNonEmpty(GetOptionalString(item, "slug"), target.Channel),
                "Kick stream metadata updated.",
                category);
        }

        return new StreamMetadataResult(StreamMetadataState.Offline, "", "", "Kick stream is offline.");
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

    private static string GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
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

    private static string NormalizeTwitchThumbnailUrl(string url)
    {
        return string.IsNullOrWhiteSpace(url)
            ? ""
            : url.Trim()
                .Replace("{width}", "440", StringComparison.OrdinalIgnoreCase)
                .Replace("{height}", "248", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeImageUrl(string url)
    {
        var trimmed = url.Trim();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            ? "https:" + trimmed
            : trimmed;
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

    private static string Fingerprint(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()))).ToLowerInvariant();
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right)
    {
        return left <= right ? left : right;
    }

    private sealed record TwitchClientIdCacheEntry(string ClientId, DateTimeOffset ExpiresAtUtc);
}

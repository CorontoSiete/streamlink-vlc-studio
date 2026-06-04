using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public static class KickOAuthService
{
    public const string LocalRedirectUri = "http://localhost:39177";
    private const string AuthorizationEndpoint = "https://id.kick.com/oauth/authorize";
    private const string TokenEndpoint = "https://id.kick.com/oauth/token";
    private static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AppAccessTokenFallbackLifetime = TimeSpan.FromMinutes(50);
    private static readonly string[] RequiredScopes = ["user:read", "channel:read", "chat:write"];
    private static readonly SemaphoreSlim AppAccessTokenGate = new(1, 1);
    private static KickAppAccessTokenCache? appAccessTokenCache;

    public static async Task<KickOAuthTokenResult> AuthorizeUserTokenAsync(
        ChatSettings settings,
        CancellationToken cancellationToken = default)
    {
        var clientId = RequireSetting(settings.KickClientId, "Kick Client ID");
        var clientSecret = RequireSetting(settings.KickClientSecret, "Kick Client Secret");
        var state = CreateBase64UrlSecret(32);
        var codeVerifier = CreateBase64UrlSecret(32);
        var codeChallenge = CreateCodeChallenge(codeVerifier);
        var authorizationUri = BuildAuthorizationUri(clientId, state, codeChallenge);

        using var listener = new HttpListener();
        listener.Prefixes.Add($"{LocalRedirectUri}/");
        listener.Start();

        Process.Start(new ProcessStartInfo
        {
            FileName = authorizationUri.ToString(),
            UseShellExecute = true
        });

        var code = await WaitForAuthorizationCodeAsync(listener, state, cancellationToken);
        return await ExchangeAuthorizationCodeAsync(clientId, clientSecret, code, codeVerifier, cancellationToken);
    }

    public static async Task<KickOAuthTokenResult> RefreshUserTokenAsync(
        ChatSettings settings,
        CancellationToken cancellationToken = default)
    {
        var clientId = RequireSetting(settings.KickClientId, "Kick Client ID");
        var clientSecret = RequireSetting(settings.KickClientSecret, "Kick Client Secret");
        var refreshToken = RequireSetting(settings.KickRefreshToken, "Kick refresh token");

        using var httpClient = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken
        });

        return await SendTokenRequestAsync(httpClient, request, fallbackRefreshToken: refreshToken, cancellationToken);
    }

    public static async Task<string?> GetUsableAccessTokenAsync(
        ChatSettings settings,
        IAppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        return await GetUsableAccessTokenAsync(
            settings,
            static (targetSettings, token, _) =>
            {
                ApplyTokenResult(targetSettings, token);
                return Task.CompletedTask;
            },
            logger,
            cancellationToken);
    }

    public static async Task<string?> GetUsableAccessTokenAsync(
        ChatSettings settings,
        Func<ChatSettings, KickOAuthTokenResult, CancellationToken, Task> applyTokenResultAsync,
        IAppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var token = NormalizeBearerToken(settings.KickOAuthToken);
        if (!string.IsNullOrWhiteSpace(token) && !ShouldRefresh(settings))
        {
            return token;
        }

        if (string.IsNullOrWhiteSpace(settings.KickRefreshToken) ||
            string.IsNullOrWhiteSpace(settings.KickClientId) ||
            string.IsNullOrWhiteSpace(settings.KickClientSecret))
        {
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }

        try
        {
            var refreshed = await RefreshUserTokenAsync(settings, cancellationToken);
            await applyTokenResultAsync(settings, refreshed, cancellationToken);
            logger?.Write(AppLogLevel.Info, "KickOAuth", "Refreshed Kick OAuth token.");
            return NormalizeBearerToken(refreshed.AccessToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.Write(AppLogLevel.Warning, "KickOAuth", "Kick OAuth token refresh failed.", ex);
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
    }

    public static void ApplyTokenResult(ChatSettings settings, KickOAuthTokenResult token)
    {
        settings.KickOAuthToken = token.AccessToken;
        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            settings.KickRefreshToken = token.RefreshToken;
        }

        settings.KickTokenExpiresAtUtc = token.ExpiresAtUtc;
    }

    public static void ClearToken(ChatSettings settings)
    {
        settings.KickOAuthToken = "";
        settings.KickRefreshToken = "";
        settings.KickTokenExpiresAtUtc = null;
    }

    public static async Task<string?> TryGetCurrentUsernameAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var token = NormalizeBearerToken(accessToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        using var httpClient = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.kick.com/public/v1/users");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("name", out var name) &&
                !string.IsNullOrWhiteSpace(name.GetString()))
            {
                return name.GetString();
            }

            if (item.TryGetProperty("username", out var username) &&
                !string.IsNullOrWhiteSpace(username.GetString()))
            {
                return username.GetString();
            }
        }

        return null;
    }

    public static async Task<long?> TryResolveBroadcasterUserIdAsync(
        string channel,
        ChatSettings settings,
        IAppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        return await TryResolveBroadcasterUserIdAsync(
            channel,
            settings,
            null,
            logger,
            cancellationToken);
    }

    public static async Task<long?> TryResolveBroadcasterUserIdAsync(
        string channel,
        ChatSettings settings,
        Func<ChatSettings, KickOAuthTokenResult, CancellationToken, Task>? applyTokenResultAsync,
        IAppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var userToken = applyTokenResultAsync is null
            ? await GetUsableAccessTokenAsync(settings, logger, cancellationToken)
            : await GetUsableAccessTokenAsync(settings, applyTokenResultAsync, logger, cancellationToken);
        if (!string.IsNullOrWhiteSpace(userToken))
        {
            var userTokenResult = await TryResolveBroadcasterUserIdWithTokenAsync(channel, userToken, cancellationToken);
            if (userTokenResult is not null)
            {
                return userTokenResult;
            }
        }

        var appToken = await TryGetAppAccessTokenAsync(settings, logger, cancellationToken);
        if (!string.IsNullOrWhiteSpace(appToken))
        {
            var appTokenResult = await TryResolveBroadcasterUserIdWithTokenAsync(channel, appToken, cancellationToken);
            if (appTokenResult is not null)
            {
                logger?.Write(AppLogLevel.Info, "KickOAuth", $"Resolved Kick broadcaster user ID for {channel} with an app access token.");
                return appTokenResult;
            }
        }

        return null;
    }

    public static string NormalizeBearerToken(string token)
    {
        var normalized = token.Trim();
        if (normalized.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["oauth:".Length..];
        }
        else if (normalized.StartsWith("oauth ", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["oauth ".Length..];
        }
        else if (normalized.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["Bearer ".Length..];
        }

        return normalized.Trim();
    }

    public static async Task<string?> TryGetAppAccessTokenAsync(
        ChatSettings settings,
        IAppLogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var clientId = settings.KickClientId.Trim();
        var clientSecret = settings.KickClientSecret.Trim();
        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            return null;
        }

        var cacheKey = Fingerprint($"{clientId}\0{clientSecret}");
        await AppAccessTokenGate.WaitAsync(cancellationToken);
        try
        {
            if (appAccessTokenCache is not null &&
                string.Equals(appAccessTokenCache.CacheKey, cacheKey, StringComparison.Ordinal) &&
                appAccessTokenCache.ExpiresAtUtc > DateTimeOffset.UtcNow.Add(RefreshSkew))
            {
                return appAccessTokenCache.AccessToken;
            }

            using var httpClient = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret
            });

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger?.Write(AppLogLevel.Warning, "KickOAuth", $"Kick app token request failed ({(int)response.StatusCode} {response.ReasonPhrase}). {ExtractApiMessage(responseBody)}");
                return null;
            }

            using var document = JsonDocument.Parse(responseBody);
            var accessToken = GetOptionalString(document.RootElement, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                logger?.Write(AppLogLevel.Warning, "KickOAuth", "Kick app token response did not include an access token.");
                return null;
            }

            var expiresAt = TryGetExpiresAt(document.RootElement) ?? DateTimeOffset.UtcNow.Add(AppAccessTokenFallbackLifetime);
            appAccessTokenCache = new KickAppAccessTokenCache(cacheKey, accessToken, expiresAt);
            return accessToken;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.Write(AppLogLevel.Warning, "KickOAuth", "Kick app token request failed.", ex);
            return null;
        }
        finally
        {
            AppAccessTokenGate.Release();
        }
    }

    private static async Task<long?> TryResolveBroadcasterUserIdWithTokenAsync(
        string channel,
        string token,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();
        var url = $"https://api.kick.com/public/v1/channels?slug={Uri.EscapeDataString(channel)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", NormalizeBearerToken(token));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("slug", out var slug) &&
                !string.Equals(slug.GetString(), channel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (item.TryGetProperty("broadcaster_user_id", out var broadcasterUserId))
            {
                return TryGetInt64(broadcasterUserId);
            }
        }

        return null;
    }

    private static async Task<KickOAuthTokenResult> ExchangeAuthorizationCodeAsync(
        string clientId,
        string clientSecret,
        string code,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = LocalRedirectUri,
            ["code_verifier"] = codeVerifier,
            ["code"] = code
        });

        return await SendTokenRequestAsync(httpClient, request, fallbackRefreshToken: null, cancellationToken);
    }

    private static async Task<KickOAuthTokenResult> SendTokenRequestAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        string? fallbackRefreshToken,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Kick OAuth token request failed ({(int)response.StatusCode} {response.ReasonPhrase}). {ExtractApiMessage(responseBody)}");
        }

        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        var accessToken = GetRequiredString(root, "access_token", "Kick token response did not include an access token.");
        var tokenType = GetOptionalString(root, "token_type");
        var refreshToken = GetOptionalString(root, "refresh_token");
        var scopes = ReadScopes(root);
        var expiresAt = TryGetExpiresAt(root);

        if (!scopes.Contains("chat:write"))
        {
            throw new InvalidOperationException("Kick authorization succeeded, but the token is missing the chat:write scope.");
        }

        return new KickOAuthTokenResult(
            accessToken,
            string.IsNullOrWhiteSpace(refreshToken) ? fallbackRefreshToken ?? "" : refreshToken,
            expiresAt,
            tokenType,
            scopes);
    }

    private static Uri BuildAuthorizationUri(string clientId, string state, string codeChallenge)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = LocalRedirectUri,
            ["scope"] = string.Join(' ', RequiredScopes),
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };

        var builder = new StringBuilder(AuthorizationEndpoint);
        builder.Append('?');
        builder.Append(string.Join('&', query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));
        return new Uri(builder.ToString());
    }

    private static async Task<string> WaitForAuthorizationCodeAsync(
        HttpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AuthorizationTimeout);
        using var registration = timeout.Token.Register(() =>
        {
            try
            {
                listener.Stop();
            }
            catch (ObjectDisposedException)
            {
            }
        });

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync();
        }
        catch (Exception ex) when (timeout.IsCancellationRequested &&
            ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            throw new TimeoutException("Timed out waiting for Kick authorization.");
        }

        var query = ParseQuery(context.Request.Url?.Query ?? "");
        var responseText = "Kick authorization finished. You can close this window.";
        var statusCode = 200;

        try
        {
            if (query.TryGetValue("error", out var error))
            {
                throw new InvalidOperationException($"Kick authorization failed: {error}");
            }

            if (!query.TryGetValue("state", out var returnedState) ||
                !string.Equals(returnedState, expectedState, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Kick authorization returned an invalid state value.");
            }

            if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            {
                throw new InvalidOperationException("Kick authorization did not return an authorization code.");
            }

            return code;
        }
        catch (Exception ex)
        {
            responseText = ex.Message;
            statusCode = 400;
            throw;
        }
        finally
        {
            await WriteBrowserResponseAsync(context.Response, statusCode, responseText);
        }
    }

    private static async Task WriteBrowserResponseAsync(HttpListenerResponse response, int statusCode, string message)
    {
        response.StatusCode = statusCode;
        response.ContentType = "text/html; charset=utf-8";
        var html = $"""
        <!doctype html>
        <html>
        <head><meta charset="utf-8"><title>Kick Authorization</title></head>
        <body style="font-family:Segoe UI,Arial,sans-serif;margin:32px;">
        <h1>Kick Authorization</h1>
        <p>{WebUtility.HtmlEncode(message)}</p>
        </body>
        </html>
        """;
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var trimmed = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var name = separator >= 0 ? part[..separator] : part;
            var value = separator >= 0 ? part[(separator + 1)..] : "";
            values[Uri.UnescapeDataString(name.Replace('+', ' '))] =
                Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return values;
    }

    private static bool ShouldRefresh(ChatSettings settings)
    {
        return settings.KickTokenExpiresAtUtc is { } expiresAt &&
            expiresAt <= DateTimeOffset.UtcNow.Add(RefreshSkew);
    }

    private static string RequireSetting(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required for Kick authorization.");
        }

        return value.Trim();
    }

    private static string CreateBase64UrlSecret(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Base64UrlEncode(bytes);
    }

    private static string CreateCodeChallenge(string codeVerifier)
    {
        return Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string Fingerprint(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static DateTimeOffset? TryGetExpiresAt(JsonElement root)
    {
        if (!root.TryGetProperty("expires_in", out var expiresInProperty))
        {
            return null;
        }

        long seconds = expiresInProperty.ValueKind switch
        {
            JsonValueKind.Number when expiresInProperty.TryGetInt64(out var numericValue) => numericValue,
            JsonValueKind.String when long.TryParse(expiresInProperty.GetString(), out var stringValue) => stringValue,
            _ => 0
        };

        return seconds <= 0 ? null : DateTimeOffset.UtcNow.AddSeconds(seconds);
    }

    private static long? TryGetInt64(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(element.GetString(), out var value) => value,
            _ => null
        };
    }

    private static string GetRequiredString(JsonElement root, string propertyName, string errorMessage)
    {
        var value = GetOptionalString(root, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(errorMessage);
        }

        return value;
    }

    private static string GetOptionalString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";
    }

    private static HashSet<string> ReadScopes(JsonElement root)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("scope", out var scopeProperty))
        {
            return scopes;
        }

        if (scopeProperty.ValueKind == JsonValueKind.String)
        {
            foreach (var scope in (scopeProperty.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                scopes.Add(scope);
            }

            return scopes;
        }

        if (scopeProperty.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in scopeProperty.EnumerateArray())
            {
                var scope = item.GetString();
                if (!string.IsNullOrWhiteSpace(scope))
                {
                    scopes.Add(scope);
                }
            }
        }

        return scopes;
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

    private sealed record KickAppAccessTokenCache(
        string CacheKey,
        string AccessToken,
        DateTimeOffset ExpiresAtUtc);
}

public sealed record KickOAuthTokenResult(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset? ExpiresAtUtc,
    string TokenType,
    HashSet<string> Scopes);

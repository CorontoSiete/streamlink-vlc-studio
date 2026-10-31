using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using StreamlinkVlcStudio.Authentication;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Http;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;
using static StreamlinkVlcStudio.Infrastructure.Chat.OAuthTokenHelpers;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public static class KickOAuthService
{
    public const string LocalRedirectUri = KickOAuthContract.RedirectUri;
    private const string AuthorizationEndpoint = "https://id.kick.com/oauth/authorize";
    private const string TokenEndpoint = "https://id.kick.com/oauth/token";
    private static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AppAccessTokenFallbackLifetime = TimeSpan.FromMinutes(50);
    private static readonly string[] RequiredScopes = KickOAuthContract.Scopes;
    private static readonly SemaphoreSlim AppAccessTokenGate = new(1, 1);
    private static KickAppAccessTokenCache? appAccessTokenCache;

    public static async Task<KickOAuthTokenResult> AuthorizeUserTokenAsync(
        ChatSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = AccountConnectionConfiguration.Current;
        var useTokenService = !AccountConnectionConfiguration.HasPersonalKickCredentials(settings);
        if (useTokenService && !configuration.CanUseKickTokenService(settings))
            throw new InvalidOperationException(AccountConnectionConfiguration.KickUnavailableMessage);
        var clientId = useTokenService ? configuration.KickClientId.Trim() : settings.KickClientId.Trim();
        var clientSecret = useTokenService ? "" : settings.KickClientSecret.Trim();
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
        var credentials = new ChatSettings { KickClientId = clientId, KickClientSecret = clientSecret, KickUseTokenService = useTokenService };
        using var httpClient = CreateTokenHttpClient();
        using var request = CreateTokenRequest(credentials, "authorization_code", code, codeVerifier, configuration);
        return (await SendTokenRequestAsync(httpClient, request, fallbackRefreshToken: null, cancellationToken, useTokenService))
            with
        { ClientId = clientId, UsesTokenService = useTokenService };
    }

    public static async Task<KickOAuthTokenResult> RefreshUserTokenAsync(
        ChatSettings settings,
        CancellationToken cancellationToken = default)
    {
        var refreshToken = OAuthTokenHelpers.RequireSetting(settings.KickRefreshToken, "Kick refresh token", "Kick");
        using var httpClient = CreateTokenHttpClient();
        using var request = CreateTokenRequest(settings, "refresh_token", refreshToken, null, AccountConnectionConfiguration.Current);
        return (await SendTokenRequestAsync(httpClient, request, fallbackRefreshToken: refreshToken, cancellationToken, settings.KickUseTokenService))
            with
        { ClientId = settings.KickClientId.Trim(), UsesTokenService = settings.KickUseTokenService };
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

    public static Task<string?> GetUsableAccessTokenAsync(
        ChatSettings settings,
        Func<ChatSettings, KickOAuthTokenResult, CancellationToken, Task> applyTokenResultAsync,
        IAppLogger? logger = null,
        CancellationToken cancellationToken = default) =>
        KickUserTokenCoordinator.Shared.ResolveAsync(settings, applyTokenResultAsync, logger, cancellationToken);

    public static Task<string?> ForceRefreshUserTokenAsync(
        ChatSettings settings, string rejectedToken, IAppLogger? logger = null, CancellationToken cancellationToken = default) =>
        KickUserTokenCoordinator.Shared.ResolveAsync(settings, static (target, result, _) =>
        {
            ApplyTokenResult(target, result);
            return Task.CompletedTask;
        }, logger, cancellationToken, rejectedToken);

    public static void ApplyTokenResult(ChatSettings settings, KickOAuthTokenResult token)
    {
        if (!string.IsNullOrWhiteSpace(token.ClientId))
        {
            settings.KickClientId = token.ClientId;
            settings.KickUseTokenService = token.UsesTokenService;
            if (token.UsesTokenService) settings.KickClientSecret = "";
        }
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
        settings.KickUseTokenService = false;
    }

    public static async Task<string?> TryGetCurrentUsernameAsync(
        string accessToken,
        CancellationToken cancellationToken = default,
        IAppLogger? logger = null)
    {
        using var httpClient = HttpClientFactory.CreateDefault();
        return await TryGetCurrentUsernameAsync(httpClient, accessToken, cancellationToken, logger).ConfigureAwait(false);
    }

    internal static async Task<string?> TryGetCurrentUsernameAsync(
        HttpClient httpClient,
        string accessToken,
        CancellationToken cancellationToken,
        IAppLogger? logger = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = NormalizeBearerToken(accessToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.kick.com/public/v1/users");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(responseBody);
            if (!TryGetArray(document.RootElement, "data", out var data))
            {
                return null;
            }

            foreach (var item in data.EnumerateArray())
            {
                var username = FirstNonEmpty(GetOptionalString(item, "name"), GetOptionalString(item, "username"));
                if (!string.IsNullOrWhiteSpace(username))
                {
                    return username;
                }
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // This optional decoration must not prevent either authorization UI from saving
            // a valid token when the users endpoint fails or reaches its HTTP deadline.
            logger.WriteSafely(AppLogLevel.Warning, "KickOAuth", "Could not resolve the authorized Kick username.", ex);
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
                logger.WriteSafely(AppLogLevel.Info, "KickOAuth", $"Resolved Kick broadcaster user ID for {channel} with an app access token.");
                return appTokenResult;
            }
        }

        return null;
    }

    public static string NormalizeBearerToken(string token)
    {
        return OAuthTokenHelpers.NormalizeBearerToken(token);
    }

    public static Task<string?> TryGetAppAccessTokenAsync(
        ChatSettings settings,
        IAppLogger? logger = null,
        CancellationToken cancellationToken = default) =>
        TryGetAppAccessTokenAsync(settings, CreateTokenHttpClient, logger, cancellationToken);

    internal static async Task<string?> TryGetAppAccessTokenAsync(
        ChatSettings settings,
        Func<HttpClient> createHttpClient,
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

        var cacheKey = OAuthTokenHelpers.CreateCredentialFingerprint(clientId, clientSecret);
        await AppAccessTokenGate.WaitAsync(cancellationToken);
        try
        {
            if (appAccessTokenCache is not null &&
                string.Equals(appAccessTokenCache.CacheKey, cacheKey, StringComparison.Ordinal) &&
                appAccessTokenCache.ExpiresAtUtc > DateTimeOffset.UtcNow.Add(RefreshSkew))
            {
                return appAccessTokenCache.AccessToken;
            }

            using var httpClient = createHttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret
            });

            using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Provider bodies and reason phrases may echo the submitted client secret.
                logger.WriteSafely(AppLogLevel.Warning, "KickOAuth", $"Kick app token request failed (HTTP {(int)response.StatusCode}).");
                return null;
            }

            var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken);
            using var document = JsonDocument.Parse(responseBody);
            var accessToken = GetOptionalString(document.RootElement, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                logger.WriteSafely(AppLogLevel.Warning, "KickOAuth", "Kick app token response did not include an access token.");
                return null;
            }

            var expiresAt = OAuthTokenHelpers.TryGetExpiresAt(document.RootElement, "expires_in") ?? DateTimeOffset.UtcNow.Add(AppAccessTokenFallbackLifetime);
            appAccessTokenCache = new KickAppAccessTokenCache(cacheKey, accessToken, expiresAt);
            return accessToken;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.WriteSafely(AppLogLevel.Warning, "KickOAuth", "Kick app token request failed.", ex);
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
        using var httpClient = HttpClientFactory.CreateDefault();
        var url = $"https://api.kick.com/public/v1/channels?slug={Uri.EscapeDataString(channel)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", NormalizeBearerToken(token));

        using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken);
        using var document = JsonDocument.Parse(responseBody);
        return ReadBroadcasterUserId(document.RootElement, channel);
    }

    internal static long? ReadBroadcasterUserId(JsonElement root, string channel)
    {
        if (!TryGetArray(root, "data", out var data))
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (!TryGetNonEmptyString(item, "slug", out var slug) ||
                !string.Equals(slug, channel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryGetInt64(item, "broadcaster_user_id") is > 0 and var broadcasterUserId)
            {
                return broadcasterUserId;
            }
        }

        return null;
    }

    // Token grants carry credentials in the POST body, which 307/308 redirects can resend.
    internal static HttpClient CreateTokenHttpClient() => HttpClientFactory.Create(
        TimeSpan.FromSeconds(20), includeUserAgent: true, acceptJson: true, allowAutoRedirect: false);

    internal static HttpRequestMessage CreateTokenRequest(
        ChatSettings settings, string grantType, string credential, string? codeVerifier,
        AccountConnectionConfiguration configuration)
    {
        var clientId = OAuthTokenHelpers.RequireSetting(settings.KickClientId, "Kick Client ID", "Kick");
        var endpoint = settings.KickUseTokenService ? configuration.RequireKickTokenServiceUri(clientId) : new Uri(TokenEndpoint);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = grantType,
            ["client_id"] = clientId,
        };
        if (!settings.KickUseTokenService)
            form["client_secret"] = OAuthTokenHelpers.RequireSetting(settings.KickClientSecret, "Kick Client Secret", "Kick");
        if (grantType == "authorization_code")
        {
            form["redirect_uri"] = LocalRedirectUri;
            form["code_verifier"] = codeVerifier ?? throw new ArgumentNullException(nameof(codeVerifier));
            form["code"] = credential;
        }
        else if (grantType == "refresh_token") form["refresh_token"] = credential;
        else throw new ArgumentException("Unsupported Kick grant type.", nameof(grantType));
        return new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(form) };
    }

    internal static async Task<KickOAuthTokenResult> SendTokenRequestAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        string? fallbackRefreshToken,
        CancellationToken cancellationToken,
        bool requireManagedScopes = false)
    {
        try
        {
            using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken);
            var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Kick connection failed (HTTP {(int)response.StatusCode}). Please try connecting again.");
            }

            return ParseTokenResponse(responseBody, fallbackRefreshToken, requireManagedScopes);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.NameResolutionError)
        {
            throw new InvalidOperationException(
                $"Your network's DNS could not find the Kick connection server ({request.RequestUri?.DnsSafeHost}). Reconnect your network or VPN, then try Connect Kick again.", ex);
        }
    }

    internal static KickOAuthTokenResult ParseTokenResponse(string responseBody, string? fallbackRefreshToken, bool requireManagedScopes)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        var accessToken = GetRequiredString(root, "access_token", "Kick token response did not include an access token.");
        var tokenType = GetOptionalString(root, "token_type");
        var refreshToken = GetOptionalString(root, "refresh_token");
        var scopes = ReadScopes(root);
        var expiresAt = OAuthTokenHelpers.TryGetExpiresAt(root, "expires_in");

        if (!scopes.Contains("chat:write"))
        {
            throw new InvalidOperationException("Kick authorization succeeded, but the token is missing the chat:write scope.");
        }

        if (requireManagedScopes && (!RequiredScopes.All(scopes.Contains) || expiresAt is null || string.IsNullOrWhiteSpace(refreshToken)))
            throw new InvalidOperationException("Kick did not return a complete renewable account connection. Please connect again and approve all requested permissions.");

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

        return OAuthTokenHelpers.BuildAuthorizationUri(AuthorizationEndpoint, query);
    }

    private static async Task<string> WaitForAuthorizationCodeAsync(
        HttpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        return await LoopbackOAuthReceiver.WaitForResultAsync(
                listener,
                "Kick",
                "/",
                expectedState,
                AuthorizationTimeout,
                query =>
                {
                    if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                    {
                        throw new InvalidOperationException("Kick authorization did not return an authorization code.");
                    }

                    return code;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static bool ShouldRefresh(ChatSettings settings)
    {
        return settings.KickTokenExpiresAtUtc is { } expiresAt &&
            expiresAt <= DateTimeOffset.UtcNow.Add(RefreshSkew);
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
    HashSet<string> Scopes,
    string ClientId = "",
    bool UsesTokenService = false);

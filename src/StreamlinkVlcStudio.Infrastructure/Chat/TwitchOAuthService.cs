using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Http;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Infrastructure.Chat.OAuthTokenHelpers;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public static class TwitchOAuthService
{
    public const string LocalRedirectUri = "http://localhost:39178";
    private const string AuthorizationEndpoint = "https://id.twitch.tv/oauth2/authorize";
    private static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromMinutes(4);
    public const string ManagePredictionsScope = "channel:manage:predictions";
    public const string CreateClipsScope = "clips:edit";
    private static readonly string[] RequiredScopes = ["chat:read", "chat:edit", "user:read:follows", ManagePredictionsScope, CreateClipsScope];

    public static async Task<TwitchOAuthTokenResult> AuthorizeUserTokenAsync(
        ChatSettings settings,
        CancellationToken cancellationToken = default)
    {
        var clientId = RequireSetting(settings.TwitchClientId, "Twitch Client ID");
        var state = CreateBase64UrlSecret(32);
        var authorizationUri = BuildAuthorizationUri(clientId, state);

        using var listener = new HttpListener();
        listener.Prefixes.Add($"{LocalRedirectUri}/");
        listener.Start();

        Process.Start(new ProcessStartInfo
        {
            FileName = authorizationUri.ToString(),
            UseShellExecute = true
        });

        var browserToken = await WaitForAuthorizationTokenAsync(listener, state, cancellationToken);
        var tokenInfo = await ValidateTokenAsync(browserToken.AccessToken, cancellationToken);

        var missingScopes = new List<string>();
        if (!tokenInfo.CanReadChat)
        {
            missingScopes.Add("chat:read");
        }

        if (!tokenInfo.CanWriteChat)
        {
            missingScopes.Add("chat:edit");
        }

        if (!tokenInfo.CanReadFollows)
        {
            missingScopes.Add("user:read:follows");
        }

        if (!tokenInfo.CanManagePredictions)
        {
            missingScopes.Add(ManagePredictionsScope);
        }

        if (!tokenInfo.CanCreateClips)
        {
            missingScopes.Add(CreateClipsScope);
        }

        if (missingScopes.Count > 0)
        {
            throw new InvalidOperationException(
                $"Twitch authorization succeeded, but the token is missing {string.Join(" and ", missingScopes)}.");
        }

        return new TwitchOAuthTokenResult(
            browserToken.AccessToken,
            tokenInfo.Login,
            tokenInfo.ExpiresAtUtc ?? browserToken.ExpiresAtUtc,
            browserToken.TokenType,
            tokenInfo.Scopes);
    }

    public static void ApplyTokenResult(ChatSettings settings, TwitchOAuthTokenResult token)
    {
        settings.TwitchOAuthToken = token.AccessToken;
        settings.TwitchTokenExpiresAtUtc = token.ExpiresAtUtc;
        settings.TwitchTokenScopes = token.Scopes.ToList();
        if (!string.IsNullOrWhiteSpace(token.Login))
        {
            settings.TwitchUsername = token.Login;
        }
    }

    public static void ClearToken(ChatSettings settings)
    {
        settings.TwitchOAuthToken = "";
        settings.TwitchTokenExpiresAtUtc = null;
        settings.TwitchTokenScopes = [];
    }

    public static async Task<TwitchTokenInfo> ValidateTokenAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        using var httpClient = HttpClientFactory.CreateDefault();
        return await ValidateTokenAsync(httpClient, token, cancellationToken);
    }

    public static async Task<TwitchTokenInfo> ValidateTokenAsync(
        HttpClient httpClient,
        string token,
        CancellationToken cancellationToken = default)
    {
        var normalizedToken = NormalizeOAuthToken(token);
        if (string.IsNullOrWhiteSpace(normalizedToken))
        {
            throw new InvalidOperationException("Twitch OAuth token is required.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", normalizedToken);

        using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The Twitch OAuth token is invalid, expired, revoked, or is not an OAuth access token. A Client ID alone cannot send chat.");
        }

        var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken);
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;

        var login = GetRequiredString(root, "login", "Twitch token validation did not return a login name.")
            .Trim()
            .ToLowerInvariant();
        var userId = GetRequiredString(root, "user_id", "Twitch token validation did not return a user ID.")
            .Trim();
        var clientId = GetOptionalString(root, "client_id");
        var scopes = OAuthTokenHelpers.ReadScopes(root, "scopes");
        var expiresAt = OAuthTokenHelpers.TryGetExpiresAt(root, "expires_in");

        return new TwitchTokenInfo(
            login,
            userId,
            clientId,
            expiresAt,
            scopes,
            scopes.Contains("chat:read"),
            scopes.Contains("chat:edit") || scopes.Contains("chat:write"),
            scopes.Contains("user:read:follows"),
            scopes.Contains(ManagePredictionsScope),
            scopes.Contains(CreateClipsScope));
    }

    public static string NormalizeOAuthToken(string token)
    {
        return OAuthTokenHelpers.NormalizeBearerToken(token);
    }

    private static Uri BuildAuthorizationUri(string clientId, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "token",
            ["client_id"] = clientId,
            ["redirect_uri"] = LocalRedirectUri,
            ["scope"] = string.Join(' ', RequiredScopes),
            ["state"] = state
        };

        var builder = new StringBuilder(AuthorizationEndpoint);
        builder.Append('?');
        builder.Append(string.Join('&', query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));
        return new Uri(builder.ToString());
    }

    private static async Task<TwitchBrowserToken> WaitForAuthorizationTokenAsync(
        HttpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        return await LoopbackOAuthReceiver.WaitForResultAsync(
                listener,
                "Twitch",
                "/twitch-oauth-token",
                expectedState,
                AuthorizationTimeout,
                query => ParseBrowserToken(query, expectedState),
                cancellationToken,
                TryHandleFragmentCaptureRequestAsync)
            .ConfigureAwait(false);
    }

    private static TwitchBrowserToken ParseBrowserToken(
        IReadOnlyDictionary<string, string> query,
        string expectedState)
    {
        if (!query.TryGetValue("state", out var returnedState) ||
            !string.Equals(returnedState, expectedState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Twitch authorization returned an invalid state value.");
        }

        if (!query.TryGetValue("access_token", out var accessToken) || string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Twitch authorization did not return an access token.");
        }

        return new TwitchBrowserToken(
            accessToken,
            TryGetExpiresAt(query),
            query.TryGetValue("token_type", out var tokenType) ? tokenType : "",
            ReadScopes(query));
    }

    private static async Task<bool> TryHandleFragmentCaptureRequestAsync(HttpListenerContext context)
    {
        if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.Ordinal) ||
            !string.Equals(context.Request.Url?.AbsolutePath, "/", StringComparison.Ordinal))
        {
            return false;
        }

        var html = """
        <!doctype html>
        <html>
        <head><meta charset="utf-8"><title>Twitch Authorization</title></head>
        <body style="font-family:Segoe UI,Arial,sans-serif;margin:32px;">
        <h1>Twitch Authorization</h1>
        <p id="status">Finishing authorization...</p>
        <script>
        (async () => {
            const status = document.getElementById('status');
            const hash = window.location.hash.startsWith('#') ? window.location.hash.substring(1) : '';
            if (!hash) {
                status.textContent = 'Twitch did not return an OAuth token.';
                return;
            }

            const response = await fetch('/twitch-oauth-token?' + hash, { method: 'GET', cache: 'no-store' });
            const html = await response.text();
            document.open();
            document.write(html);
            document.close();
        })().catch(error => {
            document.getElementById('status').textContent = 'Twitch authorization failed: ' + error;
        });
        </script>
        </body>
        </html>
        """;
        var bytes = Encoding.UTF8.GetBytes(html);
        var response = context.Response;
        try
        {
            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";
            response.Headers[HttpResponseHeader.CacheControl] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        return true;
    }

    private static DateTimeOffset? TryGetExpiresAt(IReadOnlyDictionary<string, string> query)
    {
        return query.TryGetValue("expires_in", out var expiresIn)
            ? OAuthTokenHelpers.TryGetExpiresAt(expiresIn)
            : null;
    }

    private static string RequireSetting(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required for Twitch authorization.");
        }

        return value.Trim();
    }

    private static HashSet<string> ReadScopes(IReadOnlyDictionary<string, string> query)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!query.TryGetValue("scope", out var scopeValue))
        {
            return scopes;
        }

        foreach (var scope in scopeValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            scopes.Add(scope);
        }

        return scopes;
    }

    private sealed record TwitchBrowserToken(
        string AccessToken,
        DateTimeOffset? ExpiresAtUtc,
        string TokenType,
        HashSet<string> Scopes);
}

public sealed record TwitchTokenInfo(
    string Login,
    string UserId,
    string ClientId,
    DateTimeOffset? ExpiresAtUtc,
    HashSet<string> Scopes,
    bool CanReadChat,
    bool CanWriteChat,
    bool CanReadFollows,
    bool CanManagePredictions,
    bool CanCreateClips);

public sealed record TwitchOAuthTokenResult(
    string AccessToken,
    string Login,
    DateTimeOffset? ExpiresAtUtc,
    string TokenType,
    HashSet<string> Scopes);

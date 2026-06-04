using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public static class TwitchOAuthService
{
    public const string LocalRedirectUri = "http://localhost:39178";
    private const string AuthorizationEndpoint = "https://id.twitch.tv/oauth2/authorize";
    private static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromMinutes(4);
    public const string ManagePredictionsScope = "channel:manage:predictions";
    private static readonly string[] RequiredScopes = ["chat:read", "chat:edit", "user:read:follows", ManagePredictionsScope];

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
        using var httpClient = new HttpClient();
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

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The Twitch OAuth token is invalid, expired, revoked, or is not an OAuth access token. A Client ID alone cannot send chat.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var login = GetRequiredString(root, "login", "Twitch token validation did not return a login name.")
            .Trim()
            .ToLowerInvariant();
        var userId = GetRequiredString(root, "user_id", "Twitch token validation did not return a user ID.")
            .Trim();
        var clientId = GetOptionalString(root, "client_id");
        var scopes = ReadScopes(root);
        var expiresAt = TryGetExpiresAt(root);

        return new TwitchTokenInfo(
            login,
            userId,
            clientId,
            expiresAt,
            scopes,
            scopes.Contains("chat:read"),
            scopes.Contains("chat:edit") || scopes.Contains("chat:write"),
            scopes.Contains("user:read:follows"),
            scopes.Contains(ManagePredictionsScope));
    }

    public static string NormalizeOAuthToken(string token)
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

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (timeout.IsCancellationRequested &&
                ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                throw new TimeoutException("Timed out waiting for Twitch authorization.");
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            var query = ParseQuery(context.Request.Url?.Query ?? "");

            if (query.TryGetValue("error", out var error))
            {
                var errorDescription = query.TryGetValue("error_description", out var description) && !string.IsNullOrWhiteSpace(description)
                    ? description
                    : error;
                await WriteBrowserMessageAsync(context.Response, 400, $"Twitch authorization failed: {errorDescription}");
                throw new InvalidOperationException($"Twitch authorization failed: {errorDescription}");
            }

            if (string.Equals(path, "/twitch-oauth-token", StringComparison.OrdinalIgnoreCase))
            {
                var responseText = "Twitch authorization finished. You can close this window.";
                var statusCode = 200;

                try
                {
                    return ParseBrowserToken(query, expectedState);
                }
                catch (Exception ex)
                {
                    responseText = ex.Message;
                    statusCode = 400;
                    throw;
                }
                finally
                {
                    await WriteBrowserMessageAsync(context.Response, statusCode, responseText);
                }
            }

            await WriteFragmentCapturePageAsync(context.Response);
        }
    }

    private static TwitchBrowserToken ParseBrowserToken(Dictionary<string, string> query, string expectedState)
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

    private static async Task WriteFragmentCapturePageAsync(HttpListenerResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/html; charset=utf-8";
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
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static async Task WriteBrowserMessageAsync(HttpListenerResponse response, int statusCode, string message)
    {
        response.StatusCode = statusCode;
        response.ContentType = "text/html; charset=utf-8";
        var html = $"""
        <!doctype html>
        <html>
        <head><meta charset="utf-8"><title>Twitch Authorization</title></head>
        <body style="font-family:Segoe UI,Arial,sans-serif;margin:32px;">
        <h1>Twitch Authorization</h1>
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

    private static DateTimeOffset? TryGetExpiresAt(JsonElement root)
    {
        if (!root.TryGetProperty("expires_in", out var expiresInProperty))
        {
            return null;
        }

        return TryGetExpiresAtFromElement(expiresInProperty);
    }

    private static DateTimeOffset? TryGetExpiresAt(Dictionary<string, string> query)
    {
        return query.TryGetValue("expires_in", out var expiresIn) && long.TryParse(expiresIn, out var seconds) && seconds > 0
            ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : null;
    }

    private static DateTimeOffset? TryGetExpiresAtFromElement(JsonElement element)
    {
        long seconds = element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var numericValue) => numericValue,
            JsonValueKind.String when long.TryParse(element.GetString(), out var stringValue) => stringValue,
            _ => 0
        };

        return seconds <= 0 ? null : DateTimeOffset.UtcNow.AddSeconds(seconds);
    }

    private static string RequireSetting(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required for Twitch authorization.");
        }

        return value.Trim();
    }

    private static string CreateBase64UrlSecret(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
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
        if (!root.TryGetProperty("scopes", out var scopesProperty) ||
            scopesProperty.ValueKind != JsonValueKind.Array)
        {
            return scopes;
        }

        foreach (var item in scopesProperty.EnumerateArray())
        {
            var scope = item.GetString();
            if (!string.IsNullOrWhiteSpace(scope))
            {
                scopes.Add(scope);
            }
        }

        return scopes;
    }

    private static HashSet<string> ReadScopes(Dictionary<string, string> query)
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
    bool CanManagePredictions);

public sealed record TwitchOAuthTokenResult(
    string AccessToken,
    string Login,
    DateTimeOffset? ExpiresAtUtc,
    string TokenType,
    HashSet<string> Scopes);

using System.Text.Json;
using StreamlinkVlcStudio.Authentication;

namespace StreamlinkVlcStudio.AccountService;

public static class KickTokenEndpoint
{
    private static readonly HashSet<string> AllowedFields =
        ["client_id", "grant_type", "code", "code_verifier", "redirect_uri", "refresh_token"];

    public static void Map(WebApplication app, string clientId, string clientSecret)
    {
        app.MapPost(KickOAuthContract.TokenPath, async (HttpContext context, IHttpClientFactory factory) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            if (!string.Equals(context.Request.ContentType?.Split(';')[0].Trim(),
                    "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                return Error("invalid_request", StatusCodes.Status415UnsupportedMediaType);

            IFormCollection form;
            try { form = await context.Request.ReadFormAsync(timeout.Token); }
            catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
            { return Error("invalid_request", StatusCodes.Status400BadRequest); }
            catch (OperationCanceledException)
            { return Error("request_timeout", StatusCodes.Status408RequestTimeout); }

            if (!TryBuildTokenForm(form, clientId, clientSecret, out var tokenForm))
                return Error("invalid_request", StatusCodes.Status400BadRequest);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://id.kick.com/oauth/token")
                { Content = new FormUrlEncodedContent(tokenForm) };
                using var response = await factory.CreateClient("kick").SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                // Never echo provider errors: they may contain submitted credentials.
                if (!response.IsSuccessStatusCode)
                    return Error("authorization_failed", response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized
                        ? StatusCodes.Status400BadRequest : StatusCodes.Status502BadGateway);

                await response.Content.LoadIntoBufferAsync(65_536, timeout.Token);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                var root = document.RootElement;
                if (!TryReadToken(root, out var result))
                    return Error("invalid_provider_response", StatusCodes.Status502BadGateway);
                return Results.Json(result);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
            {
                return Error("connection_service_unavailable", StatusCodes.Status502BadGateway);
            }
        }).RequireRateLimiting("tokens");
    }

    // The caller cannot change the provider, client registration, callback, or grant types.
    public static bool TryBuildTokenForm(IFormCollection form, string clientId, string clientSecret,
        out Dictionary<string, string> tokenForm)
    {
        tokenForm = new Dictionary<string, string>();
        if (form.Any(field => !AllowedFields.Contains(field.Key) || field.Value.Count != 1 ||
                field.Value[0] is not { Length: > 0 and <= 4096 }) ||
            !string.Equals(form["client_id"], clientId, StringComparison.Ordinal)) return false;
        var grant = form["grant_type"].ToString();
        if (grant == "authorization_code")
        {
            var verifier = form["code_verifier"].ToString();
            if (form.Count != 5 || form["code"].Count != 1 ||
                form["redirect_uri"] != KickOAuthContract.RedirectUri ||
                verifier.Length is < 43 or > 128 ||
                verifier.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '.' and not '_' and not '~')) return false;
            tokenForm["code"] = form["code"].ToString();
            tokenForm["code_verifier"] = verifier;
            tokenForm["redirect_uri"] = KickOAuthContract.RedirectUri;
        }
        else if (grant == "refresh_token")
        {
            if (form.Count != 3 || form["refresh_token"].Count != 1) return false;
            tokenForm["refresh_token"] = form["refresh_token"].ToString();
        }
        else return false;
        tokenForm["grant_type"] = grant;
        tokenForm["client_id"] = clientId;
        tokenForm["client_secret"] = clientSecret;
        return true;
    }

    private static bool TryReadToken(JsonElement root, out Dictionary<string, object> result)
    {
        result = new Dictionary<string, object>();
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("access_token", out var accessToken) || accessToken.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(accessToken.GetString()) ||
            !root.TryGetProperty("refresh_token", out var refreshToken) || refreshToken.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(refreshToken.GetString()) ||
            !root.TryGetProperty("expires_in", out var expires) ||
            !long.TryParse(expires.ToString(), out var seconds) || seconds <= 0 ||
            !root.TryGetProperty("scope", out var scope)) return false;
        var scopes = scope.ValueKind == JsonValueKind.String ? scope.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries) :
            scope.ValueKind == JsonValueKind.Array ? scope.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [];
        if (!KickOAuthContract.Scopes.All(required => scopes.Contains(required, StringComparer.Ordinal))) return false;
        result["access_token"] = accessToken.GetString()!;
        result["refresh_token"] = refreshToken.GetString()!;
        result["expires_in"] = seconds;
        result["scope"] = string.Join(' ', scopes);
        result["token_type"] = "Bearer";
        return true;
    }

    private static IResult Error(string error, int status) => Results.Json(new { error }, statusCode: status);
}

using System.Net.Http.Headers;
using StreamlinkVlcStudio.Infrastructure.Chat;

namespace StreamlinkVlcStudio.Infrastructure.Http;

/// <summary>Creates authenticated Twitch API requests with one credential-normalization policy.</summary>
internal static class TwitchApiRequest
{
    internal static HttpRequestMessage Create(HttpMethod method, string url, string accessToken, string clientId)
    {
        var authorization = new AuthenticationHeaderValue("Bearer", OAuthTokenHelpers.NormalizeBearerToken(accessToken));
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = authorization;
        request.Headers.TryAddWithoutValidation("Client-Id", clientId.Trim());
        return request;
    }
}

using System.Net.Http.Headers;
using System.Text.Json;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Core.Json;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

internal static class TwitchProfileImageLookup
{
    private const int MaxLoginsPerRequest = 100;

    public static async Task<IReadOnlyDictionary<string, string>> GetAsync(
        HttpClient httpClient,
        string accessToken,
        string clientId,
        IEnumerable<string> logins,
        CancellationToken cancellationToken)
    {
        var normalizedLogins = logins
            .Select(login => (login ?? "").Trim())
            .Where(login => !string.IsNullOrWhiteSpace(login))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var profileImages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var batch in normalizedLogins.Chunk(MaxLoginsPerRequest))
        {
            var query = string.Join(
                "&",
                batch.Select(login => $"login={Uri.EscapeDataString(login)}"));
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.twitch.tv/helix/users?{query}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("Client-Id", clientId);

            using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
            var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Twitch user profile lookup failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in data.EnumerateArray())
            {
                var login = JsonElementReader.GetOptionalString(item, "login");
                var profileImage = JsonElementReader.GetOptionalString(item, "profile_image_url");
                if (!string.IsNullOrWhiteSpace(login) && !string.IsNullOrWhiteSpace(profileImage))
                {
                    profileImages[login] = profileImage;
                }
            }
        }

        return profileImages;
    }
}

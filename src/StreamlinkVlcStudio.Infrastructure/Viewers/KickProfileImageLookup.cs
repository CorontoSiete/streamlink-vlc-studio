using System.Net.Http.Headers;
using System.Text.Json;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Core.Json;
using StreamlinkVlcStudio.Infrastructure.Chat;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

internal static class KickProfileImageLookup
{
    private const int MaxUserIdsPerRequest = 50;

    public static async Task<IReadOnlyDictionary<string, string>> GetAsync(
        HttpClient httpClient,
        string accessToken,
        IEnumerable<string> userIds,
        CancellationToken cancellationToken)
    {
        var normalizedUserIds = userIds
            .Select(userId => (userId ?? "").Trim())
            .Where(userId => !string.IsNullOrWhiteSpace(userId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var profileImages = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var batch in normalizedUserIds.Chunk(MaxUserIdsPerRequest))
        {
            var query = string.Join(
                "&",
                batch.Select(userId => $"id={Uri.EscapeDataString(userId)}"));
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.kick.com/public/v1/users?{query}");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                KickOAuthService.NormalizeBearerToken(accessToken));

            using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
            var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Kick user profile lookup failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in data.EnumerateArray())
            {
                var userId = JsonElementReader.GetOptionalString(item, "user_id");
                var profileImage = JsonElementReader.GetOptionalString(item, "profile_picture");
                if (!string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(profileImage))
                {
                    profileImages[userId] = NormalizeImageUrl(profileImage);
                }
            }
        }

        return profileImages;
    }

}

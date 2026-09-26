using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Logging;
using System.Net.Http.Headers;
using System.Text.Json;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Core.Json;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

internal static class ProfileImageLookup
{
    internal static Task<IReadOnlyDictionary<string, string>> GetTwitchAsync(
        HttpClient httpClient,
        string accessToken,
        string clientId,
        IEnumerable<string> logins,
        CancellationToken cancellationToken) =>
        GetAsync(httpClient, logins, 100, StringComparer.OrdinalIgnoreCase,
            batch => TwitchApiRequest.Create(HttpMethod.Get,
                "https://api.twitch.tv/helix/users?" + BuildQuery("login", batch), accessToken, clientId),
            item => (JsonElementReader.GetOptionalString(item, "login"),
                JsonElementReader.GetOptionalString(item, "profile_image_url")),
            "Twitch", cancellationToken);

    internal static Task<IReadOnlyDictionary<string, string>> GetKickAsync(
        HttpClient httpClient,
        string accessToken,
        IEnumerable<string> userIds,
        CancellationToken cancellationToken) =>
        GetAsync(httpClient, userIds, 50, StringComparer.Ordinal,
            batch =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.kick.com/public/v1/users?" + BuildQuery("id", batch));
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer", KickOAuthService.NormalizeBearerToken(accessToken));
                return request;
            },
            item => (JsonElementReader.GetOptionalString(item, "user_id"),
                NormalizeImageUrl(JsonElementReader.GetOptionalString(item, "profile_picture"))),
            "Kick", cancellationToken);

    private static string BuildQuery(string parameter, IEnumerable<string> identities) =>
        string.Join('&', identities.Select(identity => $"{parameter}={Uri.EscapeDataString(identity)}"));

    private static async Task<IReadOnlyDictionary<string, string>> GetAsync(
        HttpClient httpClient,
        IEnumerable<string> identities,
        int batchSize,
        StringComparer comparer,
        Func<string[], HttpRequestMessage> createRequest,
        Func<JsonElement, (string Identity, string Image)> readProfile,
        string platform,
        CancellationToken cancellationToken)
    {
        var normalizedIdentities = identities
            .Select(identity => (identity ?? "").Trim())
            .Where(identity => identity.Length > 0)
            .Distinct(comparer)
            .ToArray();
        var profileImages = new Dictionary<string, string>(comparer);

        foreach (var batch in normalizedIdentities.Chunk(batchSize))
        {
            using var request = createRequest(batch);
            using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
            var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"{platform} user profile lookup failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            using var document = JsonDocument.Parse(responseBody);
            if (!JsonElementReader.TryGetArray(document.RootElement, "data", out var data))
            {
                continue;
            }

            foreach (var item in data.EnumerateArray())
            {
                var (identity, profileImage) = readProfile(item);
                if (!string.IsNullOrWhiteSpace(identity) && !string.IsNullOrWhiteSpace(profileImage))
                {
                    profileImages[identity] = profileImage;
                }
            }
        }

        return profileImages;
    }

    /// <summary>
    /// Looks up profile images for <paramref name="items"/> and writes them back in place.
    /// <paramref name="applyProfileImage"/> decides how the value is merged, since some callers
    /// keep an image they already have. Lookup failures are logged and leave the items unchanged;
    /// caller cancellation still propagates.
    /// </summary>
    internal static async Task EnrichTwitchAsync<T>(
        HttpClient httpClient,
        IList<T> items,
        Func<T, string> getChannel,
        Func<T, string, T> applyProfileImage,
        string accessToken,
        string clientId,
        IAppLogger logger,
        string logCategory,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        try
        {
            var profileImages = await GetTwitchAsync(
                httpClient,
                accessToken,
                clientId,
                items.Select(getChannel),
                cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < items.Count; index++)
            {
                if (profileImages.TryGetValue(getChannel(items[index]), out var profileImage))
                {
                    items[index] = applyProfileImage(items[index], profileImage);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, logCategory, "Twitch profile images could not be loaded.", ex);
        }
    }
}

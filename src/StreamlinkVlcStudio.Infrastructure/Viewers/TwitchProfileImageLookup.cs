using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Logging;
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
            using var request = TwitchApiRequest.Create(
                HttpMethod.Get,
                $"https://api.twitch.tv/helix/users?{query}",
                accessToken,
                clientId);

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

    /// <summary>
    /// Looks up profile images for <paramref name="items"/> and writes them back in place.
    /// <paramref name="applyProfileImage"/> decides how the value is merged, since some callers
    /// keep an image they already have. Lookup failures are logged and leave the items unchanged;
    /// caller cancellation still propagates.
    /// </summary>
    internal static async Task EnrichAsync<T>(
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
            var profileImages = await GetAsync(
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

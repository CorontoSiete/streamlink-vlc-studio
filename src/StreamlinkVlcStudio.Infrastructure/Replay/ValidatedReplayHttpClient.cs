using System.Net;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.Infrastructure.Replay;

internal static class ValidatedReplayHttpClient
{
    private const int MaximumRedirects = 5;

    internal static async Task<HttpResponseMessage> SendGetAsync(
        HttpClient httpClient,
        ReplayUrlSecurityValidator validator,
        Uri initialUri,
        PlatformKind platform,
        Func<Uri, HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(initialUri);
        ArgumentNullException.ThrowIfNull(requestFactory);

        var currentUri = initialUri;
        for (var redirectCount = 0; ; redirectCount++)
        {
            await validator.ValidateAsync(currentUri, platform, cancellationToken).ConfigureAwait(false);
            using var request = requestFactory(currentUri);
            var response = await BoundedHttpResponseSender
                .SendAsync(httpClient, request, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var effectiveUri = response.RequestMessage?.RequestUri ?? currentUri;
                if (effectiveUri != currentUri)
                {
                    throw new InvalidOperationException(
                        "Validated replay requests require an HTTP client with automatic redirects disabled.");
                }

                await validator.ValidateAsync(effectiveUri, platform, cancellationToken).ConfigureAwait(false);
                if (!IsRedirect(response.StatusCode))
                {
                    return response;
                }

                if (redirectCount >= MaximumRedirects || response.Headers.Location is null)
                {
                    throw new InvalidDataException("Replay endpoint returned an invalid or excessive redirect chain.");
                }

                currentUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(effectiveUri, response.Headers.Location);
            }
            catch
            {
                response.Dispose();
                throw;
            }

            response.Dispose();
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;
}

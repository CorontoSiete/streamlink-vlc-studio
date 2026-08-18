namespace StreamlinkVlcStudio.Infrastructure.Http;

/// <summary>
/// Sends requests whose response bodies will be consumed by a bounded streaming reader.
/// Using headers-only completion prevents <see cref="HttpClient"/> from buffering an
/// untrusted response body before the caller's size limit can be enforced.
/// </summary>
internal static class BoundedHttpResponseSender
{
    internal static Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(request);
        return httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }
}

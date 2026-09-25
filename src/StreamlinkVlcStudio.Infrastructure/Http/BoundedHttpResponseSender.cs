namespace StreamlinkVlcStudio.Infrastructure.Http;

/// <summary>
/// Sends requests whose response bodies will be consumed by a bounded streaming reader.
/// Using headers-only completion prevents <see cref="HttpClient"/> from buffering an
/// untrusted response body before the caller's size limit can be enforced.
/// </summary>
internal static class BoundedHttpResponseSender
{
    internal static async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(request);
        // HttpClient.Timeout stops applying after headers arrive with ResponseHeadersRead.
        // Keep the same request budget alive until the caller disposes the response body.
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? httpClient.Timeout);
        HttpResponseMessage? response = null;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            response.Content = new DeadlineHttpContent(response.Content, deadline);
            return response;
        }
        catch
        {
            response?.Dispose();
            deadline.Dispose();
            throw;
        }
    }
}

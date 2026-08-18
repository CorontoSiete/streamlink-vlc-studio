using System.Net;
using System.Text;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

/// <summary>
/// Receives a single trusted OAuth callback while ignoring unrelated traffic that can reach a
/// loopback listener (browser probes, stale tabs, and local port scans).
/// </summary>
internal static class LoopbackOAuthReceiver
{
    internal static async Task<TResult> WaitForResultAsync<TResult>(
        HttpListener listener,
        string providerName,
        string callbackPath,
        string expectedState,
        TimeSpan timeout,
        Func<IReadOnlyDictionary<string, string>, TResult> resultFactory,
        CancellationToken cancellationToken,
        Func<HttpListenerContext, Task<bool>>? tryHandleAuxiliaryRequestAsync = null)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedState);
        ArgumentNullException.ThrowIfNull(resultFactory);

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        using var registration = timeoutCancellation.Token.Register(static state =>
        {
            try
            {
                ((HttpListener)state!).Stop();
            }
            catch (ObjectDisposedException)
            {
            }
        }, listener);

        while (true)
        {
            var context = await GetContextAsync(
                    listener,
                    providerName,
                    timeoutCancellation,
                    cancellationToken)
                .ConfigureAwait(false);
            var requestPath = context.Request.Url?.AbsolutePath ?? "/";
            if (!string.Equals(requestPath, callbackPath, StringComparison.Ordinal))
            {
                if (tryHandleAuxiliaryRequestAsync is not null &&
                    await tryHandleAuxiliaryRequestAsync(context).ConfigureAwait(false))
                {
                    continue;
                }

                await WriteMessageAsync(
                        context.Response,
                        providerName,
                        HttpStatusCode.NotFound,
                        "This is not the expected authorization callback.")
                    .ConfigureAwait(false);
                continue;
            }

            if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.Ordinal))
            {
                context.Response.Headers[HttpResponseHeader.Allow] = "GET";
                await WriteMessageAsync(
                        context.Response,
                        providerName,
                        HttpStatusCode.MethodNotAllowed,
                        "The authorization callback must use GET.")
                    .ConfigureAwait(false);
                continue;
            }

            if (!OAuthTokenHelpers.TryParseQueryString(
                    context.Request.Url?.Query ?? "",
                    out var query))
            {
                await WriteMessageAsync(
                        context.Response,
                        providerName,
                        HttpStatusCode.BadRequest,
                        $"{providerName} authorization returned a malformed callback.")
                    .ConfigureAwait(false);
                continue;
            }

            // State is intentionally authenticated before honoring an OAuth error. Otherwise an
            // unrelated local request could terminate the real authorization attempt.
            if (!query.TryGetValue("state", out var returnedState) ||
                !string.Equals(returnedState, expectedState, StringComparison.Ordinal))
            {
                await WriteMessageAsync(
                        context.Response,
                        providerName,
                        HttpStatusCode.BadRequest,
                        $"{providerName} authorization returned an invalid state value.")
                    .ConfigureAwait(false);
                continue;
            }

            if (query.TryGetValue("error", out var error) && !string.IsNullOrWhiteSpace(error))
            {
                var errorDescription = query.TryGetValue("error_description", out var description) &&
                    !string.IsNullOrWhiteSpace(description)
                        ? description
                        : error;
                var message = $"{providerName} authorization failed: {errorDescription}";
                await WriteMessageAsync(
                        context.Response,
                        providerName,
                        HttpStatusCode.BadRequest,
                        message)
                    .ConfigureAwait(false);
                throw new InvalidOperationException(message);
            }

            try
            {
                var result = resultFactory(query);
                await WriteMessageAsync(
                        context.Response,
                        providerName,
                        HttpStatusCode.OK,
                        $"{providerName} authorization finished. You can close this window.")
                    .ConfigureAwait(false);
                return result;
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                await WriteMessageAsync(
                        context.Response,
                        providerName,
                        HttpStatusCode.BadRequest,
                        ex.Message)
                    .ConfigureAwait(false);
                throw;
            }
        }
    }

    internal static async Task WriteMessageAsync(
        HttpListenerResponse response,
        string providerName,
        HttpStatusCode statusCode,
        string message)
    {
        try
        {
            response.StatusCode = (int)statusCode;
            response.ContentType = "text/html; charset=utf-8";
            response.Headers[HttpResponseHeader.CacheControl] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            var encodedProvider = WebUtility.HtmlEncode(providerName);
            var html = $"""
            <!doctype html>
            <html>
            <head><meta charset="utf-8"><title>{encodedProvider} Authorization</title></head>
            <body style="font-family:Segoe UI,Arial,sans-serif;margin:32px;">
            <h1>{encodedProvider} Authorization</h1>
            <p>{WebUtility.HtmlEncode(message)}</p>
            </body>
            </html>
            """;
            var bytes = Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            // The callback has already been validated. A browser tab closing while the local
            // acknowledgement is written must not discard a valid token or abort the receiver.
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static async Task<HttpListenerContext> GetContextAsync(
        HttpListener listener,
        string providerName,
        CancellationTokenSource timeoutCancellation,
        CancellationToken callerCancellationToken)
    {
        try
        {
            return await listener.GetContextAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (timeoutCancellation.IsCancellationRequested &&
            ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            callerCancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"Timed out waiting for {providerName} authorization.");
        }
    }
}

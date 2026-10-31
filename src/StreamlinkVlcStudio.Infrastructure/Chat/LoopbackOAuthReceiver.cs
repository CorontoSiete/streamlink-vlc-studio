using System.Net;
using System.Text;
using StreamlinkVlcStudio.Infrastructure.Http;

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
        Func<HttpListenerContext, Task<bool>>? tryHandleAuxiliaryRequestAsync = null,
        bool useFormPost = false)
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

            var expectedMethod = useFormPost ? "POST" : "GET";
            if (!string.Equals(context.Request.HttpMethod, expectedMethod, StringComparison.Ordinal))
            {
                context.Response.Headers[HttpResponseHeader.Allow] = expectedMethod;
                await WriteMessageAsync(
                        context.Response,
                        providerName,
                        HttpStatusCode.MethodNotAllowed,
                        $"The authorization callback must use {expectedMethod}.")
                    .ConfigureAwait(false);
                continue;
            }

            var parameters = useFormPost
                ? await ReadFormPostAsync(context, timeoutCancellation.Token).ConfigureAwait(false)
                : context.Request.Url?.Query ?? "";
            if (parameters is null || !OAuthTokenHelpers.TryParseQueryString(
                    parameters,
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

    private static async Task<string?> ReadFormPostAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        // Abort stalled uploads even when the HTTP listener's stream ignores cancellation.
        using var registration = readTimeout.Token.Register(context.Response.Abort);
        var request = context.Request;
        return await ReadFormBodyAsync(request.InputStream, request.ContentLength64, request.ContentType,
            request.Url?.Query, readTimeout.Token).ConfigureAwait(false);
    }

    internal static async Task<string?> ReadFormBodyAsync(Stream inputStream, long contentLength, string? contentType,
        string? query, CancellationToken cancellationToken)
    {
        const int maximumBytes = 16_384;
        if (!string.IsNullOrEmpty(query) || contentLength > maximumBytes ||
            !string.Equals(contentType?.Split(';')[0].Trim(),
                "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var bytes = await BoundedByteReader.ReadOrThrowAsync(inputStream, maximumBytes, cancellationToken)
                .ConfigureAwait(false);
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException or
            ObjectDisposedException or DecoderFallbackException)
        {
            return null;
        }
    }

    internal static Task WriteMessageAsync(
        HttpListenerResponse response,
        string providerName,
        HttpStatusCode statusCode,
        string message)
    {
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
        return WriteHtmlAsync(response, statusCode, html);
    }

    internal static async Task WriteHtmlAsync(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        string html,
        string? scriptNonce = null)
    {
        try
        {
            response.StatusCode = (int)statusCode;
            response.ContentType = "text/html; charset=utf-8";
            response.Headers[HttpResponseHeader.CacheControl] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["X-Frame-Options"] = "DENY";
            response.Headers["Content-Security-Policy"] = GetContentSecurityPolicy(scriptNonce);
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

    internal static string GetContentSecurityPolicy(string? scriptNonce = null) =>
        "default-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'; " +
        "style-src 'unsafe-inline'; " + (scriptNonce is null
            ? "script-src 'none'; connect-src 'none'"
            : $"script-src 'nonce-{scriptNonce}'; connect-src 'self'");

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

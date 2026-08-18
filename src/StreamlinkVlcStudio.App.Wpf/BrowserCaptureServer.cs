using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.App.Wpf;

public sealed class BrowserCaptureServer : IAsyncDisposable
{
    public const int Port = 39179;
    private const int MaxRequestBytes = 16384;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Func<string, Task> handleUrlAsync;
    private readonly IAppLogger logger;
    private readonly int requestedPort;
    private readonly CancellationTokenSource cancellation = new();
    private readonly object lifecycleGate = new();
    private readonly HashSet<Task> backgroundTasks = [];
    private TcpListener? listener;
    private Task? acceptLoop;
    private Task? disposalTask;

    public BrowserCaptureServer(Func<string, Task> handleUrlAsync, IAppLogger logger, int port = Port)
    {
        this.handleUrlAsync = handleUrlAsync;
        this.logger = logger;
        requestedPort = port is < 0 or > 65_535 ? Port : port;
    }

    public int ListenerPort { get; private set; } = Port;

    public bool Start()
    {
        lock (lifecycleGate)
        {
            if (listener is not null || acceptLoop is not null || disposalTask is not null)
            {
                return false;
            }

            var nextListener = new TcpListener(IPAddress.Loopback, requestedPort);
            try
            {
                nextListener.Start();
                ListenerPort = ((IPEndPoint)nextListener.LocalEndpoint).Port;
                listener = nextListener;
                acceptLoop = AcceptLoopAsync(nextListener, cancellation.Token);
                logger.Write(AppLogLevel.Info, "BrowserCapture", $"Listening on http://127.0.0.1:{ListenerPort}/capture.");
                return true;
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException)
            {
                nextListener.Dispose();
                logger.Write(AppLogLevel.Warning, "BrowserCapture", "Could not start browser capture listener.", ex);
                return false;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycleGate)
        {
            disposalTask ??= DisposeCoreAsync();
            return new ValueTask(disposalTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await cancellation.CancelAsync().ConfigureAwait(false);
        listener?.Dispose();

        var loop = acceptLoop;
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        await DrainBackgroundTasksAsync().ConfigureAwait(false);
        cancellation.Dispose();

        lock (lifecycleGate)
        {
            listener = null;
            acceptLoop = null;
        }
    }

    private async Task AcceptLoopAsync(TcpListener activeListener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await activeListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (cancellationToken.IsCancellationRequested)
            {
                logger.Write(AppLogLevel.Debug, "BrowserCapture", "Browser capture listener stopped.", ex);
                return;
            }
            catch (SocketException ex)
            {
                logger.Write(AppLogLevel.Warning, "BrowserCapture", "Browser capture listener failed.", ex);
                return;
            }

            TrackBackgroundTask(HandleClientAsync(client, cancellationToken));
        }
    }

    private void TrackBackgroundTask(Task task)
    {
        lock (lifecycleGate)
        {
            backgroundTasks.Add(task);
        }

        _ = RemoveBackgroundTaskWhenCompleteAsync(task);
    }

    private async Task RemoveBackgroundTaskWhenCompleteAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            lock (lifecycleGate)
            {
                backgroundTasks.Remove(task);
            }
        }
    }

    private async Task DrainBackgroundTasksAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (lifecycleGate)
            {
                tasks = backgroundTasks.ToArray();
            }

            if (tasks.Length == 0)
            {
                return;
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            var stream = client.GetStream();
            var readResult = await LocalHttpRequestReader.ReadWithStatusAsync(
                    stream,
                    MaxRequestBytes,
                    timeout.Token)
                .ConfigureAwait(false);
            if (!readResult.IsSuccess)
            {
                await WriteResponseAsync(
                        stream,
                        readResult.StatusCode,
                        readResult.ReasonPhrase,
                        "text/plain",
                        readResult.Message,
                        timeout.Token)
                    .ConfigureAwait(false);
                return;
            }

            var request = readResult.Request!;

            if (!TryGetAllowedCorsOrigin(request.GetOptionalHeader("Origin"), out var allowedCorsOrigin))
            {
                await WriteResponseAsync(stream, 403, "Forbidden", "text/plain", "Forbidden origin.", timeout.Token).ConfigureAwait(false);
                return;
            }

            if (!request.Path.Equals("/capture", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, 404, "Not Found", "text/plain", "Not found.", timeout.Token, allowedCorsOrigin).ConfigureAwait(false);
                return;
            }

            if (request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, 204, "No Content", "text/plain", "", timeout.Token, allowedCorsOrigin).ConfigureAwait(false);
                return;
            }

            if (!request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, 404, "Not Found", "text/plain", "Not found.", timeout.Token, allowedCorsOrigin).ConfigureAwait(false);
                return;
            }

            if (!TryReadCaptureUrl(Encoding.UTF8.GetString(request.Body), out var captureUrl, out var parseError))
            {
                await WriteResponseAsync(stream, 400, "Bad Request", "text/plain", parseError, timeout.Token, allowedCorsOrigin).ConfigureAwait(false);
                return;
            }

            if (!TryValidateCaptureUrl(captureUrl, out var validationError))
            {
                await WriteResponseAsync(
                        stream,
                        400,
                        "Bad Request",
                        "text/plain",
                        validationError,
                        timeout.Token,
                        allowedCorsOrigin)
                    .ConfigureAwait(false);
                return;
            }

            DispatchCapture(captureUrl);
            await WriteResponseAsync(stream, 202, "Accepted", "application/json", """{"ok":true}""", timeout.Token, allowedCorsOrigin).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "BrowserCapture", "Browser capture request failed.", ex);
        }
    }

    private void DispatchCapture(string captureUrl)
    {
        TrackBackgroundTask(Task.Run(async () =>
        {
            try
            {
                await handleUrlAsync(captureUrl).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.Write(AppLogLevel.Warning, "BrowserCapture", "Browser capture handler failed.", ex);
            }
        }));
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reasonPhrase,
        string contentType,
        string body,
        CancellationToken cancellationToken,
        string? allowedCorsOrigin = null)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headerLines = new List<string>
        {
            $"HTTP/1.1 {statusCode} {reasonPhrase}",
            "Vary: Origin",
            $"Content-Type: {contentType}; charset=utf-8",
            $"Content-Length: {bodyBytes.Length}",
            "Connection: close"
        };
        if (allowedCorsOrigin is not null)
        {
            headerLines.Insert(2, $"Access-Control-Allow-Origin: {allowedCorsOrigin}");
            headerLines.Insert(3, "Access-Control-Allow-Methods: POST, OPTIONS");
            headerLines.Insert(4, "Access-Control-Allow-Headers: Content-Type");
        }

        headerLines.Add("");
        headerLines.Add("");
        var header = string.Join("\r\n", headerLines);
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    public static bool TryReadCaptureUrl(string body, out string url)
    {
        // Preserve the existing helper's trimming behavior for callers that use it directly.
        return TryReadCaptureUrlCore(body, out url, out _, trimUrl: true);
    }

    private static bool TryReadCaptureUrl(string body, out string url, out string error)
    {
        // Network requests are validated against the exact JSON value so whitespace-wrapped
        // values cannot bypass the canonical URL requirement.
        return TryReadCaptureUrlCore(body, out url, out error, trimUrl: false);
    }

    private static bool TryReadCaptureUrlCore(
        string body,
        out string url,
        out string error,
        bool trimUrl)
    {
        url = "";
        error = "Missing URL.";
        try
        {
            var capture = JsonSerializer.Deserialize<BrowserCaptureRequest>(body, JsonOptions);
            if (string.IsNullOrWhiteSpace(capture?.Url))
            {
                return false;
            }

            url = trimUrl ? capture.Url.Trim() : capture.Url;
            return true;
        }
        catch (JsonException)
        {
            error = "Malformed JSON request.";
            return false;
        }
    }

    internal static bool TryValidateCaptureUrl(string url, out string error)
    {
        error = "The URL must be a canonical Twitch or Kick live-channel URL.";
        if (string.IsNullOrWhiteSpace(url) ||
            !StreamInputParser.TryParsePlatformUrl(url, out var target) ||
            target is null ||
            target.Kind != StreamTargetKind.Live ||
            !string.Equals(target.Url, url, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    internal static bool IsAllowedRequestOrigin(string? origin)
    {
        return TryGetAllowedCorsOrigin(origin, out _);
    }

    private static bool TryGetAllowedCorsOrigin(string? origin, out string? allowedCorsOrigin)
    {
        allowedCorsOrigin = null;
        if (origin is null)
        {
            // Native clients and local integration tests do not send browser CORS headers.
            return true;
        }

        var normalizedOrigin = origin.Trim();
        if (normalizedOrigin.Length == 0 ||
            !Uri.TryCreate(normalizedOrigin, UriKind.Absolute, out var originUri) ||
            (originUri.Scheme != "chrome-extension" && originUri.Scheme != "moz-extension") ||
            string.IsNullOrWhiteSpace(originUri.Host) ||
            !string.IsNullOrEmpty(originUri.UserInfo) ||
            !originUri.IsDefaultPort ||
            (!string.IsNullOrEmpty(originUri.AbsolutePath) && originUri.AbsolutePath != "/") ||
            !string.IsNullOrEmpty(originUri.Query) ||
            !string.IsNullOrEmpty(originUri.Fragment))
        {
            return false;
        }

        allowedCorsOrigin = $"{originUri.Scheme}://{originUri.IdnHost}";
        return true;
    }

    private sealed record BrowserCaptureRequest([property: JsonPropertyName("url")] string? Url);

}

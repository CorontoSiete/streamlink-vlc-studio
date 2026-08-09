using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

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
        requestedPort = port;
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
            var request = await ReadHttpRequestAsync(stream, timeout.Token).ConfigureAwait(false);
            if (request is null)
            {
                await WriteResponseAsync(stream, 400, "Bad Request", "text/plain", "Bad request.", timeout.Token).ConfigureAwait(false);
                return;
            }

            if (!TryGetAllowedCorsOrigin(request.Origin, out var allowedCorsOrigin))
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

            if (!TryReadCaptureUrl(request.Body, out var captureUrl))
            {
                await WriteResponseAsync(stream, 400, "Bad Request", "text/plain", "Missing URL.", timeout.Token, allowedCorsOrigin).ConfigureAwait(false);
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

    private static async Task<HttpRequest?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var totalRead = 0;
        var headerEnd = -1;

        while (totalRead < buffer.Length && headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            totalRead += read;
            headerEnd = FindHeaderEnd(buffer.AsSpan(0, totalRead));
        }

        if (headerEnd < 0)
        {
            return null;
        }

        var headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var headerLines = headerText.Split("\r\n", StringSplitOptions.None);
        var requestLine = headerLines.FirstOrDefault()?.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine is not { Length: >= 2 })
        {
            return null;
        }

        var contentLength = 0;
        string? origin = null;
        var originHeaderSeen = false;
        foreach (var line in headerLines.Skip(1))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            if (line[..separator].Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[(separator + 1)..].Trim(), out var parsedLength))
            {
                contentLength = parsedLength;
            }
            else if (line[..separator].Equals("Origin", StringComparison.OrdinalIgnoreCase))
            {
                if (originHeaderSeen)
                {
                    return null;
                }

                originHeaderSeen = true;
                origin = line[(separator + 1)..].Trim();
            }
        }

        if (contentLength < 0 || headerEnd + 4 + contentLength > MaxRequestBytes)
        {
            return null;
        }

        while (totalRead < headerEnd + 4 + contentLength)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            totalRead += read;
        }

        var bodyOffset = headerEnd + 4;
        var body = Encoding.UTF8.GetString(buffer, bodyOffset, contentLength);
        var path = requestLine[1].Split('?', 2)[0];
        return new HttpRequest(requestLine[0], path, body, origin);
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> bytes)
    {
        for (var index = 0; index <= bytes.Length - 4; index++)
        {
            if (bytes[index] == '\r' &&
                bytes[index + 1] == '\n' &&
                bytes[index + 2] == '\r' &&
                bytes[index + 3] == '\n')
            {
                return index;
            }
        }

        return -1;
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
        url = "";
        try
        {
            var capture = JsonSerializer.Deserialize<BrowserCaptureRequest>(body, JsonOptions);
            if (string.IsNullOrWhiteSpace(capture?.Url))
            {
                return false;
            }

            url = capture.Url.Trim();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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

    private sealed record HttpRequest(string Method, string Path, string Body, string? Origin);
}

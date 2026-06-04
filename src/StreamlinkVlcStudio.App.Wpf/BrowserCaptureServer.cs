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
    private TcpListener? listener;
    private Task? acceptLoop;

    public BrowserCaptureServer(Func<string, Task> handleUrlAsync, IAppLogger logger, int port = Port)
    {
        this.handleUrlAsync = handleUrlAsync;
        this.logger = logger;
        requestedPort = port;
    }

    public int ListenerPort { get; private set; } = Port;

    public bool Start()
    {
        try
        {
            listener = new TcpListener(IPAddress.Loopback, requestedPort);
            listener.Start();
            ListenerPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptLoop = AcceptLoopAsync(cancellation.Token);
            logger.Write(AppLogLevel.Info, "BrowserCapture", $"Listening on http://127.0.0.1:{ListenerPort}/capture.");
            return true;
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            listener = null;
            logger.Write(AppLogLevel.Warning, "BrowserCapture", "Could not start browser capture listener.", ex);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        listener?.Stop();
        if (acceptLoop is not null)
        {
            try
            {
                await acceptLoop;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        cancellation.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener is not null)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
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
            var request = await ReadHttpRequestAsync(stream, timeout.Token);
            if (request is null)
            {
                await WriteResponseAsync(stream, 400, "Bad Request", "text/plain", "Bad request.", timeout.Token);
                return;
            }

            if (request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, 204, "No Content", "text/plain", "", timeout.Token);
                return;
            }

            if (!request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
                !request.Path.Equals("/capture", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, 404, "Not Found", "text/plain", "Not found.", timeout.Token);
                return;
            }

            if (!TryReadCaptureUrl(request.Body, out var captureUrl))
            {
                await WriteResponseAsync(stream, 400, "Bad Request", "text/plain", "Missing URL.", timeout.Token);
                return;
            }

            DispatchCapture(captureUrl);
            await WriteResponseAsync(stream, 202, "Accepted", "application/json", """{"ok":true}""", timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or SocketException)
        {
            logger.Write(AppLogLevel.Warning, "BrowserCapture", "Browser capture request failed.", ex);
        }
    }

    private void DispatchCapture(string captureUrl)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await handleUrlAsync(captureUrl).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellation.IsCancellationRequested)
            {
                logger.Write(AppLogLevel.Warning, "BrowserCapture", "Browser capture handler failed.", ex);
            }
        });
    }

    private static async Task<HttpRequest?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var totalRead = 0;
        var headerEnd = -1;

        while (totalRead < buffer.Length && headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
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
        foreach (var line in headerLines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            if (line[..separator].Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[(separator + 1)..].Trim(), out var parsedLength))
            {
                contentLength = parsedLength;
            }
        }

        if (contentLength < 0 || headerEnd + 4 + contentLength > MaxRequestBytes)
        {
            return null;
        }

        while (totalRead < headerEnd + 4 + contentLength)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
            if (read == 0)
            {
                return null;
            }

            totalRead += read;
        }

        var bodyOffset = headerEnd + 4;
        var body = Encoding.UTF8.GetString(buffer, bodyOffset, contentLength);
        var path = requestLine[1].Split('?', 2)[0];
        return new HttpRequest(requestLine[0], path, body);
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
        CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = string.Join("\r\n",
            $"HTTP/1.1 {statusCode} {reasonPhrase}",
            "Access-Control-Allow-Origin: *",
            "Access-Control-Allow-Methods: POST, OPTIONS",
            "Access-Control-Allow-Headers: Content-Type",
            $"Content-Type: {contentType}; charset=utf-8",
            $"Content-Length: {bodyBytes.Length}",
            "Connection: close",
            "",
            "");
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, cancellationToken);
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes, cancellationToken);
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

    private sealed record BrowserCaptureRequest([property: JsonPropertyName("url")] string? Url);

    private sealed record HttpRequest(string Method, string Path, string Body);
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Replay;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public sealed class KickWebhookChatServer : IAsyncDisposable
{
    public const int DefaultPort = 39180;
    public const string WebhookPath = "/kick-webhook";
    private const int MaxRequestBytes = 262_144;
    private const string PublicKeyEndpoint = "https://api.kick.com/public/v1/public-key";
    private const string FallbackPublicKey = """
-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAq/+l1WnlRrGSolDMA+A8
6rAhMbQGmQ2SapVcGM3zq8ANXjnhDWocMqfWcTd95btDydITa10kDvHzw9WQOqp2
MZI7ZyrfzJuz5nhTPCiJwTwnEtWft7nV14BYRDHvlfqPUaZ+1KR4OCaO/wWIk/rQ
L/TjY0M70gse8rlBkbo2a8rKhu69RQTRsoaf4DVhDPEeSeI5jVrRDGAMGL3cGuyY
6CLKGdjVEM78g3JfYOvDU/RvfqD7L89TZ3iN94jrmWdGz34JNlEI5hqK8dd7C5EF
BEbZ5jgB8s8ReQV8H+MkuffjdAj3ajDDX3DOJMIut1lBrUVD1AaSrGCKHooWoL2e
twIDAQAB
-----END PUBLIC KEY-----
""";

    private readonly KickOfficialChatReplayStore store;
    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly int requestedPort;
    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim publicKeyGate = new(1, 1);
    private TcpListener? listener;
    private Task? acceptLoop;
    private string? publicKeyPem;

    public KickWebhookChatServer(
        KickOfficialChatReplayStore store,
        IAppLogger logger,
        int port = DefaultPort,
        HttpClient? httpClient = null)
    {
        this.store = store;
        this.logger = logger;
        this.httpClient = httpClient ?? new HttpClient();
        ownsHttpClient = httpClient is null;
        requestedPort = port < 0 ? DefaultPort : port;
    }

    public int ListenerPort { get; private set; } = DefaultPort;

    public string LocalWebhookUrl => $"http://127.0.0.1:{ListenerPort}{WebhookPath}";

    public bool Start()
    {
        try
        {
            listener = new TcpListener(IPAddress.Loopback, requestedPort);
            listener.Start();
            ListenerPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptLoop = AcceptLoopAsync(cancellation.Token);
            logger.Write(AppLogLevel.Info, "KickWebhook", $"Listening for official Kick chat webhooks at {LocalWebhookUrl}.");
            return true;
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            listener = null;
            logger.Write(AppLogLevel.Warning, "KickWebhook", "Could not start official Kick webhook listener.", ex);
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
                await acceptLoop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        publicKeyGate.Dispose();
        cancellation.Dispose();
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener is not null)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
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
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            var stream = client.GetStream();
            var request = await ReadHttpRequestAsync(stream, timeout.Token).ConfigureAwait(false);
            if (request is null)
            {
                await WriteResponseAsync(stream, 400, "Bad Request", "text/plain", "Bad request.", timeout.Token).ConfigureAwait(false);
                return;
            }

            if (!request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
                !request.Path.Equals(WebhookPath, StringComparison.OrdinalIgnoreCase))
            {
                if (request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase) &&
                    request.Path.Equals(WebhookPath, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, 204, "No Content", "text/plain", "", timeout.Token).ConfigureAwait(false);
                    return;
                }

                await WriteResponseAsync(stream, 404, "Not Found", "text/plain", "Not found.", timeout.Token).ConfigureAwait(false);
                return;
            }

            if (!await IsSignatureValidAsync(request, timeout.Token).ConfigureAwait(false))
            {
                logger.Write(AppLogLevel.Warning, "KickWebhook", "Rejected Kick webhook with invalid signature.");
                await WriteResponseAsync(stream, 401, "Unauthorized", "text/plain", "Invalid signature.", timeout.Token).ConfigureAwait(false);
                return;
            }

            var eventType = GetHeader(request.Headers, "Kick-Event-Type");
            if (!eventType.Equals(KickOfficialChatWebhookParser.ChatMessageSentEventType, StringComparison.OrdinalIgnoreCase))
            {
                logger.Write(AppLogLevel.Info, "KickWebhook", $"Ignored Kick webhook event type '{eventType}'.");
                await WriteResponseAsync(stream, 202, "Accepted", "application/json", """{"ignored":true}""", timeout.Token).ConfigureAwait(false);
                return;
            }

            var bodyText = Encoding.UTF8.GetString(request.Body);
            if (!KickOfficialChatWebhookParser.TryParseChatMessage(bodyText, out var message, out var parseError))
            {
                logger.Write(AppLogLevel.Warning, "KickWebhook", $"Rejected Kick webhook payload: {parseError}");
                await WriteResponseAsync(stream, 400, "Bad Request", "text/plain", parseError, timeout.Token).ConfigureAwait(false);
                return;
            }

            await store.AppendAsync(message, timeout.Token).ConfigureAwait(false);
            await WriteResponseAsync(stream, 200, "OK", "application/json", """{"ok":true}""", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or SocketException or CryptographicException or FormatException)
        {
            logger.Write(AppLogLevel.Warning, "KickWebhook", "Kick webhook request failed.", ex);
        }
    }

    private async Task<bool> IsSignatureValidAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var messageId = GetHeader(request.Headers, "Kick-Event-Message-Id");
        var timestamp = GetHeader(request.Headers, "Kick-Event-Message-Timestamp");
        var signature = GetHeader(request.Headers, "Kick-Event-Signature");
        if (string.IsNullOrWhiteSpace(messageId) ||
            string.IsNullOrWhiteSpace(timestamp) ||
            string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        var publicKey = await GetPublicKeyPemAsync(cancellationToken).ConfigureAwait(false);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKey);
        var signedPrefix = Encoding.UTF8.GetBytes($"{messageId}.{timestamp}.");
        var signedBody = new byte[signedPrefix.Length + request.Body.Length];
        Buffer.BlockCopy(signedPrefix, 0, signedBody, 0, signedPrefix.Length);
        Buffer.BlockCopy(request.Body, 0, signedBody, signedPrefix.Length, request.Body.Length);
        var signatureBytes = Convert.FromBase64String(signature);
        return rsa.VerifyData(
            signedBody,
            signatureBytes,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
    }

    private async Task<string> GetPublicKeyPemAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(publicKeyPem))
        {
            return publicKeyPem;
        }

        await publicKeyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(publicKeyPem))
            {
                return publicKeyPem;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, PublicKeyEndpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && TryReadPublicKey(body, out var parsedPublicKey))
                {
                    publicKeyPem = parsedPublicKey;
                    return publicKeyPem;
                }

                logger.Write(AppLogLevel.Warning, "KickWebhook", $"Kick public key request failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Write(AppLogLevel.Warning, "KickWebhook", "Kick public key request failed; using bundled public key.", ex);
            }

            publicKeyPem = FallbackPublicKey;
            return publicKeyPem;
        }
        finally
        {
            publicKeyGate.Release();
        }
    }

    private static bool TryReadPublicKey(string body, out string publicKey)
    {
        publicKey = "";
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            publicKey = FirstNonEmpty(
                GetOptionalString(root, "public_key"),
                TryReadNestedString(root, "data", "public_key"));
            return !string.IsNullOrWhiteSpace(publicKey);
        }
        catch (JsonException)
        {
            return false;
        }
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

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var contentLength = 0;
        foreach (var line in headerLines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            headers[name] = value;
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value, out var parsedLength))
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
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            totalRead += read;
        }

        var bodyOffset = headerEnd + 4;
        var body = buffer.AsSpan(bodyOffset, contentLength).ToArray();
        var path = requestLine[1].Split('?', 2)[0];
        return new HttpRequest(requestLine[0], path, headers, body);
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
            "Access-Control-Allow-Headers: Content-Type, Kick-Event-Message-Id, Kick-Event-Subscription-Id, Kick-Event-Signature, Kick-Event-Message-Timestamp, Kick-Event-Type, Kick-Event-Version",
            $"Content-Type: {contentType}; charset=utf-8",
            $"Content-Length: {bodyBytes.Length}",
            "Connection: close",
            "",
            "");
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string GetHeader(IReadOnlyDictionary<string, string> headers, string name)
    {
        return headers.TryGetValue(name, out var value) ? value.Trim() : "";
    }

    private static string GetOptionalString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return "";
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? ""
            : property.ToString().Trim();
    }

    private static string TryReadNestedString(JsonElement element, string objectName, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(objectName, out var nested) &&
            nested.ValueKind == JsonValueKind.Object
            ? GetOptionalString(nested, propertyName)
            : "";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private sealed record HttpRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Body);
}

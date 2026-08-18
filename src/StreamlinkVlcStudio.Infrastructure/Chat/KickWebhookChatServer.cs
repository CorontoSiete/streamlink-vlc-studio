using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Replay;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public sealed class KickWebhookChatServer : IAsyncDisposable
{
    public const int DefaultPort = 39180;
    public const string WebhookPath = "/kick-webhook";
    private const int MaxRequestBytes = 262_144;
    private const int MaximumConcurrentClients = 32;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly KickOfficialChatReplayStore store;
    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly int requestedPort;
    private readonly KickWebhookAuthenticator authenticator;
    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim clientAdmissions = new(MaximumConcurrentClients, MaximumConcurrentClients);
    private readonly ConcurrentDictionary<long, Task> activeClientTasks = new();
    private readonly object lifecycleGate = new();
    private TcpListener? listener;
    private Task? acceptLoop;
    private Task? disposalTask;
    private long nextClientId;
    private int disposed;

    public KickWebhookChatServer(
        KickOfficialChatReplayStore store,
        IAppLogger logger,
        int port = DefaultPort,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null)
    {
        this.store = store;
        this.logger = logger;
        this.httpClient = httpClient ?? HttpClientFactory.CreateDefault();
        ownsHttpClient = httpClient is null;
        var effectiveTimeProvider = timeProvider ?? TimeProvider.System;
        authenticator = new KickWebhookAuthenticator(
            this.httpClient,
            logger,
            effectiveTimeProvider,
            cancellation.Token);
        requestedPort = port is < 0 or > 65_535 ? DefaultPort : port;
    }

    public int ListenerPort { get; private set; } = DefaultPort;

    internal int AvailableClientAdmissionsForTest => clientAdmissions.CurrentCount;

    public string LocalWebhookUrl => $"http://127.0.0.1:{ListenerPort}{WebhookPath}";

    public bool Start()
    {
        lock (lifecycleGate)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return false;
            }

            if (listener is not null)
            {
                return true;
            }

            var nextListener = new TcpListener(IPAddress.Loopback, requestedPort);
            try
            {
                nextListener.Start();
                ListenerPort = ((IPEndPoint)nextListener.LocalEndpoint).Port;
                listener = nextListener;
                acceptLoop = AcceptLoopAsync(cancellation.Token);
                logger.Write(AppLogLevel.Info, "KickWebhook", $"Listening for official Kick chat webhooks at {LocalWebhookUrl}.");
                return true;
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException)
            {
                nextListener.Dispose();
                listener = null;
                logger.Write(AppLogLevel.Warning, "KickWebhook", "Could not start official Kick webhook listener.", ex);
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
        Volatile.Write(ref disposed, 1);
        cancellation.Cancel();
        TcpListener? listenerToStop;
        Task? acceptLoopToWait;
        lock (lifecycleGate)
        {
            listenerToStop = listener;
            listener = null;
            acceptLoopToWait = acceptLoop;
        }

        listenerToStop?.Stop();
        if (acceptLoopToWait is not null)
        {
            try
            {
                await acceptLoopToWait.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        var clients = activeClientTasks.Values.ToArray();
        if (clients.Length > 0)
        {
            try
            {
                await Task.WhenAll(clients).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Individual client failures are logged by ObserveClientAsync.
            }
        }

        clientAdmissions.Dispose();
        cancellation.Dispose();
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var currentListener = listener;
            if (currentListener is null)
            {
                return;
            }

            TcpClient client;
            try
            {
                client = await currentListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            TrackClient(client, cancellationToken);
        }
    }

    private void TrackClient(TcpClient client, CancellationToken cancellationToken)
    {
        var clientId = Interlocked.Increment(ref nextClientId);
        var admitted = clientAdmissions.Wait(0);
        var task = admitted
            ? HandleAdmittedClientAsync(client, cancellationToken)
            : RejectOverloadedClientAsync(client, cancellationToken);
        activeClientTasks[clientId] = task;
        _ = ObserveClientAsync(clientId, task);
    }

    private async Task HandleAdmittedClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            clientAdmissions.Release();
        }
    }

    private static async Task RejectOverloadedClientAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        using var clientLifetime = client;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            _ = await LocalHttpRequestReader
                .ReadWithStatusAsync(client.GetStream(), 4096, timeout.Token)
                .ConfigureAwait(false);
            await WriteResponseAsync(
                client.GetStream(),
                503,
                "Service Unavailable",
                "text/plain",
                "Webhook listener is busy.",
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
        {
        }
    }

    private async Task ObserveClientAsync(long clientId, Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "KickWebhook", "Kick webhook request failed unexpectedly.", ex);
        }
        finally
        {
            activeClientTasks.TryRemove(clientId, out _);
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
            var readResult = await LocalHttpRequestReader
                .ReadWithStatusAsync(stream, MaxRequestBytes, timeout.Token)
                .ConfigureAwait(false);
            if (!readResult.IsSuccess)
            {
                await WriteResponseAsync(
                    stream,
                    readResult.StatusCode,
                    readResult.ReasonPhrase,
                    "text/plain",
                    readResult.Message,
                    timeout.Token).ConfigureAwait(false);
                return;
            }

            var request = readResult.Request!;

            if (!request.Method.Equals("POST", StringComparison.Ordinal) ||
                !request.Path.Equals(WebhookPath, StringComparison.Ordinal))
            {
                await WriteResponseAsync(stream, 404, "Not Found", "text/plain", "Not found.", timeout.Token).ConfigureAwait(false);
                return;
            }

            var authentication = await authenticator.AuthenticateAndReserveAsync(request, timeout.Token).ConfigureAwait(false);
            if (authentication.Result == WebhookAuthenticationResult.Replay)
            {
                logger.Write(AppLogLevel.Warning, "KickWebhook", "Rejected replayed Kick webhook message ID.");
                await WriteResponseAsync(stream, 409, "Conflict", "text/plain", "Duplicate webhook message.", timeout.Token).ConfigureAwait(false);
                return;
            }

            if (authentication.Result != WebhookAuthenticationResult.Valid)
            {
                logger.Write(AppLogLevel.Warning, "KickWebhook", "Rejected Kick webhook with invalid signature.");
                await WriteResponseAsync(stream, 401, "Unauthorized", "text/plain", "Invalid signature.", timeout.Token).ConfigureAwait(false);
                return;
            }

            var reservation = authentication.Reservation;
            var accepted = false;
            try
            {
                var eventType = request.GetHeader("Kick-Event-Type");
                if (!eventType.Equals(KickOfficialChatWebhookParser.ChatMessageSentEventType, StringComparison.OrdinalIgnoreCase))
                {
                    logger.Write(AppLogLevel.Info, "KickWebhook", $"Ignored Kick webhook event type '{eventType}'.");
                    authenticator.Commit(reservation);
                    accepted = true;
                    await WriteResponseAsync(stream, 202, "Accepted", "application/json", """{"ignored":true}""", timeout.Token).ConfigureAwait(false);
                    return;
                }

                string bodyText;
                try
                {
                    bodyText = StrictUtf8.GetString(request.Body);
                }
                catch (DecoderFallbackException)
                {
                    const string invalidEncoding = "Webhook body was not valid UTF-8.";
                    logger.Write(AppLogLevel.Warning, "KickWebhook", invalidEncoding);
                    await WriteResponseAsync(stream, 400, "Bad Request", "text/plain", invalidEncoding, timeout.Token).ConfigureAwait(false);
                    return;
                }

                if (!KickOfficialChatWebhookParser.TryParseChatMessage(bodyText, out var message, out var parseError))
                {
                    logger.Write(AppLogLevel.Warning, "KickWebhook", $"Rejected Kick webhook payload: {parseError}");
                    await WriteResponseAsync(stream, 400, "Bad Request", "text/plain", parseError, timeout.Token).ConfigureAwait(false);
                    return;
                }

                await store.AppendAsync(message, timeout.Token).ConfigureAwait(false);
                authenticator.Commit(reservation);
                accepted = true;
                await WriteResponseAsync(stream, 200, "OK", "application/json", """{"ok":true}""", timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                if (!accepted)
                {
                    authenticator.Release(reservation);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or SocketException or CryptographicException or FormatException)
        {
            logger.Write(AppLogLevel.Warning, "KickWebhook", "Kick webhook request failed.", ex);
        }
    }

    internal async Task<WebhookAuthenticationResult> AuthenticateRequestAsync(
        LocalHttpRequest request,
        CancellationToken cancellationToken)
        => (await authenticator.AuthenticateAndReserveAsync(request, cancellationToken).ConfigureAwait(false)).Result;

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

    internal enum WebhookAuthenticationResult
    {
        Invalid,
        Valid,
        Replay
    }

}

using System.Net.WebSockets;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

internal sealed class TwitchPredictionEventSubClient : IAsyncDisposable
{
    private static readonly Uri DefaultWebSocketUri = new("wss://eventsub.wss.twitch.tv/ws");
    private static readonly TimeSpan WelcomeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumKeepaliveTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(30);
    private static readonly string[] PredictionSubscriptionTypes =
    [
        "channel.prediction.begin",
        "channel.prediction.progress",
        "channel.prediction.lock",
        "channel.prediction.end"
    ];

    private readonly TwitchPredictionApiClient apiClient;
    private readonly IAppLogger logger;
    private readonly string accessToken;
    private readonly string clientId;
    private readonly string broadcasterId;
    private readonly Action<TwitchPrediction> predictionReceived;
    private readonly Action<string> statusChanged;
    private readonly Func<CancellationToken, Task>? runOverride;
    private readonly TwitchPredictionEventSubParser parser = new();
    private readonly object lifecycleGate = new();
    private CancellationTokenSource? cancellation;
    private Task? runTask;
    private Task? disposalTask;
    private ClientWebSocket? webSocket;
    private bool disposed;

    public TwitchPredictionEventSubClient(
        TwitchPredictionApiClient apiClient,
        IAppLogger logger,
        string accessToken,
        string clientId,
        string broadcasterId,
        Action<TwitchPrediction> predictionReceived,
        Action<string> statusChanged,
        Func<CancellationToken, Task>? runOverride = null)
    {
        this.apiClient = apiClient;
        this.logger = logger;
        this.accessToken = accessToken;
        this.clientId = clientId;
        this.broadcasterId = broadcasterId;
        this.predictionReceived = predictionReceived;
        this.statusChanged = statusChanged;
        this.runOverride = runOverride;
    }

    public void Start()
    {
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (runTask is not null)
            {
                return;
            }

            var nextCancellation = new CancellationTokenSource();
            cancellation = nextCancellation;
            var runner = runOverride ?? RunAsync;
            runTask = Task.Run(() => runner(nextCancellation.Token), CancellationToken.None);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycleGate)
        {
            if (disposalTask is not null)
            {
                return new ValueTask(disposalTask);
            }

            disposed = true;
            var cancellationToDispose = cancellation;
            var runTaskToWait = runTask;
            var socketToAbort = webSocket;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disposalTask = completion.Task;
            _ = CompleteDisposalAsync(
                cancellationToDispose,
                runTaskToWait,
                socketToAbort,
                completion);
            return new ValueTask(disposalTask);
        }
    }

    private async Task CompleteDisposalAsync(
        CancellationTokenSource? cancellationToDispose,
        Task? runTaskToWait,
        ClientWebSocket? socketToAbort,
        TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync(cancellationToDispose, runTaskToWait, socketToAbort)
                .ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private async Task DisposeCoreAsync(
        CancellationTokenSource? cancellationToDispose,
        Task? runTaskToWait,
        ClientWebSocket? socketToAbort)
    {
        cancellationToDispose?.Cancel();
        socketToAbort?.Abort();

        if (runTaskToWait is not null)
        {
            try
            {
                await runTaskToWait.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cancellationToDispose?.Dispose();
        lock (lifecycleGate)
        {
            if (ReferenceEquals(runTask, runTaskToWait))
            {
                runTask = null;
            }

            if (ReferenceEquals(cancellation, cancellationToDispose))
            {
                cancellation = null;
            }

            if (ReferenceEquals(webSocket, socketToAbort))
            {
                webSocket = null;
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var reconnectDelay = InitialReconnectDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await RunConnectionAsync(cancellationToken).ConfigureAwait(false);
                if (result.Stop)
                {
                    return;
                }

                reconnectDelay = result.WasConnected ? InitialReconnectDelay : NextReconnectDelay(reconnectDelay);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.Write(AppLogLevel.Warning, "TwitchEventSub", "Twitch prediction EventSub WebSocket disconnected; reconnecting.", ex);
                RaiseStatusChanged($"Twitch prediction updates disconnected: {ex.Message}");
                reconnectDelay = NextReconnectDelay(reconnectDelay);
            }

            try
            {
                await Task.Delay(reconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<ConnectionResult> RunConnectionAsync(CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        if (!TrySetActiveSocket(socket))
        {
            socket.Dispose();
            return new ConnectionResult(Stop: true, WasConnected: false);
        }

        var connected = false;
        int? keepaliveTimeoutSeconds = null;
        try
        {
            await socket.ConnectAsync(DefaultWebSocketUri, cancellationToken).ConfigureAwait(false);

            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                string? json;
                using (var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    receiveTimeout.CancelAfter(GetReceiveTimeout(keepaliveTimeoutSeconds));
                    try
                    {
                        json = await ReceiveTextMessageAsync(socket, receiveTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        RaiseStatusChanged("Twitch prediction EventSub connection timed out; reconnecting.");
                        return new ConnectionResult(Stop: false, WasConnected: connected);
                    }
                }

                if (json is null)
                {
                    RaiseStatusChanged("Twitch prediction EventSub WebSocket disconnected; reconnecting.");
                    return new ConnectionResult(Stop: false, WasConnected: connected);
                }

                if (!parser.TryParse(json, out var message))
                {
                    continue;
                }

                if (message.SessionId is { Length: > 0 } sessionId)
                {
                    keepaliveTimeoutSeconds = message.KeepaliveTimeoutSeconds;
                    if (!connected)
                    {
                        await SubscribeAsync(sessionId, cancellationToken).ConfigureAwait(false);
                    }

                    connected = true;
                    RaiseStatusChanged("Twitch prediction updates connected.");
                    continue;
                }

                if (message.ReconnectUrl is { Length: > 0 } reconnectUrl &&
                    TryCreateReconnectUri(reconnectUrl, out var reconnectUri))
                {
                    RaiseStatusChanged("Twitch requested a prediction EventSub reconnect.");
                    var handoff = await HandoffReconnectAsync(socket, reconnectUri, cancellationToken).ConfigureAwait(false);
                    if (handoff.Stop)
                    {
                        return new ConnectionResult(Stop: true, WasConnected: connected);
                    }

                    var previousSocket = socket;
                    socket = handoff.Socket!;
                    if (!TryReplaceActiveSocket(previousSocket, socket))
                    {
                        socket.Dispose();
                        return new ConnectionResult(Stop: true, WasConnected: connected);
                    }

                    previousSocket.Dispose();
                    keepaliveTimeoutSeconds = handoff.KeepaliveTimeoutSeconds;
                    connected = true;
                    RaiseStatusChanged("Twitch prediction updates connected.");
                    continue;
                }

                if (message.RevocationStatus is { Length: > 0 } revocationStatus)
                {
                    RaiseStatusChanged($"Twitch prediction EventSub subscription revoked: {revocationStatus}.");
                    return new ConnectionResult(Stop: true, WasConnected: connected);
                }

                if (message is { IsDuplicate: false, Prediction: { } prediction })
                {
                    RaisePredictionReceived(prediction);
                }
            }

            return new ConnectionResult(Stop: false, WasConnected: connected);
        }
        finally
        {
            ClearActiveSocket(socket);

            socket.Dispose();
        }
    }

    private bool TrySetActiveSocket(ClientWebSocket socket)
    {
        lock (lifecycleGate)
        {
            if (disposed)
            {
                return false;
            }

            webSocket = socket;
            return true;
        }
    }

    private bool TryReplaceActiveSocket(ClientWebSocket previous, ClientWebSocket replacement)
    {
        lock (lifecycleGate)
        {
            if (disposed || !ReferenceEquals(webSocket, previous))
            {
                return false;
            }

            webSocket = replacement;
            return true;
        }
    }

    private void ClearActiveSocket(ClientWebSocket socket)
    {
        lock (lifecycleGate)
        {
            if (ReferenceEquals(webSocket, socket))
            {
                webSocket = null;
            }
        }
    }

    private async Task<ReconnectHandoffResult> HandoffReconnectAsync(
        ClientWebSocket currentSocket,
        Uri reconnectUri,
        CancellationToken cancellationToken)
    {
        using var handoffCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reconnectTask = OpenReconnectSocketAsync(reconnectUri, handoffCancellation.Token);
        var successfulHandoff = false;
        try
        {
            while (!reconnectTask.IsCompleted && currentSocket.State == WebSocketState.Open)
            {
                using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var receiveTask = ReceiveTextMessageAsync(currentSocket, receiveCancellation.Token);
                var completed = await Task.WhenAny(reconnectTask, receiveTask).ConfigureAwait(false);
                if (completed == reconnectTask)
                {
                    OpenedReconnectSocket opened;
                    try
                    {
                        opened = await reconnectTask.ConfigureAwait(false);
                    }
                    catch
                    {
                        receiveCancellation.Cancel();
                        await ObserveOverlappedReceiveAsync(receiveTask).ConfigureAwait(false);
                        throw;
                    }

                    receiveCancellation.Cancel();
                    var finalOldMessage = await ObserveOverlappedReceiveAsync(receiveTask).ConfigureAwait(false);
                    if (ShouldStopAfterHandoffMessage(finalOldMessage))
                    {
                        return ReconnectHandoffResult.Stopped;
                    }

                    var result = ValidateReconnectWelcome(opened);
                    successfulHandoff = true;
                    return result;
                }

                string? json;
                try
                {
                    json = await receiveTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
                {
                    break;
                }

                if (json is null)
                {
                    break;
                }

                if (ShouldStopAfterHandoffMessage(json))
                {
                    return ReconnectHandoffResult.Stopped;
                }
            }

            if (!reconnectTask.IsCompleted)
            {
                // The old socket can disappear before the reconnect attempt finishes.
                // Cancel the pending connect so handoff cannot wait indefinitely.
                handoffCancellation.Cancel();
            }

            var completedHandoff = ValidateReconnectWelcome(await reconnectTask.ConfigureAwait(false));
            successfulHandoff = true;
            return completedHandoff;
        }
        finally
        {
            if (!successfulHandoff)
            {
                handoffCancellation.Cancel();
                await DisposeReconnectSocketAsync(reconnectTask).ConfigureAwait(false);
            }
        }
    }

    private bool ShouldStopAfterHandoffMessage(string? json)
    {
        if (json is null || !parser.TryParse(json, out var message))
        {
            return false;
        }

        if (message.RevocationStatus is { Length: > 0 } revocationStatus)
        {
            RaiseStatusChanged($"Twitch prediction EventSub subscription revoked: {revocationStatus}.");
            return true;
        }

        if (message is { IsDuplicate: false, Prediction: { } prediction })
        {
            RaisePredictionReceived(prediction);
        }

        return false;
    }

    private ReconnectHandoffResult ValidateReconnectWelcome(OpenedReconnectSocket opened)
    {
        if (!parser.TryParse(opened.WelcomeJson, out var welcome) ||
            string.IsNullOrWhiteSpace(welcome.SessionId))
        {
            opened.Socket.Dispose();
            throw new InvalidDataException("Twitch prediction EventSub reconnect did not provide a valid welcome message.");
        }

        return new ReconnectHandoffResult(opened.Socket, welcome.KeepaliveTimeoutSeconds, Stop: false);
    }

    private static async Task<OpenedReconnectSocket> OpenReconnectSocketAsync(
        Uri reconnectUri,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(reconnectUri, cancellationToken).ConfigureAwait(false);
            using var welcomeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            welcomeTimeout.CancelAfter(WelcomeTimeout);

            string? welcomeJson;
            try
            {
                welcomeJson = await ReceiveTextMessageAsync(socket, welcomeTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out waiting for the Twitch prediction EventSub reconnect welcome.");
            }

            if (welcomeJson is null)
            {
                throw new WebSocketException("Twitch closed the prediction EventSub reconnect before its welcome message.");
            }

            return new OpenedReconnectSocket(socket, welcomeJson);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<string?> ObserveOverlappedReceiveAsync(Task<string?> receiveTask)
    {
        try
        {
            return await receiveTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static async Task DisposeReconnectSocketAsync(Task<OpenedReconnectSocket> reconnectTask)
    {
        try
        {
            var opened = await reconnectTask.ConfigureAwait(false);
            opened.Socket.Dispose();
        }
        catch (Exception)
        {
            // Cleanup after a revocation or caller cancellation must still observe a failed connect.
        }
    }

    private async Task SubscribeAsync(string sessionId, CancellationToken cancellationToken)
    {
        foreach (var subscriptionType in PredictionSubscriptionTypes)
        {
            await apiClient.CreateEventSubWebSocketSubscriptionAsync(
                subscriptionType,
                broadcasterId,
                sessionId,
                accessToken,
                clientId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string?> ReceiveTextMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        => await BoundedWebSocketTextReader.ReadAsync(socket, cancellationToken).ConfigureAwait(false);

    internal static bool TryCreateReconnectUri(string value, out Uri reconnectUri)
    {
        reconnectUri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            !candidate.Scheme.Equals(Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.IdnHost, DefaultWebSocketUri.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            candidate.Port != DefaultWebSocketUri.Port ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Fragment))
        {
            return false;
        }

        reconnectUri = candidate;
        return true;
    }

    private static TimeSpan GetReceiveTimeout(int? keepaliveTimeoutSeconds)
    {
        if (keepaliveTimeoutSeconds is not > 0)
        {
            return WelcomeTimeout;
        }

        var requestedSeconds = (long)keepaliveTimeoutSeconds.Value + 1;
        return TimeSpan.FromSeconds(Math.Min(requestedSeconds, (long)MaximumKeepaliveTimeout.TotalSeconds));
    }

    private static TimeSpan NextReconnectDelay(TimeSpan currentDelay)
    {
        return TimeSpan.FromSeconds(Math.Min(currentDelay.TotalSeconds * 2, MaximumReconnectDelay.TotalSeconds));
    }

    private void RaisePredictionReceived(TwitchPrediction prediction)
    {
        SafeEventDispatcher.Invoke(
            predictionReceived,
            prediction,
            logger,
            "TwitchEventSub",
            nameof(predictionReceived));
    }

    private void RaiseStatusChanged(string message)
    {
        SafeEventDispatcher.Invoke(
            statusChanged,
            message,
            logger,
            "TwitchEventSub",
            nameof(statusChanged));
    }

    private sealed record ConnectionResult(bool Stop, bool WasConnected);

    private sealed record OpenedReconnectSocket(ClientWebSocket Socket, string WelcomeJson);

    private sealed record ReconnectHandoffResult(ClientWebSocket? Socket, int? KeepaliveTimeoutSeconds, bool Stop)
    {
        public static ReconnectHandoffResult Stopped { get; } = new(null, null, Stop: true);
    }
}

using System.Net.WebSockets;
using System.Text;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

internal sealed class TwitchPredictionEventSubClient : IAsyncDisposable
{
    private static readonly Uri DefaultWebSocketUri = new("wss://eventsub.wss.twitch.tv/ws");
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
    private CancellationTokenSource? cancellation;
    private Task? runTask;
    private ClientWebSocket? webSocket;

    public TwitchPredictionEventSubClient(
        TwitchPredictionApiClient apiClient,
        IAppLogger logger,
        string accessToken,
        string clientId,
        string broadcasterId,
        Action<TwitchPrediction> predictionReceived,
        Action<string> statusChanged)
    {
        this.apiClient = apiClient;
        this.logger = logger;
        this.accessToken = accessToken;
        this.clientId = clientId;
        this.broadcasterId = broadcasterId;
        this.predictionReceived = predictionReceived;
        this.statusChanged = statusChanged;
    }

    public void Start()
    {
        if (runTask is not null)
        {
            return;
        }

        cancellation = new CancellationTokenSource();
        runTask = Task.Run(() => RunAsync(cancellation.Token));
    }

    public async ValueTask DisposeAsync()
    {
        cancellation?.Cancel();
        var socket = webSocket;
        if (socket is not null)
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopping", CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
            }
        }

        if (runTask is not null)
        {
            try
            {
                await runTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        webSocket?.Dispose();
        cancellation?.Dispose();
        webSocket = null;
        runTask = null;
        cancellation = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var uri = DefaultWebSocketUri;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var reconnectUri = await RunConnectionAsync(uri, cancellationToken).ConfigureAwait(false);
                if (reconnectUri is null)
                {
                    return;
                }

                uri = reconnectUri;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.Write(AppLogLevel.Warning, "TwitchEventSub", "Twitch prediction EventSub WebSocket stopped.", ex);
                statusChanged($"Twitch prediction updates unavailable: {ex.Message}");
                return;
            }
        }
    }

    private async Task<Uri?> RunConnectionAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        webSocket = socket;
        await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);

        var parser = new TwitchPredictionEventSubParser();
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var json = await ReceiveTextMessageAsync(socket, cancellationToken).ConfigureAwait(false);
            if (json is null)
            {
                statusChanged("Twitch prediction EventSub WebSocket disconnected.");
                return null;
            }

            if (!parser.TryParse(json, out var message))
            {
                continue;
            }

            if (message.SessionId is { Length: > 0 } sessionId)
            {
                await SubscribeAsync(sessionId, cancellationToken).ConfigureAwait(false);
                statusChanged("Twitch prediction updates connected.");
                continue;
            }

            if (message.ReconnectUrl is { Length: > 0 } reconnectUrl &&
                Uri.TryCreate(reconnectUrl, UriKind.Absolute, out var reconnectUri))
            {
                statusChanged("Twitch requested a prediction EventSub reconnect.");
                return reconnectUri;
            }

            if (message.RevocationStatus is { Length: > 0 } revocationStatus)
            {
                statusChanged($"Twitch prediction EventSub subscription revoked: {revocationStatus}.");
                return null;
            }

            if (message is { IsDuplicate: false, Prediction: { } prediction })
            {
                predictionReceived(prediction);
            }
        }

        return null;
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
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

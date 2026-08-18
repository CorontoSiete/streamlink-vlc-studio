using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Core.Text;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Limits;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public sealed class TwitchChatClient : IChatClient, ITwitchPredictionClient
{
    private readonly ChatSettings settings;
    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object disposalGate = new();
    private readonly object predictionEventSubGate = new();
    private readonly object predictionRequestLifecycleGate = new();
    private readonly CancellationTokenSource predictionLifetimeCancellation = new();
    private readonly SemaphoreSlim writerLock = new(1, 1);
    private readonly SemaphoreSlim predictionRequestLock = new(1, 1);
    private readonly TwitchPredictionApiClient predictionApiClient;
    private TcpClient? tcpClient;
    private SslStream? sslStream;
    private BoundedUtf8LineReader? reader;
    private StreamWriter? writer;
    private CancellationTokenSource? readCancellation;
    private Task? readTask;
    private LiveChatConnectionSupervisor? connectionSupervisor;
    private string? connectedChannel;
    private string? predictionBroadcasterId;
    private string? predictionAccessToken;
    private string? predictionClientId;
    private TwitchPredictionEventSubClient? predictionEventSubClient;
    private TaskCompletionSource predictionRequestsDrained = CreateCompletedTaskSource();
    private int activePredictionRequests;
    private Task? disposalTask;
    private bool canSendMessages;
    private bool disposed;

    public TwitchChatClient(ChatSettings settings, IAppLogger logger, HttpClient? httpClient = null)
    {
        this.settings = settings;
        this.logger = logger;
        this.httpClient = httpClient ?? HttpClientFactory.CreateDefault();
        ownsHttpClient = httpClient is null;
        if (!this.httpClient.DefaultRequestHeaders.UserAgent.Any(value =>
                string.Equals(value.ToString(), HttpClientFactory.ApplicationUserAgent, StringComparison.OrdinalIgnoreCase)))
        {
            this.httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(HttpClientFactory.ApplicationUserAgent);
        }
        predictionApiClient = new TwitchPredictionApiClient(this.httpClient);
        PredictionAccess = TwitchPredictionAccessState.Pending;
    }

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<TwitchPrediction>? PredictionReceived;
    public event EventHandler<TwitchPredictionAccessState>? PredictionAccessChanged;
    public string? CurrentUsername { get; private set; }
    public TwitchPredictionAccessState PredictionAccess { get; private set; }

    public async Task ConnectAsync(StreamTarget target, CancellationToken cancellationToken = default)
    {
        lock (disposalGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
            }

            await StopConnectionSupervisorCoreAsync().ConfigureAwait(false);
            await DisconnectCoreAsync().ConfigureAwait(false);
            var supervisor = new LiveChatConnectionSupervisor(
                logger,
                "TwitchChat",
                RaiseStatusChanged);
            connectionSupervisor = supervisor;
            supervisor.Start(token => ReconnectCoreAsync(supervisor, target, token));
            try
            {
                await ConnectCoreAsync(target, cancellationToken, supervisor).ConfigureAwait(false);
            }
            catch
            {
                // Resolve/handshake failures can occur after the websocket and
                // cancellation sources have been created. Clean them up before
                // returning the failure to the tab.
                try
                {
                    await StopConnectionSupervisorCoreAsync().ConfigureAwait(false);
                    await DisconnectCoreAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    logger.Write(AppLogLevel.Warning, "TwitchChat", "Twitch chat cleanup failed after a connection error.", cleanupException);
                }

                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task ConnectCoreAsync(
        StreamTarget target,
        CancellationToken cancellationToken,
        LiveChatConnectionSupervisor supervisor)
    {
        RaiseStatusChanged("Connecting to Twitch chat...");

        tcpClient = new TcpClient();
        await tcpClient.ConnectAsync("irc.chat.twitch.tv", 6697, cancellationToken);
        sslStream = new SslStream(tcpClient.GetStream(), leaveInnerStreamOpen: false);
        await sslStream.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions { TargetHost = "irc.chat.twitch.tv" },
            cancellationToken);

        var connectedReader = new BoundedUtf8LineReader(sslStream);
        var connectedWriter = new StreamWriter(sslStream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        reader = connectedReader;
        writer = connectedWriter;

        var token = TwitchOAuthService.NormalizeOAuthToken(settings.TwitchOAuthToken);
        var nick = $"justinfan{Random.Shared.Next(10000, 999999)}";
        var pass = "SCHMOOPIIE";
        canSendMessages = false;
        CurrentUsername = null;
        SetPredictionAccess(TwitchPredictionAccessState.Pending);

        TwitchTokenInfo? tokenInfo = null;
        if (!string.IsNullOrWhiteSpace(token))
        {
            try
            {
                tokenInfo = await TwitchOAuthService.ValidateTokenAsync(httpClient, token, cancellationToken);
                if (TwitchClientIdResolver.WarnIfConfiguredMismatch(
                        settings,
                        tokenInfo.ClientId,
                        logger,
                        "TwitchChat"))
                {
                    RaiseStatusChanged("Configured Twitch Client ID does not match this OAuth token; using the token's validated Client ID.");
                }
                var configuredUsername = settings.TwitchUsername.Trim();
                if (!string.IsNullOrWhiteSpace(configuredUsername) &&
                    !string.Equals(configuredUsername, tokenInfo.Login, StringComparison.OrdinalIgnoreCase))
                {
                    RaiseStatusChanged($"Twitch username '{configuredUsername}' does not match the token login '{tokenInfo.Login}'; using the token login.");
                }

                if (tokenInfo.CanReadChat || tokenInfo.CanWriteChat)
                {
                    nick = tokenInfo.Login;
                    pass = $"oauth:{token}";
                    canSendMessages = tokenInfo.CanWriteChat;
                    CurrentUsername = nick;
                    RaiseStatusChanged($"Using Twitch OAuth token for {nick}.");

                    if (!tokenInfo.CanReadChat)
                    {
                        RaiseStatusChanged("Twitch token is missing chat:read; receiving chat may be limited.");
                    }

                    if (!tokenInfo.CanWriteChat)
                    {
                        RaiseStatusChanged("Twitch token is valid but missing IRC send scope. Use Connect Twitch or reauthorize with chat:edit to type in chat.");
                    }
                }
                else
                {
                    RaiseStatusChanged("Twitch token is missing chat:read and chat:edit; connecting to Twitch chat read-only.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Write(AppLogLevel.Warning, "TwitchChat", "Twitch token validation failed; connecting read-only.", ex);
                RaiseStatusChanged($"Twitch token validation failed: {ex.Message}. Connecting read-only.");
                SetPredictionAccess(new TwitchPredictionAccessState(
                    true,
                    false,
                    $"Twitch prediction controls unavailable: {ex.Message}"));
            }
        }
        else
        {
            SetPredictionAccess(new TwitchPredictionAccessState(
                true,
                false,
                "Reconnect Twitch with channel:manage:predictions to manage predictions."));
        }

        var channel = target.Channel.ToLowerInvariant();
        connectedChannel = channel;
        await WriteIrcLineAsync("CAP REQ :twitch.tv/tags twitch.tv/commands", cancellationToken);
        await WriteIrcLineAsync($"PASS {pass}", cancellationToken);
        await WriteIrcLineAsync($"NICK {nick}", cancellationToken);
        await WriteIrcLineAsync($"JOIN #{channel}", cancellationToken);
        await AwaitIrcHandshakeAsync(
            channel,
            connectedReader,
            connectedWriter,
            cancellationToken).ConfigureAwait(false);

        // The caller's token only governs the connection handshake. Once connected, the read
        // loop must live until DisconnectAsync cancels its own lifecycle source; linking this to
        // the startup token disconnects healthy chat sessions when a tab's start operation ends.
        var connectedReadCancellation = new CancellationTokenSource();
        readCancellation = connectedReadCancellation;
        readTask = Task.Run(
            () => ReadLoopAsync(
                channel,
                connectedReader,
                connectedWriter,
                supervisor,
                connectedReadCancellation.Token),
            CancellationToken.None);
        RaiseStatusChanged(canSendMessages ? "Twitch chat connected with send access." : "Twitch chat connected read-only.");

        if (tokenInfo is not null)
        {
            await InitializePredictionsAsync(target, token, tokenInfo, cancellationToken);
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopConnectionSupervisorCoreAsync().ConfigureAwait(false);
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task DisconnectCoreAsync()
    {
        readCancellation?.Cancel();
        await StopPredictionEventSubAsync();
        if (readTask is not null)
        {
            try
            {
                await readTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
            }
        }

        writer?.Dispose();
        reader?.Dispose();
        sslStream?.Dispose();
        tcpClient?.Dispose();
        readCancellation?.Dispose();

        writer = null;
        reader = null;
        sslStream = null;
        tcpClient = null;
        readTask = null;
        readCancellation = null;
        connectedChannel = null;
        predictionBroadcasterId = null;
        predictionAccessToken = null;
        predictionClientId = null;
        canSendMessages = false;
        CurrentUsername = null;
        if (PredictionAccess != TwitchPredictionAccessState.Pending)
        {
            SetPredictionAccess(TwitchPredictionAccessState.Pending);
        }
    }

    private async Task ReconnectCoreAsync(
        LiveChatConnectionSupervisor supervisor,
        StreamTarget target,
        CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
            }

            if (!ReferenceEquals(connectionSupervisor, supervisor))
            {
                throw new OperationCanceledException("The Twitch chat connection was replaced.", cancellationToken);
            }

            await DisconnectCoreAsync().ConfigureAwait(false);
            try
            {
                await ConnectCoreAsync(target, cancellationToken, supervisor).ConfigureAwait(false);
            }
            catch
            {
                await DisconnectCoreAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task StopConnectionSupervisorCoreAsync()
    {
        var supervisor = connectionSupervisor;
        connectionSupervisor = null;
        if (supervisor is not null)
        {
            await supervisor.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        var sanitized = SanitizeMessage(message);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return;
        }

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
            }

            if (!canSendMessages)
            {
                throw new InvalidOperationException("Connect Twitch in Settings with a user access token that includes chat:edit. A Twitch Client ID alone cannot send chat.");
            }

            var connectedWriter = writer;
            var channel = connectedChannel;
            if (connectedWriter is null || string.IsNullOrWhiteSpace(channel))
            {
                throw new InvalidOperationException("Twitch chat is not connected.");
            }

            sanitized = TruncateIrcMessage(channel, sanitized);
            if (sanitized.Length == 0)
            {
                return;
            }

            await WriteIrcLineAsync(connectedWriter, $"PRIVMSG #{channel} :{sanitized}", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            disposalTask ??= DisposeCoreAsync();
            return new ValueTask(disposalTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        lock (disposalGate)
        {
            disposed = true;
        }

        Task predictionRequestsTask;
        lock (predictionRequestLifecycleGate)
        {
            try
            {
                predictionLifetimeCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            predictionRequestsTask = predictionRequestsDrained.Task;
        }

        await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopConnectionSupervisorCoreAsync().ConfigureAwait(false);
            await DisconnectCoreAsync().ConfigureAwait(false);
            await predictionRequestsTask.ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
            predictionLifetimeCancellation.Dispose();
            if (ownsHttpClient)
            {
                httpClient.Dispose();
            }
        }
    }

    public Task<TwitchPrediction> CreatePredictionAsync(
        TwitchPredictionCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        return RunPredictionRequestAsync((context, requestToken) => predictionApiClient.CreatePredictionAsync(
            context.BroadcasterId,
            request,
            context.AccessToken,
            context.ClientId,
            requestToken), cancellationToken, RaisePredictionReceived);
    }

    public Task<TwitchPrediction> LockPredictionAsync(string predictionId, CancellationToken cancellationToken = default)
    {
        return RunPredictionRequestAsync((context, requestToken) => predictionApiClient.LockPredictionAsync(
            context.BroadcasterId,
            predictionId,
            context.AccessToken,
            context.ClientId,
            requestToken), cancellationToken, RaisePredictionReceived);
    }

    public Task<TwitchPrediction> CancelPredictionAsync(string predictionId, CancellationToken cancellationToken = default)
    {
        return RunPredictionRequestAsync((context, requestToken) => predictionApiClient.CancelPredictionAsync(
            context.BroadcasterId,
            predictionId,
            context.AccessToken,
            context.ClientId,
            requestToken), cancellationToken, RaisePredictionReceived);
    }

    public Task<TwitchPrediction> ResolvePredictionAsync(
        string predictionId,
        string winningOutcomeId,
        CancellationToken cancellationToken = default)
    {
        return RunPredictionRequestAsync((context, requestToken) => predictionApiClient.ResolvePredictionAsync(
            context.BroadcasterId,
            predictionId,
            winningOutcomeId,
            context.AccessToken,
            context.ClientId,
            requestToken), cancellationToken, RaisePredictionReceived);
    }

    private async Task<TPrediction> RunPredictionRequestAsync<TPrediction>(
        Func<PredictionContext, CancellationToken, Task<TPrediction>> request,
        CancellationToken cancellationToken,
        Action<TPrediction>? completed)
    {
        var requestCancellation = BeginPredictionRequest(cancellationToken);
        var lockAcquired = false;
        try
        {
            await predictionRequestLock.WaitAsync(requestCancellation.Token).ConfigureAwait(false);
            lockAcquired = true;
            ThrowIfDisposed();
            var prediction = await request(GetPredictionContext(), requestCancellation.Token).ConfigureAwait(false);
            completed?.Invoke(prediction);
            return prediction;
        }
        finally
        {
            if (lockAcquired)
            {
                predictionRequestLock.Release();
            }

            CompletePredictionRequest();
            requestCancellation.Dispose();
        }
    }

    private CancellationTokenSource BeginPredictionRequest(CancellationToken cancellationToken)
    {
        lock (predictionRequestLifecycleGate)
        {
            ThrowIfDisposed();
            var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                predictionLifetimeCancellation.Token);
            if (activePredictionRequests++ == 0)
            {
                predictionRequestsDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return requestCancellation;
        }
    }

    private void CompletePredictionRequest()
    {
        lock (predictionRequestLifecycleGate)
        {
            activePredictionRequests--;
            if (activePredictionRequests == 0)
            {
                predictionRequestsDrained.TrySetResult();
            }
        }
    }

    private async Task InitializePredictionsAsync(
        StreamTarget target,
        string token,
        TwitchTokenInfo tokenInfo,
        CancellationToken cancellationToken)
    {
        await StopPredictionEventSubAsync();
        predictionBroadcasterId = null;
        predictionAccessToken = null;
        predictionClientId = null;

        if (!tokenInfo.CanManagePredictions)
        {
            SetPredictionAccess(new TwitchPredictionAccessState(
                true,
                false,
                "Reconnect Twitch to grant channel:manage:predictions before managing predictions.",
                TokenUserId: tokenInfo.UserId));
            return;
        }

        var clientId = tokenInfo.ClientId.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            SetPredictionAccess(new TwitchPredictionAccessState(
                true,
                false,
                "Twitch prediction controls need a Twitch Client ID that matches the OAuth token.",
                TokenUserId: tokenInfo.UserId));
            return;
        }

        TwitchUserInfo? broadcaster;
        try
        {
            broadcaster = !string.IsNullOrWhiteSpace(target.BroadcasterId)
                ? new TwitchUserInfo(target.BroadcasterId.Trim(), target.Channel, target.Channel)
                : await predictionApiClient.ResolveUserByLoginAsync(target.Channel, token, clientId, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Warning, "TwitchPredictions", $"Could not resolve Twitch broadcaster ID for {target.DisplayName}.", ex);
            SetPredictionAccess(new TwitchPredictionAccessState(
                true,
                false,
                $"Twitch prediction controls unavailable: {ex.Message}",
                TokenUserId: tokenInfo.UserId));
            return;
        }

        if (broadcaster is null || string.IsNullOrWhiteSpace(broadcaster.Id))
        {
            SetPredictionAccess(new TwitchPredictionAccessState(
                true,
                false,
                "Twitch prediction controls could not resolve this channel's broadcaster ID.",
                TokenUserId: tokenInfo.UserId));
            return;
        }

        if (!IsCurrentChatConnection(target.Channel))
        {
            return;
        }

        if (!string.Equals(broadcaster.Id, tokenInfo.UserId, StringComparison.Ordinal))
        {
            SetPredictionAccess(new TwitchPredictionAccessState(
                true,
                false,
                "Prediction controls are enabled only when the connected Twitch token owns this channel.",
                broadcaster.Id,
                FirstNonEmpty(broadcaster.Login, target.Channel),
                tokenInfo.UserId));
            return;
        }

        predictionBroadcasterId = broadcaster.Id;
        predictionAccessToken = token;
        predictionClientId = clientId;
        SetPredictionAccess(new TwitchPredictionAccessState(
            true,
            true,
            $"Prediction controls enabled for {FirstNonEmpty(broadcaster.DisplayName, broadcaster.Login, target.Channel)}.",
            broadcaster.Id,
            FirstNonEmpty(broadcaster.Login, target.Channel),
            tokenInfo.UserId));

        try
        {
            var currentPrediction = await predictionApiClient.GetLatestPredictionAsync(
                broadcaster.Id,
                token,
                clientId,
                cancellationToken).ConfigureAwait(false);
            if (!IsCurrentChatConnection(target.Channel))
            {
                return;
            }

            if (currentPrediction is { IsOpen: true })
            {
                RaisePredictionReceived(currentPrediction);
            }

            var eventSubClient = new TwitchPredictionEventSubClient(
                predictionApiClient,
                logger,
                token,
                clientId,
                broadcaster.Id,
                RaisePredictionReceived,
                RaiseStatusChanged);
            var keepEventSubClient = false;
            lock (predictionEventSubGate)
            {
                if (IsCurrentChatConnection(target.Channel))
                {
                    eventSubClient.Start();
                    predictionEventSubClient = eventSubClient;
                    keepEventSubClient = true;
                }
            }

            if (!keepEventSubClient)
            {
                await eventSubClient.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Warning, "TwitchPredictions", $"Twitch prediction setup failed for {target.DisplayName}.", ex);
            RaiseStatusChanged($"Twitch prediction updates unavailable: {ex.Message}");
        }
    }

    private void SetPredictionAccess(TwitchPredictionAccessState access)
    {
        PredictionAccess = access;
        SafeEventDispatcher.Invoke(
            PredictionAccessChanged,
            this,
            access,
            logger,
            "TwitchPredictions",
            nameof(PredictionAccessChanged));
    }

    private async Task StopPredictionEventSubAsync()
    {
        TwitchPredictionEventSubClient? eventSubClient;
        lock (predictionEventSubGate)
        {
            eventSubClient = predictionEventSubClient;
            predictionEventSubClient = null;
        }

        if (eventSubClient is not null)
        {
            await eventSubClient.DisposeAsync();
        }
    }

    private bool IsCurrentChatConnection(string channel)
    {
        return string.Equals(connectedChannel, channel, StringComparison.OrdinalIgnoreCase) &&
            readCancellation is { IsCancellationRequested: false };
    }

    private PredictionContext GetPredictionContext()
    {
        if (!PredictionAccess.CanManage ||
            string.IsNullOrWhiteSpace(predictionBroadcasterId) ||
            string.IsNullOrWhiteSpace(predictionAccessToken) ||
            string.IsNullOrWhiteSpace(predictionClientId))
        {
            throw new InvalidOperationException(PredictionAccess.Message);
        }

        return new PredictionContext(predictionBroadcasterId, predictionAccessToken, predictionClientId);
    }

    private async Task ReadLoopAsync(
        string channel,
        BoundedUtf8LineReader connectedReader,
        StreamWriter connectedWriter,
        LiveChatConnectionSupervisor supervisor,
        CancellationToken cancellationToken)
    {
        var connectedAt = Stopwatch.GetTimestamp();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await connectedReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    RaiseStatusChanged("Twitch chat disconnected by the server.");
                    break;
                }

                if (!ReferenceEquals(connectedReader, reader) ||
                    !ReferenceEquals(connectedWriter, writer))
                {
                    break;
                }

                if (!TwitchIrcProtocol.TryReadCommand(line, out var command, out var parameters))
                {
                    continue;
                }

                if (string.Equals(command, "PING", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteIrcLineAsync(connectedWriter, $"PONG {parameters}", cancellationToken);
                    continue;
                }

                if (string.Equals(command, "RECONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    RaiseStatusChanged("Twitch requested a chat reconnect.");
                    break;
                }

                var notice = TryParseNoticeMessage(line);
                if (notice is not null)
                {
                    RaiseStatusChanged($"Twitch notice: {notice}");
                    continue;
                }

                var message = TwitchIrcParser.TryParsePrivMsg(line, channel);
                if (message is not null)
                {
                    RaiseMessageReceived(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "TwitchChat", "Twitch chat disconnected.", ex);
            RaiseStatusChanged($"Twitch chat disconnected: {ex.Message}");
        }
        finally
        {
            var shouldReconnect = !cancellationToken.IsCancellationRequested &&
                ReferenceEquals(connectionSupervisor, supervisor);
            // A remote close ends the read loop without going through DisconnectAsync.
            // Do not leave prediction controls enabled for a dead IRC session. The
            // reference checks keep an old loop from resetting a newer connection.
            if (ReferenceEquals(reader, connectedReader) &&
                ReferenceEquals(writer, connectedWriter))
            {
                canSendMessages = false;
                readCancellation?.Cancel();
                SetPredictionAccess(TwitchPredictionAccessState.Pending);
                try
                {
                    await StopPredictionEventSubAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.Write(AppLogLevel.Debug, "TwitchChat", "Twitch prediction EventSub cleanup after an IRC disconnect failed.", ex);
                }
            }

            if (shouldReconnect)
            {
                supervisor.NotifyConnectionEnded(Stopwatch.GetElapsedTime(connectedAt));
            }
        }
    }

    private void RaiseMessageReceived(ChatMessage message)
    {
        SafeEventDispatcher.Invoke(
            MessageReceived,
            this,
            message,
            logger,
            "TwitchChat",
            nameof(MessageReceived));
    }

    private void RaiseStatusChanged(string message)
    {
        SafeEventDispatcher.Invoke(
            StatusChanged,
            this,
            message,
            logger,
            "TwitchChat",
            nameof(StatusChanged));
    }

    private void RaisePredictionReceived(TwitchPrediction prediction)
    {
        SafeEventDispatcher.Invoke(
            PredictionReceived,
            this,
            prediction,
            logger,
            "TwitchPredictions",
            nameof(PredictionReceived));
    }

    private void ThrowIfDisposed()
    {
        lock (disposalGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }
    }

    private async Task WriteIrcLineAsync(string line, CancellationToken cancellationToken)
    {
        var connectedWriter = writer;
        if (connectedWriter is null)
        {
            throw new InvalidOperationException("Twitch chat is not connected.");
        }

        await WriteIrcLineAsync(connectedWriter, line, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteIrcLineAsync(
        StreamWriter connectedWriter,
        string line,
        CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(line) + 2 > PayloadLimits.TwitchOutboundIrcBytes)
        {
            throw new InvalidDataException(
                $"IRC command exceeded the {PayloadLimits.TwitchOutboundIrcBytes}-byte limit.");
        }

        await writerLock.WaitAsync(cancellationToken);
        try
        {
            await connectedWriter.WriteLineAsync(line.AsMemory(), cancellationToken);
        }
        finally
        {
            writerLock.Release();
        }
    }

    private static string SanitizeMessage(string message)
    {
        return ChatTextNormalizer.NormalizeSingleLine(message);
    }

    internal static string TruncateIrcMessage(string channel, string message)
    {
        var availableBytes = PayloadLimits.TwitchOutboundIrcBytes -
            Encoding.UTF8.GetByteCount($"PRIVMSG #{channel} :") -
            2;
        if (availableBytes <= 0)
        {
            return "";
        }

        var usedBytes = 0;
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(message);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var elementBytes = Encoding.UTF8.GetByteCount(element);
            if (usedBytes > availableBytes - elementBytes)
            {
                return message[..enumerator.ElementIndex];
            }

            usedBytes += elementBytes;
        }

        return message;
    }

    internal async Task AwaitIrcHandshakeAsync(
        string channel,
        BoundedUtf8LineReader connectedReader,
        StreamWriter connectedWriter,
        CancellationToken cancellationToken,
        TimeSpan? handshakeTimeout = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(handshakeTimeout ?? TimeSpan.FromSeconds(10));
        var welcomed = false;
        var joined = false;
        try
        {
            for (var lineCount = 0; lineCount < 200 && (!welcomed || !joined); lineCount++)
            {
                var line = await connectedReader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null)
                {
                    throw new IOException("Twitch closed chat before completing the welcome and JOIN handshake.");
                }

                if (!TwitchIrcProtocol.TryReadCommand(line, out var command, out var parameters))
                {
                    continue;
                }

                if (string.Equals(command, "PING", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteIrcLineAsync(connectedWriter, $"PONG {parameters}", timeout.Token).ConfigureAwait(false);
                    continue;
                }

                if (string.Equals(command, "NOTICE", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Twitch rejected the chat handshake: {TryParseNoticeMessage(line) ?? "unknown reason"}");
                }

                welcomed |= string.Equals(command, "001", StringComparison.OrdinalIgnoreCase);
                joined |= string.Equals(command, "JOIN", StringComparison.OrdinalIgnoreCase) &&
                    TwitchIrcProtocol.IsJoinForChannel(parameters, channel);
            }

            if (!welcomed || !joined)
            {
                throw new InvalidDataException("Twitch sent too many messages without completing the welcome and JOIN handshake.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out waiting for the Twitch chat welcome and JOIN acknowledgement.");
        }
    }

    private static string? TryParseNoticeMessage(string line)
    {
        if (!TwitchIrcProtocol.TryReadCommand(line, out var command, out _) ||
            !string.Equals(command, "NOTICE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var markerIndex = line.LastIndexOf(" :", StringComparison.Ordinal);
        if (markerIndex < 0 || markerIndex + 2 >= line.Length)
        {
            return "Twitch sent a notice.";
        }

        return line[(markerIndex + 2)..].Trim();
    }

    private static TaskCompletionSource CreateCompletedTaskSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private sealed record PredictionContext(string BroadcasterId, string AccessToken, string ClientId);
}

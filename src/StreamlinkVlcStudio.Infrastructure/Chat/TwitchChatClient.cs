using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Core.Text;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public sealed class TwitchChatClient : IChatClient, ITwitchPredictionClient
{
    private readonly ChatSettings settings;
    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly SemaphoreSlim writerLock = new(1, 1);
    private readonly SemaphoreSlim predictionRequestLock = new(1, 1);
    private readonly TwitchPredictionApiClient predictionApiClient;
    private TcpClient? tcpClient;
    private SslStream? sslStream;
    private StreamReader? reader;
    private StreamWriter? writer;
    private CancellationTokenSource? readCancellation;
    private Task? readTask;
    private string? connectedChannel;
    private string? predictionBroadcasterId;
    private string? predictionAccessToken;
    private string? predictionClientId;
    private TwitchPredictionEventSubClient? predictionEventSubClient;
    private bool canSendMessages;

    public TwitchChatClient(ChatSettings settings, IAppLogger logger, HttpClient? httpClient = null)
    {
        this.settings = settings;
        this.logger = logger;
        this.httpClient = httpClient ?? new HttpClient();
        ownsHttpClient = httpClient is null;
        this.httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("StreamlinkVlcStudio/0.1");
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
        await DisconnectAsync(cancellationToken);
        StatusChanged?.Invoke(this, "Connecting to Twitch chat...");

        tcpClient = new TcpClient();
        await tcpClient.ConnectAsync("irc.chat.twitch.tv", 6697, cancellationToken);
        sslStream = new SslStream(tcpClient.GetStream(), leaveInnerStreamOpen: false);
        await sslStream.AuthenticateAsClientAsync("irc.chat.twitch.tv");

        reader = new StreamReader(sslStream, Encoding.UTF8);
        writer = new StreamWriter(sslStream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };

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
                var configuredUsername = settings.TwitchUsername.Trim();
                if (!string.IsNullOrWhiteSpace(configuredUsername) &&
                    !string.Equals(configuredUsername, tokenInfo.Login, StringComparison.OrdinalIgnoreCase))
                {
                    StatusChanged?.Invoke(this, $"Twitch username '{configuredUsername}' does not match the token login '{tokenInfo.Login}'; using the token login.");
                }

                if (tokenInfo.CanReadChat || tokenInfo.CanWriteChat)
                {
                    nick = tokenInfo.Login;
                    pass = $"oauth:{token}";
                    canSendMessages = tokenInfo.CanWriteChat;
                    CurrentUsername = nick;
                    StatusChanged?.Invoke(this, $"Using Twitch OAuth token for {nick}.");

                    if (!tokenInfo.CanReadChat)
                    {
                        StatusChanged?.Invoke(this, "Twitch token is missing chat:read; receiving chat may be limited.");
                    }

                    if (!tokenInfo.CanWriteChat)
                    {
                        StatusChanged?.Invoke(this, "Twitch token is valid but missing IRC send scope. Use Connect Twitch or reauthorize with chat:edit to type in chat.");
                    }
                }
                else
                {
                    StatusChanged?.Invoke(this, "Twitch token is missing chat:read and chat:edit; connecting to Twitch chat read-only.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Write(AppLogLevel.Warning, "TwitchChat", "Twitch token validation failed; connecting read-only.", ex);
                StatusChanged?.Invoke(this, $"Twitch token validation failed: {ex.Message}. Connecting read-only.");
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

        connectedChannel = target.Channel.ToLowerInvariant();
        await WriteIrcLineAsync("CAP REQ :twitch.tv/tags twitch.tv/commands", cancellationToken);
        await WriteIrcLineAsync($"PASS {pass}", cancellationToken);
        await WriteIrcLineAsync($"NICK {nick}", cancellationToken);
        await WriteIrcLineAsync($"JOIN #{connectedChannel}", cancellationToken);

        readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTask = Task.Run(() => ReadLoopAsync(connectedChannel, readCancellation.Token), CancellationToken.None);
        StatusChanged?.Invoke(this, canSendMessages ? "Twitch chat connected with send access." : "Twitch chat connected read-only.");

        if (tokenInfo is not null)
        {
            await InitializePredictionsAsync(target, token, tokenInfo, cancellationToken);
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        readCancellation?.Cancel();
        await StopPredictionEventSubAsync();
        if (readTask is not null)
        {
            try
            {
                await readTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
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
    }

    public async Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        var sanitized = SanitizeMessage(message);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return;
        }

        if (!canSendMessages)
        {
            throw new InvalidOperationException("Connect Twitch in Settings with a user access token that includes chat:edit. A Twitch Client ID alone cannot send chat.");
        }

        if (writer is null || string.IsNullOrWhiteSpace(connectedChannel))
        {
            throw new InvalidOperationException("Twitch chat is not connected.");
        }

        await WriteIrcLineAsync($"PRIVMSG #{connectedChannel} :{sanitized}", cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        writerLock.Dispose();
        predictionRequestLock.Dispose();
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    public Task<TwitchPrediction> CreatePredictionAsync(
        TwitchPredictionCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        return RunPredictionRequestAsync(context => predictionApiClient.CreatePredictionAsync(
            context.BroadcasterId,
            request,
            context.AccessToken,
            context.ClientId,
            cancellationToken), cancellationToken);
    }

    public Task<TwitchPrediction> LockPredictionAsync(string predictionId, CancellationToken cancellationToken = default)
    {
        return RunPredictionRequestAsync(context => predictionApiClient.LockPredictionAsync(
            context.BroadcasterId,
            predictionId,
            context.AccessToken,
            context.ClientId,
            cancellationToken), cancellationToken);
    }

    public Task<TwitchPrediction> CancelPredictionAsync(string predictionId, CancellationToken cancellationToken = default)
    {
        return RunPredictionRequestAsync(context => predictionApiClient.CancelPredictionAsync(
            context.BroadcasterId,
            predictionId,
            context.AccessToken,
            context.ClientId,
            cancellationToken), cancellationToken);
    }

    public Task<TwitchPrediction> ResolvePredictionAsync(
        string predictionId,
        string winningOutcomeId,
        CancellationToken cancellationToken = default)
    {
        return RunPredictionRequestAsync(context => predictionApiClient.ResolvePredictionAsync(
            context.BroadcasterId,
            predictionId,
            winningOutcomeId,
            context.AccessToken,
            context.ClientId,
            cancellationToken), cancellationToken);
    }

    private async Task<TwitchPrediction> RunPredictionRequestAsync(
        Func<PredictionContext, Task<TwitchPrediction>> request,
        CancellationToken cancellationToken)
    {
        await predictionRequestLock.WaitAsync(cancellationToken);
        try
        {
            var prediction = await request(GetPredictionContext()).ConfigureAwait(false);
            PredictionReceived?.Invoke(this, prediction);
            return prediction;
        }
        finally
        {
            predictionRequestLock.Release();
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

        var clientId = FirstNonEmpty(settings.TwitchClientId, tokenInfo.ClientId);
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
            if (currentPrediction is { IsOpen: true })
            {
                PredictionReceived?.Invoke(this, currentPrediction);
            }

            predictionEventSubClient = new TwitchPredictionEventSubClient(
                predictionApiClient,
                logger,
                token,
                clientId,
                broadcaster.Id,
                prediction => PredictionReceived?.Invoke(this, prediction),
                message => StatusChanged?.Invoke(this, message));
            predictionEventSubClient.Start();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Warning, "TwitchPredictions", $"Twitch prediction setup failed for {target.DisplayName}.", ex);
            StatusChanged?.Invoke(this, $"Twitch prediction updates unavailable: {ex.Message}");
        }
    }

    private void SetPredictionAccess(TwitchPredictionAccessState access)
    {
        PredictionAccess = access;
        PredictionAccessChanged?.Invoke(this, access);
    }

    private async Task StopPredictionEventSubAsync()
    {
        var eventSubClient = predictionEventSubClient;
        predictionEventSubClient = null;
        if (eventSubClient is not null)
        {
            await eventSubClient.DisposeAsync();
        }
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

    private async Task ReadLoopAsync(string channel, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && reader is not null)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (line.StartsWith("PING", StringComparison.OrdinalIgnoreCase) && writer is not null)
                {
                    await WriteIrcLineAsync("PONG :tmi.twitch.tv", cancellationToken);
                    continue;
                }

                var notice = TryParseNoticeMessage(line);
                if (notice is not null)
                {
                    StatusChanged?.Invoke(this, $"Twitch notice: {notice}");
                    continue;
                }

                var message = TwitchIrcParser.TryParsePrivMsg(line, channel);
                if (message is not null)
                {
                    MessageReceived?.Invoke(this, message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "TwitchChat", "Twitch chat disconnected.", ex);
            StatusChanged?.Invoke(this, $"Twitch chat disconnected: {ex.Message}");
        }
    }

    private async Task WriteIrcLineAsync(string line, CancellationToken cancellationToken)
    {
        await writerLock.WaitAsync(cancellationToken);
        try
        {
            if (writer is null)
            {
                throw new InvalidOperationException("Twitch chat is not connected.");
            }

            await writer.WriteLineAsync(line);
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

    private static string? TryParseNoticeMessage(string line)
    {
        if (!line.Contains(" NOTICE ", StringComparison.OrdinalIgnoreCase))
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

    private sealed record PredictionContext(string BroadcasterId, string AccessToken, string ClientId);
}

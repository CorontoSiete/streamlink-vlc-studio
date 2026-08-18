using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Core.Text;
using StreamlinkVlcStudio.Infrastructure.Http;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Infrastructure.Chat.OAuthTokenHelpers;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public sealed class KickChatClient : IChatClient, IChatHistoryBackfillClient
{
    private const string PusherAppKey = "32cbd69e4b950bf97679";
    private const int KickRecentChatInitialBackfillLimit = 25;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DisconnectCleanupTimeout = TimeSpan.FromSeconds(2);
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly ChatSettings settings;
    private readonly IAppLogger logger;
    private readonly KickChatHistoryBackfillService historyBackfillService;
    private readonly KickChatTransport transport;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim recentChatBackfillGate = new(1, 1);
    private readonly object recentChatBackfillLifecycleGate = new();
    private readonly HashSet<string> requestedRecentChatCursors = new(StringComparer.Ordinal);
    private CancellationTokenSource? recentChatBackfillCancellation = new();
    private Task? recentChatBackfillTask;
    private Task? disposalTask;
    private ClientWebSocket? webSocket;
    private CancellationTokenSource? readCancellation;
    private Task? readTask;
    private LiveChatConnectionSupervisor? connectionSupervisor;
    private string? connectedChannel;
    private string? currentChannelId;
    private string? currentChatroomId;
    private string? recentChatNextCursor;
    private DateTimeOffset? oldestRecentChatTimestampUtc;
    private long? currentBroadcasterUserId;
    private string? validatedSendToken;
    private bool canSendMessages;
    private bool recentChatDirectBackfillBlocked;
    private bool recentChatBackfillExhausted;
    private bool disposed;

    public KickChatClient(ChatSettings settings, IAppLogger logger, HttpClient? httpClient = null)
    {
        this.settings = settings;
        this.logger = logger;
        this.httpClient = httpClient ?? HttpClientFactory.CreateDefault();
        ownsHttpClient = httpClient is null;
        KickHttpHeaders.Configure(this.httpClient);
        historyBackfillService = new KickChatHistoryBackfillService(this.httpClient, logger);
        transport = new KickChatTransport(this.httpClient, logger);
    }

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<string>? StatusChanged;
    public string? CurrentUsername { get; private set; }

    public async Task ConnectAsync(StreamTarget target, CancellationToken cancellationToken = default)
    {
        lock (recentChatBackfillLifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (recentChatBackfillLifecycleGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
            }

            await StopConnectionSupervisorCoreAsync().ConfigureAwait(false);
            await DisconnectCoreAsync().ConfigureAwait(false);
            var supervisor = new LiveChatConnectionSupervisor(
                logger,
                "KickChat",
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
                    logger.Write(AppLogLevel.Warning, "KickChat", "Kick chat cleanup failed after a connection error.", cleanupException);
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
        RaiseStatusChanged("Resolving Kick chatroom...");
        connectedChannel = target.Channel;

        var channelInfo = await ResolveChannelInfoAsync(target.Channel, cancellationToken);
        if (string.IsNullOrWhiteSpace(channelInfo.ChatroomId))
        {
            throw new InvalidOperationException("Kick chatroom ID could not be resolved. Open Settings while this Kick tab is selected and enter the selected Kick chatroom ID.");
        }

        ResetRecentChatBackfillState(channelInfo.ChannelId, channelInfo.ChatroomId);
        currentBroadcasterUserId = channelInfo.BroadcasterUserId;
        CurrentUsername = string.IsNullOrWhiteSpace(settings.KickUsername)
            ? settings.KickSendAsBot ? "bot" : "me"
            : settings.KickUsername.Trim();

        var token = await ResolveSendTokenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            try
            {
                await ValidateSendTokenAsync(token, cancellationToken);
                RaiseStatusChanged("Kick OAuth token has chat send access.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Write(AppLogLevel.Warning, "KickChat", "Kick token validation failed; chat will be read-only.", ex);
                RaiseStatusChanged($"Kick token validation failed: {ex.Message}");
            }
        }

        RaiseStatusChanged("Connecting to Kick chat...");
        var connectedWebSocket = new ClientWebSocket();
        webSocket = connectedWebSocket;
        var uri = new Uri($"wss://ws-us2.pusher.com/app/{PusherAppKey}?protocol=7&client=dotnet&version=0.1&flash=false");
        await connectedWebSocket.ConnectAsync(uri, cancellationToken);

        var subscriptionChannel = $"chatrooms.{channelInfo.ChatroomId}.v2";
        await WaitForPusherAcknowledgementAsync(
            connectedWebSocket,
            "pusher:connection_established",
            expectedChannel: null,
            cancellationToken).ConfigureAwait(false);

        var subscribe = JsonSerializer.Serialize(new
        {
            @event = "pusher:subscribe",
            data = new
            {
                auth = "",
                channel = subscriptionChannel
            }
        });
        await SendWebSocketAsync(connectedWebSocket, subscribe, cancellationToken);
        await WaitForPusherAcknowledgementAsync(
            connectedWebSocket,
            "pusher_internal:subscription_succeeded",
            subscriptionChannel,
            cancellationToken).ConfigureAwait(false);

        // The caller's token only governs the connection handshake. The websocket and recent-chat
        // backfill must remain alive until DisconnectAsync cancels this independent lifecycle source.
        var connectedReadCancellation = new CancellationTokenSource();
        readCancellation = connectedReadCancellation;
        readTask = Task.Run(
            () => ReadLoopAsync(target.Channel, connectedWebSocket, supervisor, connectedReadCancellation.Token),
            CancellationToken.None);
        RaiseStatusChanged(canSendMessages ? "Kick chat connected with send access." : "Kick chat connected read-only.");
        StartRecentChatBackfill(target.Channel, channelInfo.ChatroomId, connectedReadCancellation.Token);
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
        CancelRecentChatBackfillsForDisconnect();
        readCancellation?.Cancel();

        if (webSocket is { State: WebSocketState.Open })
        {
            try
            {
                using var closeTimeout = new CancellationTokenSource(DisconnectCleanupTimeout);
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", closeTimeout.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
            }
        }

        if (readTask is not null)
        {
            try
            {
                await readTask.WaitAsync(DisconnectCleanupTimeout);
            }
            catch (Exception)
            {
            }
        }

        if (recentChatBackfillTask is not null)
        {
            try
            {
                await recentChatBackfillTask.WaitAsync(DisconnectCleanupTimeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Write(AppLogLevel.Info, "KickChat", "Kick recent chat backfill cleanup failed.", ex);
            }
            recentChatBackfillTask = null;
        }

        if (await recentChatBackfillGate.WaitAsync(DisconnectCleanupTimeout).ConfigureAwait(false))
        {
            recentChatBackfillGate.Release();
        }
        else
        {
            logger.Write(
                AppLogLevel.Warning,
                "KickChat",
                "Timed out waiting for canceled Kick chat backfill work to stop; disconnect will continue.");
        }

        webSocket?.Dispose();
        readCancellation?.Dispose();
        webSocket = null;
        readTask = null;
        readCancellation = null;
        connectedChannel = null;
        currentChannelId = null;
        currentChatroomId = null;
        recentChatNextCursor = null;
        oldestRecentChatTimestampUtc = null;
        requestedRecentChatCursors.Clear();
        recentChatDirectBackfillBlocked = false;
        recentChatBackfillExhausted = false;
        currentBroadcasterUserId = null;
        validatedSendToken = null;
        canSendMessages = false;
        CurrentUsername = null;
        ResetRecentChatBackfillCancellationAfterDisconnect();
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
            lock (recentChatBackfillLifecycleGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
            }

            if (!ReferenceEquals(connectionSupervisor, supervisor))
            {
                throw new OperationCanceledException("The Kick chat connection was replaced.", cancellationToken);
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
        ThrowIfDisposed();
        var sanitized = SanitizeMessage(message);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return;
        }

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            var token = await ResolveSendTokenAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Connect Kick in Settings before sending chat.");
            }

            token = await ValidateSendTokenAsync(token, cancellationToken).ConfigureAwait(false);

            var payload = new Dictionary<string, object>
            {
                ["content"] = sanitized,
                ["type"] = settings.KickSendAsBot ? "bot" : "user"
            };

            if (!settings.KickSendAsBot)
            {
                if (currentBroadcasterUserId is null && !string.IsNullOrWhiteSpace(connectedChannel))
                {
                    currentBroadcasterUserId = (await ResolveChannelInfoAsync(connectedChannel, cancellationToken)).BroadcasterUserId;
                }

                if (currentBroadcasterUserId is null)
                {
                    throw new InvalidOperationException("Kick broadcaster user ID could not be resolved. Add it manually in Settings before sending as a user.");
                }

                payload["broadcaster_user_id"] = currentBroadcasterUserId.Value;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.kick.com/public/v1/chat");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
            var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Kick chat send failed ({(int)response.StatusCode} {response.ReasonPhrase}). {ApiErrorMessage.Extract(responseBody)}");
            }

            if (!KickSendResponseIndicatesSuccess(responseBody))
            {
                throw new InvalidOperationException($"Kick chat did not confirm the message was sent. {ApiErrorMessage.Extract(responseBody)}");
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (recentChatBackfillLifecycleGate)
        {
            disposalTask ??= DisposeCoreAsync();
            return new ValueTask(disposalTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        lock (recentChatBackfillLifecycleGate)
        {
            disposed = true;
        }

        await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopConnectionSupervisorCoreAsync().ConfigureAwait(false);
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
            if (ownsHttpClient)
            {
                httpClient.Dispose();
            }

        }
    }

    public async Task<ChatHistoryBackfillResult> BackfillRecentChatRangeAsync(
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var channel = connectedChannel;
        var channelId = currentChannelId;
        var chatroomId = currentChatroomId;
        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(chatroomId))
        {
            return new ChatHistoryBackfillResult(false, 0, false, null, null);
        }

        fromTimestampUtc = fromTimestampUtc.ToUniversalTime();
        throughTimestampUtc = throughTimestampUtc.ToUniversalTime();
        if (throughTimestampUtc < fromTimestampUtc)
        {
            throughTimestampUtc = fromTimestampUtc;
        }

        if (!TryCreateRecentBackfillToken(cancellationToken, out var backfillCancellation, out var backfillToken))
        {
            return CreateRetryableBackfillResult();
        }

        using (backfillCancellation)
        {
            try
            {
                logger.Write(
                    AppLogLevel.Debug,
                    "KickChat",
                    $"Kick seekback backfill requested for {channel}: {FormatBackfillTimestamp(fromTimestampUtc)} through {FormatBackfillTimestamp(throughTimestampUtc)}.");

                var timestampResult = await BackfillRecentChatFromStartTimeAsync(
                        channel,
                        channelId,
                        chatroomId,
                        fromTimestampUtc,
                        throughTimestampUtc,
                        backfillToken)
                    .ConfigureAwait(false);
                LogKickSeekbackBackfillResult(channel, timestampResult);
                return timestampResult;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.Write(AppLogLevel.Debug, "KickChat", $"Kick seekback backfill for {channel} was canceled by chat disconnect; it will remain retryable.");
                return CreateRetryableBackfillResult();
            }
            catch (ObjectDisposedException ex)
            {
                logger.Write(AppLogLevel.Debug, "KickChat", $"Kick seekback backfill for {channel} raced disposed chat resources; it will remain retryable.", ex);
                return CreateRetryableBackfillResult();
            }
        }
    }

    private bool TryCreateRecentBackfillToken(
        CancellationToken cancellationToken,
        out CancellationTokenSource? backfillCancellation,
        out CancellationToken backfillToken)
    {
        CancellationToken lifecycleToken;
        lock (recentChatBackfillLifecycleGate)
        {
            if (disposed ||
                recentChatBackfillCancellation is null ||
                recentChatBackfillCancellation.IsCancellationRequested)
            {
                backfillCancellation = null;
                backfillToken = cancellationToken;
                return false;
            }

            lifecycleToken = recentChatBackfillCancellation.Token;
        }

        try
        {
            backfillCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifecycleToken);
            backfillToken = backfillCancellation.Token;
            return true;
        }
        catch (ObjectDisposedException)
        {
            backfillCancellation = null;
            backfillToken = cancellationToken;
            return false;
        }
    }

    private void CancelRecentChatBackfillsForDisconnect()
    {
        CancellationTokenSource? cancellation;
        lock (recentChatBackfillLifecycleGate)
        {
            cancellation = recentChatBackfillCancellation;
            recentChatBackfillCancellation = null;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cancellation?.Dispose();
    }

    private void ResetRecentChatBackfillCancellationAfterDisconnect()
    {
        lock (recentChatBackfillLifecycleGate)
        {
            if (!disposed && recentChatBackfillCancellation is null)
            {
                recentChatBackfillCancellation = new CancellationTokenSource();
            }
        }
    }

    private static ChatHistoryBackfillResult CreateRetryableBackfillResult()
    {
        return new ChatHistoryBackfillResult(false, 0, false, null, null);
    }

    private void LogKickSeekbackBackfillResult(string channel, ChatHistoryBackfillResult result)
    {
        logger.Write(
            AppLogLevel.Debug,
            "KickChat",
            $"Kick seekback backfill completed for {channel}: loaded={result.LoadedMessageCount.ToString(CultureInfo.InvariantCulture)}, " +
            $"covered={result.CoveredRequestedRange}, range={FormatBackfillTimestamp(result.CoveredFromTimestampUtc)} through {FormatBackfillTimestamp(result.CoveredThroughTimestampUtc)}.");
    }

    private static string FormatBackfillTimestamp(DateTimeOffset? timestampUtc)
    {
        return timestampUtc is { } timestamp
            ? timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : "none";
    }

    private async Task<KickChannelInfo> ResolveChannelInfoAsync(string channel, CancellationToken cancellationToken)
    {
        var hasConfiguredBroadcasterUserId = false;
        if (settings.TryGetKickBroadcasterUserId(channel, out var configuredBroadcasterUserId) &&
            long.TryParse(
                configuredBroadcasterUserId,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedConfiguredBroadcasterUserId) &&
            parsedConfiguredBroadcasterUserId > 0)
        {
            hasConfiguredBroadcasterUserId = true;
        }

        var channelInfo = await transport
            .ResolveChannelInfoAsync(channel, settings, cancellationToken)
            .ConfigureAwait(false);

        if (!settings.KickSendAsBot && !hasConfiguredBroadcasterUserId)
        {
            var broadcasterUserId = await TryResolveBroadcasterUserIdFromKickApiAsync(channel, cancellationToken)
                .ConfigureAwait(false);
            if (broadcasterUserId is not null)
            {
                channelInfo = channelInfo with { BroadcasterUserId = broadcasterUserId };
            }
        }

        return channelInfo;
    }

    private void StartRecentChatBackfill(
        string channel,
        string chatroomId,
        CancellationToken cancellationToken)
    {
        recentChatBackfillTask = RunRecentChatBackfillAsync();

        async Task RunRecentChatBackfillAsync()
        {
            try
            {
                await EmitRecentChatBackfillAsync(channel, chatroomId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.Write(AppLogLevel.Info, $"KickChat", $"Kick recent chat backfill failed for {channel}.", ex);
            }
        }
    }

    private async Task EmitRecentChatBackfillAsync(
        string channel,
        string chatroomId,
        CancellationToken cancellationToken)
    {
        var result = await BackfillRecentChatAsync(
                channel,
                chatroomId,
                oldestTimestampUtc: null,
                throughTimestampUtc: null,
                KickRecentChatInitialBackfillLimit,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.LoadedMessageCount > 0)
        {
            logger.Write(AppLogLevel.Info, "KickChat", $"Loaded {result.LoadedMessageCount} recent Kick chat messages for {channel}.");
        }
    }

    private async Task<ChatHistoryBackfillResult> BackfillRecentChatFromStartTimeAsync(
        string channel,
        string? channelId,
        string chatroomId,
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc,
        CancellationToken cancellationToken)
    {
        await recentChatBackfillGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(channel, connectedChannel, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(chatroomId, currentChatroomId, StringComparison.Ordinal))
            {
                return new ChatHistoryBackfillResult(false, 0, false, null, null);
            }

            var result = await historyBackfillService.BackfillRecentChatFromStartTimeAsync(
                    channel,
                    channelId,
                    chatroomId,
                    fromTimestampUtc,
                    throughTimestampUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            EmitKickBackfillMessages(result.Messages);
            return result;
        }
        finally
        {
            recentChatBackfillGate.Release();
        }
    }

    private void EmitKickBackfillMessages(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages
            .OrderBy(message => message.Timestamp)
            .ThenBy(message => message.MessageId, StringComparer.Ordinal))
        {
            RaiseMessageReceived(message);
        }

        if (messages.Count == 0)
        {
            return;
        }

        var pageOldest = messages
            .Min(message => message.Timestamp)
            .ToUniversalTime();
        oldestRecentChatTimestampUtc = oldestRecentChatTimestampUtc is { } oldest &&
            oldest <= pageOldest
                ? oldest
                : pageOldest;
    }

    private async Task<ChatHistoryBackfillResult> BackfillRecentChatAsync(
        string channel,
        string chatroomId,
        DateTimeOffset? oldestTimestampUtc,
        DateTimeOffset? throughTimestampUtc,
        int maxMessages,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(chatroomId) ||
            !chatroomId.All(char.IsDigit) ||
            maxMessages <= 0)
        {
            return new ChatHistoryBackfillResult(false, 0, false, null, null);
        }

        oldestTimestampUtc = oldestTimestampUtc?.ToUniversalTime();
        throughTimestampUtc = throughTimestampUtc?.ToUniversalTime();
        if (oldestTimestampUtc is { } oldest &&
            throughTimestampUtc is { } through &&
            through < oldest)
        {
            throughTimestampUtc = oldest;
        }

        await recentChatBackfillGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(channel, connectedChannel, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(chatroomId, currentChatroomId, StringComparison.Ordinal) ||
                recentChatBackfillExhausted)
            {
                return CreateRecentCursorBackfillResult(
                    false,
                    0,
                    oldestTimestampUtc,
                    throughTimestampUtc);
            }

            var loadedMessages = new List<ChatMessage>();
            var attempted = false;
            while (loadedMessages.Count < maxMessages && !recentChatBackfillExhausted)
            {
                if (oldestTimestampUtc is { } targetOldest &&
                    oldestRecentChatTimestampUtc is { } currentOldest &&
                    currentOldest <= targetOldest)
                {
                    break;
                }

                var cursor = recentChatNextCursor;
                var cursorKey = cursor ?? "";
                if (requestedRecentChatCursors.Contains(cursorKey))
                {
                    recentChatBackfillExhausted = true;
                    break;
                }

                var page = recentChatDirectBackfillBlocked
                    ? null
                    : await TryReadKickRecentMessagesDirectAsync(channel, chatroomId, cursor, cancellationToken).ConfigureAwait(false);
                page ??= await TryReadKickRecentMessagesWithCurlAsync(channel, chatroomId, cursor, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                attempted = true;
                if (page is null)
                {
                    break;
                }

                requestedRecentChatCursors.Add(cursorKey);

                if (page.Messages.Count == 0)
                {
                    recentChatBackfillExhausted = true;
                    break;
                }

                var remainingMessageCount = maxMessages - loadedMessages.Count;
                var selectedPageMessages = page.Messages.Count <= remainingMessageCount
                    ? page.Messages
                    : page.Messages.TakeLast(remainingMessageCount).ToArray();
                loadedMessages.AddRange(selectedPageMessages);
                var pageOldest = selectedPageMessages[0].Timestamp.ToUniversalTime();
                oldestRecentChatTimestampUtc = oldestRecentChatTimestampUtc is { } existingOldest &&
                    existingOldest <= pageOldest
                        ? existingOldest
                        : pageOldest;

                var nextCursor = KickChatTransport.NormalizeCursor(page.Cursor);
                if (string.IsNullOrWhiteSpace(nextCursor) ||
                    string.Equals(nextCursor, cursor, StringComparison.Ordinal))
                {
                    recentChatBackfillExhausted = true;
                    break;
                }

                recentChatNextCursor = nextCursor;
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (var message in loadedMessages
                .OrderBy(message => message.Timestamp)
                .ThenBy(message => message.MessageId, StringComparer.Ordinal))
            {
                RaiseMessageReceived(message);
            }

            return CreateRecentCursorBackfillResult(
                attempted,
                loadedMessages.Count,
                oldestTimestampUtc,
                throughTimestampUtc);
        }
        finally
        {
            recentChatBackfillGate.Release();
        }
    }

    private ChatHistoryBackfillResult CreateRecentCursorBackfillResult(
        bool attempted,
        int loadedMessageCount,
        DateTimeOffset? oldestTimestampUtc,
        DateTimeOffset? throughTimestampUtc)
    {
        if (oldestTimestampUtc is not { } requestedOldest ||
            throughTimestampUtc is not { } requestedThrough ||
            oldestRecentChatTimestampUtc is not { } loadedOldest)
        {
            return new ChatHistoryBackfillResult(attempted, loadedMessageCount, false, null, null);
        }

        requestedOldest = requestedOldest.ToUniversalTime();
        requestedThrough = requestedThrough.ToUniversalTime();
        loadedOldest = loadedOldest.ToUniversalTime();
        var coveredFrom = loadedOldest <= requestedOldest ? requestedOldest : loadedOldest;
        var coveredThrough = requestedThrough < coveredFrom ? coveredFrom : requestedThrough;
        return new ChatHistoryBackfillResult(
            attempted,
            loadedMessageCount,
            loadedOldest <= requestedOldest,
            coveredFrom,
            coveredThrough);
    }

    private async Task<KickRecentChatPage?> TryReadKickRecentMessagesDirectAsync(
        string channel,
        string chatroomId,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var result = await transport.ReadRecentMessagesDirectAsync(
                channel,
                chatroomId,
                cursor,
                startTimeUtc: null,
                cancellationToken)
            .ConfigureAwait(false);
        recentChatDirectBackfillBlocked |= result.DirectForbidden;
        return result.Page;
    }

    private async Task<KickRecentChatPage?> TryReadKickRecentMessagesWithCurlAsync(
        string channel,
        string chatroomId,
        string? cursor,
        CancellationToken cancellationToken)
    {
        return await transport.ReadRecentMessagesWithCurlAsync(
                channel,
                chatroomId,
                cursor,
                startTimeUtc: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void ResetRecentChatBackfillState(string? channelId, string chatroomId)
    {
        currentChannelId = KickChannelInfoJson.NormalizeNumericId(channelId);
        currentChatroomId = chatroomId;
        recentChatNextCursor = null;
        oldestRecentChatTimestampUtc = null;
        requestedRecentChatCursors.Clear();
        recentChatDirectBackfillBlocked = false;
        recentChatBackfillExhausted = false;
    }

    private async Task ReadLoopAsync(
        string channel,
        ClientWebSocket connectedWebSocket,
        LiveChatConnectionSupervisor supervisor,
        CancellationToken cancellationToken)
    {
        var connectedAt = Stopwatch.GetTimestamp();
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   ReferenceEquals(connectedWebSocket, webSocket) &&
                   connectedWebSocket.State == WebSocketState.Open)
            {
                var payload = await BoundedWebSocketTextReader
                    .ReadAsync(connectedWebSocket, cancellationToken)
                    .ConfigureAwait(false);
                if (payload is null)
                {
                    RaiseStatusChanged("Kick chat disconnected by the server.");
                    return;
                }

                if (!ReferenceEquals(connectedWebSocket, webSocket))
                {
                    return;
                }

                if (IsPusherPing(payload))
                {
                    await SendWebSocketAsync(
                        connectedWebSocket,
                        """{"event":"pusher:pong","data":{}}""",
                        cancellationToken);
                    continue;
                }

                var message = KickPusherParser.TryParse(payload, channel);
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
            logger.Write(AppLogLevel.Warning, "KickChat", "Kick chat disconnected.", ex);
            RaiseStatusChanged($"Kick chat disconnected: {ex.Message}");
        }
        finally
        {
            var shouldReconnect = !cancellationToken.IsCancellationRequested &&
                ReferenceEquals(connectionSupervisor, supervisor);
            // A remote close does not pass through DisconnectAsync. Cancel any
            // seekback/initial history work so a dead websocket cannot keep doing
            // network work until the tab is explicitly restarted.
            if (ReferenceEquals(connectedWebSocket, webSocket))
            {
                canSendMessages = false;
                readCancellation?.Cancel();
                CancelRecentChatBackfillsForDisconnect();
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
            "KickChat",
            nameof(MessageReceived));
    }

    private void RaiseStatusChanged(string message)
    {
        SafeEventDispatcher.Invoke(
            StatusChanged,
            this,
            message,
            logger,
            "KickChat",
            nameof(StatusChanged));
    }

    private void ThrowIfDisposed()
    {
        lock (recentChatBackfillLifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }
    }

    private static async Task SendWebSocketAsync(
        WebSocket connectedWebSocket,
        string payload,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await connectedWebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    internal static bool IsPusherPing(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("event", out var eventElement) &&
                eventElement.ValueKind == JsonValueKind.String &&
                string.Equals(eventElement.GetString(), "pusher:ping", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static async Task WaitForPusherAcknowledgementAsync(
        WebSocket socket,
        string expectedEvent,
        string? expectedChannel,
        CancellationToken cancellationToken,
        TimeSpan? handshakeTimeout = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(handshakeTimeout ?? HandshakeTimeout);
        try
        {
            for (var messageCount = 0; messageCount < 100; messageCount++)
            {
                var payload = await BoundedWebSocketTextReader.ReadAsync(socket, timeout.Token).ConfigureAwait(false);
                if (payload is null)
                {
                    throw new WebSocketException("Kick closed chat before acknowledging the connection.");
                }

                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                var eventName = root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("event", out var eventElement) &&
                    eventElement.ValueKind == JsonValueKind.String
                        ? eventElement.GetString()
                        : null;
                if (string.Equals(eventName, "pusher:ping", StringComparison.Ordinal))
                {
                    await SendWebSocketAsync(socket, """{"event":"pusher:pong","data":{}}""", timeout.Token)
                        .ConfigureAwait(false);
                    continue;
                }

                if (string.Equals(eventName, "pusher:error", StringComparison.Ordinal) ||
                    string.Equals(eventName, "pusher:subscription_error", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Kick rejected the chat handshake: {ReadPusherError(root)}");
                }

                if (!string.Equals(eventName, expectedEvent, StringComparison.Ordinal))
                {
                    continue;
                }

                if (expectedChannel is not null &&
                    (!root.TryGetProperty("channel", out var channelElement) ||
                     channelElement.ValueKind != JsonValueKind.String ||
                     !string.Equals(channelElement.GetString(), expectedChannel, StringComparison.Ordinal)))
                {
                    continue;
                }

                return;
            }

            throw new InvalidDataException("Kick sent too many messages without acknowledging the chat handshake.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for Kick chat acknowledgement '{expectedEvent}'.");
        }
    }

    private static string ReadPusherError(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
        {
            return "unknown error";
        }

        return data.ValueKind == JsonValueKind.String
            ? data.GetString() ?? "unknown error"
            : data.GetRawText();
    }

    private async Task<long?> TryResolveBroadcasterUserIdFromKickApiAsync(string channel, CancellationToken cancellationToken)
    {
        try
        {
            return await KickOAuthService.TryResolveBroadcasterUserIdAsync(channel, settings, logger, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Warning, "KickChat", $"Kick API channel lookup failed for {channel}.", ex);
        }

        return null;
    }

    private async Task<string?> ResolveSendTokenAsync(CancellationToken cancellationToken)
    {
        return await KickOAuthService.GetUsableAccessTokenAsync(settings, logger, cancellationToken);
    }

    private async Task<string> ValidateSendTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (canSendMessages && string.Equals(validatedSendToken, token, StringComparison.Ordinal))
        {
            return token;
        }

        var tokenInfo = await IntrospectTokenAsync(token, cancellationToken);
        if (!tokenInfo.Active)
        {
            var refreshedToken = await TryRefreshAndValidateSendTokenAsync(token, cancellationToken);
            if (!string.IsNullOrWhiteSpace(refreshedToken))
            {
                return refreshedToken;
            }

            throw new InvalidOperationException("The Kick OAuth token is inactive, expired, or revoked.");
        }

        if (!string.Equals(tokenInfo.TokenType, "user", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Kick chat sending requires a user access token.");
        }

        if (!tokenInfo.Scopes.Contains("chat:write"))
        {
            throw new InvalidOperationException("The Kick OAuth token is missing the chat:write scope.");
        }

        validatedSendToken = token;
        canSendMessages = true;
        return token;
    }

    private async Task<string?> TryRefreshAndValidateSendTokenAsync(string previousToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.KickRefreshToken) ||
            string.IsNullOrWhiteSpace(settings.KickClientId) ||
            string.IsNullOrWhiteSpace(settings.KickClientSecret))
        {
            return null;
        }

        try
        {
            var refreshed = await KickOAuthService.RefreshUserTokenAsync(settings, cancellationToken);
            KickOAuthService.ApplyTokenResult(settings, refreshed);
            var refreshedToken = NormalizeBearerToken(refreshed.AccessToken);
            if (string.IsNullOrWhiteSpace(refreshedToken) ||
                string.Equals(refreshedToken, previousToken, StringComparison.Ordinal))
            {
                return null;
            }

            var tokenInfo = await IntrospectTokenAsync(refreshedToken, cancellationToken);
            if (!tokenInfo.Active ||
                !string.Equals(tokenInfo.TokenType, "user", StringComparison.OrdinalIgnoreCase) ||
                !tokenInfo.Scopes.Contains("chat:write"))
            {
                return null;
            }

            validatedSendToken = refreshedToken;
            canSendMessages = true;
            logger.Write(AppLogLevel.Info, "KickChat", "Refreshed expired Kick OAuth token.");
            return refreshedToken;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Warning, "KickChat", "Failed to refresh expired Kick OAuth token.", ex);
            return null;
        }
    }

    private async Task<KickTokenInfo> IntrospectTokenAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://id.kick.com/oauth/token/introspect");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken);
        var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Kick token introspection failed ({(int)response.StatusCode} {response.ReasonPhrase}). {ApiErrorMessage.Extract(responseBody)}");
        }

        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Kick token introspection returned an unexpected response.");
        }

        var active = data.TryGetProperty("active", out var activeProperty) &&
            activeProperty.ValueKind == JsonValueKind.True;
        var tokenType = GetOptionalString(data, "token_type");
        var scopes = ReadScopes(data);

        return new KickTokenInfo(active, tokenType, scopes);
    }

    private static string SanitizeMessage(string message)
    {
        return ChatTextNormalizer.NormalizeSingleLine(message, 500);
    }

    private static bool KickSendResponseIndicatesSuccess(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var root = document.RootElement;
            if (TryGetBool(root, "is_sent") is { } rootIsSent)
            {
                return rootIsSent;
            }

            if (root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                TryGetBool(data, "is_sent") is { } dataIsSent)
            {
                return dataIsSent;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        // A non-empty response that does not explicitly confirm delivery is not
        // safe to treat as sent. Empty success bodies remain supported above.
        return false;
    }

    private sealed record KickTokenInfo(bool Active, string TokenType, HashSet<string> Scopes);
}

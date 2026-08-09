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
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Infrastructure.Chat.OAuthTokenHelpers;
using static StreamlinkVlcStudio.Infrastructure.Processes.ProcessExtensions;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public sealed class KickChatClient : IChatClient, IChatHistoryBackfillClient
{
    private const string PusherAppKey = "32cbd69e4b950bf97679";
    private const int KickRecentChatInitialBackfillLimit = 25;
    private static readonly TimeSpan KickRecentChatBackfillTimeout = TimeSpan.FromSeconds(5);
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly ChatSettings settings;
    private readonly IAppLogger logger;
    private readonly KickChatHistoryBackfillService historyBackfillService;
    private readonly SemaphoreSlim recentChatBackfillGate = new(1, 1);
    private readonly object recentChatBackfillLifecycleGate = new();
    private readonly HashSet<string> requestedRecentChatCursors = new(StringComparer.Ordinal);
    private CancellationTokenSource? recentChatBackfillCancellation = new();
    private Task? recentChatBackfillTask;
    private Task? disposalTask;
    private ClientWebSocket? webSocket;
    private CancellationTokenSource? readCancellation;
    private Task? readTask;
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
        this.httpClient = httpClient ?? new HttpClient();
        ownsHttpClient = httpClient is null;
        this.httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) StreamlinkVlcStudio/0.1");
        this.httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        historyBackfillService = new KickChatHistoryBackfillService(this.httpClient, logger);
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

        await DisconnectAsync(cancellationToken);
        StatusChanged?.Invoke(this, "Resolving Kick chatroom...");
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
                StatusChanged?.Invoke(this, "Kick OAuth token has chat send access.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Write(AppLogLevel.Warning, "KickChat", "Kick token validation failed; chat will be read-only.", ex);
                StatusChanged?.Invoke(this, $"Kick token validation failed: {ex.Message}");
            }
        }

        StatusChanged?.Invoke(this, "Connecting to Kick chat...");
        webSocket = new ClientWebSocket();
        var uri = new Uri($"wss://ws-us2.pusher.com/app/{PusherAppKey}?protocol=7&client=dotnet&version=0.1&flash=false");
        await webSocket.ConnectAsync(uri, cancellationToken);

        var subscribe = JsonSerializer.Serialize(new
        {
            @event = "pusher:subscribe",
            data = new
            {
                auth = "",
                channel = $"chatrooms.{channelInfo.ChatroomId}.v2"
            }
        });
        await SendWebSocketAsync(subscribe, cancellationToken);

        readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTask = Task.Run(() => ReadLoopAsync(target.Channel, readCancellation.Token), CancellationToken.None);
        StatusChanged?.Invoke(this, canSendMessages ? "Kick chat connected with send access." : "Kick chat connected read-only.");
        StartRecentChatBackfill(target.Channel, channelInfo.ChatroomId, readCancellation.Token);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CancelRecentChatBackfillsForDisconnect();
        readCancellation?.Cancel();

        if (webSocket is { State: WebSocketState.Open })
        {
            try
            {
                using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                closeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
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
                await readTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (Exception)
            {
            }
        }

        if (recentChatBackfillTask is not null)
        {
            await recentChatBackfillTask.ConfigureAwait(false);
            recentChatBackfillTask = null;
        }

        await recentChatBackfillGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        recentChatBackfillGate.Release();

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

    public async Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        var sanitized = SanitizeMessage(message);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return;
        }

        var token = await ResolveSendTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Connect Kick in Settings before sending chat.");
        }

        token = await ValidateSendTokenAsync(token, cancellationToken);

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

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Kick chat send failed ({(int)response.StatusCode} {response.ReasonPhrase}). {ExtractApiMessage(responseBody)}");
        }

        if (!KickSendResponseIndicatesSuccess(responseBody))
        {
            throw new InvalidOperationException($"Kick chat did not confirm the message was sent. {ExtractApiMessage(responseBody)}");
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

        await DisconnectAsync();
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }

        recentChatBackfillGate.Dispose();
        historyBackfillService.Dispose();
    }

    public async Task<ChatHistoryBackfillResult> BackfillRecentChatRangeAsync(
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc,
        CancellationToken cancellationToken = default)
    {
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
        string? channelId = null;
        string? chatroomId = null;
        long? broadcasterUserId = null;
        var hasConfiguredBroadcasterUserId = false;

        if (settings.KickChatroomIds.TryGetValue(channel, out var configured) && !string.IsNullOrWhiteSpace(configured))
        {
            chatroomId = configured.Trim();
        }

        if (settings.KickBroadcasterUserIds.TryGetValue(channel, out var configuredBroadcasterUserId) &&
            long.TryParse(configuredBroadcasterUserId, out var parsedConfiguredBroadcasterUserId))
        {
            broadcasterUserId = parsedConfiguredBroadcasterUserId;
            hasConfiguredBroadcasterUserId = true;
        }

        try
        {
            var url = $"https://kick.com/api/v2/channels/{Uri.EscapeDataString(channel)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri($"https://kick.com/{Uri.EscapeDataString(channel)}");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            if (root.TryGetProperty("id", out var id))
            {
                channelId = id.ToString();
            }

            if (string.IsNullOrWhiteSpace(channelId) &&
                root.TryGetProperty("channel_id", out var channelIdProperty))
            {
                channelId = channelIdProperty.ToString();
            }

            if (string.IsNullOrWhiteSpace(chatroomId) &&
                root.TryGetProperty("chatroom", out var chatroom) &&
                chatroom.TryGetProperty("id", out var chatroomIdValue))
            {
                chatroomId = chatroomIdValue.ToString();
            }

            if (string.IsNullOrWhiteSpace(chatroomId) &&
                root.TryGetProperty("chatroom_id", out var chatroomIdProperty))
            {
                chatroomId = chatroomIdProperty.ToString();
            }

            if (broadcasterUserId is null &&
                root.TryGetProperty("user", out var user) &&
                user.TryGetProperty("id", out var userId))
            {
                broadcasterUserId = TryGetInt64(userId);
            }

            if (broadcasterUserId is null &&
                root.TryGetProperty("broadcaster_user_id", out var broadcasterUserIdProperty))
            {
                broadcasterUserId = TryGetInt64(broadcasterUserIdProperty);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = string.IsNullOrWhiteSpace(chatroomId)
                ? $"Kick public channel metadata failed for {channel}."
                : $"Kick public channel metadata failed for {channel}; using configured chatroom ID.";
            logger.Write(AppLogLevel.Warning, "KickChat", message, ex);
        }

        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(chatroomId))
        {
            var curlMetadata = await TryResolveChannelInfoWithCurlAsync(channel, cancellationToken);
            if (curlMetadata is not null)
            {
                if (string.IsNullOrWhiteSpace(channelId))
                {
                    channelId = curlMetadata.ChannelId;
                }

                if (string.IsNullOrWhiteSpace(chatroomId))
                {
                    chatroomId = curlMetadata.ChatroomId;
                }

                broadcasterUserId ??= curlMetadata.BroadcasterUserId;
                logger.Write(AppLogLevel.Info, "KickChat", $"Resolved Kick chatroom ID for {channel} with curl fallback.");
            }
        }

        if (!settings.KickSendAsBot && !hasConfiguredBroadcasterUserId)
        {
            broadcasterUserId = await TryResolveBroadcasterUserIdFromKickApiAsync(channel, cancellationToken) ??
                broadcasterUserId;
        }

        return new KickChannelInfo(NormalizeNumericId(channelId), NormalizeNumericId(chatroomId), broadcasterUserId);
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
            MessageReceived?.Invoke(this, message);
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

                loadedMessages.AddRange(page.Messages);
                var pageOldest = page.Messages[0].Timestamp.ToUniversalTime();
                oldestRecentChatTimestampUtc = oldestRecentChatTimestampUtc is { } existingOldest &&
                    existingOldest <= pageOldest
                        ? existingOldest
                        : pageOldest;

                var nextCursor = NormalizeCursor(page.Cursor);
                if (string.IsNullOrWhiteSpace(nextCursor) ||
                    string.Equals(nextCursor, cursor, StringComparison.Ordinal))
                {
                    recentChatBackfillExhausted = true;
                    break;
                }

                recentChatNextCursor = nextCursor;
            }

            foreach (var message in loadedMessages
                .OrderBy(message => message.Timestamp)
                .ThenBy(message => message.MessageId, StringComparer.Ordinal))
            {
                MessageReceived?.Invoke(this, message);
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(KickRecentChatBackfillTimeout);
        try
        {
            var escapedChatroomId = Uri.EscapeDataString(chatroomId);
            var escapedChannel = Uri.EscapeDataString(channel);
            var url = KickChatApi.BuildRecentMessagesUrl(escapedChatroomId, cursor, startTimeUtc: null);

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                url);
            request.Headers.Referrer = new Uri($"https://kick.com/{escapedChannel}");

            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    recentChatDirectBackfillBlocked = true;
                }

                logger.Write(
                    AppLogLevel.Info,
                    "KickChat",
                    $"Kick recent chat backfill failed for {channel}: {(int)response.StatusCode} {response.ReasonPhrase}.");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            return ReadKickRecentChatPage(document.RootElement, channel);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.Write(AppLogLevel.Info, "KickChat", $"Kick recent chat backfill timed out for {channel}.");
            return null;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Info, "KickChat", $"Kick recent chat backfill failed for {channel}.", ex);
            return null;
        }
    }

    private async Task<KickRecentChatPage?> TryReadKickRecentMessagesWithCurlAsync(
        string channel,
        string chatroomId,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var curlPath = ResolveCurlPath();
        if (string.IsNullOrWhiteSpace(curlPath))
        {
            logger.Write(AppLogLevel.Warning, "KickChat", "curl.exe was not found; Kick recent chat backfill fallback is unavailable.");
            return null;
        }

        var escapedChannel = Uri.EscapeDataString(channel);
        var escapedChatroomId = Uri.EscapeDataString(chatroomId);
        var startInfo = new ProcessStartInfo
        {
            FileName = curlPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in BuildKickRecentMessagesCurlArguments(escapedChannel, escapedChatroomId, cursor))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(18));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                await KillProcessTreeAsync(process).ConfigureAwait(false);
                await ObserveOutputReadsAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                logger.Write(AppLogLevel.Info, "KickChat", $"curl.exe timed out loading recent Kick chat for {channel}.");
                return null;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                logger.Write(AppLogLevel.Info, "KickChat", $"curl.exe failed loading recent Kick chat for {channel}: {stderr.Trim()}");
                return null;
            }

            using var document = JsonDocument.Parse(stdout);
            var page = ReadKickRecentChatPage(document.RootElement, channel);
            logger.Write(AppLogLevel.Info, "KickChat", $"Loaded recent Kick chat for {channel} with curl fallback.");
            return page;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Info, "KickChat", $"curl.exe recent chat fallback failed for {channel}.", ex);
            return null;
        }
    }

    private static KickRecentChatPage ReadKickRecentChatPage(JsonElement root, string channel)
    {
        if (!TryGetKickRecentChatMessageArray(root, out var messagesElement))
        {
            return new KickRecentChatPage([], ReadKickRecentChatCursor(root));
        }

        var messages = new List<ChatMessage>();
        foreach (var item in messagesElement.EnumerateArray())
        {
            var message = KickPusherParser.TryParseMessageData(item, channel);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        var orderedMessages = messages
            .OrderBy(message => message.Timestamp)
            .ThenBy(message => message.MessageId, StringComparer.Ordinal)
            .ToArray();
        return new KickRecentChatPage(orderedMessages, ReadKickRecentChatCursor(root));
    }

    private static bool TryGetKickRecentChatMessageArray(JsonElement root, out JsonElement messages)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("data", out var data))
            {
                if (data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("messages", out messages) &&
                    messages.ValueKind == JsonValueKind.Array)
                {
                    return true;
                }

                if (data.ValueKind == JsonValueKind.Array)
                {
                    messages = data;
                    return true;
                }
            }

            if (root.TryGetProperty("messages", out messages) &&
                messages.ValueKind == JsonValueKind.Array)
            {
                return true;
            }
        }

        messages = default;
        return false;
    }

    private static string? ReadKickRecentChatCursor(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            TryReadNonEmptyString(data, "cursor", out var dataCursor))
        {
            return dataCursor;
        }

        return TryReadNonEmptyString(root, "cursor", out var rootCursor)
            ? rootCursor
            : null;
    }

    private static bool TryReadNonEmptyString(JsonElement element, string propertyName, out string value)
    {
        value = "";
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        value = property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : property.ToString();
        value = value.Trim();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? NormalizeCursor(string? cursor)
    {
        return string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim();
    }

    private static string? NormalizeNumericId(string? value)
    {
        var normalized = NormalizeCursor(value);
        return normalized is not null && normalized.All(char.IsDigit)
            ? normalized
            : null;
    }

    private void ResetRecentChatBackfillState(string? channelId, string chatroomId)
    {
        currentChannelId = NormalizeNumericId(channelId);
        currentChatroomId = chatroomId;
        recentChatNextCursor = null;
        oldestRecentChatTimestampUtc = null;
        requestedRecentChatCursors.Clear();
        recentChatBackfillExhausted = false;
    }

    private async Task<KickChannelInfo?> TryResolveChannelInfoWithCurlAsync(string channel, CancellationToken cancellationToken)
    {
        var curlPath = ResolveCurlPath();
        if (string.IsNullOrWhiteSpace(curlPath))
        {
            logger.Write(AppLogLevel.Warning, "KickChat", "curl.exe was not found; Kick chatroom metadata fallback is unavailable.");
            return null;
        }

        var escapedChannel = Uri.EscapeDataString(channel);
        var startInfo = new ProcessStartInfo
        {
            FileName = curlPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in BuildKickMetadataCurlArguments(escapedChannel))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(18));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                await KillProcessTreeAsync(process).ConfigureAwait(false);
                await ObserveOutputReadsAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                logger.Write(AppLogLevel.Warning, "KickChat", $"curl.exe timed out resolving Kick metadata for {channel}.");
                return null;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                logger.Write(AppLogLevel.Warning, "KickChat", $"curl.exe failed resolving Kick metadata for {channel}: {stderr.Trim()}");
                return null;
            }

            using var document = JsonDocument.Parse(stdout);
            return ReadKickChannelInfo(document.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Warning, "KickChat", $"curl.exe metadata fallback failed for {channel}.", ex);
            return null;
        }
    }

    private async Task ReadLoopAsync(string channel, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var pending = new ArraySegment<byte>(buffer);

        try
        {
            while (!cancellationToken.IsCancellationRequested && webSocket is { State: WebSocketState.Open })
            {
                using var memory = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await webSocket.ReceiveAsync(pending, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    memory.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var payload = Encoding.UTF8.GetString(memory.ToArray());
                if (payload.Contains("\"pusher:ping\"", StringComparison.OrdinalIgnoreCase))
                {
                    await SendWebSocketAsync("""{"event":"pusher:pong","data":{}}""", cancellationToken);
                    continue;
                }

                var message = KickPusherParser.TryParse(payload, channel);
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
            logger.Write(AppLogLevel.Warning, "KickChat", "Kick chat disconnected.", ex);
            StatusChanged?.Invoke(this, $"Kick chat disconnected: {ex.Message}");
        }
    }

    private async Task SendWebSocketAsync(string payload, CancellationToken cancellationToken)
    {
        if (webSocket is null)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(payload);
        await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
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

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Kick token introspection failed ({(int)response.StatusCode} {response.ReasonPhrase}). {ExtractApiMessage(responseBody)}");
        }

        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Kick token introspection returned an unexpected response.");
        }

        var active = data.TryGetProperty("active", out var activeProperty) &&
            activeProperty.ValueKind == JsonValueKind.True;
        var tokenType = data.TryGetProperty("token_type", out var tokenTypeProperty)
            ? tokenTypeProperty.GetString() ?? ""
            : "";
        var scopes = ReadScopes(data);

        return new KickTokenInfo(active, tokenType, scopes);
    }

    private static string SanitizeMessage(string message)
    {
        return ChatTextNormalizer.NormalizeSingleLine(message, 500);
    }

    private static string? ResolveCurlPath()
    {
        var configured = Environment.GetEnvironmentVariable("STREAMLINK_KICK_CURL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        return "curl.exe";
    }

    private static IEnumerable<string> BuildKickMetadataCurlArguments(string escapedChannel)
    {
        yield return "--location";
        yield return "--silent";
        yield return "--show-error";
        yield return "--fail";
        yield return "--compressed";
        yield return "--max-time";
        yield return "15";
        yield return "--user-agent";
        yield return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";
        yield return "--header";
        yield return "Accept: application/json,text/plain,*/*";
        yield return "--header";
        yield return "Accept-Language: *";
        yield return "--referer";
        yield return $"https://kick.com/{escapedChannel}";
        yield return $"https://kick.com/api/v2/channels/{escapedChannel}";
    }

    private static IEnumerable<string> BuildKickRecentMessagesCurlArguments(
        string escapedChannel,
        string escapedChatroomId,
        string? cursor)
    {
        yield return "--location";
        yield return "--silent";
        yield return "--show-error";
        yield return "--fail";
        yield return "--compressed";
        yield return "--max-time";
        yield return "15";
        yield return "--user-agent";
        yield return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";
        yield return "--header";
        yield return "Accept: application/json,text/plain,*/*";
        yield return "--header";
        yield return "Accept-Language: *";
        yield return "--referer";
        yield return $"https://kick.com/{escapedChannel}";
        yield return KickChatApi.BuildRecentMessagesUrl(escapedChatroomId, cursor, startTimeUtc: null);
    }

    private static KickChannelInfo ReadKickChannelInfo(JsonElement root)
    {
        string? channelId = null;
        string? chatroomId = null;
        long? broadcasterUserId = null;

        if (root.TryGetProperty("id", out var rootId))
        {
            channelId = rootId.ToString();
        }

        if (string.IsNullOrWhiteSpace(channelId) &&
            root.TryGetProperty("channel_id", out var channelIdProperty))
        {
            channelId = channelIdProperty.ToString();
        }

        if (string.IsNullOrWhiteSpace(channelId) &&
            root.TryGetProperty("data", out var data) &&
            data.TryGetProperty("id", out var dataId))
        {
            channelId = dataId.ToString();
        }

        if (string.IsNullOrWhiteSpace(channelId) &&
            root.TryGetProperty("data", out data) &&
            data.TryGetProperty("channel_id", out var dataChannelId))
        {
            channelId = dataChannelId.ToString();
        }

        if (root.TryGetProperty("chatroom", out var chatroom) &&
            chatroom.TryGetProperty("id", out var id))
        {
            chatroomId = id.ToString();
        }

        if (string.IsNullOrWhiteSpace(chatroomId) &&
            root.TryGetProperty("data", out data) &&
            data.TryGetProperty("chatroom", out var dataChatroom) &&
            dataChatroom.TryGetProperty("id", out var dataChatroomId))
        {
            chatroomId = dataChatroomId.ToString();
        }

        if (string.IsNullOrWhiteSpace(chatroomId) &&
            root.TryGetProperty("chatroom_id", out var chatroomIdProperty))
        {
            chatroomId = chatroomIdProperty.ToString();
        }

        if (root.TryGetProperty("user", out var user) &&
            user.TryGetProperty("id", out var userId))
        {
            broadcasterUserId = TryGetInt64(userId);
        }

        if (broadcasterUserId is null &&
            root.TryGetProperty("data", out data) &&
            data.TryGetProperty("user", out var dataUser) &&
            dataUser.TryGetProperty("id", out var dataUserId))
        {
            broadcasterUserId = TryGetInt64(dataUserId);
        }

        if (broadcasterUserId is null &&
            root.TryGetProperty("broadcaster_user_id", out var broadcasterUserIdProperty))
        {
            broadcasterUserId = TryGetInt64(broadcasterUserIdProperty);
        }

        return new KickChannelInfo(
            NormalizeNumericId(channelId),
            NormalizeNumericId(chatroomId),
            broadcasterUserId);
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
            if (document.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("is_sent", out var isSent) &&
                isSent.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return isSent.GetBoolean();
            }
        }
        catch (JsonException)
        {
        }

        return true;
    }

    private static string ExtractApiMessage(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
        }

        return responseBody.Length <= 240 ? responseBody : responseBody[..240];
    }

    private sealed record KickChannelInfo(string? ChannelId, string? ChatroomId, long? BroadcasterUserId);

    private sealed record KickRecentChatPage(IReadOnlyList<ChatMessage> Messages, string? Cursor);

    private sealed record KickTokenInfo(bool Active, string TokenType, HashSet<string> Scopes);
}

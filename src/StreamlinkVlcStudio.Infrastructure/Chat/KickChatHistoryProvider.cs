using System.Globalization;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public sealed class KickChatHistoryProvider : IKickChatHistoryProvider, IDisposable
{
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly KickChatHistoryBackfillService backfillService;

    public KickChatHistoryProvider(IAppLogger logger, HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? HttpClientFactory.CreateDefault();
        ownsHttpClient = httpClient is null;
        KickHttpHeaders.Configure(this.httpClient);
        backfillService = new KickChatHistoryBackfillService(this.httpClient, logger);
    }

    public async Task<ChatHistoryBackfillResult> BackfillRecentChatRangeAsync(
        StreamTarget target,
        ChatSettings settings,
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc,
        CancellationToken cancellationToken = default)
    {
        if (target.Platform != PlatformKind.Kick)
        {
            return new ChatHistoryBackfillResult(false, 0, false, null, null);
        }

        var channelInfo = await backfillService
            .ResolveChannelInfoAsync(target.Channel, settings, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(channelInfo.ChatroomId))
        {
            return new ChatHistoryBackfillResult(false, 0, false, null, null);
        }

        return await backfillService
            .BackfillRecentChatFromStartTimeAsync(
                target.Channel,
                channelInfo.ChannelId,
                channelInfo.ChatroomId,
                fromTimestampUtc,
                throughTimestampUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

}

internal sealed class KickChatHistoryBackfillService
{
    private const int KickRecentChatSeekBackfillLimit = 2_500;
    private readonly KickChatTransport transport;
    private readonly IAppLogger logger;
    private readonly SemaphoreSlim backfillGate = new(1, 1);
    private bool directBackfillBlocked;
    private string? directBackfillBlockedKey;

    public KickChatHistoryBackfillService(HttpClient httpClient, IAppLogger logger)
    {
        transport = new KickChatTransport(httpClient, logger);
        this.logger = logger;
    }

    public async Task<KickChannelInfo> ResolveChannelInfoAsync(
        string channel,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        return await transport
            .ResolveChannelInfoAsync(channel, settings, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ChatHistoryBackfillResult> BackfillRecentChatFromStartTimeAsync(
        string channel,
        string? channelId,
        string chatroomId,
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc,
        CancellationToken cancellationToken)
    {
        var candidateIds = KickChatTransport.BuildMessagesChannelIds(channelId, chatroomId).ToArray();
        if (candidateIds.Length == 0)
        {
            return new ChatHistoryBackfillResult(false, 0, false, null, null);
        }

        fromTimestampUtc = fromTimestampUtc.ToUniversalTime();
        throughTimestampUtc = throughTimestampUtc.ToUniversalTime();
        if (throughTimestampUtc < fromTimestampUtc)
        {
            throughTimestampUtc = fromTimestampUtc;
        }

        await backfillGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var backfillKey = string.Join("|", candidateIds);
            if (!string.Equals(directBackfillBlockedKey, backfillKey, StringComparison.Ordinal))
            {
                directBackfillBlockedKey = backfillKey;
                directBackfillBlocked = false;
            }

            var attempted = false;
            var hasFailure = false;
            var verifiedEmptyIds = new HashSet<string>(StringComparer.Ordinal);
            ChatHistoryBackfillResult? emptyResult = null;
            if (!directBackfillBlocked)
            {
                foreach (var messagesChannelId in candidateIds)
                {
                    var page = await TryReadKickRecentMessagesFromStartTimeDirectAsync(
                            channel,
                            messagesChannelId,
                            fromTimestampUtc,
                            cancellationToken)
                        .ConfigureAwait(false);
                    attempted = true;
                    if (page is null)
                    {
                        hasFailure = true;
                        if (directBackfillBlocked)
                        {
                            break;
                        }

                        continue;
                    }

                    var result = await BackfillKickStartTimePageRangeAsync(
                            channel,
                            messagesChannelId,
                            page,
                            fromTimestampUtc,
                            throughTimestampUtc,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (result.LoadedMessageCount > 0)
                    {
                        return result;
                    }

                    if (result.CoveredRequestedRange)
                    {
                        verifiedEmptyIds.Add(messagesChannelId);
                        emptyResult ??= result;
                    }
                    else
                    {
                        hasFailure = true;
                    }
                }
            }

            if (verifiedEmptyIds.Count == candidateIds.Length)
            {
                return emptyResult ?? new ChatHistoryBackfillResult(
                    attempted,
                    0,
                    true,
                    fromTimestampUtc,
                    throughTimestampUtc);
            }

            if (directBackfillBlocked || hasFailure)
            {
                foreach (var messagesChannelId in candidateIds)
                {
                    if (verifiedEmptyIds.Contains(messagesChannelId))
                    {
                        continue;
                    }

                    var page = await TryReadKickRecentMessagesFromStartTimeWithCurlAsync(
                            channel,
                            messagesChannelId,
                            fromTimestampUtc,
                            cancellationToken)
                        .ConfigureAwait(false);
                    attempted = true;
                    if (page is null)
                    {
                        hasFailure = true;
                        continue;
                    }

                    var result = await BackfillKickStartTimePageRangeAsync(
                            channel,
                            messagesChannelId,
                            page,
                            fromTimestampUtc,
                            throughTimestampUtc,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (result.LoadedMessageCount > 0)
                    {
                        return result;
                    }

                    if (result.CoveredRequestedRange)
                    {
                        verifiedEmptyIds.Add(messagesChannelId);
                        emptyResult ??= result;
                    }
                    else
                    {
                        hasFailure = true;
                    }
                }
            }

            if (verifiedEmptyIds.Count == candidateIds.Length)
            {
                return emptyResult ?? new ChatHistoryBackfillResult(
                    attempted,
                    0,
                    true,
                    fromTimestampUtc,
                    throughTimestampUtc);
            }

            return new ChatHistoryBackfillResult(attempted || hasFailure, 0, false, null, null);
        }
        finally
        {
            backfillGate.Release();
        }
    }

    private async Task<ChatHistoryBackfillResult> BackfillKickStartTimePageRangeAsync(
        string channel,
        string messagesChannelId,
        KickRecentChatPage firstPage,
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc,
        CancellationToken cancellationToken)
    {
        var loadedMessages = new List<ChatMessage>();
        var seenMessageKeys = new HashSet<string>(StringComparer.Ordinal);
        var requestedCursors = new HashSet<string>(StringComparer.Ordinal);
        var page = firstPage;
        var pageCount = 0;
        var completionReason = "through";
        while (true)
        {
            pageCount++;
            var loadedThroughBeforePageUtc = GetLatestMessageTimestampUtc(loadedMessages);
            var pageLatestTimestampUtc = GetLatestMessageTimestampUtc(page.Messages);
            if (pageCount > 1 &&
                loadedThroughBeforePageUtc is { } loadedThroughBeforePage &&
                (pageLatestTimestampUtc is null || pageLatestTimestampUtc < loadedThroughBeforePage))
            {
                completionReason = "non-advancing";
                break;
            }

            AddKickBackfillPageMessages(
                loadedMessages,
                seenMessageKeys,
                page,
                KickRecentChatSeekBackfillLimit);

            var loadedThroughTimestampUtc = GetLatestMessageTimestampUtc(loadedMessages);
            if (loadedThroughTimestampUtc is { } loadedThrough &&
                loadedThrough >= throughTimestampUtc)
            {
                break;
            }

            if (loadedMessages.Count >= KickRecentChatSeekBackfillLimit)
            {
                completionReason = "cap";
                break;
            }

            if (pageCount >= KickRecentChatSeekBackfillLimit)
            {
                completionReason = "page-cap";
                break;
            }

            var cursor = KickChatTransport.NormalizeCursor(page.Cursor);
            if (string.IsNullOrWhiteSpace(cursor) ||
                !requestedCursors.Add(cursor))
            {
                completionReason = "exhausted";
                break;
            }

            var nextPage = await TryReadKickRecentMessagesCursorPageAsync(
                    channel,
                    messagesChannelId,
                    cursor,
                    cancellationToken)
                .ConfigureAwait(false);
            if (nextPage is null)
            {
                completionReason = "failure";
                break;
            }

            page = nextPage;
        }

        var orderedMessages = OrderKickMessages(loadedMessages).ToArray();
        var requestedRangeMessages = FilterKickBackfillMessagesToRequestedRange(
            orderedMessages,
            fromTimestampUtc,
            throughTimestampUtc);
        ChatHistoryBackfillResult result;
        if (completionReason == "exhausted" && orderedMessages.Length == 0)
        {
            result = new ChatHistoryBackfillResult(
                Attempted: true,
                LoadedMessageCount: requestedRangeMessages.Length,
                CoveredRequestedRange: true,
                CoveredFromTimestampUtc: fromTimestampUtc,
                CoveredThroughTimestampUtc: throughTimestampUtc,
                Messages: requestedRangeMessages);
            LogKickStartTimeBackfillPageRange(channel, messagesChannelId, pageCount, completionReason, result);
            return result;
        }

        var partialThroughTimestampUtc = GetLatestMessageTimestampUtc(orderedMessages);
        if (partialThroughTimestampUtc is null)
        {
            result = new ChatHistoryBackfillResult(true, 0, false, null, null, requestedRangeMessages);
            LogKickStartTimeBackfillPageRange(channel, messagesChannelId, pageCount, completionReason, result);
            return result;
        }

        if (completionReason == "failure")
        {
            result = new ChatHistoryBackfillResult(
                Attempted: true,
                LoadedMessageCount: requestedRangeMessages.Length,
                CoveredRequestedRange: false,
                CoveredFromTimestampUtc: null,
                CoveredThroughTimestampUtc: null,
                Messages: requestedRangeMessages);
            LogKickStartTimeBackfillPageRange(channel, messagesChannelId, pageCount, completionReason, result);
            return result;
        }

        if (partialThroughTimestampUtc.Value < fromTimestampUtc)
        {
            partialThroughTimestampUtc = fromTimestampUtc;
        }

        var coveredRequestedRange = partialThroughTimestampUtc.Value >= throughTimestampUtc;
        result = new ChatHistoryBackfillResult(
            Attempted: true,
            LoadedMessageCount: requestedRangeMessages.Length,
            CoveredRequestedRange: coveredRequestedRange,
            CoveredFromTimestampUtc: fromTimestampUtc,
            CoveredThroughTimestampUtc: coveredRequestedRange ? throughTimestampUtc : partialThroughTimestampUtc,
            Messages: requestedRangeMessages);
        LogKickStartTimeBackfillPageRange(channel, messagesChannelId, pageCount, completionReason, result);
        return result;
    }

    private void LogKickStartTimeBackfillPageRange(
        string channel,
        string messagesChannelId,
        int pageCount,
        string completionReason,
        ChatHistoryBackfillResult result)
    {
        logger.Write(
            AppLogLevel.Debug,
            "KickChat",
            $"Kick seekback start_time page range for {channel} via {messagesChannelId}: " +
            $"pages={pageCount.ToString(CultureInfo.InvariantCulture)}, loaded={result.LoadedMessageCount.ToString(CultureInfo.InvariantCulture)}, " +
            $"reason={completionReason}, covered={result.CoveredRequestedRange}, " +
            $"range={FormatBackfillTimestamp(result.CoveredFromTimestampUtc)} through {FormatBackfillTimestamp(result.CoveredThroughTimestampUtc)}.");
    }

    private async Task<KickRecentChatPage?> TryReadKickRecentMessagesCursorPageAsync(
        string channel,
        string messagesChannelId,
        string cursor,
        CancellationToken cancellationToken)
    {
        var page = directBackfillBlocked
            ? null
            : await TryReadKickRecentMessagesDirectAsync(channel, messagesChannelId, cursor, cancellationToken).ConfigureAwait(false);
        return page ?? await TryReadKickRecentMessagesWithCurlAsync(channel, messagesChannelId, cursor, cancellationToken).ConfigureAwait(false);
    }

    private async Task<KickRecentChatPage?> TryReadKickRecentMessagesDirectAsync(
        string channel,
        string messagesChannelId,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var result = await transport.ReadRecentMessagesDirectAsync(
                channel,
                messagesChannelId,
                cursor,
                startTimeUtc: null,
                cancellationToken)
            .ConfigureAwait(false);
        directBackfillBlocked |= result.DirectForbidden;
        return result.Page;
    }

    private async Task<KickRecentChatPage?> TryReadKickRecentMessagesFromStartTimeDirectAsync(
        string channel,
        string messagesChannelId,
        DateTimeOffset startTimeUtc,
        CancellationToken cancellationToken)
    {
        var result = await transport.ReadRecentMessagesDirectAsync(
                channel,
                messagesChannelId,
                cursor: null,
                startTimeUtc,
                cancellationToken)
            .ConfigureAwait(false);
        directBackfillBlocked |= result.DirectForbidden;
        return result.Page;
    }

    private async Task<KickRecentChatPage?> TryReadKickRecentMessagesWithCurlAsync(
        string channel,
        string messagesChannelId,
        string? cursor,
        CancellationToken cancellationToken)
    {
        return await transport.ReadRecentMessagesWithCurlAsync(
                channel,
                messagesChannelId,
                cursor,
                startTimeUtc: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<KickRecentChatPage?> TryReadKickRecentMessagesFromStartTimeWithCurlAsync(
        string channel,
        string messagesChannelId,
        DateTimeOffset startTimeUtc,
        CancellationToken cancellationToken)
    {
        return await transport.ReadRecentMessagesWithCurlAsync(
                channel,
                messagesChannelId,
                cursor: null,
                startTimeUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void AddKickBackfillPageMessages(
        List<ChatMessage> loadedMessages,
        HashSet<string> seenMessageKeys,
        KickRecentChatPage page,
        int maxMessages)
    {
        foreach (var message in OrderKickMessages(page.Messages))
        {
            if (loadedMessages.Count >= maxMessages)
            {
                break;
            }

            if (seenMessageKeys.Add(GetKickMessageDeduplicationKey(message)))
            {
                loadedMessages.Add(message);
            }
        }
    }

    private static string GetKickMessageDeduplicationKey(ChatMessage message)
    {
        return string.IsNullOrWhiteSpace(message.MessageId)
            ? $"{message.Timestamp.ToUniversalTime().UtcTicks}:{message.Username}:{message.Message}"
            : message.MessageId;
    }

    private static IEnumerable<ChatMessage> OrderKickMessages(IEnumerable<ChatMessage> messages)
    {
        return messages
            .OrderBy(message => message.Timestamp)
            .ThenBy(message => message.MessageId, StringComparer.Ordinal);
    }

    private static DateTimeOffset? GetLatestMessageTimestampUtc(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return null;
        }

        return messages
            .Max(message => message.Timestamp)
            .ToUniversalTime();
    }

    private static ChatMessage[] FilterKickBackfillMessagesToRequestedRange(
        IReadOnlyList<ChatMessage> messages,
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc)
    {
        if (messages.Count == 0)
        {
            return [];
        }

        fromTimestampUtc = fromTimestampUtc.ToUniversalTime();
        throughTimestampUtc = throughTimestampUtc.ToUniversalTime();
        return messages
            .Where(message =>
            {
                var timestampUtc = message.Timestamp.ToUniversalTime();
                return timestampUtc >= fromTimestampUtc && timestampUtc <= throughTimestampUtc;
            })
            .ToArray();
    }

    private static string FormatBackfillTimestamp(DateTimeOffset? timestampUtc)
    {
        return timestampUtc is { } timestamp
            ? timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : "none";
    }

}

internal sealed record KickRecentChatPage(IReadOnlyList<ChatMessage> Messages, string? Cursor);

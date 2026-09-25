using System.Globalization;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Core.Time;
using StreamlinkVlcStudio.Infrastructure.Chat;

namespace StreamlinkVlcStudio.Infrastructure.Vod;

/// <summary>
/// Reads Kick VOD chat from the public recent-messages endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Kick has no VOD-specific chat endpoint; the same
/// <c>/api/v2/channels/{id}/messages</c> resource that the live client uses also serves history,
/// which has been observed to reach back at least 90 days — far beyond the VOD retention window.
/// </para>
/// <para>
/// Two properties of that endpoint drive this design:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>?cursor=</c> pages <b>backwards</b>, returning roughly 25 messages in descending time order
/// that sit strictly before the cursor, plus the cursor for the next (older) page. It does not page
/// forwards, so a chunk is gathered by walking back from the end of the chunk to its start.
/// </description></item>
/// <item><description>
/// The cursor is a timestamp in microseconds since the Unix epoch, so one can be synthesized for
/// any instant. That removes the need for a <c>?start_time=</c> seed request, which only ever
/// returns the two or three messages immediately after the supplied time.
/// </description></item>
/// </list>
/// </remarks>
internal sealed class KickVodChatFetcher
{
    /// <summary>How much VOD time one fetch covers. Small enough that a busy chat still fits the page budget.</summary>
    internal static readonly TimeSpan ChunkSize = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Backward pages allowed per chunk. If this budget is exhausted, leave the range retryable
    /// rather than claiming that messages we never retrieved have been covered.
    /// </summary>
    private const int MaximumPagesPerChunk = 20;

    private readonly KickChatTransport transport;
    private readonly IAppLogger logger;
    private readonly object gate = new();
    private readonly Dictionary<string, string> resolvedChannelIds = new(StringComparer.OrdinalIgnoreCase);
    private bool directRequestsForbidden;

    public KickVodChatFetcher(
        HttpClient httpClient,
        IAppLogger logger,
        Func<string, string, CancellationToken, Task<string?>>? curlOverride = null)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        transport = new KickChatTransport(httpClient, this.logger, curlOverride);
    }

    public async Task<VodChatFetchResult> FetchAsync(
        ReplaySessionInfo replay,
        AppSettings settings,
        TimeSpan fromOffset,
        CancellationToken cancellationToken)
    {
        if (replay.StreamStartedAtUtc is not { } startedAt)
        {
            return VodChatFetchResult.Unsupported(
                "Kick VOD chat needs the broadcast start time so messages can be aligned to playback, " +
                "and Kick did not report one for this video.");
        }

        if (fromOffset < TimeSpan.Zero)
        {
            fromOffset = TimeSpan.Zero;
        }

        var startedAtUtc = startedAt.ToUniversalTime();
        if (!DurationValues.TryAdd(fromOffset, ChunkSize, out var throughOffset) ||
            !DurationValues.TryAdd(startedAtUtc, fromOffset, out var fromTimestampUtc) ||
            !DurationValues.TryAdd(startedAtUtc, throughOffset, out var throughTimestampUtc))
        {
            return VodChatFetchResult.Unsupported(
                "The Kick VOD chat time range falls outside the supported date range.");
        }

        var channelIds = await ResolveMessagesChannelIdsAsync(replay, settings, cancellationToken)
            .ConfigureAwait(false);
        if (channelIds.Count == 0)
        {
            return VodChatFetchResult.Unsupported(
                $"Kick did not report a channel or chatroom id for {replay.Channel}, " +
                "so its VOD chat cannot be looked up.");
        }

        var failureReason = "";
        IReadOnlyList<ChatMessage> partialMessages = [];
        foreach (var channelId in channelIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = await ReadChunkAsync(
                    replay.Channel,
                    channelId,
                    fromTimestampUtc,
                    throughTimestampUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            if (chunk.FailureReason is { } reason)
            {
                failureReason = reason;
                if (chunk.Messages.Count > partialMessages.Count) partialMessages = chunk.Messages;
                continue;
            }

            RememberResolvedChannelId(replay.Channel, channelId);
            var messages = ProjectToOffsets(chunk.Messages, startedAtUtc, fromOffset, throughOffset);
            return VodChatFetchResult.Loaded(messages, throughOffset);
        }

        return VodChatFetchResult.Failed(
            string.IsNullOrWhiteSpace(failureReason)
                ? $"Kick VOD chat could not be read for {replay.Channel}."
                : failureReason,
            ProjectToOffsets(partialMessages, startedAtUtc, fromOffset, throughOffset));
    }

    /// <summary>
    /// Walks backward from the end of the chunk until a page reaches past its start, then returns
    /// everything gathered. Messages outside the chunk are dropped by <see cref="ProjectToOffsets"/>.
    /// </summary>
    private async Task<KickVodChatChunk> ReadChunkAsync(
        string channel,
        string messagesChannelId,
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc,
        CancellationToken cancellationToken)
    {
        var collected = new List<ChatMessage>();
        var seenMessageKeys = new HashSet<string>(StringComparer.Ordinal);
        var requestedCursors = new HashSet<string>(StringComparer.Ordinal);
        var cursor = ToCursor(throughTimestampUtc);
        for (var pageIndex = 0; pageIndex < MaximumPagesPerChunk; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!requestedCursors.Add(cursor))
            {
                return new KickVodChatChunk(collected, "Kick repeated a VOD chat cursor before covering the requested time range.");
            }

            var page = await ReadPageAsync(channel, messagesChannelId, cursor, cancellationToken)
                .ConfigureAwait(false);
            if (page is null)
            {
                // Pages run backwards: newer messages cannot establish coverage of the missing
                // beginning of the range. Retry the original offset when a later page fails.
                return new KickVodChatChunk(collected, $"Kick did not answer the VOD chat request for {channel}.");
            }

            foreach (var message in page.Messages)
            {
                if (seenMessageKeys.Add(GetMessageKey(message)))
                {
                    collected.Add(message);
                }
            }

            if (page.Messages.Count == 0)
            {
                return new KickVodChatChunk(collected, null);
            }

            var oldestTimestampUtc = page.Messages.Min(message => message.Timestamp).ToUniversalTime();
            if (oldestTimestampUtc <= fromTimestampUtc)
            {
                return new KickVodChatChunk(collected, null);
            }

            var nextCursor = KickChatTransport.NormalizeCursor(page.Cursor);
            if (string.IsNullOrWhiteSpace(nextCursor))
            {
                return new KickVodChatChunk(collected, null);
            }

            cursor = nextCursor;
        }

        return new KickVodChatChunk(collected, $"Kick VOD chat exceeded its page budget for {channel} while filling " +
            $"{FormatTimestamp(fromTimestampUtc)}-{FormatTimestamp(throughTimestampUtc)}.");
    }

    private async Task<KickRecentChatPage?> ReadPageAsync(
        string channel,
        string messagesChannelId,
        string cursor,
        CancellationToken cancellationToken)
    {
        if (!IsDirectForbidden())
        {
            var direct = await transport
                .ReadRecentMessagesDirectAsync(
                    channel,
                    messagesChannelId,
                    cursor,
                    startTimeUtc: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (direct.DirectForbidden)
            {
                MarkDirectForbidden();
            }
            else if (direct.Page is { } page)
            {
                return page;
            }
        }

        return await transport
            .ReadRecentMessagesWithCurlAsync(
                channel,
                messagesChannelId,
                cursor,
                startTimeUtc: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> ResolveMessagesChannelIdsAsync(
        ReplaySessionInfo replay,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (TryGetResolvedChannelId(replay.Channel, out var cachedId))
        {
            return [cachedId];
        }

        var candidates = new List<string>();
        AddCandidate(candidates, replay.ChatRoomId);
        if (settings.Chat.TryGetKickChatroomId(replay.Channel, out var configuredChatroomId))
        {
            AddCandidate(candidates, configuredChatroomId);
        }

        try
        {
            var channelInfo = await transport
                .ResolveChannelInfoAsync(replay.Channel, settings.Chat, cancellationToken)
                .ConfigureAwait(false);
            foreach (var candidate in KickChatTransport.BuildMessagesChannelIds(
                channelInfo.ChannelId,
                channelInfo.ChatroomId ?? ""))
            {
                AddCandidate(candidates, candidate);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.Write(
                AppLogLevel.Info,
                "VodChat",
                $"Could not resolve Kick channel metadata for {replay.Channel}.",
                ex);
        }

        return candidates;
    }

    private static void AddCandidate(List<string> candidates, string? value)
    {
        var normalized = KickChannelInfoJson.NormalizeNumericId(value);
        if (!string.IsNullOrWhiteSpace(normalized) &&
            !candidates.Contains(normalized, StringComparer.Ordinal))
        {
            candidates.Add(normalized);
        }
    }

    private static IReadOnlyList<VodChatMessage> ProjectToOffsets(
        IReadOnlyList<ChatMessage> messages,
        DateTimeOffset startedAtUtc,
        TimeSpan fromOffset,
        TimeSpan throughOffset)
    {
        var projected = new List<VodChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            var offset = message.Timestamp.ToUniversalTime() - startedAtUtc;
            if (offset < fromOffset || offset > throughOffset)
            {
                continue;
            }

            projected.Add(new VodChatMessage(offset, message));
        }

        projected.Sort(static (left, right) =>
        {
            var byOffset = left.Offset.CompareTo(right.Offset);
            return byOffset != 0
                ? byOffset
                : string.CompareOrdinal(left.Message.MessageId, right.Message.MessageId);
        });
        return projected;
    }

    /// <summary>
    /// Kick's opaque page cursor is a Unix timestamp in microseconds, so an exclusive upper bound
    /// for any instant can be built directly instead of seeding with a <c>start_time</c> request.
    /// </summary>
    internal static string ToCursor(DateTimeOffset timestampUtc)
    {
        var ticks = timestampUtc.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        // Round the exclusive upper bound up to a microsecond. Rounding down to milliseconds
        // loses messages just before a fractional chunk boundary, including before the epoch.
        var microseconds = ticks / TimeSpan.TicksPerMicrosecond;
        if (ticks % TimeSpan.TicksPerMicrosecond > 0) microseconds++;
        return microseconds.ToString(CultureInfo.InvariantCulture);
    }

    private static string GetMessageKey(ChatMessage message) =>
        string.IsNullOrWhiteSpace(message.MessageId)
            ? $"{message.Timestamp.ToUniversalTime().UtcTicks}:{message.Username}:{message.Message}"
            : message.MessageId;

    private static string FormatTimestamp(DateTimeOffset timestampUtc) =>
        timestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private bool IsDirectForbidden()
    {
        lock (gate)
        {
            return directRequestsForbidden;
        }
    }

    private void MarkDirectForbidden()
    {
        lock (gate)
        {
            directRequestsForbidden = true;
        }
    }

    private bool TryGetResolvedChannelId(string channel, out string channelId)
    {
        lock (gate)
        {
            return resolvedChannelIds.TryGetValue(channel, out channelId!);
        }
    }

    private void RememberResolvedChannelId(string channel, string channelId)
    {
        lock (gate)
        {
            resolvedChannelIds[channel] = channelId;
        }
    }

    private sealed record KickVodChatChunk(IReadOnlyList<ChatMessage> Messages, string? FailureReason);
}

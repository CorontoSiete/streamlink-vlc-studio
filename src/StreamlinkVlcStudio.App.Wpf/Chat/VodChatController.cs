using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

/// <summary>
/// Drives VOD chat for one stream tab: keeps a fetch frontier a little ahead of playback, stores
/// what it finds on a <see cref="VodChatTimeline"/>, and hands the view model the messages playback
/// has reached so they can be appended exactly like live chat.
/// </summary>
/// <remarks>
/// <para>
/// A single background pump owns all fetching. It only ever asks for the next chunk after the
/// frontier, so no request can be "stale" — a page fetched for offset X is equally valid whether or
/// not the viewer has seeked since, because offsets are absolute. A seek therefore never discards
/// downloaded chat; it only moves the frontier and the timeline's read cursor.
/// </para>
/// <para>
/// Chat the network cannot supply is still covered: <see cref="CaptureLiveMessage"/> files live
/// messages onto the same timeline at their broadcast offset. That is the only source for a Twitch
/// stream watched through its live DVR window, because Twitch publishes no VOD comments id until
/// the broadcast ends.
/// </para>
/// </remarks>
internal sealed class VodChatController : IAsyncDisposable
{
    /// <summary>Upper bound on stored messages. Roughly an hour of a very busy chat.</summary>
    private const int MaximumTimelineMessages = 40_000;

    /// <summary>Live messages held while the replay session is still being resolved.</summary>
    private const int MaximumBufferedLiveMessages = 4_000;

    /// <summary>Consecutive fetch failures tolerated before the reason is surfaced to the viewer.</summary>
    private const int FailuresBeforeNotice = 3;

    /// <summary>How far ahead of playback the frontier is kept.</summary>
    private static readonly TimeSpan LookAhead = TimeSpan.FromSeconds(45);

    /// <summary>How much chat before the resume point is loaded, so the panel is never blank.</summary>
    private static readonly TimeSpan ResumeBackfill = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan IdlePollDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(3);

    /// <summary>Tolerance for filing a live message that arrives a moment past the known duration.</summary>
    private static readonly TimeSpan LiveCaptureSlack = TimeSpan.FromMinutes(5);

    private readonly IVodChatProvider? provider;
    private readonly IAppLogger logger;
    private readonly VodChatTimeline timeline = new();
    private readonly object gate = new();
    private readonly Queue<ChatMessage> bufferedLiveMessages = new();
    private readonly HashSet<Session> activeSessions = [];

    private Session? session;
    private Task? disposalTask;
    private bool disposed;

    public VodChatController(IVodChatProvider? provider, IAppLogger logger)
    {
        this.provider = provider;
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>True once any chat — fetched or captured — is on the timeline.</summary>
    public bool HasMessages => timeline.HasMessages;

    internal int TimelineCount => timeline.Count;

    /// <summary>The most recent playback position the controller was told about.</summary>
    public TimeSpan Position
    {
        get
        {
            lock (gate)
            {
                return session?.Position ?? TimeSpan.Zero;
            }
        }
    }

    /// <summary>
    /// Completes once the pump has caught up with the look-ahead, or has established that it cannot
    /// make progress. The pump runs until cancelled, so its own task is never a useful signal.
    /// </summary>
    internal async Task WaitUntilCaughtUpAsync()
    {
        while (true)
        {
            lock (gate)
            {
                if (disposed || session is not { } current)
                {
                    return;
                }

                if (!current.FetchInFlight &&
                    (current.Unsupported ||
                        current.Exhausted ||
                        current.ConsecutiveFailures >= FailuresBeforeNotice ||
                        current.Frontier > current.Position + LookAhead))
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Points the controller at a replay session and a resume position.
    /// </summary>
    /// <remarks>
    /// Re-anchoring the same session (a seek) keeps everything already downloaded and only rewinds
    /// the read cursor and, when the new position is outside what has been fetched, the frontier.
    /// Switching to a different session clears the timeline.
    /// </remarks>
    public void Start(
        ReplaySessionInfo replay,
        AppSettings settings,
        TimeSpan position,
        Func<TimeSpan> getDuration)
    {
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(getDuration);

        var resumeFrom = Floor(position - ResumeBackfill);
        Session started;
        Session? replaced;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (session is { } current && current.Matches(replay))
            {
                current.Settings = settings;
                current.GetDuration = getDuration;
                current.Position = position;
                current.ReanchorTo(resumeFrom);
                timeline.MoveCursorTo(resumeFrom);
                FlushBufferedLiveMessagesCore(current);
                return;
            }

            replaced = session;
            timeline.Clear();
            started = new Session(replay, settings, getDuration, resumeFrom, position);
            session = started;
            timeline.MoveCursorTo(resumeFrom);
            if (FlushBufferedLiveMessagesCore(started) is { } firstBufferedOffset && firstBufferedOffset < resumeFrom)
            {
                // Initial metadata may arrive after live chat. Keep that already-captured feed
                // available on first attachment; subsequent explicit seeks use their own window.
                timeline.MoveCursorTo(firstBufferedOffset);
            }
            activeSessions.Add(started);
            // Publish the pump together with the session. Stop/disposal must never observe
            // a placeholder task and dispose the source before the worker reads its token.
            var token = started.Cancellation.Token;
            started.Pump = Task.Run(() => RunPumpAsync(started, token));
        }

        replaced?.Cancel();
    }

    /// <summary>
    /// Swaps in a replay session that describes the same broadcast under a new id, which happens
    /// when a Twitch DVR window is published as a VOD.
    /// </summary>
    /// <remarks>
    /// Content offsets are measured from the broadcast start either way, so everything already
    /// captured stays valid; only the source of future fetches changes. Fetching is re-enabled
    /// because the previous id could not serve chat at all.
    /// </remarks>
    public void Promote(ReplaySessionInfo replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        lock (gate)
        {
            if (disposed || session is not { } current)
            {
                return;
            }

            current.Promote(replay);
        }
    }

    /// <summary>Stops fetching and forgets everything stored for the current session.</summary>
    public void Stop()
    {
        Session? stopped;
        lock (gate)
        {
            stopped = session;
            session = null;
            timeline.Clear();
            bufferedLiveMessages.Clear();
        }

        stopped?.Cancel();
    }

    /// <summary>Records the latest playback position so the pump knows how far ahead to stay.</summary>
    public void UpdatePosition(TimeSpan position)
    {
        lock (gate)
        {
            if (session is { } current)
            {
                current.Position = position;
            }
        }
    }

    /// <summary>
    /// Returns the chat playback has reached since the last call, oldest first, and records the
    /// position. Safe to call from the playback clock thread.
    /// </summary>
    public IReadOnlyList<ChatMessage> TakeMessagesDueAt(TimeSpan position, int maximumMessages)
    {
        lock (gate)
        {
            if (session is not { } current)
            {
                return [];
            }

            current.Position = position;
            return timeline.TakeMessagesDueAt(position, maximumMessages);
        }
    }

    /// <summary>
    /// Takes the one-shot explanation for why chat is missing, if the pump produced one. The caller
    /// decides whether it is worth showing — it is not, when captured chat is already on screen.
    /// </summary>
    public bool TryTakeNotice(out string notice)
    {
        lock (gate)
        {
            notice = "";
            if (session is not { NoticePending: true } current ||
                string.IsNullOrEmpty(current.NoticeText))
            {
                return false;
            }

            current.NoticePending = false;
            notice = current.NoticeText;
            return true;
        }
    }

    /// <summary>
    /// Files a message received from the live chat connection at its broadcast offset.
    /// </summary>
    /// <remarks>
    /// Called for every live message, including while the tab is still at the live edge, so that
    /// seeking back later has chat to show. Messages that arrive before the replay session has been
    /// resolved are buffered and filed once it is.
    /// </remarks>
    /// <returns>
    /// True when the filed message is already at or behind the current playback position, which is
    /// the caller cue to publish it immediately rather than waiting for the next clock tick.
    /// </returns>
    public bool CaptureLiveMessage(ChatMessage message)
    {
        if (message is null)
        {
            return false;
        }

        lock (gate)
        {
            if (disposed)
            {
                return false;
            }

            if (session is not { } current)
            {
                BufferLiveMessageCore(message);
                return false;
            }

            if (!TryCaptureCore(current, message, out var offset))
            {
                BufferLiveMessageCore(message);
                return false;
            }

            return offset is { } captured && captured <= current.Position;
        }
    }

    public ValueTask DisposeAsync()
    {
        Session[] stopped;
        TaskCompletionSource completion;
        lock (gate)
        {
            if (disposalTask is not null)
            {
                return new ValueTask(disposalTask);
            }

            disposed = true;
            stopped = activeSessions.ToArray();
            session = null;
            timeline.Clear();
            bufferedLiveMessages.Clear();
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disposalTask = completion.Task;
        }

        _ = CompleteDisposalAsync(stopped, completion);
        return new ValueTask(completion.Task);
    }

    private static async Task CompleteDisposalAsync(Session[] stopped, TaskCompletionSource completion)
    {
        try
        {
            foreach (var state in stopped)
            {
                state.Cancel();
            }

            await Task.WhenAll(stopped.Select(state => state.Pump)).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private async Task RunPumpAsync(Session state, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!IsCurrent(state))
                {
                    return;
                }

                if (!TryBeginFetch(state, out var request))
                {
                    await Task.Delay(IdlePollDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                VodChatFetchResult result;
                try
                {
                    result = provider is null
                        ? VodChatFetchResult.Unsupported("No VOD chat provider is configured.")
                        : await provider.FetchAsync(request.Replay, request.Settings, request.FromOffset, cancellationToken)
                            .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    result = VodChatFetchResult.Failed($"VOD chat request failed: {ex.Message}");
                    logger.Write(AppLogLevel.Debug, "VodChat", "VOD chat request threw.", ex);
                }

                var applied = ApplyResult(state, result, request.FromOffset, request.Epoch);
                if (applied && result.Outcome == VodChatFetchOutcome.Failed)
                {
                    await Task.Delay(FailureRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "VodChat", "The VOD chat pump stopped unexpectedly.", ex);
        }
        finally
        {
            // Each pump owns its source, including pumps retired by Stop or replacement.
            state.Dispose();
            lock (gate)
            {
                state.FetchInFlight = false;
                activeSessions.Remove(state);
            }
        }
    }

    private bool ApplyResult(
        Session state,
        VodChatFetchResult result,
        TimeSpan fromOffset,
        long epoch)
    {
        lock (gate)
        {
            state.FetchInFlight = false;
            if (disposed || !ReferenceEquals(session, state))
            {
                return false;
            }

            ApplyResultCore(state, result, fromOffset, epoch);
            return epoch == state.FrontierEpoch;
        }
    }

    private void ApplyResultCore(Session state, VodChatFetchResult result, TimeSpan fromOffset, long epoch)
    {
        // Downloaded messages are keyed to absolute offsets, so they stay valid even if the viewer
        // seeked while the request was in flight. Fetch status and the frontier belong to a generation.
        if (result.Messages.Count > 0)
        {
            timeline.AddRange(result.Messages, MaximumTimelineMessages);
        }

        if (epoch != state.FrontierEpoch)
        {
            // Old pages can contribute chat at absolute offsets, but their completion,
            // failure and unsupported flags describe the old fetch position/source.
            return;
        }

        switch (result.Outcome)
        {
            case VodChatFetchOutcome.Loaded:
            case VodChatFetchOutcome.Completed:
                state.ConsecutiveFailures = 0;
                state.AdvanceFrontier(fromOffset, result.CoveredThroughOffset);
                if (result.Outcome == VodChatFetchOutcome.Completed)
                {
                    state.Exhausted = true;
                    logger.Write(
                        AppLogLevel.Debug,
                        "VodChat",
                        $"VOD chat reached the end of {state.Replay.Channel} ({state.Replay.ReplayId}).");
                }

                return;

            case VodChatFetchOutcome.Unsupported:
                state.Unsupported = true;
                SetNotice(state, result.Reason);
                logger.Write(AppLogLevel.Info, "VodChat", result.Reason);
                return;

            default:
                state.ConsecutiveFailures++;
                if (state.ConsecutiveFailures == FailuresBeforeNotice)
                {
                    SetNotice(state, result.Reason);
                }

                logger.Write(
                    AppLogLevel.Info,
                    "VodChat",
                    $"VOD chat fetch attempt {state.ConsecutiveFailures} failed: {result.Reason}");
                return;
        }
    }

    /// <summary>Decides whether another chunk is needed right now, and from where.</summary>
    private bool TryBeginFetch(Session state, out FetchRequest request)
    {
        var duration = SafeGetDuration(state);
        lock (gate)
        {
            request = default;
            if (disposed || !ReferenceEquals(session, state) || state.Unsupported || state.Exhausted ||
                (duration > TimeSpan.Zero && state.Frontier >= duration) ||
                state.Frontier > state.Position + LookAhead)
            {
                return false;
            }

            state.FetchInFlight = true;
            request = new FetchRequest(state.Replay, state.Settings, state.Frontier, state.FrontierEpoch);
            return true;
        }
    }

    private readonly record struct FetchRequest(
        ReplaySessionInfo Replay, AppSettings Settings, TimeSpan FromOffset, long Epoch);

    private TimeSpan SafeGetDuration(Session state)
    {
        try
        {
            return state.GetDuration();
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Debug, "VodChat", "Could not read the replay duration.", ex);
            return TimeSpan.Zero;
        }
    }

    private bool IsCurrent(Session state)
    {
        lock (gate)
        {
            return !disposed && ReferenceEquals(session, state);
        }
    }

    private void SetNotice(Session state, string notice)
    {
        if (string.IsNullOrWhiteSpace(notice))
        {
            return;
        }

        lock (gate)
        {
            if (!ReferenceEquals(session, state) || state.NoticeText.Length > 0)
            {
                return;
            }

            state.NoticeText = notice.Trim();
            state.NoticePending = true;
        }
    }

    /// <summary>
    /// Files a live message at its broadcast offset. Returns false only when that offset cannot be
    /// computed yet, so the caller buffers the message until the session is known.
    /// </summary>
    private bool TryCaptureCore(Session state, ChatMessage message, out TimeSpan? offset)
    {
        offset = null;
        if (state.Replay.StreamStartedAtUtc is not { } startedAt)
        {
            return false;
        }

        var captured = message.Timestamp.ToUniversalTime() - startedAt.ToUniversalTime();
        if (captured < TimeSpan.Zero)
        {
            // Older than the broadcast; nothing on this timeline can place it.
            return true;
        }

        var duration = SafeGetDuration(state);
        if (duration > TimeSpan.Zero && captured > duration + LiveCaptureSlack)
        {
            return true;
        }

        timeline.Add(new VodChatMessage(captured, message), MaximumTimelineMessages);
        offset = captured;
        return true;
    }

    private void BufferLiveMessageCore(ChatMessage message)
    {
        bufferedLiveMessages.Enqueue(message);
        while (bufferedLiveMessages.Count > MaximumBufferedLiveMessages)
        {
            bufferedLiveMessages.Dequeue();
        }
    }

    private TimeSpan? FlushBufferedLiveMessagesCore(Session state)
    {
        if (bufferedLiveMessages.Count == 0 ||
            state.Replay.StreamStartedAtUtc is null)
        {
            return null;
        }

        TimeSpan? firstOffset = null;
        foreach (var message in bufferedLiveMessages)
        {
            if (TryCaptureCore(state, message, out var offset) && offset is { } capturedOffset &&
                (firstOffset is null || capturedOffset < firstOffset.Value))
            {
                firstOffset = capturedOffset;
            }
        }

        bufferedLiveMessages.Clear();
        return firstOffset;
    }

    private static TimeSpan Floor(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    /// <summary>One replay session's fetch state. Replaced wholesale when the session changes.</summary>
    private sealed class Session : IDisposable
    {
        public Session(
            ReplaySessionInfo replay,
            AppSettings settings,
            Func<TimeSpan> getDuration,
            TimeSpan resumeFrom,
            TimeSpan position)
        {
            Replay = replay;
            Settings = settings;
            GetDuration = getDuration;
            // Fetching starts a little before the resume point so chat is never blank, but the
            // position is where playback actually is; the two are not interchangeable.
            Frontier = resumeFrom;
            FetchedFrom = resumeFrom;
            Position = position;
        }

        public ReplaySessionInfo Replay { get; set; }

        public AppSettings Settings { get; set; }

        public Func<TimeSpan> GetDuration { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>The offset the next fetch starts from.</summary>
        public TimeSpan Frontier { get; private set; }

        /// <summary>The oldest offset the current contiguous fetch run began at.</summary>
        public TimeSpan FetchedFrom { get; private set; }

        /// <summary>Bumped whenever a seek moves the frontier, so in-flight advances are ignored.</summary>
        public long FrontierEpoch { get; private set; }

        public TimeSpan Position { get; set; }

        public int ConsecutiveFailures { get; set; }

        public bool Unsupported { get; set; }

        public bool Exhausted { get; set; }

        /// <summary>Why chat is missing, once known. Kept so a later seek can say so again.</summary>
        public string NoticeText { get; set; } = "";

        public bool NoticePending { get; set; }

        public bool FetchInFlight { get; set; }

        public Task Pump { get; set; } = Task.CompletedTask;

        public bool Matches(ReplaySessionInfo other) =>
            Replay.Platform == other.Platform &&
            string.Equals(Replay.Channel, other.Channel, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Replay.ReplayId, other.ReplayId, StringComparison.Ordinal);

        /// <summary>
        /// Handles a seek. Positions already inside the fetched run need no new requests; anything
        /// else restarts the run at the new point and lets the pump walk forward from there.
        /// </summary>
        public void ReanchorTo(TimeSpan resumeFrom)
        {
            if (resumeFrom >= FetchedFrom && resumeFrom <= Frontier)
            {
                NoticePending = NoticeText.Length > 0;
                return;
            }

            Frontier = resumeFrom;
            FetchedFrom = resumeFrom;
            FrontierEpoch++;
            ConsecutiveFailures = 0;
            Exhausted = false;
            // The seek wipes the visible chat, so offer the explanation again.
            NoticePending = NoticeText.Length > 0;
        }

        public void Promote(ReplaySessionInfo replay)
        {
            Replay = replay;
            FrontierEpoch++;
            Unsupported = false;
            Exhausted = false;
            NoticeText = "";
            NoticePending = false;
            ConsecutiveFailures = 0;
        }

        public void AdvanceFrontier(TimeSpan fromOffset, TimeSpan coveredThrough)
        {
            // The fetchers guarantee forward movement; this is belt-and-braces against a stall.
            var next = coveredThrough > fromOffset ? coveredThrough : fromOffset + TimeSpan.FromSeconds(1);
            if (next > Frontier)
            {
                Frontier = next;
            }
        }

        public void Cancel()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => Cancellation.Dispose();
    }
}

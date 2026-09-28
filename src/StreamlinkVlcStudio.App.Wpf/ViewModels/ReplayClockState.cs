using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

// Clock acceptance, frozen positions, and resume holds share one synchronization boundary.
internal sealed class ReplayClockState
{
    private readonly StreamTarget Target;
    private readonly Func<PlaybackStatus> getStatus;
    private readonly Func<bool> isReplayMode;
    private readonly Func<long> getSeekGeneration;
    private readonly Func<long, long?, bool> isSampleCurrent;
    private readonly Func<double> getSeekValue;
    private readonly Func<float> getPlaybackRate;
    private readonly Func<PlaybackClock?> readClock;

    internal ReplayClockState(StreamTarget target, Func<PlaybackStatus> getStatus, Func<bool> isReplayMode,
        Func<long> getSeekGeneration, Func<long, long?, bool> isSampleCurrent,
        Func<double> getSeekValue, Func<float> getPlaybackRate, Func<PlaybackClock?> readClock)
    {
        Target = target;
        this.getStatus = getStatus;
        this.isReplayMode = isReplayMode;
        this.getSeekGeneration = getSeekGeneration;
        this.isSampleCurrent = isSampleCurrent;
        this.getSeekValue = getSeekValue;
        this.getPlaybackRate = getPlaybackRate;
        this.readClock = readClock;
    }

    internal long PlaybackStateVersion => Volatile.Read(ref replayClockPlaybackStateVersion);
    internal TimeSpan? ResumeHoldPosition { get { lock (replayClockAnchorGate) return pendingResumeHoldPosition; } }
    internal bool ResumeHoldAllowsLiveTransition { get { lock (replayClockAnchorGate) return pendingResumeHoldAllowsLiveTransition; } }

    internal void CaptureResumeHold(bool allowLiveTransition, TimeSpan? position)
    {
        lock (replayClockAnchorGate)
        {
            pendingResumeHoldPosition = position;
            pendingResumeHoldAllowsLiveTransition = allowLiveTransition;
        }
    }
    internal void ClearResumeHold() => CaptureResumeHold(false, null);

    internal void ApplyPlaybackState(PlaybackStatus previous, PlaybackStatus next, ReplayClockSnapshot? pauseClock,
        bool replayMode, Action publishStatus)
    {
        lock (replayClockAnchorGate)
        {
            if (previous == PlaybackStatus.Paused && next == PlaybackStatus.Playing &&
                replayMode && replayClockAnchorAvailable && pausedReplayClock is { } heldClock)
            {
                SetReplayClockAnchor(heldClock.Position, heldClock.Duration,
                    getSeekGeneration(), replayClockAnchorAwaitingSeekConfirmation);
                ResetReplayClockSampleTrackingCore();
            }
            pausedReplayClock = pauseClock;
            if (pauseClock is { } capturedClock && pendingResumeHoldPosition.HasValue)
                pendingResumeHoldPosition = capturedClock.Position;
            publishStatus();
            Interlocked.Increment(ref replayClockPlaybackStateVersion);
        }
    }

    internal void BeginSeek()
    {
        lock (replayClockAnchorGate) ResetReplayClockSampleTrackingCore();
    }
    internal void CancelSeek()
    {
        lock (replayClockAnchorGate) pausedReplayClock = null;
    }
    internal void CommitSeek(TimeSpan position, TimeSpan duration, bool paused)
    {
        lock (replayClockAnchorGate)
        {
            pausedReplayClock = paused ? new ReplayClockSnapshot(position, duration, true) : null;
            pendingResumeHoldPosition = paused && !Target.IsExplicitVod ? position : null;
            pendingResumeHoldAllowsLiveTransition = paused && !Target.IsExplicitVod;
            Interlocked.Increment(ref replayClockPlaybackStateVersion);
        }
    }
    private static readonly TimeSpan ReplayClockSampleTolerance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReplayClockMaximumPlausibleDuration = TimeSpan.FromDays(14);
    private readonly object replayClockAnchorGate = new();
    private long replayClockPlaybackStateVersion;
    private TimeSpan? pendingResumeHoldPosition;
    private bool pendingResumeHoldAllowsLiveTransition;
    private ReplayClockSnapshot? pausedReplayClock;
    private bool replayClockAnchorAvailable;
    private TimeSpan replayClockAnchorOffset;
    private DateTimeOffset replayClockAnchorObservedAtUtc;
    private long replayClockAnchorSeekGeneration;
    private bool replayClockAnchorAwaitingSeekConfirmation;
    private bool replayClockAcceptedSampleAvailable;
    private TimeSpan replayClockAcceptedSamplePosition;
    private DateTimeOffset replayClockAcceptedSampleObservedAtUtc;
    private long replayClockAcceptedSampleSeekGeneration;

    internal ReplayClockSnapshot ResolveReplayClock(
        ReplaySessionInfo replay,
        long sampledSeekOperationVersion,
        bool sampleBeganDuringSeek,
        long? sampledPlaybackStateVersion = null)
    {
        var playbackStateVersion = sampledPlaybackStateVersion ?? Volatile.Read(ref replayClockPlaybackStateVersion);
        lock (replayClockAnchorGate)
        {
            if (getStatus() == PlaybackStatus.Paused && pausedReplayClock is { } heldClock)
            {
                return heldClock;
            }
        }

        var clock = ResolveLiveReplayClock(
            replay,
            sampledSeekOperationVersion,
            sampleBeganDuringSeek,
            playbackStateVersion);
        lock (replayClockAnchorGate)
        {
            // Replay metadata may first become available after the pause. Only that case needs
            // a lazy snapshot; an in-flight pre-pause sample must never replace a captured one.
            if (getStatus() == PlaybackStatus.Paused &&
                playbackStateVersion == Volatile.Read(ref replayClockPlaybackStateVersion))
            {
                return pausedReplayClock ??= clock;
            }
        }

        return clock;
    }

    internal ReplayClockSnapshot ResolveLiveReplayClock(
        ReplaySessionInfo replay,
        long sampledSeekOperationVersion,
        bool sampleBeganDuringSeek,
        long sampledPlaybackStateVersion)
    {
        var duration = GetCurrentReplayDuration(replay);
        if (!isReplayMode())
        {
            return new ReplayClockSnapshot(duration, duration, true);
        }

        var observedAtUtc = DateTimeOffset.UtcNow;
        var isSeekable = true;
        TimeSpan? sourceDuration = null;
        if (readClock() is { } clock)
        {
            isSeekable = clock.IsSeekable;
            if (clock.Duration is { } mediaDuration && mediaDuration > TimeSpan.Zero)
            {
                // Keep the media's currently published edge separately from the estimated
                // wall-clock duration. A growing live replay can trail wall time while its
                // next HLS segments are still being published.
                sourceDuration = mediaDuration;
            }

            if (!Target.IsExplicitVod)
            {
                duration = NormalizeReplayClockDuration(clock.Duration, duration);
            }

            if (TryNormalizeReplayClockPosition(clock.Position, duration, out var position) &&
                IsReplayClockSampleAccepted(position, duration, observedAtUtc) &&
                TryAcceptReplayClockSample(
                    position,
                    duration,
                    sampledSeekOperationVersion,
                    sampleBeganDuringSeek,
                    observedAtUtc,
                    sampledPlaybackStateVersion))
            {
                return new ReplayClockSnapshot(position, duration, isSeekable, sourceDuration);
            }
        }

        return new ReplayClockSnapshot(
            EstimateReplayClockFromAnchor(duration, observedAtUtc),
            duration,
            isSeekable,
            sourceDuration);
    }

    internal static TimeSpan NormalizeReplayClockDuration(TimeSpan? mediaDuration, TimeSpan fallbackDuration)
    {
        if (mediaDuration is not { } duration ||
            duration <= TimeSpan.Zero ||
            duration > ReplayClockMaximumPlausibleDuration)
        {
            return fallbackDuration;
        }

        return duration > fallbackDuration ? duration : fallbackDuration;
    }

    internal static bool TryNormalizeReplayClockPosition(TimeSpan position, TimeSpan duration, out TimeSpan normalizedPosition)
    {
        normalizedPosition = TimeSpan.Zero;
        if (position < TimeSpan.Zero ||
            position > SafeAdd(duration, ReplayClockSampleTolerance))
        {
            return false;
        }

        normalizedPosition = ClampReplayOffset(position, duration);
        return true;
    }

    internal bool IsReplayClockSampleAccepted(TimeSpan position, TimeSpan duration, DateTimeOffset observedAtUtc)
    {
        var anchor = GetReplayClockAnchor();
        if (!anchor.HasValue)
        {
            return true;
        }

        var snapshot = anchor.Value;
        if (snapshot.AcceptedSampleAvailable &&
            snapshot.AcceptedSampleSeekGeneration == getSeekGeneration())
        {
            var minimumPositionFromLastSample = snapshot.AcceptedSamplePosition - ReplayClockSampleTolerance;
            if (minimumPositionFromLastSample < TimeSpan.Zero)
            {
                minimumPositionFromLastSample = TimeSpan.Zero;
            }

            if (position < minimumPositionFromLastSample)
            {
                return false;
            }
        }

        var elapsed = observedAtUtc - snapshot.ObservedAtUtc;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var expectedPosition = ClampReplayOffset(SafeAdd(snapshot.Offset, ScaleElapsed(elapsed, getPlaybackRate())), duration);
        var lowerBound = expectedPosition - ReplayClockSampleTolerance;
        if (lowerBound < TimeSpan.Zero)
        {
            lowerBound = TimeSpan.Zero;
        }

        var upperBound = SafeAdd(expectedPosition, ReplayClockSampleTolerance);
        if (upperBound > duration)
        {
            upperBound = duration;
        }

        return position >= lowerBound && position <= upperBound;
    }

    internal TimeSpan EstimateReplayClockFromAnchor(TimeSpan duration, DateTimeOffset observedAtUtc)
    {
        var anchor = GetReplayClockAnchor();
        if (anchor is null)
        {
            return ClampReplayOffset(TimeSpan.FromSeconds(getSeekValue()), duration);
        }

        var elapsed = getStatus() == PlaybackStatus.Playing
            ? observedAtUtc - anchor.Value.ObservedAtUtc
            : TimeSpan.Zero;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return ClampReplayOffset(SafeAdd(anchor.Value.Offset, ScaleElapsed(elapsed, getPlaybackRate())), duration);
    }

    internal void SetReplayClockAnchor(
        TimeSpan offset,
        TimeSpan duration,
        long seekGeneration,
        bool awaitingSeekConfirmation,
        DateTimeOffset? observedAtUtc = null)
    {
        lock (replayClockAnchorGate)
        {
            replayClockAnchorAvailable = true;
            replayClockAnchorOffset = ClampReplayOffset(offset, duration);
            replayClockAnchorObservedAtUtc = observedAtUtc ?? DateTimeOffset.UtcNow;
            replayClockAnchorSeekGeneration = seekGeneration;
            replayClockAnchorAwaitingSeekConfirmation = awaitingSeekConfirmation;
        }
    }

    internal void ReanchorForPlaybackRateChange(
        TimeSpan position,
        TimeSpan duration,
        long seekGeneration,
        DateTimeOffset observedAtUtc)
    {
        lock (replayClockAnchorGate)
        {
            replayClockAnchorAvailable = true;
            replayClockAnchorOffset = ClampReplayOffset(position, duration);
            replayClockAnchorObservedAtUtc = observedAtUtc;
            replayClockAnchorSeekGeneration = seekGeneration;
            replayClockAnchorAwaitingSeekConfirmation = false;
            ResetReplayClockSampleTrackingCore();
        }
    }

    internal bool TryAcceptReplayClockSample(
        TimeSpan position,
        TimeSpan duration,
        long seekGeneration,
        bool sampleBeganDuringSeek,
        DateTimeOffset observedAtUtc,
        long sampledPlaybackStateVersion)
    {
        if (sampleBeganDuringSeek)
        {
            return false;
        }

        lock (replayClockAnchorGate)
        {
            // BeginReplaySeekOperation publishes the in-progress flag before advancing the
            // generation. Rechecking both while holding the anchor lock prevents a sample that
            // began before a seek from replacing the new seek anchor after it is reset.
            if (!isSampleCurrent(seekGeneration, sampledPlaybackStateVersion))
            {
                return false;
            }

            var normalizedPosition = ClampReplayOffset(position, duration);
            replayClockAcceptedSampleAvailable = true;
            replayClockAcceptedSamplePosition = normalizedPosition;
            replayClockAcceptedSampleObservedAtUtc = observedAtUtc;
            replayClockAcceptedSampleSeekGeneration = seekGeneration;

            if (!replayClockAnchorAvailable ||
                seekGeneration != replayClockAnchorSeekGeneration ||
                normalizedPosition > replayClockAnchorOffset)
            {
                replayClockAnchorAvailable = true;
                replayClockAnchorOffset = normalizedPosition;
                replayClockAnchorObservedAtUtc = observedAtUtc;
                replayClockAnchorSeekGeneration = seekGeneration;
            }

            replayClockAnchorAwaitingSeekConfirmation = false;
            return true;
        }
    }

    internal ReplayClockAnchorSnapshot? GetReplayClockAnchor()
    {
        lock (replayClockAnchorGate)
        {
            if (!replayClockAnchorAvailable)
            {
                return null;
            }

            return new ReplayClockAnchorSnapshot(
                replayClockAnchorOffset,
                replayClockAnchorObservedAtUtc,
                replayClockAnchorSeekGeneration,
                replayClockAnchorAwaitingSeekConfirmation,
                replayClockAcceptedSampleAvailable,
                replayClockAcceptedSamplePosition,
                replayClockAcceptedSampleObservedAtUtc,
                replayClockAcceptedSampleSeekGeneration);
        }
    }

    internal void ClearReplayClockAnchor()
    {
        lock (replayClockAnchorGate)
        {
            replayClockAnchorAvailable = false;
            replayClockAnchorOffset = TimeSpan.Zero;
            replayClockAnchorObservedAtUtc = DateTimeOffset.MinValue;
            replayClockAnchorSeekGeneration = 0;
            replayClockAnchorAwaitingSeekConfirmation = false;
            ResetReplayClockSampleTrackingCore();
        }
    }

    internal void ResetReplayClockSampleTrackingCore()
    {
        replayClockAcceptedSampleAvailable = false;
        replayClockAcceptedSamplePosition = TimeSpan.Zero;
        replayClockAcceptedSampleObservedAtUtc = DateTimeOffset.MinValue;
        replayClockAcceptedSampleSeekGeneration = 0;
    }

    internal static TimeSpan SafeAdd(TimeSpan left, TimeSpan right)
    {
        if (right > TimeSpan.Zero && left > TimeSpan.MaxValue - right)
        {
            return TimeSpan.MaxValue;
        }

        if (right < TimeSpan.Zero && left < TimeSpan.MinValue - right)
        {
            return TimeSpan.MinValue;
        }

        return left + right;
    }

    private static TimeSpan ScaleElapsed(TimeSpan elapsed, float rate)
    {
        if (elapsed <= TimeSpan.Zero || !float.IsFinite(rate) || rate <= 0f)
        {
            return elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;
        }

        var scaledTicks = elapsed.Ticks * (double)rate;
        return scaledTicks >= TimeSpan.MaxValue.Ticks
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks((long)scaledTicks);
    }

    internal TimeSpan GetCurrentReplayDuration(ReplaySessionInfo replay)
    {
        var duration = NormalizeReplayDuration(replay.Duration);
        if (Target.IsExplicitVod)
        {
            return duration > TimeSpan.Zero ? duration : TimeSpan.FromSeconds(1);
        }

        if (replay.StreamStartedAtUtc is { } startedAt &&
            TryGetPlausibleElapsedSince(startedAt, DateTimeOffset.UtcNow, out var elapsed))
        {
            if (elapsed > duration)
            {
                duration = elapsed;
            }
        }

        return duration > TimeSpan.Zero ? duration : TimeSpan.FromSeconds(1);
    }

    internal static TimeSpan NormalizeReplayDuration(TimeSpan duration)
    {
        return duration > TimeSpan.Zero && duration <= ReplayClockMaximumPlausibleDuration
            ? duration
            : TimeSpan.FromSeconds(1);
    }

    internal static bool TryGetPlausibleElapsedSince(
        DateTimeOffset startedAt,
        DateTimeOffset observedAt,
        out TimeSpan elapsed)
    {
        elapsed = TimeSpan.Zero;
        var elapsedTicks = observedAt.UtcDateTime.Ticks - startedAt.UtcDateTime.Ticks;
        if (elapsedTicks <= 0)
        {
            return false;
        }

        elapsed = TimeSpan.FromTicks(elapsedTicks);
        return elapsed <= ReplayClockMaximumPlausibleDuration;
    }

    internal static TimeSpan ClampReplayOffset(TimeSpan value, TimeSpan duration)
    {
        if (value < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return value > duration ? duration : value;
    }
}

internal readonly record struct ReplayClockSnapshot(
    TimeSpan Position,
    TimeSpan Duration,
    bool IsSeekable,
    TimeSpan? SourceDuration = null);

internal readonly record struct ReplayClockAnchorSnapshot(
        TimeSpan Offset,
        DateTimeOffset ObservedAtUtc,
        long SeekGeneration,
        bool AwaitingSeekConfirmation,
        bool AcceptedSampleAvailable,
        TimeSpan AcceptedSamplePosition,
        DateTimeOffset AcceptedSampleObservedAtUtc,
        long AcceptedSampleSeekGeneration);

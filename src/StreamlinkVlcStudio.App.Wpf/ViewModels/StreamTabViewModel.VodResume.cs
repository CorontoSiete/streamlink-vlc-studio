using System.Diagnostics;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed partial class StreamTabViewModel
{
    private static readonly TimeSpan VodResumeSaveInterval = TimeSpan.FromSeconds(5);
    private readonly object vodResumeGate = new();
    private readonly IVodPlaybackHistory? vodPlaybackHistory;
    private bool vodResumeTracking;
    private bool vodResumeHistoryLoaded;
    private bool vodResumeClosed;
    private TimeSpan vodStartupPosition;
    private TimeSpan vodStartupDuration;
    private TimeSpan? vodExpectedSeekPosition;
    private VodPlaybackBookmark? vodLastBookmark;
    private long vodLastSampleTimestamp;
    private long vodLastSaveTimestamp;

    private async Task<TimeSpan> GetVodResumePositionAsync(CancellationToken cancellationToken)
    {
        if (!Target.IsExplicitVod || vodPlaybackHistory is null) return TimeSpan.Zero;
        vodResumeHistoryLoaded = false;
        vodStartupDuration = TimeSpan.Zero;
        try
        {
            var bookmark = await vodPlaybackHistory.GetAsync(Target, cancellationToken);
            vodResumeHistoryLoaded = true;
            if (bookmark is null || bookmark.Completed) return TimeSpan.Zero;
            // Browser metadata may predate the last viewing of a growing VOD. Use the saved
            // decoder duration, and let PlayFromAsync validate against the actual media.
            var duration = vodStartupDuration = bookmark.Duration;
            // An exact end timestamp has no frame to present; keep the final second available.
            // Near-end bookmarks otherwise stay exact. Only VLC's Ended state means completion.
            return bookmark.Position < duration ? bookmark.Position :
                TimeSpan.FromTicks(Math.Max(0, duration.Ticks - TimeSpan.TicksPerSecond));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "VOD resume", $"Could not read the saved position for {Target.DisplayName}.", ex);
            return TimeSpan.Zero;
        }
    }

    private void StartVodResumeTracking()
    {
        lock (vodResumeGate)
        {
            // If history could not be read, do not overwrite an unknown bookmark with a new start.
            vodResumeTracking = Target.IsExplicitVod && vodPlaybackHistory is not null &&
                vodResumeHistoryLoaded && !vodResumeClosed;
            vodExpectedSeekPosition = vodStartupPosition > TimeSpan.Zero ? vodStartupPosition : null;
            vodLastBookmark = null;
            vodLastSaveTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private void ExpectVodResumeSeek(TimeSpan position)
    {
        lock (vodResumeGate)
        {
            // Only called after a successful seek. Slider previews and failed seeks never become
            // bookmarks. A paused VLC seek may still be queued, so wait for its actual clock.
            vodExpectedSeekPosition = position;
        }
    }

    internal void CaptureVodResumePosition(bool closing = false)
    {
        if (!Target.IsExplicitVod || vodPlaybackHistory is null) return;
        lock (vodResumeGate)
        {
            if (vodResumeClosed) return;
            try
            {
                if (!vodResumeTracking || IsReplaySeekInProgress ||
                    Status is not (PlaybackStatus.Playing or PlaybackStatus.Paused) ||
                    playbackEngine is not { } engine) return;
                var seekVersion = Volatile.Read(ref replaySeekOperationVersion);
                var stateVersion = Volatile.Read(ref replayClockPlaybackStateVersion);
                var hasHealth = engine.TryGetPlaybackHealth(out var health);
                if (hasHealth && health.State == PlaybackEngineState.Ended)
                {
                    CaptureVodCompletion(engine, seekVersion, stateVersion);
                    return;
                }
                if (hasHealth && health.State is not (PlaybackEngineState.Playing or PlaybackEngineState.Paused)) return;
                if (!engine.TryGetPlaybackClock(out var clock) || !clock.IsSeekable ||
                    !ReferenceEquals(playbackEngine, engine) ||
                    !IsReplayClockSampleCurrent(seekVersion, stateVersion)) return;
                var duration = clock.Duration is { } mediaDuration && mediaDuration > TimeSpan.Zero
                    ? mediaDuration : Target.MediaDuration;
                var position = clock.Position;
                if (duration <= TimeSpan.Zero || duration > ReplayClockMaximumPlausibleDuration ||
                    position < TimeSpan.Zero || position > duration) return;

                if (vodExpectedSeekPosition is { } expected)
                {
                    if ((position - expected).Duration() > ReplayClockSampleTolerance) return;
                    if (position == TimeSpan.Zero && expected != TimeSpan.Zero) return;
                }
                else if (vodLastBookmark is { } previous)
                {
                    // Ignore shutdown resets, late pre-seek samples and impossible jumps. Paused
                    // or buffering wall time is never added to the saved playback position.
                    var elapsed = Stopwatch.GetElapsedTime(vodLastSampleTimestamp);
                    if (previous.Completed || position < previous.Position ||
                        position - previous.Position > elapsed + ReplayClockSampleTolerance) return;
                }
                else if (position == TimeSpan.Zero) return;

                vodExpectedSeekPosition = null;
                vodLastSampleTimestamp = Stopwatch.GetTimestamp();
                vodLastBookmark = new VodPlaybackBookmark(position, duration, DateTimeOffset.UtcNow);
                vodPlaybackHistory.Remember(Target, vodLastBookmark);
            }
            catch (Exception ex)
            {
                logger.Write(AppLogLevel.Warning, "VOD resume", $"Could not capture the position for {Target.DisplayName}.", ex);
            }
            finally
            {
                // Freeze before detached cleanup is queued. An old tab must never update the
                // shared bookmark after its replacement has started.
                if (closing) vodResumeClosed = true;
            }
        }
    }

    private void CaptureVodCompletion(IPlaybackEngine engine, long seekVersion, long stateVersion)
    {
        lock (vodResumeGate)
        {
            if (!vodResumeTracking || vodResumeClosed || vodPlaybackHistory is null ||
                !ReferenceEquals(playbackEngine, engine) ||
                !IsReplayClockSampleCurrent(seekVersion, stateVersion)) return;
            try
            {
                // The caller observed this input's EOF. A final-frame seek can finish before
                // any position is sampled, and VLC can reset its clock/seekability at EOF.
                // Use the known media length without requiring a near-end resume bookmark.
                var duration = engine.TryGetPlaybackClock(out var clock) &&
                    clock.Duration is { } mediaDuration && mediaDuration > TimeSpan.Zero &&
                    mediaDuration <= ReplayClockMaximumPlausibleDuration
                    ? mediaDuration
                    : vodLastBookmark?.Duration ?? (vodStartupDuration > TimeSpan.Zero
                        ? vodStartupDuration : Target.MediaDuration);
                if (duration <= TimeSpan.Zero || duration > ReplayClockMaximumPlausibleDuration ||
                    !IsReplayClockSampleCurrent(seekVersion, stateVersion)) return;
                if (vodLastBookmark is { Completed: true } && vodExpectedSeekPosition is null) return;

                var completed = new VodPlaybackBookmark(duration, duration, DateTimeOffset.UtcNow,
                    Completed: true, HasBeenWatched: true);
                vodPlaybackHistory.Remember(Target, completed);
                vodLastBookmark = completed;
                vodExpectedSeekPosition = null;
                // Persist the terminal state immediately; periodic checkpoints still retry
                // a failed write, and stop/close await the history's serialized save queue.
                _ = SaveVodResumePositionAsync(force: true);
            }
            catch (Exception ex)
            {
                logger.Write(AppLogLevel.Warning, "VOD resume", $"Could not record completion for {Target.DisplayName}.", ex);
            }
        }
    }

    private async Task SaveVodResumePositionAsync(bool force = false)
    {
        if (!Target.IsExplicitVod || vodPlaybackHistory is null) return;
        lock (vodResumeGate)
        {
            if (!force && Stopwatch.GetElapsedTime(vodLastSaveTimestamp) < VodResumeSaveInterval) return;
            vodLastSaveTimestamp = Stopwatch.GetTimestamp();
        }
        try { await vodPlaybackHistory.SaveAsync(); }
        catch (Exception ex)
        {
            // Keep the dirty in-memory bookmark so the next checkpoint or close retries it.
            logger.Write(AppLogLevel.Warning, "VOD resume", $"Could not save the position for {Target.DisplayName}.", ex);
        }
    }

    private async Task StopVodResumeTrackingAsync()
    {
        CaptureVodResumePosition();
        lock (vodResumeGate) vodResumeTracking = false;
        await SaveVodResumePositionAsync(force: true);
    }
}

using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed partial class StreamTabViewModel
{
    internal void CheckVodPlaybackCompletion()
    {
        if (disposed || !Target.IsExplicitVod || IsBusy || IsReplaySeekInProgress ||
            Status is not (PlaybackStatus.Playing or PlaybackStatus.Paused) ||
            playbackEngine is not { } engine)
        {
            return;
        }

        var seekVersion = Volatile.Read(ref replaySeekOperationVersion);
        var stateVersion = Volatile.Read(ref replayClockPlaybackStateVersion);
        if (!engine.TryGetPlaybackHealth(out var health) || health.State != PlaybackEngineState.Ended)
        {
            return;
        }

        dispatch(() =>
        {
            // An end sample queued before a seek, restart or stop must not hide new video.
            if (disposed || IsBusy || !ReferenceEquals(playbackEngine, engine) ||
                !IsReplayClockSampleCurrent(seekVersion, stateVersion) ||
                !playbackTransitionGate.Wait(0))
            {
                return;
            }

            try
            {
                // Reuse the EOF sample validated above. A second nonblocking native read
                // can fail even though this sample is still current and finishes the UI.
                CaptureVodCompletion(engine, seekVersion, stateVersion);
                Status = PlaybackStatus.Finished;
                PausedByTabSwitch = false;
                if (replaySession is { IsAvailable: true } replay)
                {
                    var duration = GetCurrentReplayDuration(replay);
                    ApplyReplayClock(duration, duration, isSeekable: true);
                }
            }
            finally
            {
                playbackTransitionGate.Release();
            }
        });
    }
}

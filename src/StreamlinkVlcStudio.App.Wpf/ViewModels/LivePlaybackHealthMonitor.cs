using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

/// <summary>Uses monotonic time and output counters, so static scenes are still healthy.</summary>
internal sealed class LivePlaybackHealthMonitor
{
    internal static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    private PlaybackHealth? previous;
    private long lastProgressAt;

    public void Reset() => previous = null;

    public string? Observe(PlaybackHealth sample, long now, bool audioOnly)
    {
        if (sample.State is PlaybackEngineState.Ended or PlaybackEngineState.Error or PlaybackEngineState.Stopped)
            return $"VLC entered {sample.State}";

        if (previous is not { } before || before.Generation != sample.Generation ||
            sample.State == PlaybackEngineState.Paused)
        {
            previous = sample;
            lastProgressAt = now;
            return null;
        }

        previous = sample;
        if (HasOutputProgress(before, sample, audioOnly)) lastProgressAt = now;
        return now - lastProgressAt >= StallTimeout.TotalMilliseconds
            ? $"VLC produced no {(audioOnly ? "audio" : "video")} output for {StallTimeout.TotalSeconds:0} seconds (state={sample.State})"
            : null;
    }

    internal static bool HasOutputProgress(PlaybackHealth before, PlaybackHealth after, bool audioOnly) =>
        before.Generation == after.Generation && after.State == PlaybackEngineState.Playing &&
        (audioOnly ? after.DecodedAudio != before.DecodedAudio :
            after.DecodedVideo != before.DecodedVideo && after.DisplayedPictures != before.DisplayedPictures);

    internal static TimeSpan RetryDelay(int failureCount) =>
        TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, Math.Clamp(failureCount - 1, 0, 5))));
}

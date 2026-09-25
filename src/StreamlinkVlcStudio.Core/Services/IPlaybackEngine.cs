using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Services;

public interface IPlaybackEngine : IDisposable
{
    event EventHandler? VideoOutputRebound;
    event EventHandler? AudioStateReapplied;
    bool UsesNativeOverlay { get; }
    // True only when the current input can unpause without resetting its live timeline.
    bool PreservesReplayPositionOnResume => false;
    // Check the current input and unpause atomically. False leaves it paused.
    Task<bool> TryResumeReplayAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    string? NativeOverlayPipeName { get; }
    string? NativeOverlayPositionStatePath { get; }
    string? NativeOverlayDirectory { get; }
    void SetVideoHandle(IntPtr handle);
    Task PlayAsync(Uri mediaUri, int volume, PlaybackAudioState audioState, CancellationToken cancellationToken = default);
    // Open at this position before presenting decoded audio/video, and confirm readiness.
    Task PlayFromAsync(Uri mediaUri, TimeSpan position, int volume, PlaybackAudioState audioState, CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    bool TryGetPlaybackClock(out PlaybackClock clock);
    bool TryGetVideoSize(out int width, out int height);
    bool TryGetVideoCursor(out int x, out int y);
    void SetAudioState(int volume, PlaybackAudioState audioState);
}

using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Services;

public interface IPlaybackEngine : IDisposable
{
    event EventHandler? VideoOutputRebound;
    event EventHandler? AudioStateReapplied;
    bool UsesNativeOverlay { get; }
    string? NativeOverlayPipeName { get; }
    string? NativeOverlayPositionStatePath { get; }
    void SetVideoHandle(IntPtr handle);
    Task PlayAsync(Uri mediaUri, int volume, PlaybackAudioState audioState, CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    bool TryGetPlaybackClock(out PlaybackClock clock);
    bool TryGetVideoSize(out int width, out int height);
    bool TryGetVideoCursor(out int x, out int y);
    void SetAudioState(int volume, PlaybackAudioState audioState);
    void SetOverlayText(string? text, bool visible, double opacity, double fontSize);
}

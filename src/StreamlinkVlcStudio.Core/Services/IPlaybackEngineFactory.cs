using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Services;

public interface IPlaybackEngineFactory
{
    Task<IPlaybackEngine> CreateAsync(
        string vlcDirectory,
        bool enableNativeOverlay = true,
        string? nativeOverlayPositionStatePath = null,
        CancellationToken cancellationToken = default,
        VideoRendererMode rendererMode = VideoRendererMode.Automatic);
}

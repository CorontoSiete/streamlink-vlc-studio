namespace StreamlinkVlcStudio.Core.Services;

public interface IPlaybackEngineFactory
{
    IPlaybackEngine Create(string vlcDirectory, bool enableNativeOverlay = true, string? nativeOverlayPositionStatePath = null);
}

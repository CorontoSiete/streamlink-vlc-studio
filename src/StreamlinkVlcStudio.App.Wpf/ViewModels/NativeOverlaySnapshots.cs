using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

// The controller can compare input identity and render state without owning or calling a player.
internal sealed record NativeOverlayPlaybackSnapshot(object Identity, bool UsesNativeOverlay,
    string? NativeOverlayPipeName, string? NativeOverlayPositionStatePath, string? NativeOverlayDirectory,
    int Width, int Height)
{
    internal bool TryGetVideoSize(out int width, out int height)
    {
        width = Width;
        height = Height;
        return width > 0 && height > 0;
    }
}

internal sealed record NativeOverlayChatSnapshot(bool IsReplayMode, bool IsBehindLive,
    bool IsVisible, bool IsDockedOverrideActive, ReplaySessionInfo? Replay, ChatMessage? StatusMessage,
    NativeOverlayChatOptions? Options);

internal sealed record NativeOverlayChatOptions(ChatLayout Layout, double DockWidth, double FontSize)
{
    internal ChatSettings ToSettings() => new() { Layout = Layout, DockWidth = DockWidth, VlcOverlayFontSize = FontSize };
}

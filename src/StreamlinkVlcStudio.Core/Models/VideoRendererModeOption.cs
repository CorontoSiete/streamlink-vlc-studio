namespace StreamlinkVlcStudio.Core.Models;

public sealed record VideoRendererModeOption(VideoRendererMode Value, string DisplayName)
{
    public override string ToString() => DisplayName;

    public static IReadOnlyList<VideoRendererModeOption> All { get; } =
        Array.AsReadOnly<VideoRendererModeOption>(
        [
            new(VideoRendererMode.Automatic, "Automatic (recommended)"),
            new(VideoRendererMode.Direct3D11, "Direct3D 11"),
            new(VideoRendererMode.Gdi, "GDI compatibility")
        ]);
}

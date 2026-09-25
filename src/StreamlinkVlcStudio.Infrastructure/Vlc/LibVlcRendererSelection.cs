using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

internal static class LibVlcRendererSelection
{
    internal static VideoRendererMode Resolve(
        string vlcDirectory,
        VideoRendererMode requestedMode,
        bool usesNativeOverlay)
    {
        // An embedded Direct3D swapchain can replace the whole application as the
        // target of graphics-hook capture. Automatic must keep window composition.
        if (usesNativeOverlay || requestedMode != VideoRendererMode.Direct3D11)
        {
            return VideoRendererMode.Gdi;
        }

        return IsDirect3D11Available(vlcDirectory)
            ? VideoRendererMode.Direct3D11
            : VideoRendererMode.Gdi;
    }

    internal static bool IsDirect3D11Available(string vlcDirectory)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(vlcDirectory))
        {
            return false;
        }

        var pluginDirectory = Path.Combine(vlcDirectory, "plugins");
        if (!Directory.Exists(pluginDirectory))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(
                    pluginDirectory,
                    "*direct3d11_plugin.dll",
                    SearchOption.AllDirectories)
                .Any();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string GetVoutOption(VideoRendererMode rendererMode)
    {
        return rendererMode == VideoRendererMode.Direct3D11
            ? "direct3d11"
            : "wingdi";
    }

    internal static string GetHardwareDecodingOption(VideoRendererMode rendererMode, bool usesNativeOverlay)
    {
        // VLC 3 blends GDI subpictures before converting hardware surfaces to RGB.
        // Its software blender cannot write to DX11/DXVA surfaces, so native chat
        // disappears even while the controller and video output are healthy.
        return rendererMode != VideoRendererMode.Direct3D11 && usesNativeOverlay ? "none" : "any";
    }
}

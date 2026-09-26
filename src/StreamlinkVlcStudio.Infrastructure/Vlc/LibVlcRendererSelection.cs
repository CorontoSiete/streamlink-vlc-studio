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

    internal static string GetVoutOption(VideoRendererMode rendererMode, bool usesNativeOverlay = false)
    {
        return rendererMode == VideoRendererMode.Direct3D11
            ? "direct3d11"
            // The bundled overlay also provides GDI output that filters chat
            // separately. Retain stock GDI for older custom overlay plugins.
            : usesNativeOverlay ? "studio_gdi,wingdi" : "wingdi";
    }

    internal static string GetHardwareDecodingOption(VideoRendererMode rendererMode, bool usesNativeOverlay)
    {
        // Keep software decoding for the stock-GDI fallback: its early subtitle
        // blender cannot write to DX11/DXVA surfaces. Studio GDI also consumes
        // CPU-accessible RGB frames for its final window composition.
        return rendererMode != VideoRendererMode.Direct3D11 && usesNativeOverlay ? "none" : "any";
    }
}

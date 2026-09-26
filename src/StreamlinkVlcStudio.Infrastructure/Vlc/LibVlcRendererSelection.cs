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

    internal static string GetVoutOption(VideoRendererMode rendererMode, bool usesNativeOverlay = false,
        bool hardwareOverlayComposition = false)
    {
        return rendererMode == VideoRendererMode.Direct3D11
            ? "direct3d11"
            // The bundled overlay also provides GDI output that filters chat
            // separately. Retain stock GDI for older custom overlay plugins.
            : usesNativeOverlay ? hardwareOverlayComposition ? "studio_gdi" : "studio_gdi,wingdi" : "wingdi";
    }

    internal static string GetHardwareDecodingOption(VideoRendererMode rendererMode, bool usesNativeOverlay,
        bool hardwareOverlayComposition = false)
    {
        if (rendererMode == VideoRendererMode.Direct3D11) return "any";
        // Stock GDI blends subtitles before downloading hardware surfaces.
        if (usesNativeOverlay && !hardwareOverlayComposition) return "none";
        // VLC 3's D3D11 download filter retains its staging texture when HLS
        // restarts the decoder on seek. Reading a new decoder's surfaces through
        // that stale resource fails (0x887a0005) and presents green frames. DXVA2
        // keeps GPU decoding with a download path that survives those restarts.
        // If unsupported, VLC falls back to software, not another HW module.
        return "dxva2";
    }
}

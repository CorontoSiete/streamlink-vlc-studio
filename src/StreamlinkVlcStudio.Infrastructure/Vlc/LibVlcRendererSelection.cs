using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

internal static class LibVlcRendererSelection
{
    internal static VideoRendererMode Resolve(
        string vlcDirectory,
        VideoRendererMode requestedMode,
        bool usesNativeOverlay)
    {
        if (usesNativeOverlay || requestedMode == VideoRendererMode.Gdi)
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
}

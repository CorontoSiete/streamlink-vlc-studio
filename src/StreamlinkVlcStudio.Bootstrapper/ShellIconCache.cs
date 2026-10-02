using System.IO;
using System.Runtime.InteropServices;

namespace StreamlinkVlcStudio.Bootstrapper;

internal static partial class ShellIconCache
{
    private static readonly string[] SearchPackages =
    [
        "Microsoft.Windows.Search_cw5n1h2txyewy",
        "Microsoft.Windows.Cortana_cw5n1h2txyewy",
        "MicrosoftWindows.Client.CBS_cw5n1h2txyewy"
    ];

    private static readonly string[] ApplicationIconNames =
    [
        "StreamStudio_exe",
        "StreamlinkVlcStudio_exe",
        "StreamlinkVlcStudio_App_Wpf_exe"
    ];

    internal static void Refresh()
    {
        // Windows Search keeps separate bitmap copies that shell notifications do not clear.
        ClearSearchIcons(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        // Refresh cached shell images after replacing the app and its shortcuts.
        // Flush without waiting for Explorer so icon updates cannot delay Setup.
        const uint associationChanged = 0x08000000;
        const uint flushNoWait = 0x2000;
        SHChangeNotify(associationChanged, flushNoWait, 0, 0);
    }

    internal static int ClearSearchIcons(string localApplicationData)
    {
        var removed = 0;
        foreach (var package in SearchPackages)
        {
            var cache = Path.Combine(localApplicationData, "Packages", package, "LocalState", "AppIconCache");
            try
            {
                if (!Directory.Exists(cache) || (File.GetAttributes(cache) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                removed += ClearCacheDirectory(cache);
                foreach (var scale in Directory.EnumerateDirectories(cache))
                {
                    if (Path.GetFileName(scale).All(char.IsAsciiDigit) &&
                        (File.GetAttributes(scale) & FileAttributes.ReparsePoint) == 0)
                    {
                        removed += ClearCacheDirectory(scale);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A missing or busy Search cache must not fail an installed update.
            }
        }

        return removed;
    }

    private static int ClearCacheDirectory(string directory)
    {
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (!ApplicationIconNames.Any(icon => name.Equals(icon, StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("_" + icon, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Continue refreshing other entries when one bitmap is locked.
            }
        }

        return removed;
    }

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}

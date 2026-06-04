using System.Diagnostics;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

internal sealed record VlcOverlayPluginRuntime(string PluginRoot);

public static class VlcOverlayDirectoryResolver
{
    public const string BundledOverlayDirectoryName = "vlc-overlay";
    public const string BuildDirectoryName = "build";
    public const string PluginFileName = "libmyoverlay_plugin.dll";
    public const string ControllerFileName = "vlc_chat_overlay.exe";

    public static string? TryResolve(string? configuredDirectory, string? appBaseDirectory = null)
    {
        foreach (var candidate in EnumerateCandidateDirectories(configuredDirectory, appBaseDirectory))
        {
            if (IsValidOverlayDirectory(candidate))
            {
                return NormalizeDirectory(candidate);
            }
        }

        return null;
    }

    public static bool IsValidOverlayDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            var normalized = NormalizeDirectory(directory);
            return File.Exists(GetPluginPath(normalized)) &&
                File.Exists(GetControllerPath(normalized));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string GetPluginPath(string overlayDirectory) =>
        Path.Combine(NormalizeDirectory(overlayDirectory), BuildDirectoryName, PluginFileName);

    public static string GetControllerPath(string overlayDirectory) =>
        Path.Combine(NormalizeDirectory(overlayDirectory), BuildDirectoryName, ControllerFileName);

    public static string GetBundledOverlayDirectory(string? appBaseDirectory = null)
    {
        var baseDirectory = string.IsNullOrWhiteSpace(appBaseDirectory)
            ? AppContext.BaseDirectory
            : appBaseDirectory.Trim();
        return Path.Combine(baseDirectory, BundledOverlayDirectoryName);
    }

    public static string NormalizeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return "";
        }

        var trimmed = directory.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return trimmed;
        }
    }

    private static IEnumerable<string> EnumerateCandidateDirectories(string? configuredDirectory, string? appBaseDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            var configured = NormalizeDirectory(configuredDirectory);
            if (seen.Add(configured))
            {
                yield return configured;
            }
        }

        var bundled = NormalizeDirectory(GetBundledOverlayDirectory(appBaseDirectory));
        if (seen.Add(bundled))
        {
            yield return bundled;
        }
    }
}

internal static class VlcOverlayPluginRuntimeFactory
{
    private static readonly object PrepareGate = new();

    public static VlcOverlayPluginRuntime? TryPrepare(string vlcDirectory, string? overlayDirectory, IAppLogger logger)
    {
        try
        {
            lock (PrepareGate)
            {
                var resolvedOverlayDirectory = VlcOverlayDirectoryResolver.TryResolve(overlayDirectory);
                if (string.IsNullOrWhiteSpace(resolvedOverlayDirectory))
                {
                    logger.Write(AppLogLevel.Warning, "VlcOverlay", "VLC overlay plugin/controller files were not found; falling back to basic overlay.");
                    return null;
                }

                var sourcePlugin = VlcOverlayDirectoryResolver.GetPluginPath(resolvedOverlayDirectory);
                if (!File.Exists(sourcePlugin))
                {
                    logger.Write(AppLogLevel.Warning, "VlcOverlay", $"VLC overlay plugin was not found at {sourcePlugin}.");
                    return null;
                }

                var pluginRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "StreamlinkVlcStudio",
                    "vlc-overlay-plugins");
                var pluginSpuDirectory = Path.Combine(pluginRoot, "spu");
                Directory.CreateDirectory(pluginSpuDirectory);

                var targetPlugin = Path.Combine(pluginSpuDirectory, "libmyoverlay_plugin.dll");
                if (ShouldCopy(sourcePlugin, targetPlugin))
                {
                    File.Copy(sourcePlugin, targetPlugin, overwrite: true);
                }

                var cachePath = Path.Combine(pluginRoot, "plugins.dat");
                if (ShouldRegenerateCache(targetPlugin, cachePath))
                {
                    RegeneratePluginCache(vlcDirectory, pluginRoot, logger);
                }

                return new VlcOverlayPluginRuntime(pluginRoot);
            }
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "VlcOverlay", "VLC overlay plugin preparation failed; falling back to basic overlay.", ex);
            return null;
        }
    }

    private static bool ShouldCopy(string source, string target)
    {
        if (!File.Exists(target))
        {
            return true;
        }

        var sourceInfo = new FileInfo(source);
        var targetInfo = new FileInfo(target);
        return sourceInfo.Length != targetInfo.Length ||
            sourceInfo.LastWriteTimeUtc > targetInfo.LastWriteTimeUtc;
    }

    private static bool ShouldRegenerateCache(string pluginPath, string cachePath)
    {
        if (!File.Exists(cachePath))
        {
            return true;
        }

        return File.GetLastWriteTimeUtc(pluginPath) > File.GetLastWriteTimeUtc(cachePath);
    }

    private static void RegeneratePluginCache(string vlcDirectory, string pluginRoot, IAppLogger logger)
    {
        var cacheGenerator = Path.Combine(vlcDirectory, "vlc-cache-gen.exe");
        if (!File.Exists(cacheGenerator))
        {
            logger.Write(AppLogLevel.Warning, "VlcOverlay", "vlc-cache-gen.exe was not found; VLC may need to scan the overlay plugin at startup.");
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = cacheGenerator,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(pluginRoot);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            logger.Write(AppLogLevel.Warning, "VlcOverlay", "vlc-cache-gen.exe could not be started.");
            return;
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            logger.Write(AppLogLevel.Warning, "VlcOverlay", $"vlc-cache-gen.exe failed: {output} {error}".Trim());
        }
    }
}

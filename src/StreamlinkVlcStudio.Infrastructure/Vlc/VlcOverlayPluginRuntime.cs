using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

internal sealed record VlcOverlayPluginRuntime(
    string PluginRoot,
    string OverlayDirectory,
    string PluginPath,
    string PluginSha256,
    string ControllerPath,
    string ControllerSha256);

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

        if (string.IsNullOrWhiteSpace(appBaseDirectory) &&
            VlcOverlayBundledResourceExtractor.IsExtractedOverlayCurrent())
        {
            var extracted = NormalizeDirectory(VlcOverlayBundledResourceExtractor.GetExtractedOverlayDirectory());
            if (seen.Add(extracted))
            {
                yield return extracted;
            }
        }
    }
}

public static class VlcOverlayBundledResourceExtractor
{
    public const string ExtractedOverlayDirectoryName = "vlc-overlay-bundled";

    private const string ResourcePrefix = "StreamlinkVlcStudio.Infrastructure.Vlc.BundledOverlay";
    private static readonly object ExtractGate = new();
    private static readonly BundledOverlayFile[] RequiredFiles =
    [
        new(
            Path.Combine(VlcOverlayDirectoryResolver.BuildDirectoryName, VlcOverlayDirectoryResolver.PluginFileName),
            $"{ResourcePrefix}.build.{VlcOverlayDirectoryResolver.PluginFileName}"),
        new(
            Path.Combine(VlcOverlayDirectoryResolver.BuildDirectoryName, VlcOverlayDirectoryResolver.ControllerFileName),
            $"{ResourcePrefix}.build.{VlcOverlayDirectoryResolver.ControllerFileName}")
    ];

    public static string GetExtractedOverlayDirectory(string? appDataDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(appDataDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StreamlinkVlcStudio")
            : appDataDirectory.Trim();
        return Path.Combine(root, ExtractedOverlayDirectoryName);
    }

    public static bool HasBundledOverlayResources()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return RequiredFiles.All(file => assembly.GetManifestResourceInfo(file.ResourceName) is not null);
    }

    public static bool IsExtractedOverlayCurrent(string? appDataDirectory = null)
    {
        if (!HasBundledOverlayResources())
        {
            return false;
        }

        try
        {
            var overlayDirectory = GetExtractedOverlayDirectory(appDataDirectory);
            return RequiredFiles.All(file =>
            {
                using var resource = OpenRequiredResource(file);
                return FileMatchesResource(resource, GetTargetPath(overlayDirectory, file));
            }) &&
                VlcOverlayDirectoryResolver.IsValidOverlayDirectory(overlayDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    public static string? TryExtract(IAppLogger logger, string? appDataDirectory = null)
    {
        if (!HasBundledOverlayResources())
        {
            logger.Write(AppLogLevel.Warning, "VlcOverlay", "Embedded VLC overlay plugin/controller resources were not found.");
            return null;
        }

        try
        {
            lock (ExtractGate)
            {
                var overlayDirectory = GetExtractedOverlayDirectory(appDataDirectory);
                foreach (var file in RequiredFiles)
                {
                    ExtractFile(file, overlayDirectory);
                }

                if (!VlcOverlayDirectoryResolver.IsValidOverlayDirectory(overlayDirectory))
                {
                    logger.Write(AppLogLevel.Warning, "VlcOverlay", $"Extracted VLC overlay directory is incomplete: {overlayDirectory}");
                    return null;
                }

                return VlcOverlayDirectoryResolver.NormalizeDirectory(overlayDirectory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            logger.Write(AppLogLevel.Warning, "VlcOverlay", "Could not extract the embedded VLC overlay plugin/controller.", ex);
            return null;
        }
    }

    private static void ExtractFile(BundledOverlayFile file, string overlayDirectory)
    {
        using var resource = OpenRequiredResource(file);
        var targetPath = GetTargetPath(overlayDirectory, file);
        if (FileMatchesResource(resource, targetPath))
        {
            return;
        }

        resource.Position = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                resource.CopyTo(target);
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    private static Stream OpenRequiredResource(BundledOverlayFile file)
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(file.ResourceName);
        return stream ?? throw new FileNotFoundException($"Embedded VLC overlay resource missing: {file.ResourceName}");
    }

    private static bool FileMatchesResource(Stream resource, string targetPath)
    {
        if (!File.Exists(targetPath) || !resource.CanSeek)
        {
            return false;
        }

        var fileInfo = new FileInfo(targetPath);
        if (fileInfo.Length != resource.Length)
        {
            return false;
        }

        resource.Position = 0;
        var resourceHash = SHA256.HashData(resource);
        resource.Position = 0;
        using var target = File.OpenRead(targetPath);
        var targetHash = SHA256.HashData(target);
        return resourceHash.AsSpan().SequenceEqual(targetHash);
    }

    private static string GetTargetPath(string overlayDirectory, BundledOverlayFile file) =>
        Path.Combine(overlayDirectory, file.RelativePath);

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record BundledOverlayFile(string RelativePath, string ResourceName);
}

internal static class VlcOverlayPluginRuntimeFactory
{
    private static readonly object PrepareGate = new();

    public static VlcOverlayPluginRuntime? TryPrepare(
        string vlcDirectory,
        string? overlayDirectory,
        IAppLogger logger,
        string? appDataDirectory = null)
    {
        try
        {
            lock (PrepareGate)
            {
                var resolvedOverlayDirectory = VlcOverlayDirectoryResolver.TryResolve(overlayDirectory) ??
                    VlcOverlayBundledResourceExtractor.TryExtract(logger);
                if (string.IsNullOrWhiteSpace(resolvedOverlayDirectory))
                {
                    logger.Write(AppLogLevel.Warning, "VlcOverlay", "VLC overlay plugin/controller files were not found; falling back to basic overlay.");
                    return null;
                }

                var sourcePlugin = VlcOverlayDirectoryResolver.GetPluginPath(resolvedOverlayDirectory);
                var sourceController = VlcOverlayDirectoryResolver.GetControllerPath(resolvedOverlayDirectory);
                if (!File.Exists(sourcePlugin))
                {
                    logger.Write(AppLogLevel.Warning, "VlcOverlay", $"VLC overlay plugin was not found at {sourcePlugin}.");
                    return null;
                }

                var pluginRoot = Path.Combine(GetAppDataDirectory(appDataDirectory), "vlc-overlay-plugins");
                var pluginSpuDirectory = Path.Combine(pluginRoot, "spu");
                Directory.CreateDirectory(pluginSpuDirectory);

                var targetPlugin = Path.Combine(pluginSpuDirectory, "libmyoverlay_plugin.dll");
                var cachePath = Path.Combine(pluginRoot, "plugins.dat");
                var copiedPlugin = false;
                if (ShouldCopy(sourcePlugin, targetPlugin))
                {
                    File.Copy(sourcePlugin, targetPlugin, overwrite: true);
                    TryDeletePluginCache(cachePath, logger);
                    copiedPlugin = true;
                }

                if (copiedPlugin || ShouldRegenerateCache(cachePath))
                {
                    RegeneratePluginCache(vlcDirectory, pluginRoot, logger);
                }

                var pluginHash = ComputeFileSha256(targetPlugin);
                var controllerHash = ComputeFileSha256(sourceController);
                logger.Write(
                    AppLogLevel.Info,
                    "VlcOverlay",
                    $"Prepared VLC overlay plugin cache plugin={targetPlugin} pluginSha256={pluginHash} controller={sourceController} controllerSha256={controllerHash} source={resolvedOverlayDirectory} copied={copiedPlugin.ToString().ToLowerInvariant()}.");

                return new VlcOverlayPluginRuntime(
                    pluginRoot,
                    resolvedOverlayDirectory,
                    targetPlugin,
                    pluginHash,
                    sourceController,
                    controllerHash);
            }
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "VlcOverlay", "VLC overlay plugin preparation failed; falling back to basic overlay.", ex);
            return null;
        }
    }

    private static string GetAppDataDirectory(string? appDataDirectory)
    {
        return string.IsNullOrWhiteSpace(appDataDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StreamlinkVlcStudio")
            : appDataDirectory.Trim();
    }

    private static bool ShouldCopy(string source, string target)
    {
        if (!File.Exists(target))
        {
            return true;
        }

        var sourceInfo = new FileInfo(source);
        var targetInfo = new FileInfo(target);
        if (sourceInfo.Length != targetInfo.Length)
        {
            return true;
        }

        return !FileHashesMatch(source, target);
    }

    private static bool ShouldRegenerateCache(string cachePath)
    {
        return !File.Exists(cachePath);
    }

    private static bool FileHashesMatch(string first, string second)
    {
        using var firstStream = File.OpenRead(first);
        using var secondStream = File.OpenRead(second);
        var firstHash = SHA256.HashData(firstStream);
        var secondHash = SHA256.HashData(secondStream);
        return firstHash.AsSpan().SequenceEqual(secondHash);
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDeletePluginCache(string cachePath, IAppLogger logger)
    {
        try
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Write(AppLogLevel.Warning, "VlcOverlay", $"Could not delete stale VLC overlay plugin cache {cachePath}.", ex);
        }
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

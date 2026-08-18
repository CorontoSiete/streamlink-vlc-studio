using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Processes;
using static StreamlinkVlcStudio.Infrastructure.Processes.ProcessExtensions;

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

        var trimmed = directory.Trim();
        try
        {
            // TrimEndingDirectorySeparator preserves filesystem roots (for example, C:\\ and
            // \\server\\share\\). Manually trimming the separator turns C:\\ into C:, which is
            // drive-relative on Windows and can resolve to the wrong directory.
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
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
    internal const string CacheManifestFileName = "plugins.manifest.json";
    private const int CacheManifestFormatVersion = 1;
    private static readonly SemaphoreSlim PrepareGate = new(1, 1);
    private static readonly TimeSpan CacheGenerationTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions CacheManifestJsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task<VlcOverlayPluginRuntime?> TryPrepareAsync(
        string vlcDirectory,
        string? overlayDirectory,
        IAppLogger logger,
        string? appDataDirectory = null,
        CancellationToken cancellationToken = default)
    {
        await PrepareGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resolvedOverlayDirectory = VlcOverlayDirectoryResolver.TryResolve(overlayDirectory) ??
                VlcOverlayBundledResourceExtractor.TryExtract(logger, appDataDirectory);
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
            var manifestPath = Path.Combine(pluginRoot, CacheManifestFileName);
            var copiedPlugin = false;
            if (ShouldCopy(sourcePlugin, targetPlugin))
            {
                File.Copy(sourcePlugin, targetPlugin, overwrite: true);
                copiedPlugin = true;
            }

            var expectedManifest = CreateCacheManifest(vlcDirectory, pluginRoot);
            if (copiedPlugin || !IsCacheManifestCurrent(cachePath, manifestPath, expectedManifest))
            {
                var regenerated = await RegeneratePluginCacheAsync(
                        vlcDirectory,
                        pluginRoot,
                        cachePath,
                        logger,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (regenerated)
                {
                    await WriteCacheManifestAtomicallyAsync(
                            manifestPath,
                            expectedManifest,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    // Never retain a cache whose inputs cannot be proven current. VLC can scan
                    // the plugin directory itself when cache generation is unavailable.
                    TryDeleteFile(cachePath, logger, "stale VLC plugin cache");
                    TryDeleteFile(manifestPath, logger, "stale VLC plugin cache manifest");
                }
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "VlcOverlay", "VLC overlay plugin preparation failed; falling back to basic overlay.", ex);
            return null;
        }
        finally
        {
            PrepareGate.Release();
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

    internal static bool IsCurrentCacheManifestForTest(string vlcDirectory, string pluginRoot)
    {
        var cachePath = Path.Combine(pluginRoot, "plugins.dat");
        var manifestPath = Path.Combine(pluginRoot, CacheManifestFileName);
        return IsCacheManifestCurrent(
            cachePath,
            manifestPath,
            CreateCacheManifest(vlcDirectory, pluginRoot));
    }

    internal static void WriteCurrentCacheManifestForTest(string vlcDirectory, string pluginRoot)
    {
        var manifestPath = Path.Combine(pluginRoot, CacheManifestFileName);
        WriteCacheManifestAtomicallyAsync(
                manifestPath,
                CreateCacheManifest(vlcDirectory, pluginRoot),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private static VlcPluginCacheManifest CreateCacheManifest(
        string vlcDirectory,
        string pluginRoot)
    {
        var pluginHashes = Directory.Exists(pluginRoot)
            ? Directory
                .EnumerateFiles(pluginRoot, "*.dll", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    path => Path.GetRelativePath(pluginRoot, path).Replace('\\', '/'),
                    ComputeFileIdentity,
                    StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return new VlcPluginCacheManifest(
            CacheManifestFormatVersion,
            ComputeFileIdentity(Path.Combine(vlcDirectory, "libvlc.dll")),
            ComputeFileIdentity(Path.Combine(vlcDirectory, "vlc-cache-gen.exe")),
            pluginHashes);
    }

    private static bool IsCacheManifestCurrent(
        string cachePath,
        string manifestPath,
        VlcPluginCacheManifest expected)
    {
        if (!File.Exists(cachePath) || !File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            var actual = JsonSerializer.Deserialize<VlcPluginCacheManifest>(
                json,
                CacheManifestJsonOptions);
            return actual is not null && CacheManifestsEqual(actual, expected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool CacheManifestsEqual(
        VlcPluginCacheManifest actual,
        VlcPluginCacheManifest expected)
    {
        return actual.FormatVersion == expected.FormatVersion &&
            string.Equals(actual.VlcIdentity, expected.VlcIdentity, StringComparison.Ordinal) &&
            string.Equals(actual.CacheGeneratorIdentity, expected.CacheGeneratorIdentity, StringComparison.Ordinal) &&
            actual.PluginIdentities.Count == expected.PluginIdentities.Count &&
            expected.PluginIdentities.All(pair =>
                actual.PluginIdentities.TryGetValue(pair.Key, out var identity) &&
                string.Equals(identity, pair.Value, StringComparison.Ordinal));
    }

    private static async Task WriteCacheManifestAtomicallyAsync(
        string manifestPath,
        VlcPluginCacheManifest manifest,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{manifestPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(manifest, CacheManifestJsonOptions);
            await File.WriteAllTextAsync(
                    temporaryPath,
                    json,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
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

    private static string ComputeFileIdentity(string path)
    {
        if (!File.Exists(path))
        {
            return "missing";
        }

        var file = new FileInfo(path);
        return $"{file.Length}:{ComputeFileSha256(path)}";
    }

    private static void TryDeleteFile(string path, IAppLogger logger, string description)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Write(AppLogLevel.Warning, "VlcOverlay", $"Could not delete {description} {path}.", ex);
        }
    }

    private static async Task<bool> RegeneratePluginCacheAsync(
        string vlcDirectory,
        string pluginRoot,
        string cachePath,
        IAppLogger logger,
        CancellationToken cancellationToken)
    {
        var cacheGenerator = Path.Combine(vlcDirectory, "vlc-cache-gen.exe");
        if (!File.Exists(cacheGenerator))
        {
            logger.Write(AppLogLevel.Warning, "VlcOverlay", "vlc-cache-gen.exe was not found; VLC may need to scan the overlay plugin at startup.");
            return false;
        }

        var stagingRoot = Path.Combine(
            Path.GetDirectoryName(pluginRoot)!,
            $"{Path.GetFileName(pluginRoot)}.cache-{Guid.NewGuid():N}");
        try
        {
            CopyPluginFiles(pluginRoot, stagingRoot);
            ProcessExecutionResult result;
            try
            {
                result = await RunRedirectedProcessAsync(
                        CreateRedirectedStartInfo(cacheGenerator, [stagingRoot]),
                        CacheGenerationTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.Write(AppLogLevel.Warning, "VlcOverlay", "vlc-cache-gen.exe could not be started.", ex);
                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            if (result.TimedOut)
            {
                logger.Write(AppLogLevel.Warning, "VlcOverlay", "vlc-cache-gen.exe timed out while rebuilding the VLC plugin cache.");
                return false;
            }

            if (result.ExitCode != 0 || result.OutputWasTruncated)
            {
                logger.Write(AppLogLevel.Warning, "VlcOverlay", $"vlc-cache-gen.exe failed: {result.StandardOutput} {result.StandardError}".Trim());
                return false;
            }

            var stagedCachePath = Path.Combine(stagingRoot, "plugins.dat");
            if (!File.Exists(stagedCachePath))
            {
                logger.Write(AppLogLevel.Warning, "VlcOverlay", "vlc-cache-gen.exe completed without producing plugins.dat.");
                return false;
            }

            var swapPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.Move(stagedCachePath, swapPath, overwrite: true);
                File.Move(swapPath, cachePath, overwrite: true);
            }
            finally
            {
                TryDeleteFile(swapPath, logger, "temporary VLC plugin cache");
            }

            return true;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.Write(AppLogLevel.Debug, "VlcOverlay", $"Could not remove temporary plugin-cache directory {stagingRoot}.", ex);
            }
        }
    }

    private static void CopyPluginFiles(string pluginRoot, string stagingRoot)
    {
        foreach (var source in Directory.EnumerateFiles(pluginRoot, "*.dll", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(pluginRoot, source);
            var target = Path.Combine(stagingRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: false);
        }
    }

    private sealed record VlcPluginCacheManifest(
        int FormatVersion,
        string VlcIdentity,
        string CacheGeneratorIdentity,
        IReadOnlyDictionary<string, string> PluginIdentities);
}

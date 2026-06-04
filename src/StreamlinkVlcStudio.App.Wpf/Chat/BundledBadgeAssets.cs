using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

internal static class BundledBadgeAssets
{
    private const string AssetRoot = "Assets";
    private const string ManifestFileName = "badges.json";
    private static readonly object Sync = new();
    private static readonly Lazy<IReadOnlyDictionary<string, byte[]>> ResourceBytes = new(
        LoadResourceBytes,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Dictionary<string, string?> ExtractedManifestPaths = new(StringComparer.OrdinalIgnoreCase);

    public static string? FindTwitchBadgeManifestPath()
    {
        return FindBadgeManifestPath("TwitchBadges");
    }

    public static string? FindKickBadgeManifestPath()
    {
        return FindBadgeManifestPath("KickBadges");
    }

    private static string? FindBadgeManifestPath(string badgeDirectoryName)
    {
        var relativeManifestPath = Path.Combine(AssetRoot, badgeDirectoryName, ManifestFileName);
        return FindExistingManifestPath(relativeManifestPath, includeSourceFallback: false) ??
            ExtractBadgeManifestPath(badgeDirectoryName) ??
            FindExistingManifestPath(relativeManifestPath, includeSourceFallback: true);
    }

    private static string? FindExistingManifestPath(string relativeManifestPath, bool includeSourceFallback)
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, relativeManifestPath);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            candidate = Path.Combine(directory.FullName, relativeManifestPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (!includeSourceFallback)
            {
                continue;
            }

            candidate = Path.Combine(
                directory.FullName,
                "src",
                "StreamlinkVlcStudio.App.Wpf",
                relativeManifestPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ExtractBadgeManifestPath(string badgeDirectoryName)
    {
        lock (Sync)
        {
            if (ExtractedManifestPaths.TryGetValue(badgeDirectoryName, out var cachedPath))
            {
                return cachedPath;
            }

            var extractedPath = TryExtractBadgeManifestPath(badgeDirectoryName);
            ExtractedManifestPaths[badgeDirectoryName] = extractedPath;
            return extractedPath;
        }
    }

    private static string? TryExtractBadgeManifestPath(string badgeDirectoryName)
    {
        try
        {
            var manifestResourcePath = $"{AssetRoot}/{badgeDirectoryName}/{ManifestFileName}";
            var manifestBytes = ReadResourceBytes(manifestResourcePath);
            if (manifestBytes is null)
            {
                return null;
            }

            var manifestJsonBytes = StripUtf8ByteOrderMark(manifestBytes);
            using var document = JsonDocument.Parse(manifestJsonBytes);
            var hash = Convert.ToHexString(SHA256.HashData(manifestJsonBytes))[..16].ToLowerInvariant();
            var root = Path.Combine(GetCacheRoot(), badgeDirectoryName, hash);
            Directory.CreateDirectory(root);

            var manifestPath = Path.Combine(root, ManifestFileName);
            WriteFileIfDifferent(manifestPath, manifestJsonBytes);

            if (!document.RootElement.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                return manifestPath;
            }

            var extractedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("image", out var imageProperty) ||
                    imageProperty.ValueKind != JsonValueKind.String ||
                    !TryNormalizeRelativeAssetPath(imageProperty.GetString(), out var relativeImagePath) ||
                    !extractedImages.Add(relativeImagePath))
                {
                    continue;
                }

                var imageResourcePath = $"{AssetRoot}/{badgeDirectoryName}/{relativeImagePath}";
                var imageBytes = ReadResourceBytes(imageResourcePath);
                if (imageBytes is null ||
                    !TryGetTargetPath(root, relativeImagePath, out var imageTargetPath))
                {
                    continue;
                }

                WriteFileIfDifferent(imageTargetPath, imageBytes);
            }

            return manifestPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[]? ReadResourceBytes(string resourcePath)
    {
        var normalizedResourcePath = resourcePath.Replace('\\', '/').ToLowerInvariant();
        return ResourceBytes.Value.TryGetValue(normalizedResourcePath, out var bytes) ? bytes : null;
    }

    private static byte[] StripUtf8ByteOrderMark(byte[] bytes)
    {
        return bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF
            ? bytes[3..]
            : bytes;
    }

    private static IReadOnlyDictionary<string, byte[]> LoadResourceBytes()
    {
        var resources = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(BundledBadgeAssets).Assembly;
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(".g.resources", StringComparison.Ordinal));
        if (resourceName is null)
        {
            return resources;
        }

        try
        {
            using var resourceStream = assembly.GetManifestResourceStream(resourceName);
            if (resourceStream is null)
            {
                return resources;
            }

            using var reader = new System.Resources.ResourceReader(resourceStream);
            var enumerator = reader.GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (enumerator.Key is not string key ||
                    !key.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    if (enumerator.Value is not Stream stream)
                    {
                        continue;
                    }

                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    resources[key] = memory.ToArray();
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
        }

        return resources;
    }

    private static string GetCacheRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.GetTempPath();
        }

        return Path.Combine(localAppData, "StreamlinkVlcStudio", "BundledBadgeAssets");
    }

    private static bool TryNormalizeRelativeAssetPath(string? path, out string normalizedPath)
    {
        normalizedPath = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var candidate = path.Trim().Replace('\\', '/');
        if (candidate.Length == 0 ||
            candidate.StartsWith("/", StringComparison.Ordinal) ||
            candidate.Contains("://", StringComparison.Ordinal) ||
            candidate.IndexOf('\0') >= 0)
        {
            return false;
        }

        var parts = candidate.Split('/');
        if (parts.Any(part => part.Length == 0 || part == "." || part == ".."))
        {
            return false;
        }

        normalizedPath = candidate;
        return true;
    }

    private static bool TryGetTargetPath(string root, string relativePath, out string targetPath)
    {
        targetPath = "";
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        targetPath = candidate;
        return true;
    }

    private static void WriteFileIfDifferent(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path))
        {
            var existing = new FileInfo(path);
            if (existing.Length == bytes.Length &&
                File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            {
                return;
            }
        }

        File.WriteAllBytes(path, bytes);
    }
}

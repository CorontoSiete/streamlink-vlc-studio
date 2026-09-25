using System.Text.RegularExpressions;

namespace StreamlinkVlcStudio.Maintenance;

public static partial class PathSafety
{
    private const string ProductDirectoryName = "StreamStudio";
    private const string LegacyProductDirectoryName = "StreamlinkVlcStudio";
    private const string InstallerTempDirectoryPrefix = "StreamStudio-installer-";
    private const string LegacyInstallerTempDirectoryPrefix = "StreamlinkVlcStudio-installer-";
    private const string MaintenanceLogsDirectoryName = "StreamStudio-Maintenance-Logs";
    private const string LegacyMaintenanceLogsDirectoryName = "StreamlinkVlcStudio-Maintenance-Logs";
    private const string MaintenanceStageDirectoryPrefix = "StreamStudio-Maintenance-Stage-";
    private const string LegacyMaintenanceStageDirectoryPrefix = "StreamlinkVlcStudio-Maintenance-Stage-";

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    public static bool IsSafeManifestRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.IndexOf('\0') >= 0 ||
            Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var segments = relativePath.Replace('\\', '/').Split('/');
        return segments.Length > 0 && segments.All(IsSafeWindowsSegment);
    }

    public static bool IsExactUserDataRoot(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        string fullCandidate;
        try
        {
            fullCandidate = Normalize(candidate);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return GetUserDataRoots().Any(root => PathsEqual(root, fullCandidate));
    }

    public static IReadOnlyList<string> GetUserDataRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddLegacyRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AddLegacyRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var tempRoot = Path.GetTempPath();
        AddRoot(roots, tempRoot);
        AddLegacyRoot(roots, tempRoot);
        AddTempRoot(roots, tempRoot, MaintenanceLogsDirectoryName);
        AddTempRoot(roots, tempRoot, LegacyMaintenanceLogsDirectoryName);
        AddGuidSuffixedTempRoots(roots, tempRoot, InstallerTempDirectoryPrefix);
        AddGuidSuffixedTempRoots(roots, tempRoot, LegacyInstallerTempDirectoryPrefix);
        AddGuidSuffixedTempRoots(roots, tempRoot, MaintenanceStageDirectoryPrefix);
        AddGuidSuffixedTempRoots(roots, tempRoot, LegacyMaintenanceStageDirectoryPrefix);
        return roots.ToArray();
    }

    public static string Normalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool IsSameOrUnder(string childPath, string parentPath)
    {
        var child = Normalize(childPath);
        var parent = Normalize(parentPath);
        var parentPrefix = Path.EndsInDirectorySeparator(parent)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return string.Equals(child, parent, StringComparison.OrdinalIgnoreCase) ||
               child.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ContainsReparsePoint(string path)
    {
        var fullPath = Normalize(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return true;
        }

        var relative = fullPath[root.Length..]
            .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!TryGetAttributes(current, out var attributes))
            {
                break;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the attributes describe an ordinary file: not a directory and not a reparse
    /// point. Deleting anything that fails this test risks following a junction or symlink out
    /// of the managed install tree.
    /// </summary>
    public static bool IsPlainFile(FileAttributes attributes) =>
        (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;

    /// <summary>True when the attributes describe a real directory rather than a reparse point.</summary>
    public static bool IsPlainDirectory(FileAttributes attributes) =>
        (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;

    public static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                IOException or
                NotSupportedException or
                UnauthorizedAccessException)
        {
        }

        attributes = default;
        return false;
    }

    public static bool PathsEqual(string left, string right)
    {
        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSafeInstallRoot(string path)
    {
        string fullPath;
        try
        {
            fullPath = Normalize(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!Directory.Exists(fullPath) || ContainsReparsePoint(fullPath))
        {
            return false;
        }

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddBlocked(blocked, Path.GetPathRoot(fullPath));
        AddBlocked(blocked, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        AddBlocked(blocked, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AddBlocked(blocked, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddBlocked(blocked, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        AddBlocked(blocked, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddBlocked(blocked, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddBlocked(blocked, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        AddBlocked(blocked, Path.GetTempPath());
        return !blocked.Contains(fullPath);
    }

    private static bool IsSafeWindowsSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) ||
            segment is "." or ".." ||
            segment.EndsWith(' ') ||
            segment.EndsWith('.') ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            segment.Contains(':'))
        {
            return false;
        }

        var stem = segment.Split('.')[0].TrimEnd(' ');
        return !ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase) &&
               !LegacyReservedDeviceName().IsMatch(stem);
    }

    private static void AddRoot(HashSet<string> roots, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            roots.Add(Normalize(Path.Combine(baseDirectory, ProductDirectoryName)));
        }
    }

    private static void AddLegacyRoot(HashSet<string> roots, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            roots.Add(Normalize(Path.Combine(baseDirectory, LegacyProductDirectoryName)));
        }
    }

    private static void AddTempRoot(HashSet<string> roots, string tempRoot, string directoryName)
    {
        if (string.IsNullOrWhiteSpace(tempRoot))
        {
            return;
        }

        roots.Add(Normalize(Path.Combine(tempRoot, directoryName)));
    }

    private static void AddGuidSuffixedTempRoots(
        HashSet<string> roots,
        string tempRoot,
        string directoryPrefix)
    {
        if (string.IsNullOrWhiteSpace(tempRoot))
        {
            return;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(tempRoot, directoryPrefix + "*"))
            {
                var name = Path.GetFileName(Normalize(directory));
                if (name.Length <= directoryPrefix.Length)
                {
                    continue;
                }

                var suffix = name[directoryPrefix.Length..];
                if (Guid.TryParseExact(suffix, "N", out _))
                {
                    roots.Add(Normalize(directory));
                }
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                IOException or
                NotSupportedException or
                UnauthorizedAccessException)
        {
        }
    }

    private static void AddBlocked(HashSet<string> blocked, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            blocked.Add(Normalize(value));
        }
    }

    [GeneratedRegex("^(?:COM|LPT)[¹²³]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyReservedDeviceName();
}

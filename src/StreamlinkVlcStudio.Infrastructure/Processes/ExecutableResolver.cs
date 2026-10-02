using StreamStudio.Installation;

namespace StreamlinkVlcStudio.Infrastructure.Processes;

public static class ExecutableResolver
{
    public static string? FindStreamlink()
    {
        return FirstExisting(
            Environment.GetEnvironmentVariable("STREAMLINK_PATH"),
            FindOnPath("streamlink.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Streamlink", "bin", "streamlink.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Streamlink", "bin", "streamlink.exe"));
    }

    public static string? FindVlcDirectory()
    {
        return FindVlcDirectory(Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH"),
            () => OperatingSystem.IsWindows() ? WindowsDependencyProbe.FindMachineVlcDirectory() : null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC"));
    }

    internal static string? FindVlcDirectory(string? pluginPath, Func<string?> machineDirectory, params string?[] fallbackDirectories)
    {
        foreach (var entry in (pluginPath ?? "").Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fromEnv = NormalizeCandidatePath(entry);
            if (string.IsNullOrWhiteSpace(fromEnv))
            {
                continue;
            }

            if (HasLibVlc(fromEnv))
            {
                return fromEnv;
            }

            var parent = Directory.GetParent(fromEnv)?.FullName;
            if (HasLibVlc(parent))
            {
                return parent;
            }
        }

        // Setup and runtime verification also use VLC's machine registration.
        // Retain explicit environment selection before searching shared installs.
        return FirstDirectoryWithLibVlc([machineDirectory(), .. fallbackDirectories]);
    }

    private static string? FirstExisting(params string?[] candidates)
    {
        return candidates
            .Select(NormalizeCandidatePath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private static string? FirstDirectoryWithLibVlc(params string?[] candidates)
    {
        return candidates.FirstOrDefault(HasLibVlc);
    }

    private static bool HasLibVlc(string? directory)
    {
        return !string.IsNullOrWhiteSpace(directory) && File.Exists(Path.Combine(directory, "libvlc.dll"));
    }

    private static string? FindOnPath(string fileName) =>
        FindOnPath(fileName, Environment.GetEnvironmentVariable("PATH"));

    internal static string? FindOnPath(string fileName, string? pathValue)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            fileName.Any(char.IsControl))
        {
            return null;
        }

        foreach (var directory in (pathValue ?? "").Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalizedDirectory = NormalizeCandidatePath(directory);
            if (string.IsNullOrWhiteSpace(normalizedDirectory))
            {
                continue;
            }

            var candidate = Path.Combine(normalizedDirectory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? NormalizeCandidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var candidate = path.Trim();
        var startsWithQuote = candidate.StartsWith('"');
        var endsWithQuote = candidate.EndsWith('"');
        if (startsWithQuote != endsWithQuote || (startsWithQuote && candidate.Length < 2))
        {
            return null;
        }

        if (startsWithQuote)
        {
            candidate = candidate[1..^1].Trim();
        }

        if (candidate.Length == 0 ||
            candidate.Contains('"') ||
            candidate.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(candidate))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

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
        var pluginPath = Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH") ?? "";
        foreach (var entry in pluginPath.Split(
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

        return FirstDirectoryWithLibVlc(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC"));
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

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
        return string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('"');
    }
}

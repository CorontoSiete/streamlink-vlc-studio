using Microsoft.Win32;

namespace StreamlinkVlcStudio.Maintenance;

/// <summary>
/// Shared bounded retry for deletes that can transiently fail while another process still
/// holds the path open.
/// </summary>
internal static class DeleteRetry
{
    private const int Attempts = 5;

    /// <summary>
    /// Runs <paramref name="delete"/> until the path is gone or the attempts are exhausted.
    /// <paramref name="prepare"/> can reject the path (returning false aborts without deleting)
    /// and runs again before every attempt, so a path that turns into a directory or reparse
    /// point mid-retry is still refused.
    /// </summary>
    internal static bool Run(
        string path,
        Func<bool> prepare,
        Action delete,
        MaintenanceLog log,
        string removedMessage,
        string exhaustedMessage)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if (!PathSafety.TryGetAttributes(path, out _))
            {
                return true;
            }

            if (!prepare())
            {
                return false;
            }

            try
            {
                delete();
                if (!PathSafety.TryGetAttributes(path, out _))
                {
                    log.Write($"{removedMessage}: {path}");
                    return true;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == Attempts)
                {
                    log.Write($"{exhaustedMessage} after {Attempts} attempts: {path}. {exception.Message}");
                    return false;
                }
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(100 * attempt));
        }

        return !PathSafety.TryGetAttributes(path, out _);
    }
}

internal sealed record CleanupOutcome(
    bool ApplicationRemoved,
    IReadOnlyList<string> RetainedPaths,
    bool RegistrationRemoved);

internal static class ManagedInstallationCleaner
{
    private const string ApplicationFileName = "StreamlinkVlcStudio.exe";
    private const string UninstallerFileName = "Uninstall.exe";
    private const string UninstallRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\StreamlinkVlcStudio";

    internal static CleanupOutcome Clean(InstallOwnership ownership, MaintenanceLog log)
    {
        var retained = new List<string>();
        var controlPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ApplicationFileName,
            UninstallerFileName
        };
        var ordinaryFiles = ownership.Files
            .Where(file => !controlPaths.Contains(NormalizeRelative(file.RelativePath)))
            .OrderByDescending(file => file.RelativePath.Length)
            .ToArray();

        foreach (var file in ordinaryFiles)
        {
            var target = ownership.GetManagedPath(file);
            if (!DeletePlainFileWithRetries(target, ownership.Root, log))
            {
                retained.Add(target);
            }
        }

        var finalPaths = ownership.Files
            .Where(file => controlPaths.Contains(NormalizeRelative(file.RelativePath)))
            .Select(ownership.GetManagedPath)
            .Concat([ownership.ManifestPath, ownership.OwnerPath])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (retained.Count == 0)
        {
            if (!DeleteFinalControlFiles(finalPaths, ownership.Root, log))
            {
                retained.AddRange(finalPaths.Where(FileOrDirectoryExists));
            }
        }
        else
        {
            retained.AddRange(finalPaths.Where(FileOrDirectoryExists));
            log.Write("Managed files remain; control files and uninstall registration were preserved for retry.");
        }

        var applicationRemoved = retained.Count == 0;
        var registrationRemoved = false;
        if (applicationRemoved)
        {
            RemoveEmptyManagedDirectories(ownership, log);
            RemoveKnownShortcut(log);
            registrationRemoved = RemoveRegistrationIfMatching(ownership.Root, log);
        }

        return new CleanupOutcome(
            applicationRemoved,
            retained.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            registrationRemoved);
    }

    private static bool DeleteFinalControlFiles(
        IReadOnlyList<string> paths,
        string root,
        MaintenanceLog log)
    {
        try
        {
            foreach (var path in paths)
            {
                if (!PathSafety.TryGetAttributes(path, out var attributes))
                {
                    continue;
                }

                if (!PathSafety.IsPlainFile(attributes) ||
                    HasReparseBetweenRootAndTarget(root, path))
                {
                    log.Write($"Refusing final control path because it is not a plain file: {path}");
                    return false;
                }

                ClearReadOnly(path, attributes);
                using var handle = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            }

            foreach (var path in paths)
            {
                if (FileOrDirectoryExists(path) && !DeletePlainFileWithRetries(path, root, log))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.Write($"Final control-file preflight failed; retry ownership was preserved. {exception.Message}");
            return false;
        }
    }

    internal static bool DeletePlainFileWithRetries(string path, string root, MaintenanceLog log)
    {
        return DeleteRetry.Run(
            path,
            () =>
            {
                if (!PathSafety.TryGetAttributes(path, out var attributes))
                {
                    return true;
                }

                if (!PathSafety.IsPlainFile(attributes) || HasReparseBetweenRootAndTarget(root, path))
                {
                    log.Write($"Refusing to remove a managed directory or reparse point: {path}");
                    return false;
                }

                ClearReadOnly(path, attributes);
                return true;
            },
            () => File.Delete(path),
            log,
            "Removed managed file",
            "Managed file remained");
    }

    private static bool HasReparseBetweenRootAndTarget(string root, string target)
    {
        if (!PathSafety.IsSameOrUnder(target, root))
        {
            return true;
        }

        var relative = Path.GetRelativePath(root, target);
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (PathSafety.TryGetAttributes(current, out var attributes) &&
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void RemoveEmptyManagedDirectories(InstallOwnership ownership, MaintenanceLog log)
    {
        var directories = ownership.Files
            .Select(file => Path.GetDirectoryName(ownership.GetManagedPath(file)))
            .Where(path => !string.IsNullOrWhiteSpace(path) && !PathSafety.PathsEqual(path!, ownership.Root))
            .Select(path => PathSafety.Normalize(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => path.Length)
            .ToArray();

        foreach (var directory in directories)
        {
            try
            {
                if (PathSafety.TryGetAttributes(directory, out var attributes) &&
                    (attributes & FileAttributes.ReparsePoint) == 0)
                {
                    Directory.Delete(directory, recursive: false);
                    log.Write($"Removed empty managed directory: {directory}");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                log.Write($"Preserved nonempty or locked directory: {directory}. {exception.Message}");
            }
        }

        try
        {
            Directory.Delete(ownership.Root, recursive: false);
            log.Write($"Removed empty installation directory: {ownership.Root}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.Write($"Installation directory contains unknown files and was preserved: {ownership.Root}. {exception.Message}");
        }
    }

    private static void RemoveKnownShortcut(MaintenanceLog log)
    {
        var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        if (string.IsNullOrWhiteSpace(startMenu))
        {
            return;
        }

        var shortcut = Path.Combine(startMenu, "Programs", "Streamlink VLC Studio.lnk");
        if (PathSafety.TryGetAttributes(shortcut, out var attributes) &&
            PathSafety.IsPlainFile(attributes))
        {
            try
            {
                ClearReadOnly(shortcut, attributes);
                File.Delete(shortcut);
                log.Write($"Removed Start Menu shortcut: {shortcut}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                log.Write($"Could not remove Start Menu shortcut: {shortcut}. {exception.Message}");
            }
        }
    }

    private static bool RemoveRegistrationIfMatching(string installRoot, MaintenanceLog log)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryPath, writable: false);
            if (key is null)
            {
                return true;
            }

            var registeredLocation = key.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(registeredLocation) ||
                !PathSafety.PathsEqual(registeredLocation, installRoot))
            {
                log.Write("Uninstall registration points to a different location and was preserved.");
                return false;
            }

            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryPath, throwOnMissingSubKey: false);
            log.Write("Removed current-user uninstall registration.");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.Write($"Could not remove current-user uninstall registration: {exception.Message}");
            return false;
        }
    }

    private static void ClearReadOnly(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static string NormalizeRelative(string relativePath)
    {
        return relativePath.Replace('\\', '/');
    }

    private static bool FileOrDirectoryExists(string path)
    {
        return PathSafety.TryGetAttributes(path, out _);
    }
}

internal static class UserDataCleaner
{
    internal static IReadOnlyList<string> PurgeCurrentUserData(MaintenanceLog log)
    {
        var retained = new List<string>();
        foreach (var root in PathSafety.GetUserDataRoots())
        {
            if (!PathSafety.IsExactUserDataRoot(root))
            {
                retained.Add(root);
                log.Write($"Rejected noncanonical personal-data root: {root}");
                continue;
            }

            DeleteTreeWithoutFollowingReparsePoints(root, retained, log);
        }

        return retained.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void DeleteTreeWithoutFollowingReparsePoints(
        string root,
        List<string> retained,
        MaintenanceLog log)
    {
        if (!PathSafety.TryGetAttributes(root, out var rootAttributes))
        {
            return;
        }

        if (!PathSafety.IsPlainDirectory(rootAttributes) ||
            PathSafety.ContainsReparsePoint(root))
        {
            retained.Add(root);
            log.Write($"Personal-data root is not a plain directory and was preserved: {root}");
            return;
        }

        var pending = new Stack<(string Path, bool Expanded)>();
        pending.Push((root, false));
        while (pending.Count > 0)
        {
            var item = pending.Pop();
            if (item.Expanded)
            {
                if (!DeleteDirectoryWithRetries(item.Path, log))
                {
                    retained.Add(item.Path);
                }

                continue;
            }

            pending.Push((item.Path, true));
            string[] children;
            try
            {
                children = Directory.GetFileSystemEntries(item.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                retained.Add(item.Path);
                log.Write($"Could not enumerate personal-data directory: {item.Path}. {exception.Message}");
                continue;
            }

            foreach (var child in children)
            {
                if (!PathSafety.IsSameOrUnder(child, root) ||
                    !PathSafety.TryGetAttributes(child, out var attributes))
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (!DeleteReparsePointWithRetries(child, attributes, log))
                    {
                        retained.Add(child);
                    }
                }
                else if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push((child, false));
                }
                else if (!DeletePersonalFileWithRetries(child, log))
                {
                    retained.Add(child);
                }
            }
        }
    }

    private static bool DeletePersonalFileWithRetries(string path, MaintenanceLog log)
    {
        return RetryDelete(
            path,
            () =>
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }

                File.Delete(path);
            },
            log);
    }

    private static bool DeleteDirectoryWithRetries(string path, MaintenanceLog log)
    {
        return RetryDelete(path, () => Directory.Delete(path, recursive: false), log);
    }

    private static bool DeleteReparsePointWithRetries(
        string path,
        FileAttributes attributes,
        MaintenanceLog log)
    {
        return RetryDelete(
            path,
            () =>
            {
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(path, recursive: false);
                }
                else
                {
                    File.Delete(path);
                }
            },
            log);
    }

    private static bool RetryDelete(string path, Action delete, MaintenanceLog log)
    {
        return DeleteRetry.Run(
            path,
            () => true,
            delete,
            log,
            "Removed personal-data path",
            "Personal-data path remained");
    }
}

using StreamlinkVlcStudio.Maintenance;

internal static class MaintenanceTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Maintenance paths reject traversal and constrain personal-data roots", PathPoliciesAreStrict),
        ("ZIP cleanup removes modified managed files and preserves unknown files", ModifiedManagedFilesAreRemoved),
        ("ZIP cleanup preserves retry ownership while a managed file is locked", LockedManagedFilesPreserveRetryState),
        ("ZIP cleanup rejects corrupt ownership state before deletion", CorruptOwnershipStateIsRejected)
    ];

    private static Task PathPoliciesAreStrict()
    {
        Assert.True(PathSafety.IsSafeManifestRelativePath("vlc-overlay/build/plugin.dll"));
        Assert.True(PathSafety.IsSafeManifestRelativePath(@"vlc-overlay\build\plugin.dll"));
        foreach (var unsafePath in new[]
                 {
                     "", "../outside.dll", @"folder\..\outside.dll", @"C:\outside.dll",
                     "folder//file.dll", "folder/./file.dll", "folder/file.dll:stream", "CON.txt"
                 })
        {
            Assert.Equal(false, PathSafety.IsSafeManifestRelativePath(unsafePath));
        }

        foreach (var root in PathSafety.GetUserDataRoots())
        {
            Assert.True(PathSafety.IsExactUserDataRoot(root));
            Assert.Equal(false, PathSafety.IsExactUserDataRoot(Path.Combine(root, "child")));
            Assert.Equal(false, PathSafety.IsExactUserDataRoot(Path.GetDirectoryName(root)));
        }

        return Task.CompletedTask;
    }

    private static Task ModifiedManagedFilesAreRemoved()
    {
        var testRoot = CreateRoot();
        try
        {
            var installRoot = Path.Combine(testRoot, "install");
            Directory.CreateDirectory(installRoot);
            var managedPath = Path.Combine(installRoot, "managed.bin");
            File.WriteAllText(managedPath, "original");
            File.WriteAllText(Path.Combine(installRoot, "StreamlinkVlcStudio.exe"), "app");
            File.WriteAllText(Path.Combine(installRoot, "Uninstall.exe"), "maintenance");
            File.WriteAllText(Path.Combine(installRoot, "unknown.txt"), "preserve me");
            WriteOwnership(installRoot, ["managed.bin", "StreamlinkVlcStudio.exe", "Uninstall.exe"]);

            File.WriteAllText(managedPath, "modified after ownership was recorded");
            var ownership = InstallOwnership.Load(installRoot);
            using var log = MaintenanceLog.Create();
            var result = ManagedInstallationCleaner.Clean(ownership, log);

            Assert.True(result.ApplicationRemoved);
            Assert.Equal(0, result.RetainedPaths.Count);
            Assert.Equal(false, File.Exists(managedPath));
            Assert.Equal(false, File.Exists(Path.Combine(installRoot, "StreamlinkVlcStudio.exe")));
            Assert.Equal(false, File.Exists(Path.Combine(installRoot, "Uninstall.exe")));
            Assert.Equal(false, File.Exists(Path.Combine(installRoot, InstallOwnership.OwnerFileName)));
            Assert.Equal(false, File.Exists(Path.Combine(installRoot, InstallOwnership.ManifestFileName)));
            Assert.Equal("preserve me", File.ReadAllText(Path.Combine(installRoot, "unknown.txt")));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static Task LockedManagedFilesPreserveRetryState()
    {
        var testRoot = CreateRoot();
        try
        {
            var installRoot = Path.Combine(testRoot, "install");
            Directory.CreateDirectory(installRoot);
            var lockedPath = Path.Combine(installRoot, "locked.bin");
            File.WriteAllText(lockedPath, "locked");
            File.WriteAllText(Path.Combine(installRoot, "StreamlinkVlcStudio.exe"), "app");
            File.WriteAllText(Path.Combine(installRoot, "Uninstall.exe"), "maintenance");
            WriteOwnership(installRoot, ["locked.bin", "StreamlinkVlcStudio.exe", "Uninstall.exe"]);

            var ownership = InstallOwnership.Load(installRoot);
            CleanupOutcome firstResult;
            using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var log = MaintenanceLog.Create())
            {
                firstResult = ManagedInstallationCleaner.Clean(ownership, log);
            }

            Assert.Equal(false, firstResult.ApplicationRemoved);
            Assert.True(firstResult.RetainedPaths.Contains(lockedPath, StringComparer.OrdinalIgnoreCase));
            Assert.True(File.Exists(Path.Combine(installRoot, "StreamlinkVlcStudio.exe")));
            Assert.True(File.Exists(Path.Combine(installRoot, "Uninstall.exe")));
            Assert.True(File.Exists(Path.Combine(installRoot, InstallOwnership.OwnerFileName)));
            Assert.True(File.Exists(Path.Combine(installRoot, InstallOwnership.ManifestFileName)));

            using var retryLog = MaintenanceLog.Create();
            var retryResult = ManagedInstallationCleaner.Clean(InstallOwnership.Load(installRoot), retryLog);
            Assert.True(retryResult.ApplicationRemoved);
            Assert.Equal(false, Directory.Exists(installRoot));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }

        return Task.CompletedTask;
    }

    private static Task CorruptOwnershipStateIsRejected()
    {
        var testRoot = CreateRoot();
        try
        {
            var installRoot = Path.Combine(testRoot, "install");
            Directory.CreateDirectory(installRoot);
            var managedPath = Path.Combine(installRoot, "managed.bin");
            File.WriteAllText(managedPath, "managed");
            WriteOwnership(installRoot, ["managed.bin"]);
            File.AppendAllText(Path.Combine(installRoot, InstallOwnership.ManifestFileName), " ");

            Assert.Throws<InvalidDataException>(() => InstallOwnership.Load(installRoot));
            Assert.Equal("managed", File.ReadAllText(managedPath));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "StreamlinkVlcStudio-Maintenance-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteOwnership(string root, IReadOnlyList<string> relativePaths)
    {
        const string installId = "maintenance-test-install";
        var entries = relativePaths.Select(relativePath =>
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, relativePath));
            return new
            {
                path = relativePath.Replace('\\', '/'),
                length = bytes.LongLength,
                sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            };
        }).ToArray();
        var manifest = new
        {
            schemaVersion = 1,
            product = "streamlink-vlc-studio",
            installId,
            generatedUtc = DateTime.UtcNow.ToString("O"),
            files = entries
        };
        var manifestPath = Path.Combine(root, InstallOwnership.ManifestFileName);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        var manifestHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath))).ToLowerInvariant();
        var owner = new
        {
            schemaVersion = 1,
            product = "streamlink-vlc-studio",
            installId,
            createdUtc = DateTime.UtcNow.ToString("O"),
            manifest = InstallOwnership.ManifestFileName,
            manifestSha256 = manifestHash
        };
        File.WriteAllText(
            Path.Combine(root, InstallOwnership.OwnerFileName),
            JsonSerializer.Serialize(owner));
    }
}

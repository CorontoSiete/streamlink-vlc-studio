using StreamlinkVlcStudio.Maintenance;

internal static class MaintenanceTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Maintenance paths reject traversal and constrain personal-data roots", PathPoliciesAreStrict),
        ("Maintenance uninstall purges current-user leftovers by default", CommandLinePurgesUserDataByDefault),
        ("Personal-data cleanup removes only selected product roots", PersonalDataCleanupRemovesSelectedRoots),
        ("Personal-data cleanup tolerates the active maintenance log", PersonalDataCleanupToleratesActiveLog),
        ("ZIP cleanup removes modified managed files and preserves unknown files", ModifiedManagedFilesAreRemoved),
        ("ZIP cleanup preserves retry ownership while a managed file is locked", LockedManagedFilesPreserveRetryState),
        ("ZIP cleanup rejects corrupt ownership state before deletion", CorruptOwnershipStateIsRejected),
        ("ZIP cleanup rejects duplicate paths with different directory separators", DuplicatePathAliasesAreRejected)
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

    private static Task CommandLinePurgesUserDataByDefault()
    {
        var defaults = CommandLineOptions.Parse([]);
        Assert.True(defaults.PurgeUserData);

        var preserved = CommandLineOptions.Parse(["--preserve-user-data"]);
        Assert.Equal(false, preserved.PurgeUserData);

        var explicitPurge = CommandLineOptions.Parse(["--preserve-user-data", "/purge-user-data"]);
        Assert.True(explicitPurge.PurgeUserData);

        return Task.CompletedTask;
    }

    private static Task PersonalDataCleanupRemovesSelectedRoots()
    {
        var testRoot = CreateRoot();
        try
        {
            var dataRoot = Path.Combine(testRoot, "StreamStudio");
            var nested = Path.Combine(dataRoot, "Updates", "operation");
            var legacyDataRoot = Path.Combine(testRoot, "StreamlinkVlcStudio");
            var legacyNested = Path.Combine(legacyDataRoot, "Updates", "operation");
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(legacyNested);
            File.WriteAllText(Path.Combine(dataRoot, "settings.json"), "{}");
            File.WriteAllText(Path.Combine(nested, "setup.exe"), "cached setup");
            File.WriteAllText(Path.Combine(legacyDataRoot, "settings.json"), "{}");
            File.WriteAllText(Path.Combine(legacyNested, "setup.exe"), "cached setup");
            File.WriteAllText(Path.Combine(testRoot, "outside.txt"), "not selected");

            using var log = MaintenanceLog.Create();
            var retained = UserDataCleaner.PurgeRootsForTest([dataRoot, legacyDataRoot], log);

            Assert.Equal(0, retained.Count);
            Assert.Equal(false, Directory.Exists(dataRoot));
            Assert.Equal(false, Directory.Exists(legacyDataRoot));
            Assert.Equal("not selected", File.ReadAllText(Path.Combine(testRoot, "outside.txt")));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static Task PersonalDataCleanupToleratesActiveLog()
    {
        var testRoot = CreateRoot();
        try
        {
            var logRoot = Path.Combine(testRoot, "StreamStudio-Maintenance-Logs");
            Directory.CreateDirectory(logRoot);
            var activeLog = Path.Combine(logRoot, "uninstall-active.log");
            var oldLog = Path.Combine(logRoot, "uninstall-old.log");
            File.WriteAllText(activeLog, "active");
            File.WriteAllText(oldLog, "old");

            using var active = new FileStream(activeLog, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var log = MaintenanceLog.Create();
            var retained = UserDataCleaner.PurgeRootsForTest([logRoot], log, [activeLog]);

            Assert.Equal(0, retained.Count);
            Assert.Equal(false, File.Exists(oldLog));
            Assert.True(File.Exists(activeLog));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
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
            File.WriteAllText(Path.Combine(installRoot, "StreamStudio.exe"), "app");
            File.WriteAllText(Path.Combine(installRoot, "Uninstall.exe"), "maintenance");
            File.WriteAllText(Path.Combine(installRoot, "unknown.txt"), "preserve me");
            WriteOwnership(installRoot, ["managed.bin", "StreamStudio.exe", "Uninstall.exe"]);

            File.WriteAllText(managedPath, "modified after ownership was recorded");
            var ownership = InstallOwnership.Load(installRoot);
            using var log = MaintenanceLog.Create();
            var result = ManagedInstallationCleaner.Clean(ownership, log);

            Assert.True(result.ApplicationRemoved);
            Assert.Equal(0, result.RetainedPaths.Count);
            Assert.Equal(false, File.Exists(managedPath));
            Assert.Equal(false, File.Exists(Path.Combine(installRoot, "StreamStudio.exe")));
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
            File.WriteAllText(Path.Combine(installRoot, "StreamStudio.exe"), "app");
            File.WriteAllText(Path.Combine(installRoot, "Uninstall.exe"), "maintenance");
            WriteOwnership(installRoot, ["locked.bin", "StreamStudio.exe", "Uninstall.exe"]);

            var ownership = InstallOwnership.Load(installRoot);
            CleanupOutcome firstResult;
            using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var log = MaintenanceLog.Create())
            {
                firstResult = ManagedInstallationCleaner.Clean(ownership, log);
            }

            Assert.Equal(false, firstResult.ApplicationRemoved);
            Assert.True(firstResult.RetainedPaths.Contains(lockedPath, StringComparer.OrdinalIgnoreCase));
            Assert.True(File.Exists(Path.Combine(installRoot, "StreamStudio.exe")));
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

    private static Task DuplicatePathAliasesAreRejected()
    {
        var testRoot = CreateRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(testRoot, "lib"));
            var path = Path.Combine(testRoot, "lib", "managed.bin");
            File.WriteAllText(path, "preserve me");
            WriteOwnership(testRoot, ["lib/managed.bin", @"lib\managed.bin"], normalizePaths: false);

            Assert.Throws<InvalidDataException>(() => InstallOwnership.Load(testRoot));
            Assert.Equal("preserve me", File.ReadAllText(path));

            WriteOwnership(testRoot, [@"lib\managed.bin"], normalizePaths: false);
            Assert.Equal("lib/managed.bin", InstallOwnership.Load(testRoot).Files.Single().RelativePath);
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
            "StreamStudio-Maintenance-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteOwnership(string root, IReadOnlyList<string> relativePaths, bool normalizePaths = true)
    {
        const string installId = "maintenance-test-install";
        var entries = relativePaths.Select(relativePath =>
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, relativePath));
            return new
            {
                path = normalizePaths ? relativePath.Replace('\\', '/') : relativePath,
                length = bytes.LongLength,
                sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            };
        }).ToArray();
        var manifest = new
        {
            schemaVersion = 1,
            product = "stream-studio",
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
            product = "stream-studio",
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

using StreamlinkVlcStudio.Maintenance;

internal static partial class MaintenanceTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Maintenance paths reject traversal and constrain personal-data roots", PathPoliciesAreStrict),
        ("Maintenance uninstall purges current-user leftovers by default", CommandLinePurgesUserDataByDefault),
        ("Maintenance staged uninstall preserves the selected personal-data policy", StagedArgumentsPreserveDataPolicy),
        ("Maintenance temporary stage cleans itself after exit and preserves unknown files", StageCleanupAfterExitAsync),
        ("Maintenance temporary cleanup rejects paths outside its stage", StageCleanupRejectsUnownedPaths),
        ("Personal-data cleanup removes only selected product roots", PersonalDataCleanupRemovesSelectedRoots),
        ("Personal-data cleanup tolerates the active maintenance log", PersonalDataCleanupToleratesActiveLog),
        ("ZIP cleanup removes modified managed files and preserves unknown files", ModifiedManagedFilesAreRemoved),
        ("ZIP cleanup removes empty managed ancestors and preserves unrelated directories", EmptyManagedAncestorsAreRemoved),
        ("ZIP cleanup preserves retry ownership while a managed file is locked", LockedManagedFilesPreserveRetryState),
        ("ZIP cleanup preserves all retry files when a control file denies deletion", LockedControlFilesPreserveRetryState),
        ("ZIP cleanup cancels pending control-file deletion when a later file is mapped", MappedControlFilePreservesRetryState),
        ("ZIP cleanup preserves personal data until application removal succeeds", FailedUninstallPreservesPersonalData),
        ("Maintenance cleanup retries attribute preparation failures", CleanupPreparationFailuresAreRetried),
        ("Maintenance quiet argument failures never open a dialog", QuietArgumentFailures),
        ("Maintenance shutdown waits for startup and releases abandoned instance ownership", ShutdownWaitsForInstanceExit),
        ("Maintenance failed shutdown preserves every app file and personal data", FailedShutdownPreservesInstallation),
        ("Maintenance competing installer prevents uninstall before any changes", CompetingInstallerPreventsUninstall),
        ("Maintenance installer lease normalizes paths and recovers abandoned ownership", InstallerLeaseIdentityAndRecovery),
        ("Maintenance damaged executable cleanup failures are reported without crashing", DamagedExecutableMaintenanceFailure),
        ("Maintenance shortcut cleanup reports locks and continues with other shortcuts", ShortcutCleanupReportsFailures),
        ("Personal-data cleanup removes read-only directories and continues past locked files", ReadOnlyDirectoryCleanup),
        ("ZIP cleanup rejects corrupt ownership state before deletion", CorruptOwnershipStateIsRejected),
        ("ZIP cleanup treats malformed JSON types as invalid state and preserves retry files", MalformedOwnershipTypesAreRejected),
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

    private static Task StagedArgumentsPreserveDataPolicy()
    {
        foreach (var purge in new[] { true, false })
            foreach (var quiet in new[] { true, false })
            {
                var info = StageLauncher.CreateStartInfo(@"C:\stage\StreamStudio.Maintenance.exe",
                    @"C:\Apps\Stream Studio", purge, quiet, Guid.NewGuid().ToString("N"), @"C:\logs\uninstall.log");
                var parsed = CommandLineOptions.Parse(info.ArgumentList.ToArray());
                Assert.True(parsed.Staged);
                Assert.Equal(purge, parsed.PurgeUserData);
                Assert.Equal(quiet, parsed.Quiet);
                Assert.Equal(@"C:\Apps\Stream Studio", parsed.InstallDirectory);
            }
        return Task.CompletedTask;
    }

    private static Task StageCleanupRejectsUnownedPaths()
    {
        var root = CreateRoot();
        try
        {
            var fakeNestedStage = Path.Combine(root, "StreamStudio-Maintenance-Stage-" + Guid.NewGuid().ToString("N"));
            foreach (var candidate in new[] { root, Path.GetTempPath(), fakeNestedStage })
                Assert.Throws<InvalidDataException>(() => StageCleanup.CreateStartInfo(candidate, Environment.ProcessId));
        }
        finally { Directory.Delete(root); }
        return Task.CompletedTask;
    }

    private static async Task StageCleanupAfterExitAsync()
    {
        foreach (var unknownFile in new[] { false, true })
        {
            var stage = Path.Combine(Path.GetTempPath(), "StreamStudio-Maintenance-Stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                var executable = Path.Combine(stage, "StreamStudio.Maintenance.exe");
                File.WriteAllText(executable, "temporary helper");
                File.WriteAllText(Path.Combine(stage, ".stage-token"), "token");
                if (unknownFile) File.WriteAllText(Path.Combine(stage, "keep.txt"), "user file");
                var parentInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true
                };
                foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
                             "[Console]::WriteLine('ready'); [Console]::ReadLine() | Out-Null" })
                    parentInfo.ArgumentList.Add(argument);
                using var parent = Process.Start(parentInfo)!;
                Process? cleaner = null;
                try
                {
                    Assert.Equal("ready", await parent.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                    cleaner = Process.Start(StageCleanup.CreateStartInfo(stage, parent.Id))!;
                    await Task.Delay(600);
                    Assert.True(File.Exists(executable));
                    await parent.StandardInput.WriteLineAsync("exit");
                    await parent.StandardInput.FlushAsync();
                    await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    await cleaner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(unknownFile ? 1 : 0, cleaner.ExitCode);
                    Assert.Equal(false, File.Exists(executable));
                    Assert.Equal(false, File.Exists(Path.Combine(stage, ".stage-token")));
                    Assert.Equal(unknownFile, Directory.Exists(stage));
                    if (unknownFile) Assert.Equal("user file", File.ReadAllText(Path.Combine(stage, "keep.txt")));
                }
                finally
                {
                    if (!parent.HasExited) { parent.Kill(); await parent.WaitForExitAsync(); }
                    if (cleaner is not null)
                    {
                        if (!cleaner.HasExited) { cleaner.Kill(); await cleaner.WaitForExitAsync(); }
                        cleaner.Dispose();
                    }
                }
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
        }
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

    private static Task EmptyManagedAncestorsAreRemoved()
    {
        foreach (var preserveUnknown in new[] { false, true })
        {
            var testRoot = CreateRoot();
            try
            {
                var installRoot = Path.Combine(testRoot, "install");
                var managedPath = Path.Combine(installRoot, "runtimes", "win-x64", "native", "plugin.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(managedPath)!);
                File.WriteAllText(managedPath, "managed");
                WriteOwnership(installRoot, ["runtimes/win-x64/native/plugin.dll"]);
                var ownership = InstallOwnership.Load(installRoot);
                var unknownDirectory = Path.Combine(installRoot, "runtimes", "user-folder");
                if (preserveUnknown) Directory.CreateDirectory(unknownDirectory);
                File.Delete(managedPath);
                File.Delete(ownership.ManifestPath);
                File.Delete(ownership.OwnerPath);

                using var log = MaintenanceLog.Create();
                ManagedInstallationCleaner.RemoveEmptyManagedDirectories(ownership, log);

                Assert.Equal(false, Directory.Exists(Path.Combine(installRoot, "runtimes", "win-x64")));
                Assert.Equal(preserveUnknown, Directory.Exists(installRoot));
                Assert.Equal(preserveUnknown, Directory.Exists(unknownDirectory));
                Assert.True(Directory.Exists(testRoot));
            }
            finally { Directory.Delete(testRoot, recursive: true); }
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

    private static Task LockedControlFilesPreserveRetryState()
    {
        string[] controlFiles = ["StreamStudio.exe", "Uninstall.exe", InstallOwnership.ManifestFileName, InstallOwnership.OwnerFileName];
        foreach (var lockedName in controlFiles)
        {
            var testRoot = CreateRoot();
            try
            {
                var installRoot = Path.Combine(testRoot, "install");
                Directory.CreateDirectory(installRoot);
                File.WriteAllText(Path.Combine(installRoot, "StreamStudio.exe"), "app");
                File.WriteAllText(Path.Combine(installRoot, "Uninstall.exe"), "maintenance");
                WriteOwnership(installRoot, ["StreamStudio.exe", "Uninstall.exe"]);
                var ownership = InstallOwnership.Load(installRoot);
                var original = controlFiles.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(installRoot, name)));
                using var log = MaintenanceLog.Create();
                // Antivirus and indexing commonly allow readers while withholding delete sharing.
                using (var locked = new FileStream(Path.Combine(installRoot, lockedName), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var outcome = ManagedInstallationCleaner.Clean(ownership, log);
                    Assert.Equal(false, outcome.ApplicationRemoved);
                    foreach (var name in controlFiles)
                    {
                        var path = Path.Combine(installRoot, name);
                        Assert.True(File.Exists(path), $"Locking {lockedName} lost the retry file {name}.");
                        Assert.True(original[name].AsSpan().SequenceEqual(File.ReadAllBytes(path)));
                    }
                    _ = InstallOwnership.Load(installRoot);
                }

                Assert.True(ManagedInstallationCleaner.Clean(InstallOwnership.Load(installRoot), log).ApplicationRemoved);
                Assert.Equal(false, Directory.Exists(installRoot));
            }
            finally { Directory.Delete(testRoot, recursive: true); }
        }
        return Task.CompletedTask;
    }

    private static Task MappedControlFilePreservesRetryState()
    {
        var testRoot = CreateRoot();
        try
        {
            var installRoot = Path.Combine(testRoot, "install");
            Directory.CreateDirectory(installRoot);
            File.WriteAllText(Path.Combine(installRoot, "StreamStudio.exe"), "app");
            File.WriteAllText(Path.Combine(installRoot, "Uninstall.exe"), "maintenance");
            WriteOwnership(installRoot, ["StreamStudio.exe", "Uninstall.exe"]);
            var ownership = InstallOwnership.Load(installRoot);
            using var mapping = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(ownership.OwnerPath,
                FileMode.Open, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            using var view = mapping.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            // Keep only the mapped view: exclusive deletion access can be acquired,
            // but Windows still rejects marking the last control file for deletion.
            mapping.Dispose();
            using var log = MaintenanceLog.Create();
            Assert.Equal(false, ManagedInstallationCleaner.Clean(ownership, log).ApplicationRemoved);
            Assert.True(File.Exists(Path.Combine(installRoot, "StreamStudio.exe")));
            Assert.True(File.Exists(Path.Combine(installRoot, "Uninstall.exe")));
            _ = InstallOwnership.Load(installRoot);

            view.Dispose();
            Assert.True(ManagedInstallationCleaner.Clean(InstallOwnership.Load(installRoot), log).ApplicationRemoved);
            Assert.Equal(false, Directory.Exists(installRoot));
        }
        finally { Directory.Delete(testRoot, recursive: true); }
        return Task.CompletedTask;
    }

    private static Task FailedUninstallPreservesPersonalData()
    {
        var testRoot = CreateRoot();
        try
        {
            var installRoot = Path.Combine(testRoot, "install");
            var personalRoot = Path.Combine(testRoot, "personal");
            Directory.CreateDirectory(installRoot);
            Directory.CreateDirectory(personalRoot);
            var lockedPath = Path.Combine(installRoot, "locked.bin");
            File.WriteAllText(lockedPath, "locked");
            File.WriteAllText(Path.Combine(installRoot, "StreamStudio.exe"), "app");
            File.WriteAllText(Path.Combine(installRoot, "Uninstall.exe"), "maintenance");
            File.WriteAllText(Path.Combine(personalRoot, "settings.json"), "keep my settings");
            WriteOwnership(installRoot, ["locked.bin", "StreamStudio.exe", "Uninstall.exe"]);
            var calls = new List<string>();
            var purges = 0;
            var options = CommandLineOptions.Parse(["/quiet"]);
            using var log = MaintenanceLog.Create();
            bool RunMaintenance(string path, string argument, TimeSpan timeout, MaintenanceLog output)
            {
                calls.Add(argument);
                return true;
            }
            IReadOnlyList<string> Purge(MaintenanceLog output)
            {
                purges++;
                return UserDataCleaner.PurgeRootsForTest([personalRoot], output);
            }

            using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Equal(1, MaintenanceApplication.RemoveInstallation(
                    options, InstallOwnership.Load(installRoot), log, RunMaintenance, Purge));
                Assert.Equal(0, purges);
                Assert.Equal("keep my settings", File.ReadAllText(Path.Combine(personalRoot, "settings.json")));
                Assert.True(calls.Contains("--maintenance-register-notifications"));
                Assert.True(File.Exists(Path.Combine(installRoot, "Uninstall.exe")));
            }

            Assert.Equal(0, MaintenanceApplication.RemoveInstallation(
                options, InstallOwnership.Load(installRoot), log, RunMaintenance, Purge));
            Assert.Equal(1, purges);
            Assert.Equal(false, Directory.Exists(personalRoot));
            Assert.Equal(false, Directory.Exists(installRoot));
        }
        finally { Directory.Delete(testRoot, recursive: true); }
        return Task.CompletedTask;
    }

    private static Task CleanupPreparationFailuresAreRetried()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "locked-attributes.bin");
            File.WriteAllText(path, "cleanup fixture");
            using var log = MaintenanceLog.Create();
            var preparations = 0;
            Assert.True(DeleteRetry.Run(path, () =>
            {
                if (++preparations < 3) throw new UnauthorizedAccessException("Transient attribute lock.");
                return true;
            }, () => File.Delete(path), log, "Removed", "Retained"));
            Assert.Equal(3, preparations);
            Assert.Equal(false, File.Exists(path));

            File.WriteAllText(path, "preserve after exhausted retries");
            preparations = 0;
            Assert.Equal(false, DeleteRetry.Run(path, () =>
            {
                preparations++;
                throw new IOException("Attribute access remains unavailable.");
            }, () => throw new InvalidOperationException("Deletion must not follow failed validation."), log, "Removed", "Retained"));
            Assert.Equal(5, preparations);
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(root, recursive: true); }
        return Task.CompletedTask;
    }

    private static Task ReadOnlyDirectoryCleanup()
    {
        var root = CreateRoot();
        var directories = new[] { Path.Combine(root, "data"), Path.Combine(root, "data", "nested"), Path.Combine(root, "cache") };
        try
        {
            foreach (var directory in directories)
            {
                Directory.CreateDirectory(directory);
                File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.ReadOnly);
            }
            var lockedPath = Path.Combine(directories[1], "locked.bin");
            File.WriteAllText(lockedPath, "locked");
            File.WriteAllText(Path.Combine(directories[1], "removable.bin"), "remove");
            File.WriteAllText(Path.Combine(directories[2], "removable.bin"), "remove");
            using var log = MaintenanceLog.Create();
            using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var retained = UserDataCleaner.PurgeRootsForTest([directories[0], directories[2]], log);
                Assert.True(retained.Contains(lockedPath));
                Assert.Equal(false, File.Exists(Path.Combine(directories[1], "removable.bin")));
                Assert.Equal(false, Directory.Exists(directories[2]));
            }
            Assert.Equal(0, UserDataCleaner.PurgeRootsForTest([directories[0], directories[2]], log).Count);
            Assert.True(directories.All(path => !Directory.Exists(path)));
        }
        finally
        {
            foreach (var directory in directories)
                if (Directory.Exists(directory)) File.SetAttributes(directory, FileAttributes.Directory);
            Directory.Delete(root, recursive: true);
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

    private static Task MalformedOwnershipTypesAreRejected()
    {
        var root = CreateRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "managed.bin"), "preserve me");
            File.WriteAllText(Path.Combine(root, "Uninstall.exe"), "retry uninstall");
            var invalidValues = new[] { "null", "true", "\"1\"", "{}", "[]", "1.5", "1e100" };
            foreach (var location in new[] { "ownerSchema", "manifestSchema", "file", "length" })
                foreach (var value in invalidValues)
                {
                    WriteOwnership(root, ["managed.bin", "Uninstall.exe"]);
                    var ownerPath = Path.Combine(root, InstallOwnership.OwnerFileName);
                    var manifestPath = Path.Combine(root, InstallOwnership.ManifestFileName);
                    var owner = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ownerPath))!;
                    var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!;
                    var invalid = System.Text.Json.Nodes.JsonNode.Parse(value);
                    switch (location)
                    {
                        case "ownerSchema": owner["schemaVersion"] = invalid; break;
                        case "manifestSchema": manifest["schemaVersion"] = invalid; break;
                        case "file": manifest["files"]![0] = invalid; break;
                        case "length": manifest["files"]![0]!["length"] = invalid; break;
                    }
                    File.WriteAllText(manifestPath, manifest.ToJsonString());
                    owner["manifestSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath)));
                    File.WriteAllText(ownerPath, owner.ToJsonString());
                    var originalOwner = File.ReadAllBytes(ownerPath);
                    var originalManifest = File.ReadAllBytes(manifestPath);

                    Assert.Throws<InvalidDataException>(() => InstallOwnership.Load(root));
                    Assert.Equal("preserve me", File.ReadAllText(Path.Combine(root, "managed.bin")));
                    Assert.Equal("retry uninstall", File.ReadAllText(Path.Combine(root, "Uninstall.exe")));
                    Assert.True(originalOwner.SequenceEqual(File.ReadAllBytes(ownerPath)));
                    Assert.True(originalManifest.SequenceEqual(File.ReadAllBytes(manifestPath)));
                }
        }
        finally { Directory.Delete(root, recursive: true); }
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

using StreamlinkVlcStudio.Core;

internal static partial class UpdateModernizationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> ZipTests { get; } =
    [
        ("ZIP updater checks downloads and restarts without opening a browser", ZipUpdateUiAsync),
        ("ZIP updater restores verified packages and rejects changed installation types", ZipPreparedRecoveryAsync),
        ("ZIP updater rejects substituted release assets and damaged downloads", ZipTrustAndDownloadFailuresAsync),
        ("ZIP updater repairs interrupted same-version installations", ZipInterruptedRepairAsync),
        ("ZIP update helper rejects unsafe archives before installation", ZipArchiveSafetyAsync),
        ("ZIP update helper rejects excessive and ambiguous archive payloads", ZipArchiveLimitsAsync),
        ("ZIP update helper locks authenticated files and preserves argument boundaries", ZipInstallerArgumentsAsync),
        ("ZIP update helper handles real PowerShell success cancellation failure and reboot", ZipInstallerCompletionAsync),
        ("ZIP update helper rejects the wrong payload version without starting installation", ZipInstallerVersionAsync),
        ("ZIP update helper reopens the correct executable after installation", ZipRelaunchAsync)
    ];

    private static StagedAppUpdateService ZipService(HttpClient client, string root, RSA rsa,
        Version? version = null, Action<ProcessStartInfo>? start = null, Func<CancellationToken, Task<bool>>? verify = null) =>
        new(new MemoryLogger(), client, root, Path.Combine(root, "updates"),
            detectInstallKind: () => AppInstallKind.Zip,
            getCurrentVersion: () => version ?? new Version(1, 7, 0),
            trustedKey: rsa.ExportParameters(false), startUpdateHelper: start,
            verifyInstallation: verify ?? (_ => Task.FromResult(false)));

    private static async Task ZipUpdateUiAsync()
    {
        using var rsa = RSA.Create(3072);
        var fixture = SignedReleaseFixture.Create(rsa, 1);
        using var client = new HttpClient(fixture.Handler);
        var root = NewTemporaryDirectory();
        try
        {
            ProcessStartInfo? launch = null;
            using var service = ZipService(client, root, rsa, start: info => launch = info);
            var browsers = 0;
            var shutdowns = 0;
            await using var viewModel = CreateUpdateViewModel(service, () => shutdowns++, _ => browsers++);
            await viewModel.UpdateAppCommand.ExecuteAsync();
            Assert.Equal(AppUpdatePhase.Available, service.State.Phase);
            Assert.Equal("Download update", viewModel.AppUpdateActionText);
            Assert.Equal(0, fixture.Handler.ZipRequests);
            await viewModel.UpdateAppCommand.ExecuteAsync();
            var prepared = service.State.PreparedUpdate!;
            Assert.Equal(AppUpdatePackageKind.Zip, prepared.PackageKind);
            Assert.Equal("zip-package", await File.ReadAllTextAsync(prepared.PackagePath));
            Assert.Equal("Restart and install", viewModel.AppUpdateActionText);
            Assert.Equal(1, fixture.Handler.ZipRequests);
            Assert.Equal(0, fixture.Handler.SetupRequests);
            Assert.Equal(0, shutdowns);
            await viewModel.UpdateAppCommand.ExecuteAsync();
            Assert.Equal(1, shutdowns);
            Assert.Equal(0, browsers);
            Assert.Equal("Zip", launch!.ArgumentList[launch.ArgumentList.IndexOf("--package-kind") + 1]);
            Assert.Equal(prepared.PackagePath, launch.ArgumentList[launch.ArgumentList.IndexOf("--setup") + 1]);
            Assert.Equal(prepared.Release.Zip.Sha256, launch.ArgumentList[launch.ArgumentList.IndexOf("--setup-sha256") + 1]);
            Assert.Equal(root, launch.ArgumentList[launch.ArgumentList.IndexOf("--install-dir") + 1]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task ZipPreparedRecoveryAsync()
    {
        using var rsa = RSA.Create(3072);
        foreach (var scenario in new[] { "restart", "hash", "metadata", "install-kind" })
        {
            var fixture = SignedReleaseFixture.Create(rsa, 1);
            using var client = new HttpClient(fixture.Handler);
            var root = NewTemporaryDirectory();
            try
            {
                PreparedAppUpdate prepared;
                using (var service = ZipService(client, root, rsa))
                    prepared = await service.DownloadAsync((await service.CheckAsync(UpdateCheckReason.Manual)).Release!);
                if (scenario == "hash") await File.WriteAllTextAsync(prepared.PackagePath, "zip-corrupt");
                if (scenario == "metadata")
                    await File.WriteAllTextAsync(Path.Combine(prepared.OperationDirectory, "operation.json"),
                        JsonSerializer.Serialize(prepared with { PackageKind = AppUpdatePackageKind.Setup }));
                using var restarted = scenario == "install-kind" ? ManagedService(client, root, rsa) : ZipService(client, root, rsa);
                var check = await restarted.CheckAsync(UpdateCheckReason.Startup);
                Assert.Equal(false, check.IsNotifyOnly);
                Assert.Equal(scenario == "restart" ? AppUpdatePhase.Ready : AppUpdatePhase.Available, restarted.State.Phase);
                var downloaded = await restarted.DownloadAsync(check.Release!);
                if (scenario == "restart") Assert.Equal(prepared.OperationId, downloaded.OperationId);
                else Assert.True(downloaded.OperationId != prepared.OperationId);
                Assert.Equal(scenario == "install-kind" ? AppUpdatePackageKind.Setup : AppUpdatePackageKind.Zip, downloaded.PackageKind);
                Assert.Equal(scenario == "install-kind" ? "setup-package" : "zip-package", await File.ReadAllTextAsync(downloaded.PackagePath));
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    private static async Task ZipTrustAndDownloadFailuresAsync()
    {
        using var rsa = RSA.Create(3072);
        var fixture = SignedReleaseFixture.Create(rsa, 1);
        using var client = new HttpClient(fixture.Handler);
        var root = NewTemporaryDirectory();
        try
        {
            using var service = ZipService(client, root, rsa);
            var release = (await service.CheckAsync(UpdateCheckReason.Manual)).Release!;
            foreach (var substituted in new[]
            {
                release with { Zip = release.Zip with { DownloadUri = new Uri("https://example.test/substituted") } },
                release with { Zip = release.Zip with { Sha256 = new string('0', 64) } },
                release with { Repository = "other/repository" },
                release with { DependencyMinimums = new Dictionary<string, string> { ["streamlink"] = "99.0", ["vlc"] = "3.0.23" } }
            })
                await Assert.ThrowsAsync<CryptographicException>(() => service.DownloadAsync(substituted));
            Assert.Equal(0, fixture.Handler.ZipRequests);
            fixture.Handler.ZipOverride = Encoding.UTF8.GetBytes("zip-corrupt");
            await Assert.ThrowsAsync<CryptographicException>(() => service.DownloadAsync(release));
            fixture.Handler.ZipOverride = [1];
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release));
            Assert.Equal(AppUpdatePhase.DownloadFailed, service.State.Phase);
            Assert.Equal(0, Directory.GetDirectories(Path.Combine(root, "updates", "operations")).Length);
            fixture.Handler.ZipOverride = null;
            var recovered = await service.DownloadAsync(release);
            Assert.Equal(AppUpdatePhase.Ready, service.State.Phase);
            Assert.Equal("zip-package", await File.ReadAllTextAsync(recovered.PackagePath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task ZipInterruptedRepairAsync()
    {
        using var rsa = RSA.Create(3072);
        var fixture = SignedReleaseFixture.Create(rsa, 1);
        using var client = new HttpClient(fixture.Handler);
        var root = NewTemporaryDirectory();
        try
        {
            PreparedAppUpdate prepared;
            using (var service = ZipService(client, root, rsa, start: _ => { }))
            {
                prepared = await service.DownloadAsync((await service.CheckAsync(UpdateCheckReason.Manual)).Release!);
                await service.ApplyAndRestartAsync(prepared);
            }
            using (var incomplete = ZipService(client, root, rsa, prepared.Release.Version))
            {
                var check = await incomplete.CheckAsync(UpdateCheckReason.Startup);
                Assert.True(check.IsUpdateAvailable);
                Assert.Equal(AppUpdatePhase.Ready, incomplete.State.Phase);
                Assert.Equal(prepared.OperationId, incomplete.State.PreparedUpdate!.OperationId);
            }
            using var repaired = ZipService(client, root, rsa, prepared.Release.Version, verify: _ => Task.FromResult(true));
            Assert.Equal(false, (await repaired.CheckAsync(UpdateCheckReason.Manual)).IsUpdateAvailable);
            Assert.Equal(AppUpdatePhase.Completed, repaired.State.Phase);
            Assert.Equal(false, File.Exists(Path.Combine(root, "updates", "pending-repair.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static MemoryStream ZipArchiveFixture(params (string Path, string Content, int Attributes)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content, attributes) in entries)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                entry.ExternalAttributes = attributes;
                using var output = entry.Open();
                output.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static Task ZipArchiveSafetyAsync()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var invalidEntries = new (string, string, int)[][]
            {
                [("../escape.txt", "unsafe", 0)],
                [("nested/../../escape.txt", "unsafe", 0)],
                [(@"C:\escape.txt", "unsafe", 0)],
                [("/escape.txt", "unsafe", 0)],
                [("StreamStudio.exe:stream", "unsafe", 0)],
                [("trailing./file", "unsafe", 0)],
                [("trailing /file", "unsafe", 0)],
                [("NUL.txt", "unsafe", 0)],
                [("COM\u00b9.txt", "unsafe", 0)],
                [("CONIN$.txt", "unsafe", 0)],
                [("same.txt", "a", 0), ("SAME.TXT", "b", 0)],
                [("parent", "file", 0), ("parent/child.txt", "child", 0)],
                [("link", "target", unchecked((int)0xA0000000))],
                [("junction", "target", (int)FileAttributes.ReparsePoint)]
            };
            foreach (var invalid in invalidEntries)
            {
                using var archive = ZipArchiveFixture(
                    [("StreamStudio.exe", "app", 0), ("install.ps1", "script", 0), .. invalid]);
                var destination = Path.Combine(root, Guid.NewGuid().ToString("N"));
                Assert.Throws<InvalidDataException>(() => ZipUpdateInstaller.ExtractPayload(archive, destination));
                Assert.Equal(false, Directory.Exists(destination));
            }
            Assert.Equal(0, Directory.GetFileSystemEntries(root).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
        return Task.CompletedTask;
    }

    private static Task ZipArchiveLimitsAsync()
    {
        var root = NewTemporaryDirectory();
        try
        {
            using var empty = ZipArchiveFixture();
            Assert.Throws<InvalidDataException>(() => ZipUpdateInstaller.ExtractPayload(empty, Path.Combine(root, "empty")));
            using var excessive = ZipArchiveFixture(Enumerable.Range(0, 10_001).Select(index => ($"entry{index}", "", 0)).ToArray());
            Assert.Throws<InvalidDataException>(() => ZipUpdateInstaller.ExtractPayload(excessive, Path.Combine(root, "excessive")));
            using var missing = ZipArchiveFixture(("StreamStudio.exe", "app", 0));
            Assert.Throws<InvalidDataException>(() => ZipUpdateInstaller.ExtractPayload(missing, Path.Combine(root, "missing")));
            using var ambiguous = ZipArchiveFixture(("one/StreamStudio.exe", "app", 0), ("one/install.ps1", "script", 0),
                ("two/StreamStudio.exe", "app", 0), ("two/install.ps1", "script", 0));
            Assert.Throws<InvalidDataException>(() => ZipUpdateInstaller.ExtractPayload(ambiguous, Path.Combine(root, "ambiguous")));
            foreach (var prefix in new[] { "", "StreamStudio/" })
            {
                using var valid = ZipArchiveFixture(($"{prefix}StreamStudio.exe", "app", 0), ($"{prefix}install.ps1", "script", 0));
                var destination = Path.Combine(root, Guid.NewGuid().ToString("N"));
                var payload = ZipUpdateInstaller.ExtractPayload(valid, destination);
                Assert.Equal("app", File.ReadAllText(Path.Combine(payload, AppIdentity.ZipExecutableName)));
                Assert.Equal("script", File.ReadAllText(Path.Combine(payload, "install.ps1")));
            }
            Assert.Equal(2, Directory.GetDirectories(root).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
        return Task.CompletedTask;
    }

    private static string ZipFixtureExecutable => Path.ChangeExtension(typeof(UpdateModernizationTestCatalog).Assembly.Location, ".exe");

    private static MemoryStream ExecutableZipFixture(string script)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var input = File.OpenRead(ZipFixtureExecutable))
            using (var output = archive.CreateEntry(AppIdentity.ZipExecutableName).Open()) input.CopyTo(output);
            using var writer = new StreamWriter(archive.CreateEntry("install.ps1").Open(), new UTF8Encoding(false));
            writer.Write(script);
        }
        stream.Position = 0;
        return stream;
    }

    private static async Task ZipInstallerArgumentsAsync()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var operation = Path.Combine(root, "operation");
            Directory.CreateDirectory(operation);
            var destination = Path.Combine(root, "app's [files]; $literal");
            using var archive = ExecutableZipFixture("param($AppSource, $InstallDir)\n$script:RebootRequired = $false");
            var runs = 0;
            var result = await ZipUpdateInstaller.InstallAsync(archive, operation, destination,
                UpdateHelperRunner.ReadVersion(ZipFixtureExecutable)!, Path.Combine(root, "install.log"), (info, timeout) =>
                {
                    runs++;
                    Assert.Equal(TimeSpan.FromMinutes(30), timeout);
                    Assert.Equal(false, info.UseShellExecute);
                    Assert.True(info.CreateNoWindow);
                    Assert.Equal(destination, info.ArgumentList[info.ArgumentList.IndexOf("-Destination") + 1]);
                    var installer = info.ArgumentList[info.ArgumentList.IndexOf("-InstallerPath") + 1];
                    Assert.Throws<IOException>(() => File.WriteAllText(installer, "modified"));
                    var wrapper = info.ArgumentList[info.ArgumentList.IndexOf("-File") + 1];
                    Assert.Throws<IOException>(() => File.WriteAllText(wrapper, "modified"));
                    return Task.FromResult(new ProcessExecutionResult(0, "installed", "", false));
                });
            Assert.Equal(0, result);
            Assert.Equal(1, runs);
            Assert.True((await File.ReadAllTextAsync(Path.Combine(root, "install.log"))).Contains("installed"));
            Assert.Equal(0, Directory.GetFileSystemEntries(operation).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task ZipInstallerCompletionAsync()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var operation = Path.Combine(root, "operation");
            Directory.CreateDirectory(operation);
            var destination = Path.Combine(root, "app's [files]; $literal");
            Directory.CreateDirectory(destination);
            foreach (var (script, expected) in new[]
            {
                ("param($AppSource, $InstallDir)\n[IO.File]::WriteAllText((Join-Path $InstallDir 'updated.txt'), $AppSource)\n$script:RebootRequired = $false", 0),
                ("param($AppSource, $InstallDir)\n$script:RebootRequired = $true", 3010),
                ("param($AppSource, $InstallDir)\nthrow [ComponentModel.Win32Exception]::new(1223, 'Canceled')", 1223),
                ("param($AppSource, $InstallDir)\nthrow 'fixture failure'", 1)
            })
            {
                using var archive = ExecutableZipFixture(script);
                Assert.Equal(expected, await ZipUpdateInstaller.InstallAsync(archive, operation, destination,
                    UpdateHelperRunner.ReadVersion(ZipFixtureExecutable)!, Path.Combine(root, "install.log")));
                Assert.Equal(0, Directory.GetFileSystemEntries(operation).Length);
            }
            Assert.Equal("Local", await File.ReadAllTextAsync(Path.Combine(destination, "updated.txt")));
            Assert.True((await File.ReadAllTextAsync(Path.Combine(root, "install.log"))).Contains("fixture failure"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task ZipInstallerVersionAsync()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var actual = UpdateHelperRunner.ReadVersion(ZipFixtureExecutable)!;
            using var archive = ExecutableZipFixture("throw 'must not run'");
            var runs = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() => ZipUpdateInstaller.InstallAsync(archive, root,
                Path.Combine(root, "install"), new Version(actual.Major + 1, 0, 0), Path.Combine(root, "install.log"),
                (_, _) => { runs++; return Task.FromResult(new ProcessExecutionResult(0, "", "", false)); }));
            Assert.Equal(0, runs);
            Assert.Equal(0, Directory.GetFileSystemEntries(root).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task ZipRelaunchAsync()
    {
        var root = NewTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, AppIdentity.ZipExecutableName), "zip app");
            await File.WriteAllTextAsync(Path.Combine(root, AppIdentity.ManagedExecutableName), "other app");
            foreach (var outcome in Enum.GetValues<AppUpdateCompletionOutcome>())
            {
                var launches = new List<ProcessStartInfo>();
                var completion = new AppUpdateCompletion(Guid.NewGuid(), outcome, 0, null, "finished", DateTimeOffset.UtcNow);
                await UpdateHelperRunner.CompleteAndRelaunchAsync(Path.Combine(root, "completion.json"), completion,
                    root, info => launches.Add(info), AppUpdatePackageKind.Zip);
                Assert.Equal(outcome == AppUpdateCompletionOutcome.SucceededRebootRequired ? 0 : 1, launches.Count);
                if (launches.Count != 0) Assert.Equal(Path.Combine(root, AppIdentity.ZipExecutableName), launches[0].FileName);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

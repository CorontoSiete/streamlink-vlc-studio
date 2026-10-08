using StreamlinkVlcStudio.Maintenance;

internal static partial class MaintenanceTestCatalog
{
    private static Task QuietArgumentFailures()
    {
        foreach (var arguments in new[]
        {
            new[] { "/quiet", "--unknown" },
            new[] { "--unknown", "/Q" },
            new[] { "--quiet", "--parent-pid", "-1" },
            new[] { "/quiet", "--staged" }
        })
            Assert.Equal(87, MaintenanceApplication.Run(arguments));
        return Task.CompletedTask;
    }

    private static Task ShutdownWaitsForInstanceExit()
    {
        foreach (var scenario in new[] { "missing-event", "stalled", "delayed-event", "ready", "abandoned", "legacy", "legacy-stalled" })
        {
            var identity = "Local\\StreamStudio.Shutdown.Test." + Guid.NewGuid().ToString("N");
            using var keeper = new Mutex(false, identity + ".mutex");
            using var ready = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            Exception? workerError = null;
            var worker = new Thread(() =>
            {
                try
                {
                    using var mutex = new Mutex(false, identity + ".mutex");
                    Assert.True(mutex.WaitOne(TimeSpan.FromSeconds(2)));
                    if (scenario is "missing-event" or "abandoned")
                    {
                        ready.Set();
                        Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                        if (scenario == "missing-event") mutex.ReleaseMutex();
                        return;
                    }
                    if (scenario == "delayed-event")
                    {
                        ready.Set();
                        Thread.Sleep(80);
                    }
                    using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, identity + ".event");
                    ready.Set();
                    Assert.True(signal.WaitOne(TimeSpan.FromSeconds(3)), "Shutdown did not signal the instance.");
                    if (scenario is "stalled" or "legacy-stalled") Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                    mutex.ReleaseMutex();
                }
                catch (Exception error) { workerError = error; ready.Set(); }
            })
            { IsBackground = true };
            worker.Start();
            try
            {
                Assert.True(ready.Wait(TimeSpan.FromSeconds(2)));
                if (scenario == "abandoned")
                {
                    release.Set();
                    Assert.True(worker.Join(TimeSpan.FromSeconds(2)));
                }
                var shouldExit = scenario is "ready" or "delayed-event" or "abandoned" or "legacy";
                var timeout = TimeSpan.FromMilliseconds(shouldExit ? 1500 : 150);
                var stopped = scenario.StartsWith("legacy", StringComparison.Ordinal)
                    ? MaintenanceModeRunner.RequestShutdown(timeout,
                        (identity + ".current-event", identity + ".current-mutex"), (identity + ".event", identity + ".mutex"))
                    : MaintenanceModeRunner.RequestShutdown(timeout, identity + ".event", identity + ".mutex");
                Assert.Equal(shouldExit, stopped);
            }
            finally
            {
                release.Set();
                Assert.True(worker.Join(TimeSpan.FromSeconds(2)));
            }
            if (workerError is not null) throw workerError;
            Assert.True(keeper.WaitOne(0), "The shutdown check retained the single-instance mutex.");
            keeper.ReleaseMutex();
        }
        return Task.CompletedTask;
    }

    private static Task FailedShutdownPreservesInstallation()
    {
        var root = CreateRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "StreamStudio.exe"), "application");
            File.WriteAllText(Path.Combine(root, "Uninstall.exe"), "retry uninstall");
            File.WriteAllText(Path.Combine(root, "runtime.dll"), "keep the runtime");
            WriteOwnership(root, ["StreamStudio.exe", "Uninstall.exe", "runtime.dll"]);
            var original = Directory.GetFiles(root).ToDictionary(path => path, File.ReadAllBytes);
            var calls = new List<string>();
            using var log = MaintenanceLog.Create();
            var result = MaintenanceApplication.RemoveInstallation(CommandLineOptions.Parse(["/quiet"]),
                InstallOwnership.Load(root), log,
                (_, argument, _, _) => { calls.Add(argument); return false; },
                _ => throw new InvalidOperationException("Failed shutdown must preserve personal data."));
            Assert.Equal(1, result);
            Assert.Equal(1, calls.Count);
            Assert.Equal("--maintenance-request-shutdown", calls[0]);
            foreach (var (path, bytes) in original)
                Assert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(path)));
            _ = InstallOwnership.Load(root);
        }
        finally { Directory.Delete(root, recursive: true); }
        return Task.CompletedTask;
    }

    private static Task CompetingInstallerPreventsUninstall()
    {
        var root = CreateRoot();
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Exception? workerError = null;
        var worker = new Thread(() =>
        {
            try
            {
                using var lease = InstallationOperationLease.Acquire(root);
                ready.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            }
            catch (Exception error) { workerError = error; ready.Set(); }
        })
        { IsBackground = true };
        try
        {
            File.WriteAllText(Path.Combine(root, "StreamStudio.exe"), "old app");
            WriteOwnership(root, ["StreamStudio.exe"]);
            var ownership = InstallOwnership.Load(root);
            using var log = MaintenanceLog.Create();
            worker.Start();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(2)));
            Assert.Throws<IOException>(() => MaintenanceApplication.RemoveInstallation(CommandLineOptions.Parse(["/quiet"]),
                ownership, log,
                (_, _, _, _) => throw new InvalidOperationException("A competing installer must prevent maintenance."),
                _ => throw new InvalidOperationException("A competing installer must preserve personal data.")));
            Assert.Equal("old app", File.ReadAllText(Path.Combine(root, "StreamStudio.exe")));
            release.Set();
            Assert.True(worker.Join(TimeSpan.FromSeconds(2)));
            if (workerError is not null) throw workerError;
            Assert.Equal(0, MaintenanceApplication.RemoveInstallation(CommandLineOptions.Parse(["/quiet"]),
                ownership, log, (_, _, _, _) => true, _ => []));
            Assert.Equal(false, Directory.Exists(root));
        }
        finally
        {
            release.Set();
            if ((worker.ThreadState & System.Threading.ThreadState.Unstarted) == 0) worker.Join(TimeSpan.FromSeconds(2));
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        return Task.CompletedTask;
    }

    private static Task InstallerLeaseIdentityAndRecovery()
    {
        const string expected = "Local\\StreamStudio.Installation.25549A2C6E0AAF49EA87B4987F6EAC72914EA313C86CB5E31C88CBCFE33BFB02";
        foreach (var path in new[] { @"C:\Stream Studio\Install\", "C:/stream studio/install/../install" })
            Assert.Equal(expected, InstallationOperationLease.GetMutexName(path));
        var root = CreateRoot();
        try
        {
            using var keeper = new Mutex(false, InstallationOperationLease.GetMutexName(root));
            Exception? workerError = null;
            var worker = new Thread(() =>
            {
                try
                {
                    using var abandoned = new Mutex(false, InstallationOperationLease.GetMutexName(root));
                    Assert.True(abandoned.WaitOne(0));
                    // Deliberately exit with ownership to simulate a terminated installer.
                }
                catch (Exception error) { workerError = error; }
            });
            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(2)));
            if (workerError is not null) throw workerError;
            using (var recovered = InstallationOperationLease.Acquire(root))
            using (var nested = InstallationOperationLease.Acquire(root)) { }
            Assert.True(keeper.WaitOne(0), "A recovered or nested lease retained ownership.");
            keeper.ReleaseMutex();
        }
        finally { Directory.Delete(root, recursive: true); }
        return Task.CompletedTask;
    }

    private static Task DamagedExecutableMaintenanceFailure()
    {
        var root = CreateRoot();
        try
        {
            var app = Path.Combine(root, "app.exe");
            File.WriteAllText(app, "damaged executable");
            using var log = MaintenanceLog.Create();
            Assert.Equal(false, MaintenanceApplication.RunAppMaintenanceMode(app,
                "--maintenance-unregister-notifications", TimeSpan.FromSeconds(2), log));
            using (var reader = new StreamReader(new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                Assert.Contains("maintenance mode failed", reader.ReadToEnd());
            Assert.Equal("damaged executable", File.ReadAllText(app));
        }
        finally { Directory.Delete(root, recursive: true); }
        return Task.CompletedTask;
    }

    private static Task ShortcutCleanupReportsFailures()
    {
        var root = CreateRoot();
        try
        {
            var locked = Path.Combine(root, "locked.lnk");
            var removable = Path.Combine(root, "removable.lnk");
            File.WriteAllText(locked, "locked shortcut");
            File.WriteAllText(removable, "remove this shortcut");
            File.SetAttributes(removable, FileAttributes.ReadOnly);
            using var log = MaintenanceLog.Create();
            using (var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Equal(false, ManagedInstallationCleaner.RemoveShortcuts([locked, removable], log));
                Assert.True(File.Exists(locked));
                Assert.Equal(false, File.Exists(removable));
            }
            Assert.True(ManagedInstallationCleaner.RemoveShortcuts([locked, removable], log));
            Assert.Equal(false, File.Exists(locked));
            var report = typeof(MaintenanceApplication).GetMethod("ReportResult", BindingFlags.Static | BindingFlags.NonPublic)!;
            var options = CommandLineOptions.Parse(["/quiet", "--preserve-user-data"]);
            Assert.Equal(2, (int)report.Invoke(null, [options, new CleanupOutcome(true, [], true, false), true, Array.Empty<string>(), log])!);
            Assert.Equal(0, (int)report.Invoke(null, [options, new CleanupOutcome(true, [], true, true), true, Array.Empty<string>(), log])!);
        }
        finally { Directory.Delete(root, recursive: true); }
        return Task.CompletedTask;
    }
}

internal static partial class UpdateModernizationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> DependencyRecoveryTests =>
    [
        ("updater retries incomplete current-version installs after restart corruption and cache expiration", RetryIncompleteCurrentVersionAsync),
        ("updater clears the repair notice only after the installed runtime check succeeds", ManualRepairClearsNoticeAsync),
        ("updater successful retry supersedes a failed marker while the cached installer is locked", SuccessfulRetryClearsLockedFailureAsync),
        ("updater ignores invalid and unrelated persistent repair notices", InvalidRepairNoticesAsync),
        ("signed updater accepts new dependency minima and rejects missing malformed and overflowed versions", SignedDependencyMinimumsAsync),
        ("update helper requires a successful bounded installed dependency check", HelperDependencyVerificationAsync),
        ("updater reads the dependency capability from MSI metadata and refuses legacy or malformed declarations", InstalledDependencyCapability)
    ];

    private static async Task WriteDependencyCompletionAsync(string root, PreparedAppUpdate prepared,
        AppUpdateCompletionOutcome outcome, DateTimeOffset now, bool includeTarget = true)
    {
        var directory = Path.Combine(root, "updates", "results");
        Directory.CreateDirectory(directory);
        var completion = new AppUpdateCompletion(prepared.OperationId, outcome,
            outcome == AppUpdateCompletionOutcome.Succeeded ? 0 : 1603, null, "Runtime installation result.", now,
            includeTarget ? prepared.Release.Version : null);
        await File.WriteAllTextAsync(Path.Combine(directory, prepared.OperationId.ToString("N") + ".json"),
            JsonSerializer.Serialize(completion, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static async Task RetryIncompleteCurrentVersionAsync()
    {
        using var rsa = RSA.Create(3072);
        foreach (var outcome in new[] { AppUpdateCompletionOutcome.Failed, AppUpdateCompletionOutcome.Canceled })
            foreach (var scenario in new[] { "valid", "corrupt", "expired" })
            {
                var fixture = SignedReleaseFixture.Create(rsa, 1);
                using var client = new HttpClient(fixture.Handler);
                var root = NewTemporaryDirectory();
                var now = DateTimeOffset.UtcNow;
                try
                {
                    PreparedAppUpdate prepared;
                    using (var initial = ManagedService(client, root, rsa, () => now))
                        prepared = await initial.DownloadAsync((await initial.CheckAsync(UpdateCheckReason.Manual)).Release!);
                    // Older helpers omit TargetVersion; valid staged metadata supplies it.
                    await WriteDependencyCompletionAsync(root, prepared, outcome, now, includeTarget: false);
                    using (var installed = ManagedService(client, root, rsa, () => now, prepared.Release.Version,
                        _ => Task.FromResult(false)))
                    {
                        var completion = await installed.ConsumeCompletionAsync();
                        Assert.Equal(outcome, completion!.Outcome);
                        Assert.Equal(prepared.Release.Version, completion.TargetVersion);
                        Assert.True(File.Exists(Path.Combine(root, "updates", "pending-repair.json")));
                    }
                    if (scenario == "corrupt") await File.WriteAllTextAsync(prepared.SetupPath, "evil!-package");
                    if (scenario == "expired") now = now.AddDays(8);
                    using var restarted = ManagedService(client, root, rsa, () => now, prepared.Release.Version,
                        _ => Task.FromResult(false));
                    var check = await restarted.CheckAsync(UpdateCheckReason.Startup);
                    Assert.True(check.IsUpdateAvailable, "A replaced executable hid its incomplete runtime installation.");
                    Assert.Equal(false, check.IsNotifyOnly);
                    Assert.Equal(scenario == "valid" ? AppUpdatePhase.Ready : AppUpdatePhase.Available, restarted.State.Phase);
                    if (scenario == "expired") Assert.Equal(false, Directory.Exists(prepared.OperationDirectory));
                    var retry = await restarted.DownloadAsync(check.Release!);
                    Assert.Equal(scenario == "valid", retry.OperationId == prepared.OperationId);
                    Assert.Equal(scenario == "valid" ? 1 : 2, fixture.Handler.SetupRequests);
                    Assert.Equal("setup-package", await File.ReadAllTextAsync(retry.SetupPath));
                    // Same-version repair retains all of the normal prelaunch hash checks.
                    await File.WriteAllTextAsync(retry.SetupPath, "evil!-package");
                    await Assert.ThrowsAsync<CryptographicException>(() => restarted.ApplyAndRestartAsync(retry));
                }
                finally { Directory.Delete(root, recursive: true); }
            }
    }

    private static async Task ManualRepairClearsNoticeAsync()
    {
        using var rsa = RSA.Create(3072);
        var fixture = SignedReleaseFixture.Create(rsa, 1);
        using var client = new HttpClient(fixture.Handler);
        var root = NewTemporaryDirectory();
        try
        {
            PreparedAppUpdate prepared;
            using (var initial = ManagedService(client, root, rsa))
                prepared = await initial.DownloadAsync((await initial.CheckAsync(UpdateCheckReason.Manual)).Release!);
            await WriteDependencyCompletionAsync(root, prepared, AppUpdateCompletionOutcome.Failed, DateTimeOffset.UtcNow);
            var checks = 0;
            using var repaired = ManagedService(client, root, rsa, version: prepared.Release.Version,
                verifyInstallation: token => { token.ThrowIfCancellationRequested(); checks++; return Task.FromResult(true); });
            await repaired.ConsumeCompletionAsync();
            var result = await repaired.CheckAsync(UpdateCheckReason.Manual);
            Assert.Equal(false, result.IsUpdateAvailable);
            Assert.Equal(AppUpdatePhase.Completed, repaired.State.Phase);
            Assert.Equal(1, checks);
            Assert.Equal(false, File.Exists(Path.Combine(root, "updates", "pending-repair.json")));
            Assert.Equal(false, Directory.Exists(prepared.OperationDirectory));
            await repaired.CheckAsync(UpdateCheckReason.Manual);
            Assert.Equal(1, checks);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repaired.DownloadAsync(result.Release!));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task SuccessfulRetryClearsLockedFailureAsync()
    {
        using var rsa = RSA.Create(3072);
        var fixture = SignedReleaseFixture.Create(rsa, 1);
        using var client = new HttpClient(fixture.Handler);
        var root = NewTemporaryDirectory();
        try
        {
            PreparedAppUpdate prepared;
            using (var initial = ManagedService(client, root, rsa))
                prepared = await initial.DownloadAsync((await initial.CheckAsync(UpdateCheckReason.Manual)).Release!);
            await WriteDependencyCompletionAsync(root, prepared, AppUpdateCompletionOutcome.Failed, DateTimeOffset.UtcNow);
            using (var failed = ManagedService(client, root, rsa, version: prepared.Release.Version))
                await failed.ConsumeCompletionAsync();
            using var successful = ManagedService(client, root, rsa, version: prepared.Release.Version,
                verifyInstallation: _ => throw new InvalidOperationException("A successful result should supersede the old failure."));
            using (var locked = new FileStream(prepared.SetupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await WriteDependencyCompletionAsync(root, prepared, AppUpdateCompletionOutcome.Succeeded, DateTimeOffset.UtcNow);
                Assert.Equal(AppUpdateCompletionOutcome.Succeeded, (await successful.ConsumeCompletionAsync())!.Outcome);
                Assert.Equal(false, File.Exists(Path.Combine(root, "updates", "pending-repair.json")));
                var result = await successful.CheckAsync(UpdateCheckReason.Manual);
                Assert.Equal(false, result.IsUpdateAvailable);
                Assert.Equal(AppUpdatePhase.Completed, successful.State.Phase);
            }
            await successful.ConsumeCompletionAsync();
            Assert.Equal(false, Directory.Exists(prepared.OperationDirectory));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task InvalidRepairNoticesAsync()
    {
        using var rsa = RSA.Create(3072);
        foreach (var scenario in new[] { "malformed", "success", "empty-id", "missing-target", "other-version" })
        {
            var fixture = SignedReleaseFixture.Create(rsa, 1);
            using var client = new HttpClient(fixture.Handler);
            var root = NewTemporaryDirectory();
            try
            {
                var updates = Path.Combine(root, "updates");
                Directory.CreateDirectory(updates);
                var completion = new AppUpdateCompletion(scenario == "empty-id" ? Guid.Empty : Guid.NewGuid(),
                    scenario == "success" ? AppUpdateCompletionOutcome.Succeeded : AppUpdateCompletionOutcome.Failed,
                    1603, null, "Incomplete installation.", DateTimeOffset.UtcNow,
                    scenario == "missing-target" ? null : new Version(1, scenario == "other-version" ? 9 : 8, 0));
                await File.WriteAllTextAsync(Path.Combine(updates, "pending-repair.json"), scenario == "malformed" ? "{" :
                    JsonSerializer.Serialize(completion, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                using var service = ManagedService(client, root, rsa, version: new Version(1, 8, 0));
                var check = await service.CheckAsync(UpdateCheckReason.Manual);
                Assert.Equal(false, check.IsUpdateAvailable);
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(check.Release!));
                Assert.Equal(0, fixture.Handler.SetupRequests);
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    private static async Task SignedDependencyMinimumsAsync()
    {
        using var rsa = RSA.Create(3072);
        foreach (var scenario in new[] { "legacy", "current", "missing-streamlink", "missing-vlc", "invalid-webview", "zero-webview", "overflow-webview" })
        {
            var minimums = new Dictionary<string, string> { ["streamlink"] = "8.5.0-1", ["vlc"] = "3.0.23" };
            if (scenario != "legacy") minimums["webview2"] = "152.0.4191.53";
            if (scenario == "missing-streamlink") minimums.Remove("streamlink");
            if (scenario == "missing-vlc") minimums.Remove("vlc");
            if (scenario == "invalid-webview") minimums["webview2"] = "152.0 beta";
            if (scenario == "zero-webview") minimums["webview2"] = "0.0.0.0";
            if (scenario == "overflow-webview") minimums["webview2"] = "9999999999999999.0";
            var fixture = SignedReleaseFixture.Create(rsa, 1, dependencyMinimums: minimums);
            using var client = new HttpClient(fixture.Handler);
            var root = NewTemporaryDirectory();
            try
            {
                using var service = ManagedService(client, root, rsa);
                if (scenario is "legacy" or "current")
                    Assert.Equal(minimums.Count, (await service.CheckAsync(UpdateCheckReason.Manual)).Release!.DependencyMinimums.Count);
                else
                    await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync(UpdateCheckReason.Manual));
                Assert.Equal(0, fixture.Handler.SetupRequests);
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    private static async Task HelperDependencyVerificationAsync()
    {
        foreach (var result in new[]
        {
            new ProcessExecutionResult(0, "all dependencies ready", "", false),
            new ProcessExecutionResult(1, "", "WebView2 could not initialize", false),
            new ProcessExecutionResult(0, "", "", true),
            new ProcessExecutionResult(0, "partial", "", false, StandardOutputTruncated: true)
        })
        {
            Task Run() => UpdateHelperRunner.VerifyInstalledDependenciesAsync(Path.Combine(Path.GetTempPath(), "path with spaces", "StreamStudio.exe"),
                (info, timeout) =>
                {
                    Assert.Equal(1, info.ArgumentList.Count);
                    Assert.Equal("--maintenance-verify-dependencies", info.ArgumentList[0]);
                    Assert.Equal(TimeSpan.FromSeconds(60), timeout);
                    Assert.Equal(false, info.UseShellExecute);
                    Assert.True(info.CreateNoWindow && info.RedirectStandardOutput && info.RedirectStandardError);
                    return Task.FromResult(result);
                });
            if (result.ExitCode == 0 && !result.TimedOut && !result.OutputWasTruncated) await Run();
            else await Assert.ThrowsAsync<InvalidDataException>(Run);
        }
    }

    private static Task InstalledDependencyCapability()
    {
        var root = NewTemporaryDirectory();
        using var client = new HttpClient();
        try
        {
            using var service = new StagedAppUpdateService(new MemoryLogger(), client, root, Path.Combine(root, "updates"));
            var supports = typeof(StagedAppUpdateService).GetMethod("SupportsDependencyVerification", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Equal(false, (bool)supports.Invoke(service, null)!);
            foreach (var json in new[] { "{}", "{", "{\"dependencyVerificationProtocol\":\"1\"}", "{\"dependencyVerificationProtocol\":2}", "{\"dependencyVerificationProtocol\":1}" })
            {
                File.WriteAllText(Path.Combine(root, "release-metadata.json"), json);
                Assert.Equal(json == "{\"dependencyVerificationProtocol\":1}", (bool)supports.Invoke(service, null)!);
            }
            Assert.Equal(false, Directory.Exists(Path.Combine(root, "lib")));
        }
        finally { Directory.Delete(root, recursive: true); }
        return Task.CompletedTask;
    }
}

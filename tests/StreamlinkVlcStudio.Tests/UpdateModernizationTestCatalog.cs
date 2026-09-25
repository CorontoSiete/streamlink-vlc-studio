using System.Net.Http.Headers;

internal static class UpdateModernizationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("signed updater verifies release and reuses its 24-hour cache", SignedReleaseAndCadenceAsync),
        ("updater trust roots match the release signing key", UpdateTrustRoots),
        ("signed updater rejects tampering newer protocols and asset mismatch", SignedReleaseRejectionsAsync),
        ("update snoozing is version-specific and expires after 24 hours", UpdateSnoozing),
        ("completed updater cleanup removes staged operation files", CompletedUpdateCleanupAsync),
        ("updater retains and revalidates installers after failed or canceled installation", UnsuccessfulInstallRetryAsync),
        ("signed updater refreshes malformed cached release assets", MalformedReleaseCacheAsync),
        ("updater cleans up stalled transfers and distinguishes user cancellation", StalledDownloadRecoveryAsync),
        ("updater keeps downloading while slow transfers make progress", SlowDownloadProgressAsync),
        ("signed updater restores verified downloads across restarts", RestoresPreparedDownloadAsync),
        ("signed updater rejects staged package and metadata tampering", RejectsStagedTamperingAsync),
        ("signed updater recovers from missing truncated and invalid staged packages", InvalidStagedPackageRecoveryAsync),
        ("signed updater removes failed and canceled downloads", FailedDownloadCleanupAsync),
        ("signed updater refreshes future-dated cache after clock rollback", FutureCheckCacheAsync),
        ("signed updater refuses current-version downloads", RefusesCurrentVersionAsync),
        ("signed updater rejects modified helpers before starting a process", RejectsModifiedHelperAsync),
        ("updater skips malformed completion records and reports failed installs", InvalidCompletionRecoveryAsync),
        ("automatic updater continues after failures and throughout long sessions", AutomaticUpdateRetriesAsync),
        ("automatic updater survives unrelated completion cancellation", AutomaticCompletionCancellationAsync),
        ("automatic updater honors disabled and busy states without restarting", AutomaticUpdatePreferencesAsync),
        ("update UI cancels downloads and disables conflicting actions", UpdateDownloadUiAsync),
        ("update UI only closes after the helper actually starts", UpdateLaunchUiAsync),
        ("update helper maps real installer completion codes", UpdateExitCodes),
        ("signed updater keeps a prepared installer after a failed or canceled refresh", PreparedUpdateSurvivesCheckFailureAsync),
        ("signed updater preserves prepared trust when a newer refresh is canceled", PreparedTrustSurvivesLateCancellationAsync),
        ("automatic updater retries preparation failures without installing", AutomaticPreparationRetriesAsync),
        ("automatic update downloads are opt-in and preserve settings", AutomaticDownloadSettings),
        ("automatic updater only downloads eligible managed releases", AutomaticDownloadEligibilityAsync),
        ("automatic updater prepares downloads without restarting", AutomaticDownloadReadyAsync),
        ("automatic updater cancellation pauses that version for the session", AutomaticDownloadCancellationAsync),
        ("automatic updater stops downloading when preferences are disabled", AutomaticDownloadPreferenceChangeAsync),
        ("update UI retries failed downloads without another release check", RetryDownloadUiAsync),
        ("update UI keeps snoozed prepared updates hidden until manual action", SnoozedReadyUpdateUiAsync)
    ];

    internal static IReadOnlyList<(string Name, Func<Task> Run)> RefreshTests { get; } =
    [
        ("signed updater refreshes cached release metadata when retrying a failed download", RetryRefreshesReleaseAsync),
        ("signed updater preserves available download retry and notification actions after failed refresh", FailedRefreshPreservesActionsAsync),
        ("signed updater retry rejects invalid replacement metadata and preserves prior trust", RetryRejectsInvalidReplacementAsync),
        ("update UI checks for newer releases independently of download and install actions", ManualRefreshUiAsync),
        ("update UI blocks manual refresh while busy and recovers after failed checks", ManualRefreshBusyAndFailureAsync),
        ("update UI serializes manual actions while saving snooze preferences", ManualRefreshAdmissionAsync)
    ];

    internal static IReadOnlyList<(string Name, Func<Task> Run)> ReleaseTests { get; } =
    [
        ("updater release protocol and installed path remain compatible with 1.7.0", LegacyUpdateCompatibility)
    ];

    private static async Task RetryRejectsInvalidReplacementAsync()
    {
        using var rsa = RSA.Create(3072);
        var fixture = SignedReleaseFixture.Create(rsa, 1);
        using var client = new HttpClient(fixture.Handler);
        var root = NewTemporaryDirectory();
        try
        {
            using var service = ManagedService(client, root, rsa);
            var original = await service.CheckAsync(UpdateCheckReason.Manual);
            var replacement = SignedReleaseFixture.Create(rsa, 1, corruptSignature: true, version: "1.9.0");
            fixture.Handler.NextRelease = replacement.Handler;
            await Assert.ThrowsAsync<CryptographicException>(() => service.CheckAsync(UpdateCheckReason.Retry));
            Assert.Equal(1, replacement.Handler.ApiRequests);
            Assert.Equal(AppUpdatePhase.Available, service.State.Phase);
            Assert.Equal(original.Release, service.State.Release);
            fixture.Handler.NextRelease = null;
            var prepared = await service.DownloadAsync(original.Release!);
            Assert.Equal(original.Release, prepared.Release);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task ManualRefreshUiAsync()
    {
        foreach (var phase in new[] { AppUpdatePhase.Available, AppUpdatePhase.Ready, AppUpdatePhase.DownloadFailed, AppUpdatePhase.NotifyOnly })
        {
            var updater = new InteractiveUpdateService { CompleteDownload = true, LaunchStarted = true };
            await using var viewModel = CreateUpdateViewModel(updater, () => throw new InvalidOperationException("Unexpected shutdown"));
            viewModel.Settings.Updates.AutomaticDownloadsEnabled = true;
            viewModel.Settings.Updates.SnoozedVersion = "1.8.0";
            viewModel.Settings.Updates.SnoozedUntilUtc = DateTimeOffset.UtcNow.AddDays(1);
            if (phase == AppUpdatePhase.Ready) updater.Prepare();
            else updater.Change(updater.State with { Phase = phase });
            var prepared = updater.State.PreparedUpdate;
            Assert.True(viewModel.IsUpdateRefreshVisible);
            await viewModel.CheckForUpdatesCommand.ExecuteAsync();
            Assert.Equal(1, updater.CheckReasons.Count);
            Assert.Equal(UpdateCheckReason.Manual, updater.CheckReasons[0]);
            Assert.Equal(0, updater.Downloads);
            Assert.Equal(0, updater.Launches);
            Assert.Equal(phase, updater.State.Phase);
            Assert.Equal(prepared, updater.State.PreparedUpdate);
            Assert.Equal("", viewModel.Settings.Updates.SnoozedVersion);
            Assert.Equal<DateTimeOffset?>(null, viewModel.Settings.Updates.SnoozedUntilUtc);
            Assert.True(viewModel.CheckForUpdatesCommand.CanExecute(null));
            Assert.True(viewModel.UpdateAppCommand.CanExecute(null));
        }
    }

    private static async Task ManualRefreshBusyAndFailureAsync()
    {
        var updater = new InteractiveUpdateService();
        await using var viewModel = CreateUpdateViewModel(updater, () => throw new InvalidOperationException("Unexpected shutdown"));
        foreach (var phase in new[] { AppUpdatePhase.Checking, AppUpdatePhase.Downloading, AppUpdatePhase.Verifying, AppUpdatePhase.Launching })
        {
            updater.Change(updater.State with { Phase = phase });
            Assert.Equal(false, viewModel.CheckForUpdatesCommand.CanExecute(null));
            await viewModel.CheckForUpdatesCommand.ExecuteAsync();
        }
        Assert.Equal(0, updater.CheckReasons.Count);
        updater.Prepare();
        var prepared = updater.State.PreparedUpdate;
        updater.CheckFailure = new HttpRequestException("offline");
        await viewModel.CheckForUpdatesCommand.ExecuteAsync();
        Assert.Equal(prepared, updater.State.PreparedUpdate);
        Assert.Equal("Restart and install", viewModel.AppUpdateActionText);
        Assert.True(viewModel.IsUpdateBannerVisible);
        Assert.True(viewModel.CheckForUpdatesCommand.CanExecute(null));
        Assert.True(viewModel.UpdateAppCommand.CanExecute(null));
        Assert.Contains("offline", viewModel.AppUpdateStatus);
        updater.CheckFailure = null;
        await viewModel.CheckForUpdatesCommand.ExecuteAsync();
        Assert.Equal(2, updater.CheckReasons.Count);
    }

    private static async Task ManualRefreshAdmissionAsync()
    {
        var updater = new InteractiveUpdateService { CompleteDownload = true };
        var settings = new AppSettings();
        settings.Updates.AutomaticDownloadsEnabled = true;
        settings.Updates.SnoozedVersion = "1.8.0";
        settings.Updates.SnoozedUntilUtc = DateTimeOffset.UtcNow.AddDays(1);
        var settingsService = new BlockingUpdateSettingsService(settings);
        await using var viewModel = TestViewModels.CreateMain(settings, settingsService, new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(), action => action(),
            appUpdateService: updater, requestShutdown: () => throw new InvalidOperationException("Unexpected shutdown"));
        var check = viewModel.CheckForUpdatesCommand.ExecuteAsync();
        try
        {
            await settingsService.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(false, viewModel.UpdateAppCommand.CanExecute(null));
            Assert.Equal(false, viewModel.LaterUpdateCommand.CanExecute(null));
            await viewModel.UpdateAppCommand.ExecuteAsync();
            await viewModel.CheckForUpdatesCommand.ExecuteAsync();
            await viewModel.PrepareAutomaticUpdateAsync(AvailableResult(updater), CancellationToken.None);
            Assert.Equal(0, updater.CheckReasons.Count);
            Assert.Equal(0, updater.Downloads);
        }
        finally
        {
            settingsService.ContinueSaving.TrySetResult();
            await check.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Equal(1, updater.CheckReasons.Count);
        Assert.True(viewModel.UpdateAppCommand.CanExecute(null));
        Assert.True(viewModel.CheckForUpdatesCommand.CanExecute(null));
    }

    private sealed class BlockingUpdateSettingsService(AppSettings settings) : ISettingsService
    {
        public string SettingsPath => "memory";
        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueSaving { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default)
        {
            SaveStarted.TrySetResult();
            return ContinueSaving.Task.WaitAsync(cancellationToken);
        }
    }

    private static async Task RetryRefreshesReleaseAsync()
    {
        using var rsa = RSA.Create(3072);
        var fixture = SignedReleaseFixture.Create(rsa, 1);
        using var client = new HttpClient(fixture.Handler);
        var root = NewTemporaryDirectory();
        try
        {
            using var service = ManagedService(client, root, rsa);
            var original = await service.CheckAsync(UpdateCheckReason.Manual);
            fixture.Handler.SetupOverride = Encoding.UTF8.GetBytes("evil!-package");
            await Assert.ThrowsAsync<CryptographicException>(() => service.DownloadAsync(original.Release!));

            var replacement = SignedReleaseFixture.Create(rsa, 1, version: "1.9.0");
            fixture.Handler.NextRelease = replacement.Handler;
            var refreshed = await service.CheckAsync(UpdateCheckReason.Retry);
            Assert.Equal(new Version(1, 9, 0), refreshed.Release!.Version);
            Assert.Equal(1, replacement.Handler.ApiRequests);
            var prepared = await service.DownloadAsync(refreshed.Release);
            Assert.Equal(new Version(1, 9, 0), prepared.Release.Version);
            Assert.Equal(1, replacement.Handler.SetupRequests);
            await service.CheckAsync(UpdateCheckReason.Startup);
            Assert.Equal(1, replacement.Handler.ApiRequests);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task FailedRefreshPreservesActionsAsync()
    {
        using var rsa = RSA.Create(3072);
        foreach (var phase in new[] { AppUpdatePhase.Available, AppUpdatePhase.DownloadFailed, AppUpdatePhase.NotifyOnly })
        {
            var fixture = SignedReleaseFixture.Create(rsa, 1);
            using var client = new HttpClient(fixture.Handler);
            var root = NewTemporaryDirectory();
            try
            {
                using var service = new StagedAppUpdateService(new MemoryLogger(), client, root, Path.Combine(root, "updates"),
                    detectInstallKind: () => phase == AppUpdatePhase.NotifyOnly ? AppInstallKind.Zip : AppInstallKind.Managed,
                    getCurrentVersion: () => new Version(1, 7, 0), trustedKey: rsa.ExportParameters(false));
                var check = await service.CheckAsync(UpdateCheckReason.Manual);
                if (phase == AppUpdatePhase.DownloadFailed)
                {
                    fixture.Handler.SetupOverride = Encoding.UTF8.GetBytes("evil!-package");
                    await Assert.ThrowsAsync<CryptographicException>(() => service.DownloadAsync(check.Release!));
                    fixture.Handler.SetupOverride = null;
                }
                fixture.Handler.FailApi = true;
                await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckAsync(UpdateCheckReason.Manual));
                Assert.Equal(phase, service.State.Phase);
                Assert.Equal(check.Release, service.State.Release);
                Assert.Contains("Could not refresh", service.State.Message);
                if (phase != AppUpdatePhase.NotifyOnly)
                {
                    await service.DownloadAsync(service.State.Release!);
                    Assert.Equal(AppUpdatePhase.Ready, service.State.Phase);
                }
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    private static Task LegacyUpdateCompatibility()
    {
        // These values are compiled into released 1.7.0 clients and cannot be migrated
        // by changing the new client's constants alone.
        Assert.Equal("StreamlinkVlcStudio-Setup.exe", StagedAppUpdateService.SetupAssetName);
        Assert.Equal("StreamlinkVlcStudio-release.zip", StagedAppUpdateService.ZipAssetName);
        Assert.Equal("StreamlinkVlcStudio.exe", StreamlinkVlcStudio.Core.AppIdentity.ManagedExecutableName);
        Assert.Equal("StreamlinkVlcStudio", StreamlinkVlcStudio.Core.AppIdentity.UpdateDirectoryName);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StreamlinkVlcStudio.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
        var wix = (System.Xml.Linq.XNamespace)"http://wixtoolset.org/schemas/v4/wxs";
        var package = System.Xml.Linq.XDocument.Load(Path.Combine(root, "scripts", "installer", "StreamlinkVlcStudio.wxs"));
        var installedDirectory = package.Descendants(wix + "Directory").Single(node => (string?)node.Attribute("Id") == "INSTALLFOLDER");
        Assert.Equal("Streamlink VLC Studio", (string?)installedDirectory.Attribute("Name"));
        var executable = package.Descendants(wix + "File").Single(node => (string?)node.Attribute("Id") == "StreamlinkVlcStudioExecutable");
        Assert.Equal("StreamlinkVlcStudio.exe", (string?)executable.Attribute("Name"));
        Assert.Equal(StagedAppUpdateService.AppUpgradeCode.Trim('{', '}'),
            (string?)package.Root!.Element(wix + "Package")!.Attribute("UpgradeCode"));
        var bundle = System.Xml.Linq.XDocument.Load(Path.Combine(root, "scripts", "installer", "StreamlinkVlcStudio.Bundle.wxs"));
        var launch = bundle.Descendants(wix + "Variable").Single(node => (string?)node.Attribute("Name") == "InstalledApplicationPath");
        Assert.Equal(@"[ProgramFiles64Folder]Streamlink VLC Studio\StreamlinkVlcStudio.exe", (string?)launch.Attribute("Value"));
        var install = bundle.Descendants(wix + "MsiProperty").Single(node => (string?)node.Attribute("Name") == "INSTALLFOLDER");
        Assert.Equal(@"[ProgramFiles64Folder]Streamlink VLC Studio", (string?)install.Attribute("Value"));
        return Task.CompletedTask;
    }

    private static Task UpdateTrustRoots()
    {
        string? root = null;
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StreamlinkVlcStudio.sln")))
                directory = directory.Parent;
            if (directory is null) continue;
            root = directory.FullName;
            break;
        }
        if (root is null) throw new InvalidOperationException("Could not locate repository root.");
        using var signingKey = RSA.Create();
        signingKey.ImportFromPem(File.ReadAllText(Path.Combine(root, "shared", "update-signing-public-key.pem")));
        var publicKey = signingKey.ExportSubjectPublicKeyInfo();
        var keyId = Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "shared", "release-contract.json")));
        Assert.Equal(keyId, contract.RootElement.GetProperty("release").GetProperty("manifestSignature").GetProperty("keyId").GetString());
        Assert.Equal(keyId, StagedAppUpdateService.TrustedKeyId);
        using var client = new HttpClient();
        // The constructor computes the identity of the embedded app key independently.
        using var service = new StagedAppUpdateService(new MemoryLogger(), client, root, Path.Combine(root, "updates"), trustedKeyId: keyId);
        var script = File.ReadAllText(Path.Combine(root, "scripts", "install.ps1"));
        string ReadPin(string name)
        {
            var match = System.Text.RegularExpressions.Regex.Match(script, @"(?m)^\$script:" + name + @" = '([^']+)'\r?$");
            Assert.True(match.Success);
            return match.Groups[1].Value;
        }
        Assert.Equal(keyId, ReadPin("UpdateManifestKeyId"));
        using var installerKey = RSA.Create();
        installerKey.ImportParameters(new RSAParameters
        {
            Modulus = Convert.FromBase64String(ReadPin("UpdateManifestPublicModulusBase64")),
            Exponent = [1, 0, 1]
        });
        Assert.True(publicKey.AsSpan().SequenceEqual(installerKey.ExportSubjectPublicKeyInfo()));
        return Task.CompletedTask;
    }

    private static async Task MalformedReleaseCacheAsync()
    {
        using var rsa = RSA.Create(3072);
        var fixture = SignedReleaseFixture.Create(rsa, 1);
        using var client = new HttpClient(fixture.Handler);
        var root = NewTemporaryDirectory();
        try
        {
            using var service = ManagedService(client, root, rsa);
            await service.CheckAsync(UpdateCheckReason.Manual);
            var cachePath = Path.Combine(root, "updates", "last-check.json");
            var cache = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(cachePath))!;
            cache["release"]!["assets"]!.AsArray().Insert(0, null);
            await File.WriteAllTextAsync(cachePath, cache.ToJsonString());

            var result = await service.CheckAsync(UpdateCheckReason.Startup);
            Assert.True(result.IsUpdateAvailable);
            Assert.Equal(2, fixture.Handler.ApiRequests);
            Assert.Equal(AppUpdatePhase.Available, service.State.Phase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task StalledDownloadRecoveryAsync()
    {
        using var rsa = RSA.Create(3072);
        foreach (var cancel in new[] { false, true })
        {
            var fixture = SignedReleaseFixture.Create(rsa, 1);
            using var client = new HttpClient(fixture.Handler);
            using var cancellation = new CancellationTokenSource();
            var root = NewTemporaryDirectory();
            try
            {
                using var service = new StagedAppUpdateService(new MemoryLogger(), client, root, Path.Combine(root, "updates"),
                    detectInstallKind: () => AppInstallKind.Managed,
                    getCurrentVersion: () => new Version(1, 7, 0), trustedKey: rsa.ExportParameters(false),
                    downloadIdleTimeout: cancel ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(100));
                var result = await service.CheckAsync(UpdateCheckReason.Manual);
                using var stream = new PacedDownloadStream(stall: true);
                fixture.Handler.SetupContent = () => new StreamContent(stream);
                var download = service.DownloadAsync(result.Release!, cancellationToken: cancellation.Token);
                await stream.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
                if (cancel)
                {
                    cancellation.Cancel();
                    await Assert.ThrowsAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(2)));
                }
                else
                {
                    await Assert.ThrowsAsync<TimeoutException>(() => download.WaitAsync(TimeSpan.FromSeconds(2)));
                    Assert.True(service.State.Message.Contains("stopped receiving data", StringComparison.Ordinal));
                }
                Assert.Equal(cancel ? AppUpdatePhase.Available : AppUpdatePhase.DownloadFailed, service.State.Phase);
                Assert.True(stream.Disposed);
                Assert.Equal(0, Directory.GetDirectories(Path.Combine(root, "updates", "operations")).Length);
                fixture.Handler.SetupContent = null;
                var retry = await service.DownloadAsync(result.Release!);
                Assert.Equal("setup-package", await File.ReadAllTextAsync(retry.SetupPath));
                Assert.Equal(AppUpdatePhase.Ready, service.State.Phase);
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    private static async Task SlowDownloadProgressAsync()
    {
        using var rsa = RSA.Create(3072);
        var fixture = SignedReleaseFixture.Create(rsa, 1);
        using var client = new HttpClient(fixture.Handler);
        var root = NewTemporaryDirectory();
        try
        {
            using var service = new StagedAppUpdateService(new MemoryLogger(), client, root, Path.Combine(root, "updates"),
                detectInstallKind: () => AppInstallKind.Managed,
                getCurrentVersion: () => new Version(1, 7, 0), trustedKey: rsa.ExportParameters(false),
                downloadIdleTimeout: TimeSpan.FromSeconds(1));
            var result = await service.CheckAsync(UpdateCheckReason.Manual);
            fixture.Handler.SetupContent = () => new StreamContent(new PacedDownloadStream(stall: false));
            var progress = new List<AppUpdateProgress>();
            var prepared = await service.DownloadAsync(result.Release!, new InlineProgress(progress.Add));
            Assert.Equal(AppUpdatePhase.Ready, service.State.Phase);
            Assert.Equal("setup-package", await File.ReadAllTextAsync(prepared.SetupPath));
            Assert.True(progress.Count > 1);
            Assert.Equal(100d, progress[^1].Percentage);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task UnsuccessfulInstallRetryAsync()
    {
        using var rsa = RSA.Create(3072);
        foreach (var outcome in new[] { AppUpdateCompletionOutcome.Canceled, AppUpdateCompletionOutcome.Failed })
            foreach (var scenario in new[] { "valid", "tampered", "expired" })
            {
                var fixture = SignedReleaseFixture.Create(rsa, 1);
                using var client = new HttpClient(fixture.Handler);
                var root = NewTemporaryDirectory();
                try
                {
                    PreparedAppUpdate prepared;
                    using (var service = ManagedService(client, root, rsa))
                    {
                        var check = await service.CheckAsync(UpdateCheckReason.Manual);
                        prepared = await service.DownloadAsync(check.Release!);
                    }
                    var results = Path.Combine(root, "updates", "results");
                    Directory.CreateDirectory(results);
                    var resultPath = Path.Combine(results, prepared.OperationId.ToString("N") + ".json");
                    await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new AppUpdateCompletion(
                        prepared.OperationId, outcome, outcome == AppUpdateCompletionOutcome.Canceled ? 1223 : 1603,
                        null, "Setup did not complete.", DateTimeOffset.UtcNow)));
                    if (scenario == "tampered") await File.WriteAllTextAsync(prepared.SetupPath, "evil!-package");

                    using var restarted = ManagedService(client, root, rsa,
                        now: () => scenario == "expired" ? DateTimeOffset.UtcNow.AddDays(8) : DateTimeOffset.UtcNow);
                    Assert.Equal(outcome, (await restarted.ConsumeCompletionAsync())!.Outcome);
                    Assert.True(File.Exists(prepared.SetupPath));
                    Assert.Equal(false, File.Exists(resultPath));
                    await restarted.CheckAsync(UpdateCheckReason.Startup);
                    Assert.Equal(scenario == "valid" ? AppUpdatePhase.Ready : AppUpdatePhase.Available, restarted.State.Phase);
                    if (scenario == "valid") Assert.Equal(prepared.OperationId, restarted.State.PreparedUpdate!.OperationId);
                    if (scenario == "expired") Assert.Equal(false, Directory.Exists(prepared.OperationDirectory));
                    Assert.Equal(1, fixture.Handler.SetupRequests);
                }
                finally { Directory.Delete(root, recursive: true); }
            }
    }

    private static async Task PreparedTrustSurvivesLateCancellationAsync()
    {
        using var rsa = RSA.Create(3072);
        var fixture = SignedReleaseFixture.Create(rsa, 1);
        using var client = new HttpClient(fixture.Handler);
        using var cancellation = new CancellationTokenSource();
        var root = NewTemporaryDirectory();
        var cancelAtVersionRead = false;
        try
        {
            using var service = new StagedAppUpdateService(new MemoryLogger(), client, root, Path.Combine(root, "updates"),
                detectInstallKind: () => AppInstallKind.Managed,
                getCurrentVersion: () =>
                {
                    if (cancelAtVersionRead) cancellation.Cancel();
                    return new Version(1, 7, 0);
                }, trustedKey: rsa.ExportParameters(false));
            var check = await service.CheckAsync(UpdateCheckReason.Manual);
            var prepared = await service.DownloadAsync(check.Release!);
            fixture.Handler.NextRelease = SignedReleaseFixture.Create(rsa, 1, version: "1.9.0").Handler;
            cancelAtVersionRead = true;
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.CheckAsync(UpdateCheckReason.Manual, cancellation.Token));
            Assert.Equal(AppUpdatePhase.Ready, service.State.Phase);
            Assert.Equal(prepared, service.State.PreparedUpdate);
            cancelAtVersionRead = false;
            var restored = await service.DownloadAsync(prepared.Release);
            Assert.Equal(prepared.OperationId, restored.OperationId);
            Assert.Equal(1, fixture.Handler.SetupRequests);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task InvalidStagedPackageRecoveryAsync()
    {
        using var rsa = RSA.Create(3072);
        foreach (var scenario in new[] { "missing", "truncated", "path" })
        {
            var fixture = SignedReleaseFixture.Create(rsa, 1);
            using var client = new HttpClient(fixture.Handler);
            var root = NewTemporaryDirectory();
            try
            {
                PreparedAppUpdate prepared;
                using (var service = ManagedService(client, root, rsa))
                {
                    var check = await service.CheckAsync(UpdateCheckReason.Manual);
                    prepared = await service.DownloadAsync(check.Release!);
                }
                if (scenario == "missing") File.Delete(prepared.SetupPath);
                else if (scenario == "truncated") await File.WriteAllTextAsync(prepared.SetupPath, "partial");
                else
                {
                    await File.WriteAllTextAsync(Path.Combine(prepared.OperationDirectory, "operation.json"),
                        JsonSerializer.Serialize(prepared with { SetupPath = Path.Combine(root, "outside.exe") }));
                }

                using var restarted = ManagedService(client, root, rsa);
                var result = await restarted.CheckAsync(UpdateCheckReason.Startup);
                Assert.Equal(AppUpdatePhase.Available, restarted.State.Phase);
                var fresh = await restarted.DownloadAsync(result.Release!);
                Assert.True(fresh.OperationId != prepared.OperationId);
                Assert.Equal("setup-package", await File.ReadAllTextAsync(fresh.SetupPath));
                Assert.Equal(2, fixture.Handler.SetupRequests);
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    private static async Task AutomaticCompletionCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new ScheduledUpdateService { CompletionFailure = new OperationCanceledException("Result read interrupted") };
        var waits = 0;
        await new AutomaticUpdateController(service, () => true, _ => { }, _ => { }, new MemoryLogger(),
            (_, _) =>
            {
                if (++waits == 2) cancellation.Cancel();
                return Task.CompletedTask;
            }).RunAsync(cancellation.Token);
        Assert.Equal(1, service.Checks);
    }

    private static async Task SnoozedReadyUpdateUiAsync()
    {
        var updater = new InteractiveUpdateService { LaunchStarted = true };
        var shutdowns = 0;
        await using var viewModel = CreateUpdateViewModel(updater, () => shutdowns++);
        viewModel.Settings.Updates.SnoozedVersion = "1.8.0";
        viewModel.Settings.Updates.SnoozedUntilUtc = DateTimeOffset.UtcNow.AddHours(24);
        updater.Prepare(); // Also models restoring Ready after a refresh throws, without onChecked.
        Assert.Equal(false, viewModel.IsUpdateBannerVisible);
        Assert.Equal("Restart and install", viewModel.AppUpdateActionText);
        await viewModel.UpdateAppCommand.ExecuteAsync();
        Assert.Equal(1, shutdowns);
        Assert.Equal("", viewModel.Settings.Updates.SnoozedVersion);
    }

    private static async Task PreparedUpdateSurvivesCheckFailureAsync()
    {
        using var rsa = RSA.Create(3072);
        var fixture = SignedReleaseFixture.Create(rsa, 1);
        using var client = new HttpClient(fixture.Handler);
        var root = NewTemporaryDirectory();
        try
        {
            using var service = ManagedService(client, root, rsa);
            var check = await service.CheckAsync(UpdateCheckReason.Manual);
            var prepared = await service.DownloadAsync(check.Release!);
            fixture.Handler.FailApi = true;
            await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckAsync(UpdateCheckReason.Manual));
            Assert.Equal(AppUpdatePhase.Ready, service.State.Phase);
            Assert.Equal(prepared, service.State.PreparedUpdate);
            Assert.True(File.Exists(prepared.SetupPath));
            Assert.Contains("still ready", service.State.Message);

            using var cancellation = new CancellationTokenSource();
            service.StateChanged += (_, e) => { if (e.State.Phase == AppUpdatePhase.Checking) cancellation.Cancel(); };
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.CheckAsync(UpdateCheckReason.Manual, cancellation.Token));
            Assert.Equal(AppUpdatePhase.Ready, service.State.Phase);
            Assert.Equal(prepared, service.State.PreparedUpdate);
            Assert.Equal(1, fixture.Handler.SetupRequests);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Task AutomaticDownloadSettings()
    {
        var oldSettings = JsonSerializer.Deserialize<UpdateSettings>("{\"AutomaticChecksEnabled\":true}")!;
        Assert.Equal(false, oldSettings.AutomaticDownloadsEnabled);
        oldSettings.AutomaticDownloadsEnabled = true;
        var restored = JsonSerializer.Deserialize<UpdateSettings>(JsonSerializer.Serialize(oldSettings))!;
        Assert.True(restored.AutomaticDownloadsEnabled);
        return Task.CompletedTask;
    }

    private static AppUpdateCheckResult AvailableResult(InteractiveUpdateService updater,
        AppInstallKind kind = AppInstallKind.Managed, bool notifyOnly = false) =>
        new(true, true, notifyOnly, kind, updater.State.Release, "Available", DateTimeOffset.UtcNow);

    private static async Task AutomaticDownloadEligibilityAsync()
    {
        foreach (var scenario in new[] { "default", "disabled", "zip", "unmanaged", "notify", "snoozed", "current", "ready" })
        {
            var updater = new InteractiveUpdateService { CompleteDownload = true };
            await using var viewModel = CreateUpdateViewModel(updater, () => throw new InvalidOperationException("Unexpected shutdown"));
            viewModel.Settings.Updates.AutomaticDownloadsEnabled = scenario != "default";
            viewModel.Settings.Updates.AutomaticChecksEnabled = scenario != "disabled";
            if (scenario == "snoozed")
            {
                viewModel.Settings.Updates.SnoozedVersion = "1.8.0";
                viewModel.Settings.Updates.SnoozedUntilUtc = DateTimeOffset.UtcNow.AddHours(24);
            }
            if (scenario == "ready") updater.Prepare();
            var result = AvailableResult(updater,
                scenario == "zip" ? AppInstallKind.Zip : scenario == "unmanaged" ? AppInstallKind.Unmanaged : AppInstallKind.Managed,
                scenario == "notify");
            if (scenario == "current") result = result with { IsUpdateAvailable = false };
            await viewModel.PrepareAutomaticUpdateAsync(result, CancellationToken.None);
            Assert.Equal(0, updater.Downloads);
        }
    }

    private static async Task AutomaticDownloadReadyAsync()
    {
        foreach (var kind in new[] { AppInstallKind.Managed, AppInstallKind.LegacyManaged })
        {
            var updater = new InteractiveUpdateService { CompleteDownload = true };
            await using var viewModel = CreateUpdateViewModel(updater, () => throw new InvalidOperationException("Unexpected shutdown"));
            viewModel.Settings.Updates.AutomaticDownloadsEnabled = true;
            await viewModel.PrepareAutomaticUpdateAsync(AvailableResult(updater, kind), CancellationToken.None);
            Assert.Equal(1, updater.Downloads);
            Assert.Equal(AppUpdatePhase.Ready, updater.State.Phase);
            Assert.Equal("Restart and install", viewModel.AppUpdateActionText);
            Assert.Equal(false, viewModel.CanCancelUpdate);
        }
    }

    private static async Task AutomaticDownloadCancellationAsync()
    {
        var updater = new InteractiveUpdateService();
        await using var viewModel = CreateUpdateViewModel(updater, () => throw new InvalidOperationException("Unexpected shutdown"));
        viewModel.Settings.Updates.AutomaticDownloadsEnabled = true;
        var result = AvailableResult(updater);
        var pending = viewModel.PrepareAutomaticUpdateAsync(result, CancellationToken.None);
        await updater.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.CanCancelUpdate);
        viewModel.CancelUpdateCommand.Execute(null);
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.PrepareAutomaticUpdateAsync(result, CancellationToken.None);
        Assert.Equal(1, updater.Downloads);
        updater.CompleteDownload = true;
        await viewModel.UpdateAppCommand.ExecuteAsync();
        Assert.Equal(2, updater.Downloads);
        Assert.Equal(AppUpdatePhase.Ready, updater.State.Phase);
    }

    private static async Task AutomaticDownloadPreferenceChangeAsync()
    {
        foreach (var disableChecks in new[] { false, true })
        {
            var updater = new InteractiveUpdateService();
            await using var viewModel = CreateUpdateViewModel(updater, () => throw new InvalidOperationException("Unexpected shutdown"));
            viewModel.Settings.Updates.AutomaticDownloadsEnabled = true;
            var pending = viewModel.PrepareAutomaticUpdateAsync(AvailableResult(updater), CancellationToken.None);
            await updater.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (disableChecks) viewModel.Settings.Updates.AutomaticChecksEnabled = false;
            else viewModel.Settings.Updates.AutomaticDownloadsEnabled = false;
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(AppUpdatePhase.Available, updater.State.Phase);
            Assert.Equal(false, viewModel.CanCancelUpdate);
            updater.CompleteDownload = true;
            await viewModel.PrepareAutomaticUpdateAsync(AvailableResult(updater), CancellationToken.None);
            Assert.Equal(1, updater.Downloads);
            viewModel.Settings.Updates.AutomaticChecksEnabled = true;
            viewModel.Settings.Updates.AutomaticDownloadsEnabled = true;
            await viewModel.PrepareAutomaticUpdateAsync(AvailableResult(updater), CancellationToken.None);
            Assert.Equal(2, updater.Downloads);
            Assert.Equal(AppUpdatePhase.Ready, updater.State.Phase);
        }
    }

    private static async Task RetryDownloadUiAsync()
    {
        var updater = new InteractiveUpdateService { FailDownload = true };
        await using var viewModel = CreateUpdateViewModel(updater, () => throw new InvalidOperationException("Unexpected shutdown"));
        await viewModel.UpdateAppCommand.ExecuteAsync();
        Assert.Equal("Retry download", viewModel.AppUpdateActionText);
        Assert.True(viewModel.IsUpdateBannerVisible);
        updater.FailDownload = false;
        updater.CompleteDownload = true;
        await viewModel.UpdateAppCommand.ExecuteAsync();
        Assert.Equal(2, updater.Downloads);
        Assert.Equal(AppUpdatePhase.Ready, updater.State.Phase);
    }

    private static async Task AutomaticPreparationRetriesAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new ScheduledUpdateService();
        var delays = new List<TimeSpan>();
        var attempts = 0;
        await new AutomaticUpdateController(service, () => true, _ => { }, _ => { }, new MemoryLogger(),
            (delay, _) =>
            {
                delays.Add(delay);
                if (delays.Count == 5) cancellation.Cancel();
                return Task.CompletedTask;
            },
            (_, _) => ++attempts < 3 ? Task.FromException(new HttpRequestException("download interrupted")) : Task.CompletedTask)
            .RunAsync(cancellation.Token);
        Assert.Equal(4, attempts);
        Assert.Equal(TimeSpan.FromMinutes(15), delays[1]);
        Assert.Equal(TimeSpan.FromMinutes(30), delays[2]);
        Assert.Equal(TimeSpan.FromHours(1), delays[3]);
    }

    private static async Task SignedReleaseAndCadenceAsync()
    {
        using var rsa = RSA.Create(3072);
        var fixture = SignedReleaseFixture.Create(rsa, protocol: 1);
        using var client = new HttpClient(fixture.Handler);
        var root = NewTemporaryDirectory();
        try
        {
            using var service = new StagedAppUpdateService(
                new MemoryLogger(),
                client,
                root,
                Path.Combine(root, "updates"),
                () => new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
                detectInstallKind: () => AppInstallKind.Zip,
                getCurrentVersion: () => new Version(1, 7, 0),
                trustedKey: rsa.ExportParameters(false));

            var first = await service.CheckAsync(UpdateCheckReason.Manual);
            Assert.True(first.IsUpdateAvailable);
            Assert.True(first.IsNotifyOnly);
            Assert.Equal(new Version(1, 8, 0), first.Release!.Version);
            Assert.Equal(AppUpdatePhase.NotifyOnly, service.State.Phase);
            Assert.Equal(1, fixture.Handler.ApiRequests);

            var cached = await service.CheckAsync(UpdateCheckReason.Startup);
            Assert.True(cached.IsUpdateAvailable);
            Assert.Equal(1, fixture.Handler.ApiRequests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SignedReleaseRejectionsAsync()
    {
        using var rsa = RSA.Create(3072);
        foreach (var fixture in new[]
        {
            SignedReleaseFixture.Create(rsa, protocol: 1, corruptSignature: true),
            SignedReleaseFixture.Create(rsa, protocol: 1, wrongKeyId: true),
            SignedReleaseFixture.Create(rsa, protocol: 2),
            SignedReleaseFixture.Create(rsa, protocol: 1, mismatchedSetupLength: true)
        })
        {
            using var client = new HttpClient(fixture.Handler);
            var root = NewTemporaryDirectory();
            try
            {
                using var service = new StagedAppUpdateService(
                    new MemoryLogger(),
                    client,
                    root,
                    Path.Combine(root, "updates"),
                    detectInstallKind: () => AppInstallKind.Managed,
                    getCurrentVersion: () => new Version(1, 7, 0),
                    trustedKey: rsa.ExportParameters(false));
                await Assert.ThrowsAsync<Exception>(() => service.CheckAsync(UpdateCheckReason.Manual));
                Assert.Equal(AppUpdatePhase.Failed, service.State.Phase);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Task UpdateSnoozing()
    {
        var settings = new UpdateSettings
        {
            SnoozedVersion = "1.8.0",
            SnoozedUntilUtc = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero)
        };
        Assert.True(settings.IsSnoozed(new Version(1, 8, 0), new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(false, settings.IsSnoozed(new Version(1, 8, 1), new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(false, settings.IsSnoozed(new Version(1, 8, 0), new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero)));
        return Task.CompletedTask;
    }

    private static async Task CompletedUpdateCleanupAsync()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var updateRoot = Path.Combine(root, "updates");
            var operationId = Guid.NewGuid();
            var operation = Path.Combine(updateRoot, "operations", operationId.ToString("N"));
            var unrelatedOperation = Path.Combine(updateRoot, "operations", Guid.NewGuid().ToString("N"));
            var results = Path.Combine(updateRoot, "results");
            var logs = Path.Combine(updateRoot, "logs");
            Directory.CreateDirectory(Path.Combine(operation, "nested"));
            Directory.CreateDirectory(unrelatedOperation);
            Directory.CreateDirectory(results);
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(operation, StagedAppUpdateService.SetupAssetName), "setup");
            File.WriteAllText(Path.Combine(operation, "StreamStudio.exe"), "helper");
            File.WriteAllText(Path.Combine(operation, "nested", "old-file.dll"), "old");
            File.WriteAllText(Path.Combine(unrelatedOperation, "keep.txt"), "keep");
            var logPath = Path.Combine(logs, operationId.ToString("N") + ".log");
            File.WriteAllText(logPath, "installer log");
            var resultPath = Path.Combine(results, operationId.ToString("N") + ".json");
            var completion = new AppUpdateCompletion(
                operationId,
                AppUpdateCompletionOutcome.Succeeded,
                0,
                logPath,
                "The update was installed successfully.",
                new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(completion));

            using var client = new HttpClient();
            using var service = new StagedAppUpdateService(
                new MemoryLogger(),
                client,
                root,
                updateRoot);

            var consumed = await service.ConsumeCompletionAsync();

            Assert.NotNull(consumed);
            Assert.Equal(operationId, consumed!.OperationId);
            Assert.Equal(false, Directory.Exists(operation));
            Assert.Equal(false, File.Exists(resultPath));
            Assert.Equal("installer log", File.ReadAllText(logPath));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(unrelatedOperation, "keep.txt")));
            Assert.Equal(AppUpdatePhase.Completed, service.State.Phase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task UpdateExitCodes()
    {
        Assert.Equal(AppUpdateCompletionOutcome.Succeeded, UpdateHelperRunner.MapExitCode(0));
        Assert.Equal(AppUpdateCompletionOutcome.SucceededRebootRequired, UpdateHelperRunner.MapExitCode(3010));
        Assert.Equal(AppUpdateCompletionOutcome.SucceededRebootRequired, UpdateHelperRunner.MapExitCode(1641));
        Assert.Equal(AppUpdateCompletionOutcome.Canceled, UpdateHelperRunner.MapExitCode(1602));
        Assert.Equal(AppUpdateCompletionOutcome.Canceled, UpdateHelperRunner.MapExitCode(1223));
        Assert.Equal(AppUpdateCompletionOutcome.Canceled, UpdateHelperRunner.MapExitCode(unchecked((int)0x80070642)));
        Assert.Equal(AppUpdateCompletionOutcome.Canceled, UpdateHelperRunner.MapExitCode(unchecked((int)0x800704C7)));
        Assert.Equal(AppUpdateCompletionOutcome.Failed, UpdateHelperRunner.MapExitCode(5));
        Assert.True(UpdateHelperRunner.TryRun(["--update-helper", "invalid"], out var invalidExit));
        Assert.Equal(1, invalidExit);
        return Task.CompletedTask;
    }

    private static StagedAppUpdateService ManagedService(HttpClient client, string root, RSA rsa, Func<DateTimeOffset>? now = null, Version? version = null) =>
        new(new MemoryLogger(), client, root, Path.Combine(root, "updates"), now,
            detectInstallKind: () => AppInstallKind.Managed,
            getCurrentVersion: () => version ?? new Version(1, 7, 0), trustedKey: rsa.ExportParameters(false));

    private static async Task RestoresPreparedDownloadAsync()
    {
        using var rsa = RSA.Create(3072);
        var fixture = SignedReleaseFixture.Create(rsa, 1);
        using var client = new HttpClient(fixture.Handler);
        var root = NewTemporaryDirectory();
        try
        {
            PreparedAppUpdate prepared;
            using (var first = ManagedService(client, root, rsa))
            {
                var result = await first.CheckAsync(UpdateCheckReason.Manual);
                prepared = await first.DownloadAsync(result.Release!);
                Assert.Equal(AppUpdatePhase.Ready, first.State.Phase);
                var checkedAgain = await first.CheckAsync(UpdateCheckReason.Manual);
                Assert.True(checkedAgain.IsUpdateAvailable);
                Assert.Equal(AppUpdatePhase.Ready, first.State.Phase);
                Assert.Equal(prepared.OperationId, first.State.PreparedUpdate!.OperationId);
            }
            using var restarted = ManagedService(client, root, rsa);
            File.Delete(prepared.HelperPath);
            var check = await restarted.CheckAsync(UpdateCheckReason.Startup);
            Assert.Equal(AppUpdatePhase.Ready, restarted.State.Phase);
            Assert.Equal(prepared.OperationId, restarted.State.PreparedUpdate!.OperationId);
            var repeated = await restarted.DownloadAsync(check.Release!);
            Assert.Equal(prepared.OperationId, repeated.OperationId);
            Assert.Equal(1, fixture.Handler.SetupRequests);
            Assert.Equal(1, Directory.GetDirectories(Path.Combine(root, "updates", "operations")).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task RejectsStagedTamperingAsync()
    {
        using var rsa = RSA.Create(3072);
        foreach (var tamperMetadata in new[] { false, true })
        {
            var fixture = SignedReleaseFixture.Create(rsa, 1);
            using var client = new HttpClient(fixture.Handler);
            var root = NewTemporaryDirectory();
            try
            {
                PreparedAppUpdate prepared;
                using (var service = ManagedService(client, root, rsa))
                {
                    var check = await service.CheckAsync(UpdateCheckReason.Manual);
                    prepared = await service.DownloadAsync(check.Release!);
                }
                var tampered = Encoding.UTF8.GetBytes("evil!-package");
                Assert.Equal(prepared.Release.Setup.Length, tampered.LongLength);
                await File.WriteAllBytesAsync(prepared.SetupPath, tampered);
                if (tamperMetadata)
                {
                    var forged = prepared with
                    {
                        Release = prepared.Release with
                        {
                            Setup = prepared.Release.Setup with { Sha256 = Convert.ToHexString(SHA256.HashData(tampered)).ToLowerInvariant() }
                        }
                    };
                    await File.WriteAllTextAsync(Path.Combine(prepared.OperationDirectory, "operation.json"),
                        JsonSerializer.Serialize(forged, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                }
                using var restarted = ManagedService(client, root, rsa);
                var checkedAgain = await restarted.CheckAsync(UpdateCheckReason.Startup);
                Assert.Equal(AppUpdatePhase.Available, restarted.State.Phase);
                Assert.True(restarted.State.PreparedUpdate is null);
                var fresh = await restarted.DownloadAsync(checkedAgain.Release!);
                Assert.True(fresh.OperationId != prepared.OperationId);
                Assert.Equal("setup-package", await File.ReadAllTextAsync(fresh.SetupPath));
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    private static async Task FailedDownloadCleanupAsync()
    {
        using var rsa = RSA.Create(3072);
        foreach (var cancel in new[] { false, true })
        {
            var fixture = SignedReleaseFixture.Create(rsa, 1);
            using var client = new HttpClient(fixture.Handler);
            using var cancellation = new CancellationTokenSource();
            var root = NewTemporaryDirectory();
            try
            {
                using var service = ManagedService(client, root, rsa);
                var check = await service.CheckAsync(UpdateCheckReason.Manual);
                if (!cancel) fixture.Handler.SetupOverride = Encoding.UTF8.GetBytes("evil!-package");
                var progress = new InlineProgress(_ => { if (cancel) cancellation.Cancel(); });
                await Assert.ThrowsAsync<Exception>(() => service.DownloadAsync(check.Release!, progress, cancellation.Token));
                Assert.Equal(cancel ? AppUpdatePhase.Available : AppUpdatePhase.DownloadFailed, service.State.Phase);
                Assert.Equal(0, Directory.GetDirectories(Path.Combine(root, "updates", "operations")).Length);
                fixture.Handler.SetupOverride = null;
                var prepared = await service.DownloadAsync(check.Release!);
                Assert.Equal("setup-package", await File.ReadAllTextAsync(prepared.SetupPath));
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    private static async Task FutureCheckCacheAsync()
    {
        using var rsa = RSA.Create(3072);
        var fixture = SignedReleaseFixture.Create(rsa, 1);
        using var client = new HttpClient(fixture.Handler);
        var root = NewTemporaryDirectory();
        var now = DateTimeOffset.UtcNow;
        try
        {
            using var service = ManagedService(client, root, rsa, () => now);
            await service.CheckAsync(UpdateCheckReason.Startup);
            now = now.AddHours(-2);
            await service.CheckAsync(UpdateCheckReason.Startup);
            Assert.Equal(2, fixture.Handler.ApiRequests);
            now = now.AddHours(25);
            await service.CheckAsync(UpdateCheckReason.Startup);
            Assert.Equal(3, fixture.Handler.ApiRequests);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task RefusesCurrentVersionAsync()
    {
        using var rsa = RSA.Create(3072);
        var fixture = SignedReleaseFixture.Create(rsa, 1);
        using var client = new HttpClient(fixture.Handler);
        var root = NewTemporaryDirectory();
        try
        {
            using var service = ManagedService(client, root, rsa, version: new Version(1, 8, 0));
            var check = await service.CheckAsync(UpdateCheckReason.Manual);
            Assert.Equal(false, check.IsUpdateAvailable);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(check.Release!));
            Assert.Equal(0, fixture.Handler.SetupRequests);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task AutomaticUpdateRetriesAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new ScheduledUpdateService { FailuresRemaining = 2, FailCompletion = true };
        var delays = new List<TimeSpan>();
        var successes = 0;
        await new AutomaticUpdateController(service, () => true, _ => successes++, _ => { }, new MemoryLogger(),
            (delay, _) =>
            {
                delays.Add(delay);
                if (delays.Count == 5) cancellation.Cancel();
                return Task.CompletedTask;
            }).RunAsync(cancellation.Token);
        Assert.Equal(4, service.Checks);
        Assert.Equal(2, successes);
        Assert.Equal(TimeSpan.FromSeconds(20), delays[0]);
        Assert.Equal(TimeSpan.FromMinutes(15), delays[1]);
        Assert.Equal(TimeSpan.FromMinutes(30), delays[2]);
        Assert.Equal(TimeSpan.FromHours(1), delays[3]);
        Assert.Equal(TimeSpan.FromHours(1), delays[4]);
    }

    private static async Task RejectsModifiedHelperAsync()
    {
        using var rsa = RSA.Create(3072);
        var fixture = SignedReleaseFixture.Create(rsa, 1);
        using var client = new HttpClient(fixture.Handler);
        var root = NewTemporaryDirectory();
        try
        {
            using var service = ManagedService(client, root, rsa);
            var check = await service.CheckAsync(UpdateCheckReason.Manual);
            var prepared = await service.DownloadAsync(check.Release!);
            await File.WriteAllTextAsync(prepared.HelperPath, "untrusted executable");
            await Assert.ThrowsAsync<CryptographicException>(() => service.ApplyAndRestartAsync(prepared));
            Assert.Equal(AppUpdatePhase.Failed, service.State.Phase);
            Assert.Equal(false, Directory.Exists(Path.Combine(root, "updates", "results")));
            await service.CheckAsync(UpdateCheckReason.Manual);
            Assert.Equal(AppUpdatePhase.Ready, service.State.Phase);
            Assert.Equal(prepared.OperationId, service.State.PreparedUpdate!.OperationId);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task InvalidCompletionRecoveryAsync()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var results = Path.Combine(root, "updates", "results");
            Directory.CreateDirectory(results);
            var id = Guid.NewGuid();
            var completion = new AppUpdateCompletion(id, AppUpdateCompletionOutcome.Failed, 5, null, "Setup was refused.", DateTimeOffset.UtcNow);
            var good = Path.Combine(results, id.ToString("N") + ".json");
            await File.WriteAllTextAsync(good, JsonSerializer.Serialize(completion));
            File.SetLastWriteTimeUtc(good, DateTime.UtcNow.AddMinutes(-1));
            await File.WriteAllTextAsync(Path.Combine(results, Guid.NewGuid().ToString("N") + ".json"), "{invalid json");
            foreach (var scenario in new[] { "identity", "empty-id", "outcome", "message" })
            {
                var invalidId = Guid.NewGuid();
                var record = completion with { OperationId = invalidId };
                record = scenario switch
                {
                    "identity" => record with { OperationId = id },
                    "empty-id" => record with { OperationId = Guid.Empty },
                    "outcome" => record with { Outcome = (AppUpdateCompletionOutcome)999 },
                    _ => record with { Message = "" }
                };
                await File.WriteAllTextAsync(Path.Combine(results, invalidId.ToString("N") + ".json"), JsonSerializer.Serialize(record));
            }
            await File.WriteAllTextAsync(Path.Combine(results, Guid.NewGuid().ToString("N") + ".json"), "null");
            await File.WriteAllTextAsync(Path.Combine(results, Guid.NewGuid().ToString("N") + ".json"), "");
            using var client = new HttpClient();
            using var service = new StagedAppUpdateService(new MemoryLogger(), client, root, Path.Combine(root, "updates"));
            var result = await service.ConsumeCompletionAsync();
            Assert.Equal(id, result!.OperationId);
            Assert.Equal(AppUpdatePhase.Failed, service.State.Phase);
            Assert.Equal(0, Directory.GetFiles(results).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task AutomaticUpdatePreferencesAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new ScheduledUpdateService();
        var enabled = false;
        var waits = 0;
        var completions = 0;
        await new AutomaticUpdateController(service, () => enabled, _ => { }, _ => completions++, new MemoryLogger(),
            (_, _) =>
            {
                waits++;
                if (waits == 2)
                {
                    Assert.Equal(0, service.Checks);
                    enabled = true;
                    service.State = new(AppUpdatePhase.Downloading, "busy");
                }
                if (waits == 3)
                {
                    Assert.Equal(0, service.Checks);
                    service.State = AppUpdateState.Idle;
                }
                if (waits == 4) cancellation.Cancel();
                return Task.CompletedTask;
            }).RunAsync(cancellation.Token);
        Assert.Equal(1, completions);
        Assert.Equal(1, service.Checks);
    }

    private sealed class InlineProgress(Action<AppUpdateProgress> report) : IProgress<AppUpdateProgress>
    {
        public void Report(AppUpdateProgress value) => report(value);
    }

    private sealed class PacedDownloadStream(bool stall) : MemoryStream(Encoding.UTF8.GetBytes("setup-package"))
    {
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stall && Position > 0)
            {
                Stalled.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (!stall && Position < Length) await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static async Task UpdateDownloadUiAsync()
    {
        var updater = new InteractiveUpdateService();
        await using var viewModel = CreateUpdateViewModel(updater, () => throw new InvalidOperationException("Unexpected shutdown"));
        var pending = viewModel.UpdateAppCommand.ExecuteAsync();
        await updater.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.IsUpdateBannerVisible);
        Assert.True(viewModel.CanCancelUpdate);
        Assert.Equal(false, viewModel.UpdateAppCommand.CanExecute(null));
        Assert.Equal(false, viewModel.CheckForUpdatesCommand.CanExecute(null));
        Assert.Equal(false, viewModel.LaterUpdateCommand.CanExecute(null));
        viewModel.CancelUpdateCommand.Execute(null);
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(false, viewModel.CanCancelUpdate);
        Assert.True(viewModel.UpdateAppCommand.CanExecute(null));
        Assert.True(viewModel.CheckForUpdatesCommand.CanExecute(null));
        Assert.Equal("Download update", viewModel.AppUpdateActionText);
        Assert.Equal("Canceled", viewModel.AppUpdateStatus);
    }

    private static async Task UpdateLaunchUiAsync()
    {
        foreach (var started in new[] { false, true })
        {
            var updater = new InteractiveUpdateService { LaunchStarted = started };
            updater.Prepare();
            var shutdowns = 0;
            await using var viewModel = CreateUpdateViewModel(updater, () => shutdowns++);
            await viewModel.UpdateAppCommand.ExecuteAsync();
            Assert.Equal(started ? 1 : 0, shutdowns);
            Assert.Equal(started ? "Started" : "Could not start", viewModel.AppUpdateStatus);
        }
    }

    private static MainViewModel CreateUpdateViewModel(IAppUpdateService updater, Action shutdown)
    {
        var settings = new AppSettings();
        return TestViewModels.CreateMain(settings, new FakeSettingsService(settings), new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(), action => action(),
            appUpdateService: updater, requestShutdown: shutdown);
    }

    private sealed class InteractiveUpdateService : IAppUpdateService
    {
        private static readonly AppUpdateAsset Asset = new("setup.exe", new Uri("https://example.test/setup"), 1, new string('0', 64));
        private static readonly AppUpdateRelease Release = new(new Version(1, 8, 0), "v1.8.0", new string('a', 40), "test/repo",
            new Uri("https://example.test/release"), 1, Asset, Asset, new Dictionary<string, string>());
        public event EventHandler<AppUpdateStateChangedEventArgs>? StateChanged;
        public AppUpdateState State { get; private set; } = new(AppUpdatePhase.Available, "Available", Release);
        public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool LaunchStarted { get; init; }
        public bool CompleteDownload { get; set; }
        public bool FailDownload { get; set; }
        public int Downloads { get; private set; }
        public int Launches { get; private set; }
        public List<UpdateCheckReason> CheckReasons { get; } = [];
        public Exception? CheckFailure { get; set; }
        public Task<AppUpdateCheckResult> CheckAsync(UpdateCheckReason reason, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckReasons.Add(reason);
            var previous = State;
            Change(new(AppUpdatePhase.Checking, "Checking"));
            Change(previous);
            if (CheckFailure is { } failure) return Task.FromException<AppUpdateCheckResult>(failure);
            return Task.FromResult(new AppUpdateCheckResult(true, true, previous.Phase == AppUpdatePhase.NotifyOnly,
                previous.Phase == AppUpdatePhase.NotifyOnly ? AppInstallKind.Zip : AppInstallKind.Managed,
                previous.Release, previous.Message, DateTimeOffset.UtcNow));
        }
        public async Task<PreparedAppUpdate> DownloadAsync(AppUpdateRelease release, IProgress<AppUpdateProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Downloads++;
            Change(new(AppUpdatePhase.Downloading, "Downloading", release));
            DownloadStarted.TrySetResult();
            if (FailDownload)
            {
                Change(new(AppUpdatePhase.DownloadFailed, "Connection lost", release));
                throw new HttpRequestException("Connection lost");
            }
            if (CompleteDownload)
            {
                Prepare();
                return State.PreparedUpdate!;
            }
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException)
            {
                Change(new(AppUpdatePhase.Available, "Canceled", release));
                throw;
            }
            throw new InvalidOperationException("The download must be canceled.");
        }
        public void Prepare() => Change(new(AppUpdatePhase.Ready, "Ready", Release,
            new(Guid.NewGuid(), Release, "test", "test", "test", DateTimeOffset.UtcNow)));
        public Task<AppUpdateLaunchResult> ApplyAndRestartAsync(PreparedAppUpdate update, CancellationToken cancellationToken = default)
        {
            Launches++;
            return Task.FromResult(new AppUpdateLaunchResult(LaunchStarted, LaunchStarted ? "Started" : "Could not start"));
        }
        public void Change(AppUpdateState value)
        {
            State = value;
            StateChanged?.Invoke(this, new(value));
        }
    }

    private sealed class ScheduledUpdateService : IAppUpdateService
    {
        public AppUpdateState State { get; set; } = AppUpdateState.Idle;
        public int Checks { get; private set; }
        public int FailuresRemaining { get; set; }
        public bool FailCompletion { get; init; }
        public Exception? CompletionFailure { get; init; }
        public Task<AppUpdateCheckResult> CheckAsync(UpdateCheckReason reason, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Checks++;
            if (FailuresRemaining-- > 0) throw new HttpRequestException("offline");
            return Task.FromResult(new AppUpdateCheckResult(true, false, false, AppInstallKind.Managed, null, "current", DateTimeOffset.UtcNow));
        }
        public Task<AppUpdateCompletion?> ConsumeCompletionAsync(CancellationToken cancellationToken = default) => CompletionFailure is { } failure
            ? Task.FromException<AppUpdateCompletion?>(failure)
            : FailCompletion
            ? Task.FromException<AppUpdateCompletion?>(new JsonException("invalid result"))
            : Task.FromResult<AppUpdateCompletion?>(new(Guid.NewGuid(), AppUpdateCompletionOutcome.Succeeded, 0, null, "installed", DateTimeOffset.UtcNow));
    }

    private static string NewTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "StreamStudio-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record SignedReleaseFixture(UpdateHttpHandler Handler)
    {
        public static SignedReleaseFixture Create(
            RSA rsa,
            int protocol,
            bool corruptSignature = false,
            bool mismatchedSetupLength = false,
            bool wrongKeyId = false,
            string version = "1.8.0")
        {
            var setup = Encoding.UTF8.GetBytes("setup-package");
            var zip = Encoding.UTF8.GetBytes("zip-package");
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                protocolVersion = protocol,
                channel = "stable",
                prerelease = false,
                version,
                tag = "v" + version,
                commit = new string('a', 40),
                repository = "CorontoSiete/streamlink-vlc-studio",
                releasePage = "https://github.com/CorontoSiete/streamlink-vlc-studio/releases/tag/v" + version,
                keyId = wrongKeyId
                    ? new string('0', 64)
                    : Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant(),
                dependencyMinimums = new Dictionary<string, string> { ["streamlink"] = "8.2.1", ["vlc"] = "3.0.23" },
                setup = new { name = StagedAppUpdateService.SetupAssetName, length = setup.LongLength, sha256 = Convert.ToHexString(SHA256.HashData(setup)).ToLowerInvariant() },
                zip = new { name = StagedAppUpdateService.ZipAssetName, length = zip.LongLength, sha256 = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant() }
            });
            var signature = rsa.SignData(manifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            if (corruptSignature) signature[0] ^= 0x80;
            var handler = new UpdateHttpHandler(manifest, signature, setup, zip, mismatchedSetupLength, version);
            return new(handler);
        }
    }

    private sealed class UpdateHttpHandler(
        byte[] manifest,
        byte[] signature,
        byte[] setup,
        byte[] zip,
        bool mismatchedSetupLength,
        string version) : HttpMessageHandler
    {
        public int ApiRequests { get; private set; }
        public int SetupRequests { get; private set; }
        public byte[]? SetupOverride { get; set; }
        public Func<HttpContent>? SetupContent { get; set; }
        public bool FailApi { get; set; }
        public UpdateHttpHandler? NextRelease { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (NextRelease is { } next) return next.SendAsync(request, cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                ApiRequests++;
                if (FailApi) throw new HttpRequestException("offline");
                var setupSize = setup.LongLength + (mismatchedSetupLength ? 1 : 0);
                var json = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    tag_name = "v" + version,
                    draft = false,
                    prerelease = false,
                    assets = new object[]
                    {
                        Asset(StagedAppUpdateService.UpdateManifestName, manifest.LongLength, "manifest"),
                        Asset(StagedAppUpdateService.UpdateSignatureName, signature.LongLength, "signature"),
                        Asset(StagedAppUpdateService.SetupAssetName, setupSize, "setup"),
                        Asset(StagedAppUpdateService.ZipAssetName, zip.LongLength, "zip")
                    }
                });
                var apiResponse = Response(json, "application/json");
                apiResponse.RequestMessage = request;
                return Task.FromResult(apiResponse);
            }

            if (path == "/setup") SetupRequests++;
            var response = path switch
            {
                "/manifest" => Response(manifest),
                "/signature" => Response(signature),
                "/setup" => SetupContent is { } createContent
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = createContent() }
                    : Response(SetupOverride ?? setup),
                "/zip" => Response(zip),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private static object Asset(string name, long size, string path) => new
        {
            name,
            size,
            browser_download_url = "https://downloads.example/" + path
        };

        private static HttpResponseMessage Response(byte[] bytes, string mediaType = "application/octet-stream")
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}

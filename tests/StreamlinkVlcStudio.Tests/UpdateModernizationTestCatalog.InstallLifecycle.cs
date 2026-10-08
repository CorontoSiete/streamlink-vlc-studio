internal static partial class UpdateModernizationTestCatalog
{
    private static async Task InterruptedApplyRecoversAsync()
    {
        using var rsa = RSA.Create(3072);
        foreach (var scenario in new[] { "valid", "corrupt", "expired" })
        {
            var fixture = SignedReleaseFixture.Create(rsa, 1);
            using var client = new HttpClient(fixture.Handler);
            var root = NewTemporaryDirectory();
            var now = DateTimeOffset.UtcNow;
            try
            {
                PreparedAppUpdate? prepared = null;
                var launches = 0;
                using (var initial = ManagedService(client, root, rsa, () => now, startUpdateHelper: info =>
                {
                    launches++;
                    Assert.Equal(prepared!.HelperPath, info.FileName);
                    Assert.Equal(prepared.OperationDirectory, info.WorkingDirectory);
                    Assert.True(info.UseShellExecute);
                    var pending = ReadInstallIntent(Path.Combine(root, "updates", "pending-repair.json"));
                    var marker = ReadInstallIntent(Path.Combine(prepared.OperationDirectory, "failure.json"));
                    Assert.Equal(prepared.OperationId, pending.OperationId);
                    Assert.Equal(prepared.Release.Version, pending.TargetVersion);
                    Assert.Equal(pending, marker);
                    Assert.Equal(AppUpdateCompletionOutcome.Failed, pending.Outcome);
                }))
                {
                    prepared = await initial.DownloadAsync((await initial.CheckAsync(UpdateCheckReason.Manual)).Release!);
                    Assert.True((await initial.ApplyAndRestartAsync(prepared)).Started);
                    Assert.Equal(1, launches);
                    Assert.Equal(AppUpdatePhase.Launching, initial.State.Phase);
                }
                if (scenario == "corrupt") await File.WriteAllTextAsync(prepared.SetupPath, "tampered installer");
                if (scenario == "expired") now = now.AddDays(8);
                // The executable was replaced, then the helper died before writing a result.
                using var restarted = ManagedService(client, root, rsa, () => now, prepared.Release.Version,
                    verifyInstallation: _ => Task.FromResult(false));
                Assert.True(await restarted.ConsumeCompletionAsync() is null);
                var check = await restarted.CheckAsync(UpdateCheckReason.Startup);
                Assert.True(check.IsUpdateAvailable);
                Assert.Equal(scenario == "valid" ? AppUpdatePhase.Ready : AppUpdatePhase.Available, restarted.State.Phase);
                Assert.True(File.Exists(Path.Combine(root, "updates", "pending-repair.json")));
                var retry = await restarted.DownloadAsync(check.Release!);
                Assert.Equal(prepared.Release.Version, retry.Release.Version);
                if (scenario == "valid") Assert.Equal(prepared.OperationId, retry.OperationId);
                else Assert.True(retry.OperationId != prepared.OperationId);
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    private static async Task RecoveryRecordFailurePreventsLaunchAsync()
    {
        using var rsa = RSA.Create(3072);
        foreach (var lockedRecord in new[] { "operation", "repair" })
        {
            var fixture = SignedReleaseFixture.Create(rsa, 1);
            using var client = new HttpClient(fixture.Handler);
            var root = NewTemporaryDirectory();
            try
            {
                var launches = 0;
                using var service = ManagedService(client, root, rsa, startUpdateHelper: _ => launches++);
                var prepared = await service.DownloadAsync((await service.CheckAsync(UpdateCheckReason.Manual)).Release!);
                var path = lockedRecord == "operation" ? Path.Combine(prepared.OperationDirectory, "failure.json")
                    : Path.Combine(root, "updates", "pending-repair.json");
                await File.WriteAllTextAsync(path, "previous record");
                using (var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await Assert.ThrowsAsync<IOException>(() => service.ApplyAndRestartAsync(prepared));
                    Assert.Equal(0, launches);
                    Assert.Equal(AppUpdatePhase.Ready, service.State.Phase);
                    Assert.Equal(prepared, service.State.PreparedUpdate);
                    Assert.True(File.Exists(prepared.SetupPath));
                    Assert.Equal(0, Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Length);
                }
                Assert.Equal("previous record", File.ReadAllText(path));
                Assert.True((await service.ApplyAndRestartAsync(prepared)).Started);
                Assert.Equal(1, launches);
                Assert.Equal(prepared.OperationId, ReadInstallIntent(path).OperationId);
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    private static async Task HelperLaunchFailureRemainsRetryableAsync()
    {
        using var rsa = RSA.Create(3072);
        var fixture = SignedReleaseFixture.Create(rsa, 1);
        using var client = new HttpClient(fixture.Handler);
        var root = NewTemporaryDirectory();
        try
        {
            var launches = 0;
            using var service = ManagedService(client, root, rsa, startUpdateHelper: _ =>
            {
                if (++launches == 1) throw new System.ComponentModel.Win32Exception(1223);
            });
            var prepared = await service.DownloadAsync((await service.CheckAsync(UpdateCheckReason.Manual)).Release!);
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => service.ApplyAndRestartAsync(prepared));
            Assert.Equal(AppUpdatePhase.Ready, service.State.Phase);
            Assert.Equal(prepared, service.State.PreparedUpdate);
            Assert.Equal(prepared.OperationId, ReadInstallIntent(Path.Combine(root, "updates", "pending-repair.json")).OperationId);
            Assert.True((await service.ApplyAndRestartAsync(prepared)).Started);
            Assert.Equal(2, launches);
            Assert.Equal(1, fixture.Handler.SetupRequests);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task SuccessClearsInstallIntentAsync()
    {
        using var rsa = RSA.Create(3072);
        var fixture = SignedReleaseFixture.Create(rsa, 1);
        using var client = new HttpClient(fixture.Handler);
        var root = NewTemporaryDirectory();
        try
        {
            PreparedAppUpdate prepared;
            using (var service = ManagedService(client, root, rsa, startUpdateHelper: _ => { }))
            {
                prepared = await service.DownloadAsync((await service.CheckAsync(UpdateCheckReason.Manual)).Release!);
                await service.ApplyAndRestartAsync(prepared);
            }
            await WriteDependencyCompletionAsync(root, prepared, AppUpdateCompletionOutcome.Succeeded, DateTimeOffset.UtcNow);
            using var restarted = ManagedService(client, root, rsa, version: prepared.Release.Version,
                verifyInstallation: _ => throw new InvalidOperationException("A successful completion must supersede the install intent."));
            Assert.Equal(AppUpdateCompletionOutcome.Succeeded, (await restarted.ConsumeCompletionAsync())!.Outcome);
            Assert.Equal(false, File.Exists(Path.Combine(root, "updates", "pending-repair.json")));
            Assert.Equal(false, Directory.Exists(prepared.OperationDirectory));
            Assert.Equal(false, (await restarted.CheckAsync(UpdateCheckReason.Startup)).IsUpdateAvailable);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static AppUpdateCompletion ReadInstallIntent(string path) =>
        JsonSerializer.Deserialize<AppUpdateCompletion>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
}

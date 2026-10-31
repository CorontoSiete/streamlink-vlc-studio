internal static partial class VodDownloadTestCatalog
{
    private static async Task DownloadPromotionLockAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.DownloadChanged += item =>
        {
            if (item.CompletedSegments == 1) firstWritten.TrySetResult();
        };
        fixture.Handler.Override = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("segment-b.ts", StringComparison.Ordinal))
                await release.Task.WaitAsync(token);
            return null;
        };
        var queued = await fixture.EnqueueAsync();
        await firstWritten.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var partial = Path.Combine(Path.GetDirectoryName(fixture.Record(queued.Id))!, ".partial");
        var denied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (args.Exception.HResult == unchecked((int)0x80070005) &&
                args.Exception.Message.Contains("'" + partial + "'", StringComparison.OrdinalIgnoreCase))
                denied.TrySetResult();
        }
        AppDomain.CurrentDomain.FirstChanceException += OnException;
        try
        {
            await using (var locked = new FileStream(Path.Combine(partial, "asset-000000.ts"),
                FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                release.TrySetResult();
                await denied.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            var completed = await fixture.WaitAsync(queued.Id, VodDownloadState.Completed);
            Assert.Equal(completed.LocalMediaPath, (await fixture.Service.GetOfflineTargetAsync(queued.Id)).LocalMediaPath);
            Assert.True(!Directory.Exists(partial));
        }
        finally
        {
            release.TrySetResult();
            AppDomain.CurrentDomain.FirstChanceException -= OnException;
        }
    }

    private static async Task InterruptedDownloadCleanupAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        var item = await fixture.DownloadAsync();
        await fixture.Service.DisposeAsync();
        var itemDirectory = Path.GetDirectoryName(fixture.Record(item.Id))!;
        Directory.Move(Path.Combine(itemDirectory, "media"), Path.Combine(itemDirectory, ".partial"));
        var conversion = Path.Combine(itemDirectory, ".conversion");
        Directory.CreateDirectory(conversion);
        await File.WriteAllTextAsync(Path.Combine(conversion, "vod.mp4"), "unfinished conversion");
        await File.WriteAllTextAsync(fixture.Record(item.Id), JsonSerializer.Serialize(item with
        {
            State = VodDownloadState.Downloading,
            LocalMediaPath = ""
        }));
        await fixture.RestartAsync();
        var restored = (await fixture.Service.GetDownloadsAsync()).Single();
        Assert.Equal(VodDownloadState.Interrupted, restored.State);
        Assert.True(!Directory.Exists(Path.Combine(itemDirectory, ".partial")),
            "Restart left the interrupted download's video files on disk.");
        Assert.True(!Directory.Exists(conversion));
        Assert.True(File.Exists(fixture.Record(item.Id)));
    }

    private static async Task StartupMediaCleanupAsync(bool damageCurrent)
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var item = await fixture.DownloadAsync();
        await fixture.Service.DisposeAsync();
        var itemDirectory = Path.GetDirectoryName(fixture.Record(item.Id))!;
        var obsolete = Path.Combine(itemDirectory, "media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(obsolete);
        var oldFile = Path.Combine(obsolete, "old-video.ts");
        await File.WriteAllTextAsync(oldFile, "old generation");
        File.SetAttributes(oldFile, FileAttributes.ReadOnly);
        var unrelated = Path.Combine(itemDirectory, "media-personal-backup");
        Directory.CreateDirectory(unrelated);
        await File.WriteAllTextAsync(Path.Combine(unrelated, "personal.mp4"), "keep");
        foreach (var folder in new[] { ".partial", ".conversion" })
        {
            Directory.CreateDirectory(Path.Combine(itemDirectory, folder));
            await File.WriteAllTextAsync(Path.Combine(itemDirectory, folder, "video.tmp"), "interrupted");
        }
        if (damageCurrent) await File.AppendAllTextAsync(item.LocalMediaPath, "changed playlist");
        try
        {
            await fixture.RestartAsync();
            var restored = (await fixture.Service.GetDownloadsAsync()).Single();
            Assert.Equal(damageCurrent ? VodDownloadState.Failed : VodDownloadState.Completed, restored.State);
            Assert.Equal(damageCurrent, Directory.Exists(obsolete));
            Assert.True(!Directory.Exists(Path.Combine(itemDirectory, ".partial")));
            Assert.True(!Directory.Exists(Path.Combine(itemDirectory, ".conversion")));
            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(unrelated, "personal.mp4")));
            Assert.True(File.Exists(item.LocalMediaPath));
            if (!damageCurrent) await fixture.Service.GetOfflineTargetAsync(item.Id);
        }
        finally
        {
            if (File.Exists(oldFile)) File.SetAttributes(oldFile, FileAttributes.Normal);
        }
    }

    private static async Task LockedInterruptedCleanupAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        var item = await fixture.DownloadAsync();
        await fixture.Service.DisposeAsync();
        var directory = Path.GetDirectoryName(fixture.Record(item.Id))!;
        var partial = Path.Combine(directory, ".partial");
        Directory.Move(Path.Combine(directory, "media"), partial);
        await File.WriteAllTextAsync(fixture.Record(item.Id), JsonSerializer.Serialize(item with { State = VodDownloadState.Downloading }));
        await using var locked = new FileStream(Path.Combine(partial, "asset-000000.ts"),
            FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var service = new VodDownloadService(fixture.Library, fixture.Resolver, fixture.Logger,
            null, fixture.Client, null, cleanupInterval: TimeSpan.FromMilliseconds(25));
        Assert.Equal(VodDownloadState.Interrupted, (await service.GetDownloadsAsync()).Single().State);
        Assert.True(Directory.Exists(partial));
        Assert.True(!File.Exists(Path.Combine(partial, "asset-000001.ts")));
        await locked.DisposeAsync();
        await TestWait.UntilAsync(() => !Directory.Exists(partial), TimeSpan.FromSeconds(3));
        Assert.True(File.Exists(fixture.Record(item.Id)));
    }

    private static async Task CleanupJunctionAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var item = await fixture.DownloadAsync();
        await fixture.Service.DisposeAsync();
        var itemDirectory = Path.GetDirectoryName(fixture.Record(item.Id))!;
        var external = Path.Combine(fixture.Root, "external-videos");
        Directory.CreateDirectory(external);
        var externalFile = Path.Combine(external, "keep.mp4");
        await File.WriteAllTextAsync(externalFile, "keep external media");
        var junction = Path.Combine(itemDirectory, "media-" + Guid.NewGuid().ToString("N"));
        try
        {
            var info = BoundedProcessRunner.CreateRedirectedStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoProfile", "-NonInteractive", "-Command",
                    "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path $env:SVS_TEST_JUNCTION_PATH -Target $env:SVS_TEST_JUNCTION_TARGET | Out-Null"]);
            info.Environment["SVS_TEST_JUNCTION_PATH"] = junction;
            info.Environment["SVS_TEST_JUNCTION_TARGET"] = external;
            var result = await new BoundedProcessRunner().RunAsync(info, TimeSpan.FromSeconds(10));
            Assert.True(!result.TimedOut && result.ExitCode == 0, result.StandardError);
            var obsolete = Path.Combine(itemDirectory, "media-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(obsolete);
            await File.WriteAllTextAsync(Path.Combine(obsolete, "old.mp4"), "unused");
            await fixture.RestartAsync();
            Assert.Equal(VodDownloadState.Completed, (await fixture.Service.GetDownloadsAsync()).Single().State);
            Assert.True(!Directory.Exists(obsolete));
            Assert.True(Directory.Exists(junction));
            Assert.Equal("keep external media", await File.ReadAllTextAsync(externalFile));
            await fixture.Service.GetOfflineTargetAsync(item.Id);
            await Assert.ThrowsAsync<IOException>(() => fixture.Service.RemoveAsync(item.Id));
            Assert.True(File.Exists(item.LocalMediaPath));
            Assert.Equal("keep external media", await File.ReadAllTextAsync(externalFile));
        }
        finally
        {
            // Remove only the junction itself, never recurse into its target.
            if (Directory.Exists(junction)) Directory.Delete(junction);
        }
    }
}

using DownloadFixture = VodDownloadTestCatalog.DownloadFixture;

internal static partial class VodDownloadFormatTestCatalog
{
    private static async Task UnusedConversionCleanupAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var original = await fixture.DownloadAsync();
        await fixture.Service.DisposeAsync();
        await using var service = CreateService(fixture, new TestConverter());
        await service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        var completed = await WaitForFormatAsync(service, original.Id, VodDownloadFormat.Mp4);
        Assert.True(!Directory.Exists(Path.GetDirectoryName(original.LocalMediaPath)!),
            "Conversion left an unused original video on disk until restart.");
        Assert.True(File.Exists(completed.LocalMediaPath));
        var stored = JsonSerializer.Deserialize<VodDownloadItem>(await File.ReadAllTextAsync(fixture.Record(original.Id)))!;
        Assert.Equal(completed.MediaFolder, stored.MediaFolder);
        Assert.Equal(completed.LocalMediaPath, (await service.GetOfflineTargetAsync(original.Id)).LocalMediaPath);
    }

    private static async Task DownloadFormatCommitFailureAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        await fixture.Service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Override = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("segment-a.ts", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return null;
        };
        var item = await fixture.EnqueueAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var directory = Path.GetDirectoryName(fixture.Record(item.Id))!;
        var obsolete = Path.Combine(directory, "media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(obsolete);
        await File.WriteAllTextAsync(Path.Combine(obsolete, "old.mp4"), "recoverable older copy");
        await using (var locked = new FileStream(fixture.Record(item.Id), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            release.TrySetResult();
            var failed = await fixture.WaitAsync(item.Id, VodDownloadState.Failed);
            Assert.True(!failed.IsActive, "A failed metadata commit stranded a download as active.");
            Assert.Equal("", failed.LocalMediaPath);
            Assert.True(File.Exists(Path.Combine(directory, "media", OfflineVodPackage.ManifestName)));
            Assert.True(Directory.Exists(obsolete), "Cleanup ran before the completed replacement was committed.");
            Assert.True(!Directory.Exists(Path.Combine(directory, ".partial")));
            Assert.True(!Directory.Exists(Path.Combine(directory, ".conversion")));
        }
        fixture.Handler.NetworkAvailable = false;
        await fixture.RestartAsync();
        var recovered = (await fixture.Service.GetDownloadsAsync()).Single();
        Assert.Equal(VodDownloadState.Completed, recovered.State);
        Assert.Equal(VodDownloadFormat.Hls, recovered.Format);
        Assert.Equal(recovered.LocalMediaPath, (await fixture.Service.GetOfflineTargetAsync(item.Id)).LocalMediaPath);
        Assert.True(!Directory.Exists(obsolete));
    }

    private static async Task PlaybackGenerationsCleanupAsync()
    {
        RequireFfmpeg();
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        await UseRealMediaAsync(fixture);
        var original = await fixture.DownloadAsync();
        await using var first = await fixture.Service.AcquireOfflinePlaybackAsync(original.Id);
        await using var second = await fixture.Service.AcquireOfflinePlaybackAsync(original.Id);
        await fixture.Service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        var mp4 = await WaitForFormatAsync(fixture.Service, original.Id, VodDownloadFormat.Mp4);
        await using var mp4Playback = await fixture.Service.AcquireOfflinePlaybackAsync(original.Id);
        await fixture.Service.ChangeDownloadFormatAsync(VodDownloadFormat.Hls, fixture.Options);
        var hls = await WaitForFormatAsync(fixture.Service, original.Id, VodDownloadFormat.Hls);
        await first.DisposeAsync();
        await first.DisposeAsync();
        Assert.True(File.Exists(original.LocalMediaPath), "One playback owner released another owner's segments.");
        await second.DisposeAsync();
        Assert.True(!Directory.Exists(Path.GetDirectoryName(original.LocalMediaPath)!));
        Assert.True(File.Exists(mp4.LocalMediaPath));
        await mp4Playback.DisposeAsync();
        Assert.True(!Directory.Exists(Path.GetDirectoryName(mp4.LocalMediaPath)!));
        Assert.True(File.Exists(hls.LocalMediaPath));
        await DecodeMediaAsync(hls.LocalMediaPath);
        await using var current = await fixture.Service.AcquireOfflinePlaybackAsync(original.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RemoveAsync(original.Id));
        await current.DisposeAsync();
        await fixture.Service.RemoveAsync(original.Id);
        Assert.True(!Directory.Exists(Path.GetDirectoryName(fixture.Record(original.Id))!));
    }

    private static async Task PlaybackFolderCaseCleanupAsync()
    {
        RequireFfmpeg();
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        await UseRealMediaAsync(fixture);
        var original = await fixture.DownloadAsync();
        await fixture.Service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        var mp4 = await WaitForFormatAsync(fixture.Service, original.Id, VodDownloadFormat.Mp4);
        await fixture.Service.DisposeAsync();
        var itemDirectory = Path.GetDirectoryName(fixture.Record(original.Id))!;
        var renamed = Path.Combine(itemDirectory, "media-abcdef0123456789abcdef0123456789");
        if (!Path.GetDirectoryName(mp4.LocalMediaPath)!.Equals(renamed, StringComparison.OrdinalIgnoreCase))
            Directory.Move(Path.GetDirectoryName(mp4.LocalMediaPath)!, renamed);
        await File.WriteAllTextAsync(fixture.Record(original.Id), JsonSerializer.Serialize(mp4 with
        {
            MediaFolder = "media-ABCDEF0123456789ABCDEF0123456789",
            LocalMediaPath = ""
        }));
        var obsolete = Path.Combine(itemDirectory, "MEDIA-" + Guid.NewGuid().ToString("N").ToUpperInvariant());
        Directory.CreateDirectory(obsolete);
        await File.WriteAllTextAsync(Path.Combine(obsolete, "unused.mp4"), "obsolete generation");
        await fixture.RestartAsync();
        await using var playback = await fixture.Service.AcquireOfflinePlaybackAsync(original.Id);
        Assert.True(!Directory.Exists(obsolete));
        await fixture.Service.ChangeDownloadFormatAsync(VodDownloadFormat.Hls, fixture.Options);
        var hls = await WaitForFormatAsync(fixture.Service, original.Id, VodDownloadFormat.Hls);
        Assert.True(Directory.Exists(renamed), "Windows path casing bypassed the open player's media protection.");
        await DecodeMediaAsync(playback.Target.LocalMediaPath);
        await playback.DisposeAsync();
        Assert.True(!Directory.Exists(renamed));
        Assert.True(File.Exists(hls.LocalMediaPath));
    }

    private static async Task LockedConversionCleanupAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        var original = await fixture.DownloadAsync();
        await fixture.Service.DisposeAsync();
        await using var service = CreateService(fixture, new TestConverter(), TimeSpan.FromMilliseconds(25));
        await service.GetDownloadsAsync();
        var directory = Path.GetDirectoryName(original.LocalMediaPath)!;
        var lockedPath = Path.Combine(directory, "asset-000000.ts");
        var readOnlyPath = Path.Combine(directory, "asset-000001.ts");
        File.SetAttributes(readOnlyPath, File.GetAttributes(readOnlyPath) | FileAttributes.ReadOnly);
        var orphan = Path.Combine(Path.GetDirectoryName(directory)!, "media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        await File.WriteAllTextAsync(Path.Combine(orphan, "vod.mp4"), "obsolete generation");
        try
        {
            await using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
            var completed = await WaitForFormatAsync(service, original.Id, VodDownloadFormat.Mp4);
            Assert.True(File.Exists(lockedPath));
            Assert.True(!File.Exists(readOnlyPath), "A locked file prevented cleanup of a separate read-only segment.");
            Assert.True(!Directory.Exists(orphan), "A locked generation prevented cleanup of an independent generation.");
            Assert.Equal(completed.LocalMediaPath, (await service.GetOfflineTargetAsync(original.Id)).LocalMediaPath);
            await locked.DisposeAsync();
            await TestWait.UntilAsync(() => !Directory.Exists(directory), TimeSpan.FromSeconds(3));
            Assert.True(File.Exists(completed.LocalMediaPath));
        }
        finally
        {
            if (File.Exists(readOnlyPath)) File.SetAttributes(readOnlyPath, FileAttributes.Normal);
        }
    }

    private static async Task LockedFailedConversionCleanupAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var original = await fixture.DownloadAsync();
        await fixture.Service.DisposeAsync();
        var converter = new TestConverter { Block = true, Fail = true };
        await using var service = CreateService(fixture, converter, TimeSpan.FromMilliseconds(25));
        await service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        await converter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var conversion = Path.Combine(Path.GetDirectoryName(fixture.Record(original.Id))!, ".conversion");
        var unfinished = Path.Combine(conversion, "unfinished");
        await using var locked = new FileStream(unfinished, FileMode.Open, FileAccess.Read, FileShare.Read);
        converter.Release.TrySetResult();
        var failed = await WaitForFormatAsync(service, original.Id, VodDownloadFormat.Hls);
        Assert.True(failed.CanRetryConversion);
        Assert.True(File.Exists(unfinished));
        Assert.Equal(original.LocalMediaPath, (await service.GetOfflineTargetAsync(original.Id)).LocalMediaPath);
        await locked.DisposeAsync();
        await TestWait.UntilAsync(() => !Directory.Exists(conversion), TimeSpan.FromSeconds(3));
        Assert.True(File.Exists(original.LocalMediaPath));
    }

    private static async Task PlaybackCleanupAfterShutdownAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var original = await fixture.DownloadAsync();
        await fixture.Service.DisposeAsync();
        await using var service = CreateService(fixture, new TestConverter());
        await using var playback = await service.AcquireOfflinePlaybackAsync(original.Id);
        await service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        var completed = await WaitForFormatAsync(service, original.Id, VodDownloadFormat.Mp4);
        await service.DisposeAsync();
        Assert.True(File.Exists(original.LocalMediaPath));
        await playback.DisposeAsync();
        Assert.True(!Directory.Exists(Path.GetDirectoryName(original.LocalMediaPath)!));
        Assert.True(File.Exists(completed.LocalMediaPath));
    }

    private static async Task ConversionPromotionLockAsync(bool permanent)
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var original = await fixture.DownloadAsync();
        await fixture.Service.DisposeAsync();
        var converter = new TestConverter { Block = true };
        await using var service = CreateService(fixture, converter, TimeSpan.FromMilliseconds(25));
        await service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        await converter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var conversion = Path.Combine(Path.GetDirectoryName(fixture.Record(original.Id))!, ".conversion");
        var denied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (args.Exception.HResult == unchecked((int)0x80070005) &&
                args.Exception.Message.Contains("'" + conversion + "'", StringComparison.OrdinalIgnoreCase))
                denied.TrySetResult();
        }
        AppDomain.CurrentDomain.FirstChanceException += OnException;
        try
        {
            await using (var locked = new FileStream(Path.Combine(conversion, "unfinished"),
                FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                converter.Release.TrySetResult();
                await denied.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (permanent)
                {
                    var failed = await WaitForFormatAsync(service, original.Id, VodDownloadFormat.Hls);
                    Assert.True(failed.CanRetryConversion);
                    Assert.True(failed.Error.Length > 0);
                    Assert.True(File.Exists(original.LocalMediaPath));
                    Assert.True(File.Exists(Path.Combine(conversion, "unfinished")));
                }
            }
            var completed = await WaitForFormatAsync(service, original.Id, permanent ? VodDownloadFormat.Hls : VodDownloadFormat.Mp4);
            if (!permanent) Assert.True(!Directory.Exists(Path.GetDirectoryName(original.LocalMediaPath)!));
            await TestWait.UntilAsync(() => !Directory.Exists(conversion), TimeSpan.FromSeconds(3));
            Assert.Equal(completed.LocalMediaPath, (await service.GetOfflineTargetAsync(original.Id)).LocalMediaPath);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= OnException; }
    }

    private static async Task DamagedCurrentCleanupAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var original = await fixture.DownloadAsync();
        await fixture.Service.DisposeAsync();
        await using var service = CreateService(fixture, new TestConverter(), TimeSpan.FromMilliseconds(25));
        await using var playback = await service.AcquireOfflinePlaybackAsync(original.Id);
        await service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        var mp4 = await WaitForFormatAsync(service, original.Id, VodDownloadFormat.Mp4);
        await File.WriteAllBytesAsync(mp4.LocalMediaPath, [1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetOfflineTargetAsync(original.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RetryAsync(original.Id, fixture.Options));
        await playback.DisposeAsync();
        Assert.True(File.Exists(original.LocalMediaPath), "Cleanup removed the remaining complete generation after the current copy failed validation.");
        await service.RetryAsync(original.Id, fixture.Options);
        var completed = await WaitForFormatAsync(service, original.Id, VodDownloadFormat.Mp4);
        Assert.True(!Directory.Exists(Path.GetDirectoryName(original.LocalMediaPath)!));
        Assert.True(!Directory.Exists(Path.GetDirectoryName(mp4.LocalMediaPath)!));
        Assert.Equal(completed.LocalMediaPath, (await service.GetOfflineTargetAsync(original.Id)).LocalMediaPath);
    }

    private static async Task InvalidConversionSourceCleanupAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var original = await fixture.DownloadAsync();
        await fixture.Service.DisposeAsync();
        await using var service = CreateService(fixture, new TestConverter(), TimeSpan.FromMilliseconds(25));
        await using var playback = await service.AcquireOfflinePlaybackAsync(original.Id);
        await service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        var mp4 = await WaitForFormatAsync(service, original.Id, VodDownloadFormat.Mp4);
        await File.WriteAllBytesAsync(mp4.LocalMediaPath, [1]);
        var failure = new TaskCompletionSource<VodDownloadItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DownloadChanged += item =>
        {
            if (item.State == VodDownloadState.Failed) failure.TrySetResult(item);
        };
        await service.ChangeDownloadFormatAsync(VodDownloadFormat.Hls, fixture.Options);
        var failed = await failure.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(!failed.CanRetryConversion);
        Assert.Equal("", failed.LocalMediaPath);
        await playback.DisposeAsync();
        Assert.True(File.Exists(original.LocalMediaPath));
        await service.RetryAsync(original.Id, fixture.Options);
        var completed = await WaitForFormatAsync(service, original.Id, VodDownloadFormat.Hls);
        Assert.True(!Directory.Exists(Path.GetDirectoryName(original.LocalMediaPath)!));
        Assert.True(!File.Exists(mp4.LocalMediaPath), "The redownload kept the damaged MP4 in its reused media folder.");
        Assert.Equal(completed.LocalMediaPath, (await service.GetOfflineTargetAsync(original.Id)).LocalMediaPath);
    }

    private static Task PlaybackTabCleanupAsync(bool delayedDisposal) => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var original = await fixture.DownloadAsync();
        await fixture.Service.DisposeAsync();
        await using var service = CreateService(fixture, new TestConverter(), TimeSpan.FromMilliseconds(25));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            DisposeAction = () =>
            {
                disposing.TrySetResult();
                if (delayedDisposal) release.Task.GetAwaiter().GetResult();
            }
        });
        var settings = VodResumeTestCatalog.Settings();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        await using var model = new MainViewModel(new MainViewModelDependencies
        {
            Settings = settings,
            SettingsService = new FakeSettingsService(settings),
            StreamlinkService = fixture.Resolver,
            PlaybackFactory = factory,
            ChatFactory = new FakeChatClientFactory(),
            Logger = fixture.Logger,
            Dispatch = action => dispatcher.BeginInvoke(action),
            VodDownloadService = service
        });
        try
        {
            model.Initialize();
            await TestWait.UntilAsync(() => model.VodDownloads.Count == 1, TimeSpan.FromSeconds(3));
            await model.VodDownloads[0].PlayCommand.ExecuteAsync();
            var tab = model.Tabs.Single();
            tab.SetVideoHandle(new IntPtr(42));
            await TestWait.UntilAsync(() => tab.Status == PlaybackStatus.Playing, TimeSpan.FromSeconds(3));
            await model.VodDownloads[0].PlayCommand.ExecuteAsync();
            Assert.Equal(1, model.Tabs.Count);
            await service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
            var completed = await WaitForFormatAsync(service, original.Id, VodDownloadFormat.Mp4);
            Assert.True(File.Exists(original.LocalMediaPath));
            Assert.Equal(2, (await OfflineVodPackage.ValidateAsync(Path.GetDirectoryName(original.LocalMediaPath)!,
                CancellationToken.None)).SegmentCount);
            model.CloseTab(tab);
            await disposing.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await tab.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(6));
            if (delayedDisposal)
            {
                Assert.True(!tab.PlaybackCleanupIdleTask.IsCompleted);
                Assert.True(File.Exists(original.LocalMediaPath));
                Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(original.LocalMediaPath)!, "asset-000001.ts")));
            }
            release.TrySetResult();
            await tab.PlaybackCleanupIdleTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(!Directory.Exists(Path.GetDirectoryName(original.LocalMediaPath)!));
            Assert.True(File.Exists(completed.LocalMediaPath));
        }
        finally { release.TrySetResult(); }
    });

    private static Task ClosedPlaybackActionAsync(bool retry) => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        var original = await fixture.DownloadAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            DisposeAction = () =>
            {
                disposing.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
        });
        var settings = VodResumeTestCatalog.Settings();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        await using var model = new MainViewModel(new MainViewModelDependencies
        {
            Settings = settings,
            SettingsService = new FakeSettingsService(settings),
            StreamlinkService = fixture.Resolver,
            PlaybackFactory = factory,
            ChatFactory = new FakeChatClientFactory(),
            Logger = fixture.Logger,
            Dispatch = action => dispatcher.BeginInvoke(action),
            VodDownloadService = fixture.Service,
            ConfirmDeleteVodDownload = _ => true
        });
        try
        {
            model.Initialize();
            await TestWait.UntilAsync(() => model.VodDownloads.Count == 1, TimeSpan.FromSeconds(3));
            await model.VodDownloads[0].PlayCommand.ExecuteAsync();
            var tab = model.Tabs.Single();
            tab.SetVideoHandle(new IntPtr(42));
            await TestWait.UntilAsync(() => tab.Status == PlaybackStatus.Playing, TimeSpan.FromSeconds(3));
            if (retry)
            {
                await File.AppendAllTextAsync(original.LocalMediaPath, "tampered playlist");
                await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.GetOfflineTargetAsync(original.Id));
                await TestWait.UntilAsync(() => model.VodDownloads[0].CanRetry, TimeSpan.FromSeconds(3));
            }
            model.CloseTab(tab);
            await disposing.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var action = retry ? model.VodDownloads[0].RetryCommand.ExecuteAsync() : model.VodDownloads[0].DeleteCommand.ExecuteAsync();
            Assert.True(!action.IsCompleted, "A download action did not wait for the closing player's media ownership.");
            Assert.True(File.Exists(original.LocalMediaPath));
            release.TrySetResult();
            await action.WaitAsync(TimeSpan.FromSeconds(5));
            if (retry)
            {
                var completed = await fixture.WaitAsync(original.Id, VodDownloadState.Completed);
                Assert.Equal(completed.LocalMediaPath, (await fixture.Service.GetOfflineTargetAsync(original.Id)).LocalMediaPath);
            }
            else Assert.True(!Directory.Exists(Path.GetDirectoryName(fixture.Record(original.Id))!));
        }
        finally { release.TrySetResult(); }
    });
}

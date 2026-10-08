internal static partial class CodeCleanupTestCatalog
{
    private static async Task DownloadCancellationCallbackFailureAsync()
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(PlatformKind.Twitch);
        var item = await BlockDownloadResolutionAsync(fixture,
            () => throw new InvalidOperationException("Injected download cancellation failure."));

        await fixture.Service.CancelAsync(item.Id);
        await fixture.WaitAsync(item.Id, VodDownloadState.Canceled);
        fixture.Resolver.ResolveStreamUrlOverride = (_, _) =>
            Task.FromResult(new StreamlinkResolvedUrl(fixture.PlaylistUri, "Fixture"));
        await fixture.Service.RetryAsync(item.Id, fixture.Options);
        await fixture.WaitAsync(item.Id, VodDownloadState.Completed);
    }

    private static async Task DownloadCancellationLockAsync()
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(PlatformKind.Twitch);
        Task<IReadOnlyList<VodDownloadItem>>? callbackRead = null;
        var callbackReadCompleted = false;
        var item = await BlockDownloadResolutionAsync(fixture, () =>
        {
            // Avoid blocking the test when the old implementation holds the lock.
            // A synchronous library read here must be possible before cancellation returns.
            callbackRead = fixture.Service.GetDownloadsAsync();
            callbackReadCompleted = callbackRead.IsCompletedSuccessfully;
        });

        await fixture.Service.CancelAsync(item.Id);
        Assert.NotNull(callbackRead);
        await callbackRead!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(callbackReadCompleted, "Download cancellation invoked a callback while holding the library lock.");
        await fixture.WaitAsync(item.Id, VodDownloadState.Canceled);
    }

    private static async Task DownloadShutdownCallbackFailureAsync()
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(PlatformKind.Twitch);
        var active = await BlockDownloadResolutionAsync(fixture,
            () => throw new InvalidOperationException("Injected download shutdown failure."));
        var queued = await fixture.EnqueueAsync(VodDownloadUrlParser.Parse("https://twitch.tv/videos/54321"));
        var shutdown = (CancellationTokenSource)typeof(VodDownloadService)
            .GetField("shutdown", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Service)!;

        var disposal = fixture.Service.DisposeAsync().AsTask();
        Assert.True(ReferenceEquals(disposal, fixture.Service.DisposeAsync().AsTask()));
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<ObjectDisposedException>(() => _ = shutdown.Token);
        foreach (var item in new[] { active, queued })
        {
            var stored = JsonSerializer.Deserialize<VodDownloadItem>(await File.ReadAllTextAsync(fixture.Record(item.Id)));
            Assert.NotNull(stored);
            Assert.Equal(VodDownloadState.Interrupted, stored!.State);
        }
    }

    private static async Task<VodDownloadItem> BlockDownloadResolutionAsync(
        VodDownloadTestCatalog.DownloadFixture fixture, Action cancellationCallback)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Resolver.ResolveStreamUrlOverride = async (_, token) =>
        {
            var wait = Task.Delay(Timeout.InfiniteTimeSpan, token);
            // Register last so the injected callback runs before the canceled delay can
            // resume this operation and dispose its registration.
            using var registration = token.Register(cancellationCallback);
            entered.TrySetResult();
            await wait;
            throw new InvalidOperationException("The blocked resolver must be canceled.");
        };
        var item = await fixture.EnqueueAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return item;
    }

    private static Task TabStartCancellationFailureAsync()
    {
        using var controller = new TabStartController(2);
        var first = controller.TryBegin(Guid.NewGuid())!;
        var second = controller.TryBegin(Guid.NewGuid())!;
        using var registration = first.Token.Register(
            () => throw new InvalidOperationException("Injected tab-start cancellation failure."));
        try
        {
            controller.Clear();
            Assert.True(first.Token.IsCancellationRequested);
            Assert.True(second.Token.IsCancellationRequested);
            Assert.True(!controller.IsActive(first.TabId) && !controller.IsActive(second.TabId));
        }
        finally
        {
            controller.End(first);
            controller.End(second);
        }
        return Task.CompletedTask;
    }

    private static async Task HomeShutdownCallbackFailureAsync()
    {
        var feature = new HomeCleanupProbe();
        Task? reenteredDisposal = null;
        using var registration = feature.Token.Register(() =>
        {
            reenteredDisposal = feature.DisposeAsync().AsTask();
            throw new InvalidOperationException("Injected Home cancellation failure.");
        });
        try
        {
            var disposal = feature.DisposeAsync().AsTask();
            Assert.True(ReferenceEquals(disposal, reenteredDisposal));
            Assert.True(!disposal.IsCompleted);
            Assert.Equal(1, feature.Stops);
            feature.AllowDrain.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, feature.Releases);
            Assert.Throws<ObjectDisposedException>(() => _ = feature.Token);
        }
        finally
        {
            registration.Dispose();
            feature.AllowDrain.TrySetResult();
            await feature.DisposeAsync();
        }
    }

    private static async Task HomeShutdownDrainFailureAsync()
    {
        foreach (var failWhileReleasing in new[] { false, true })
        {
            var feature = new HomeCleanupProbe { FailWhileDraining = !failWhileReleasing, FailWhileReleasing = failWhileReleasing };
            var backgroundWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            feature.TrackWork(backgroundWork.Task);
            feature.AllowDrain.TrySetResult();
            try
            {
                var disposal = feature.DisposeAsync().AsTask();
                Assert.True(!disposal.IsCompleted, "Home disposal skipped its unfinished background work.");
                Assert.Equal(0, feature.Releases);
                backgroundWork.TrySetResult();
                await Assert.ThrowsAsync<InvalidDataException>(() => disposal.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(1, feature.Releases);
                Assert.Throws<ObjectDisposedException>(() => _ = feature.Token);
            }
            finally
            {
                backgroundWork.TrySetResult();
                try { await feature.DisposeAsync(); }
                catch (InvalidDataException) { }
                feature.DisposeToken();
            }
        }
    }

    private static Task MainShutdownCallbackFailureAsync() => AssertViewModelShutdownAsync(CreateCleanupMain());

    private static Task StreamTabShutdownCallbackFailureAsync() => AssertViewModelShutdownAsync(CreateCleanupTab());

    private static async Task MainShutdownDrainFailureAsync()
    {
        var main = CreateCleanupMain();
        var tab = CreateCleanupTab();
        main.Tabs.Add(tab);
        var tabCancellation = (CancellationTokenSource)typeof(StreamTabViewModel)
            .GetField("lifetimeCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;
        var background = (BackgroundOperationController)typeof(MainViewModel)
            .GetField("backgroundOperationController", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        background.Track(work.Task);
        typeof(MainViewModel).GetField("automaticUpdateTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(main, Task.FromException(new InvalidDataException("Injected background shutdown failure.")));
        try
        {
            var disposal = main.DisposeAsync().AsTask();
            Assert.True(!disposal.IsCompleted, "Main shutdown skipped its unfinished tracked work after a failure.");
            work.TrySetResult();
            await Assert.ThrowsAsync<InvalidDataException>(() => disposal.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Throws<ObjectDisposedException>(() => _ = tabCancellation.Token);
        }
        finally
        {
            work.TrySetResult();
            try { await main.DisposeAsync(); }
            catch (InvalidDataException) { }
            await tab.DisposeAsync();
        }
    }

    private static MainViewModel CreateCleanupMain(IBrowseService? browseService = null,
        IStreamMetadataService? streamMetadataService = null)
    {
        var settings = new AppSettings();
        return TestViewModels.CreateMain(settings, new FakeSettingsService(settings), new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(), action => action(),
            browseService: browseService, streamMetadataService: streamMetadataService);
    }

    private static StreamTabViewModel CreateCleanupTab(IAppLogger? logger = null) =>
        TestViewModels.CreateTab(StreamInputParser.FromChannel(PlatformKind.Twitch, "streamer"), "best",
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
            logger ?? new MemoryLogger(), action => action());

    private static async Task AssertViewModelShutdownAsync(IAsyncDisposable viewModel)
    {
        var cancellation = (CancellationTokenSource)viewModel.GetType()
            .GetField("lifetimeCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewModel)!;
        Task? reenteredDisposal = null;
        using var registration = cancellation.Token.Register(() =>
        {
            reenteredDisposal = viewModel.DisposeAsync().AsTask();
            throw new InvalidOperationException("Injected view-model cancellation failure.");
        });
        try
        {
            var disposal = viewModel.DisposeAsync().AsTask();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(ReferenceEquals(disposal, reenteredDisposal));
            Assert.Throws<ObjectDisposedException>(() => _ = cancellation.Token);
        }
        finally
        {
            registration.Dispose();
            try { await viewModel.DisposeAsync(); }
            catch (AggregateException) { }
            cancellation.Dispose();
        }
    }

    private sealed class HomeCleanupProbe : HomeFeatureViewModel
    {
        private readonly object refreshTimerGate = new();
        private Timer? refreshTimer;

        internal HomeCleanupProbe(IAppLogger? logger = null) : base(new MainViewModelDependencies
        {
            Settings = new AppSettings(),
            SettingsService = new FakeSettingsService(new AppSettings()),
            StreamlinkService = new FakeStreamlinkService(),
            PlaybackFactory = new FakePlaybackEngineFactory(),
            ChatFactory = new FakeChatClientFactory(),
            Logger = logger ?? new MemoryLogger(),
            Dispatch = action => action()
        }, _ => { })
        { }

        internal TaskCompletionSource AllowDrain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token => lifetimeCancellation.Token;
        internal bool FailWhileDraining { get; init; }
        internal bool FailWhileReleasing { get; init; }
        internal int Stops { get; private set; }
        internal int Releases { get; private set; }
        internal void TrackWork(Task work) => Track(work);
        internal void DisposeToken() => lifetimeCancellation.Dispose();
        internal void StartRefreshTimer(Action refresh) =>
            EnsureRefreshTimerStarted(refreshTimerGate, ref refreshTimer, TimeSpan.FromMilliseconds(50), refresh);
        protected override void StopOperations()
        {
            Stops++;
            StopRefreshTimer(refreshTimerGate, ref refreshTimer);
        }
        protected override Task WaitForOperationsAsync() => FailWhileDraining
            ? Task.FromException(new InvalidDataException("Injected Home drain failure.")) : AllowDrain.Task;
        protected override void ReleaseResources()
        {
            Releases++;
            if (FailWhileReleasing) throw new InvalidDataException("Injected Home resource failure.");
        }
    }
}

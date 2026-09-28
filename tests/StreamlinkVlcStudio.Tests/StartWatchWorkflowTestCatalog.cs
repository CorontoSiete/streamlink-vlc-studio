internal static class StartWatchWorkflowTestCatalog
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("start watch workflow: Twitch playback and chat do not wait for replay discovery", OptionalReplayAsync),
        ("start watch workflow: hiding a playing tab cancels pending replay discovery and suspends video", HideDuringReplayDiscoveryAsync),
        ("start watch workflow: Stop cancels an active transport resolution", () => CancelActiveStartAsync(waitForPlayer: false)),
        ("start watch workflow: Stop cancels a player waiting for its first frame", () => CancelActiveStartAsync(waitForPlayer: true)),
        ("start watch workflow: Stop removes a queued start before it allocates resources", () => CancelQueuedStartAsync(restart: false)),
        ("start watch workflow: a stopped queued tab can be started again exactly once", () => CancelQueuedStartAsync(restart: true)),
        ("start watch workflow: Stop before dispatch prevents a delayed start", CancelBeforeDispatchAsync),
        ("start watch workflow: a cancelled direct start creates no transport or player", CancelledDirectStartAsync),
        ("start watch workflow: Stop releases the player while a cancelled transport completes late", () => LateTransportAsync(close: false)),
        ("start watch workflow: close releases the player and disposes a late transport exactly once", () => LateTransportAsync(close: true)),
        ("start watch workflow: late replay UI callbacks cannot revive a stopped tab", () => LateReplayUiAsync(fail: false)),
        ("start watch workflow: late replay failure callbacks cannot overwrite a stopped tab", () => LateReplayUiAsync(fail: true)),
        ("start watch workflow: resumed live playback does not wait for replay discovery", ResumeDuringReplayDiscoveryAsync),
        ("start watch workflow: rapid Stop and Play before dispatch only starts the latest request", RestartBeforeDispatchAsync),
        ("start watch resources: suspended live tabs schedule no health callbacks and resume monitoring", SuspendedHealthWorkAsync)
    ];

    private static Task OptionalReplayAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var replay = new PendingReplayResolver();
        await using var fixture = new Fixture(replay: replay, connectChat: true);
        var tab = fixture.CreateTab();
        var start = tab.StartAsync(fixture.Settings);
        try
        {
            await replay.Requested.Task.WaitAsync(Timeout);
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            Assert.True(start.IsCompletedSuccessfully, "Optional replay discovery must not hold playback startup open.");
            Assert.Equal(false, tab.IsBusy);
            await TestWait.UntilAsync(() => fixture.Chat.Client.Connected, Timeout);
            Assert.Equal(0, fixture.Streamlink.ResolveStreamUrlCount);
        }
        finally
        {
            replay.Release.TrySetResult(UnavailableReplay);
            await start.WaitAsync(Timeout);
        }
    });

    private static Task HideDuringReplayDiscoveryAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var replay = new PendingReplayResolver { IgnoreCancellation = true };
        await using var fixture = new Fixture(replay: replay);
        await fixture.Main.OpenStreamAsync(Target("watching"));
        var tab = fixture.Main.SelectedTab!;
        tab.SetVideoHandle(new IntPtr(1234));
        var token = await replay.Requested.Task.WaitAsync(Timeout);
        try
        {
            fixture.Main.SelectHomeCommand.Execute(null);
            await fixture.Main.InactivePlaybackPolicyIdleTask.WaitAsync(Timeout);
            Assert.Equal(PlaybackStatus.Paused, tab.Status);
            Assert.True(tab.IsLivePlaybackConnectionSuspended);
            Assert.True(token.IsCancellationRequested);
            replay.Release.SetResult(AvailableReplay);
            await tab.PlaybackCleanupIdleTask.WaitAsync(Timeout);
            await System.Windows.Threading.Dispatcher.Yield();
            Assert.Equal(false, tab.IsReplaySeekEnabled);
            Assert.Equal(0, fixture.Streamlink.ResolveStreamUrlCount);
            Assert.Equal(0, fixture.Playback.Engine!.PreparedReplayUris.Count);
        }
        finally
        {
            replay.Release.TrySetResult(UnavailableReplay);
        }
    });

    private static Task CancelActiveStartAsync(bool waitForPlayer) => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeTransportSession();
        var engine = new FakePlaybackEngine { PlayCompletion = waitForPlayer ? release.Task : Task.CompletedTask };
        await using var fixture = new Fixture(engine: engine);
        fixture.Streamlink.StartExternalHttpOverride = async (_, token) =>
        {
            requested.TrySetResult(token);
            if (!waitForPlayer) await release.Task.WaitAsync(token);
            return session;
        };
        var tab = fixture.CreateTab();
        var start = tab.StartAsync(fixture.Settings);
        var token = await requested.Task.WaitAsync(Timeout);
        if (waitForPlayer) await engine.PlayStarted.Task.WaitAsync(Timeout);
        var stop = tab.StopAsync();
        try
        {
            Assert.True(token.IsCancellationRequested, "Stop must cancel startup before waiting for its lifecycle lock.");
            await Task.WhenAll(start, stop).WaitAsync(Timeout);
            Assert.Equal(PlaybackStatus.Stopped, tab.Status);
            Assert.Equal(0, engine.PlayCount);
            if (waitForPlayer) Assert.Equal(1, session.DisposeCount);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(start, stop).WaitAsync(Timeout);
        }
    });

    private static Task CancelQueuedStartAsync(bool restart) => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture();
        fixture.Settings.KeepInactiveTabsRunning = true;
        fixture.Streamlink.StartExternalHttpOverride = async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return new FakeTransportSession();
        };
        try
        {
            for (var index = 0; index < 3; index++)
            {
                await fixture.Main.OpenStreamAsync(Target("queued" + index));
                fixture.Main.SelectedTab!.SetVideoHandle(new IntPtr(1234 + index));
            }
            await TestWait.UntilAsync(() => fixture.Streamlink.StartCount == 2, Timeout);
            var queued = fixture.Main.SelectedTab!;
            await fixture.Main.StopSelectedCommand.ExecuteAsync().WaitAsync(Timeout);
            Assert.Equal(PlaybackStatus.Stopped, queued.Status);
            if (restart) await fixture.Main.PlaySelectedCommand.ExecuteAsync();
            release.SetResult();
            await TestWait.UntilAsync(() => fixture.Main.Tabs.Take(2).All(tab => !tab.IsBusy && tab.Status == PlaybackStatus.Playing), Timeout);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (restart)
                await TestWait.UntilAsync(() => queued.Status == PlaybackStatus.Playing, Timeout);
            else
                Assert.Equal(PlaybackStatus.Stopped, queued.Status);
            Assert.Equal(restart ? 3 : 2, fixture.Streamlink.StartCount);
            Assert.Equal(restart ? 3 : 2, fixture.Playback.CreateCount);
            Assert.Equal(restart ? 1 : 0, fixture.Streamlink.StartExternalHttpRequests.Count(request => request.Target.Channel == "queued2"));
        }
        finally
        {
            release.TrySetResult();
        }
    });

    private static Task CancelBeforeDispatchAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var callbacks = new Queue<Action>();
        await using var fixture = new Fixture(tryDispatch: action => { callbacks.Enqueue(action); return true; });
        await fixture.Main.OpenStreamAsync(Target("undispatched"));
        var tab = fixture.Main.SelectedTab!;
        tab.SetVideoHandle(new IntPtr(1234));
        await fixture.Main.StopSelectedCommand.ExecuteAsync();
        while (callbacks.TryDequeue(out var callback)) callback();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal(PlaybackStatus.Stopped, tab.Status);
        Assert.Equal(0, fixture.Streamlink.StartCount);
        Assert.Equal(0, fixture.Playback.CreateCount);
    });

    private static Task CancelledDirectStartAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new Fixture();
        var tab = fixture.CreateTab();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await tab.StartAsync(fixture.Settings, cancellationToken: cancellation.Token);
        Assert.Equal(0, fixture.Streamlink.StartCount);
        Assert.Equal(0, fixture.Playback.CreateCount);
    });

    private static Task LateTransportAsync(bool close) => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource<IStreamTransportSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture();
        fixture.Streamlink.StartExternalHttpOverride = (_, token) =>
        {
            requested.TrySetResult(token);
            return release.Task; // A provider that completes even after cancellation.
        };
        var tab = fixture.CreateTab();
        var start = tab.StartAsync(fixture.Settings);
        var token = await requested.Task.WaitAsync(Timeout);
        var stop = close ? tab.DisposeAsync().AsTask() : tab.StopAsync();
        var late = new FakeTransportSession();
        try
        {
            await Task.WhenAll(start, stop).WaitAsync(Timeout);
            Assert.True(token.IsCancellationRequested);
            Assert.True(token.WaitHandle.WaitOne(0), "The producer must retain a usable token until its request finishes.");
            Assert.True(fixture.Playback.Engine!.Stopped);
            Assert.Equal(0, fixture.Playback.Engine.PlayCount);
            Assert.Equal(false, tab.PlaybackCleanupIdleTask.IsCompleted);
            release.SetResult(late);
            await tab.PlaybackCleanupIdleTask.WaitAsync(Timeout);
            Assert.Equal(1, late.DisposeCount);
            Assert.Equal(PlaybackStatus.Stopped, tab.Status);
            Assert.Throws<ObjectDisposedException>(() => _ = token.WaitHandle);
        }
        finally
        {
            release.TrySetResult(late);
            await Task.WhenAll(start, stop).WaitAsync(Timeout);
        }
    });

    private static Task LateReplayUiAsync(bool fail) => TestSta.RunOffscreenAsync(async () =>
    {
        var replay = new PendingReplayResolver();
        var callbacks = new ConcurrentQueue<Action>();
        var defer = false;
        await using var fixture = new Fixture(replay: replay, dispatch: action =>
        {
            if (defer) callbacks.Enqueue(action);
            else action();
        });
        var tab = fixture.CreateTab();
        await tab.StartAsync(fixture.Settings).WaitAsync(Timeout);
        await replay.Requested.Task.WaitAsync(Timeout);
        defer = true;
        if (fail) replay.Release.SetException(new HttpRequestException("Old replay request failed."));
        else replay.Release.SetResult(AvailableReplay);
        await tab.PlaybackCleanupIdleTask.WaitAsync(Timeout);
        await tab.StopAsync().WaitAsync(Timeout);
        var tooltip = tab.ReplaySeekToolTip;
        defer = false;
        while (callbacks.TryDequeue(out var callback)) callback();
        Assert.Equal(PlaybackStatus.Stopped, tab.Status);
        Assert.Equal(false, tab.IsReplaySeekEnabled);
        Assert.Equal(tooltip, tab.ReplaySeekToolTip);
    });

    private static Task ResumeDuringReplayDiscoveryAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var replay = new PendingReplayResolver();
        await using var fixture = new Fixture(replay: replay);
        var tab = fixture.CreateTab();
        await tab.StartAsync(fixture.Settings).WaitAsync(Timeout);
        await replay.Requested.Task.WaitAsync(Timeout);
        await tab.PauseForTabSwitchAsync().WaitAsync(Timeout);
        await tab.PlaybackCleanupIdleTask.WaitAsync(Timeout);
        var resume = tab.ResumeFromTabSwitchAsync();
        try
        {
            await resume.WaitAsync(Timeout);
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            Assert.Equal(false, tab.IsBusy);
            Assert.Equal(2, fixture.Playback.Engine!.PlayCount);
            await tab.PauseForTabSwitchAsync().WaitAsync(Timeout);
            Assert.True(tab.IsLivePlaybackConnectionSuspended);
        }
        finally
        {
            replay.Release.TrySetResult(UnavailableReplay);
            await resume.WaitAsync(Timeout);
        }
    });

    private static Task RestartBeforeDispatchAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var callbacks = new Queue<Action>();
        await using var fixture = new Fixture(tryDispatch: action => { callbacks.Enqueue(action); return true; });
        await fixture.Main.OpenStreamAsync(Target("restart"));
        var tab = fixture.Main.SelectedTab!;
        tab.SetVideoHandle(new IntPtr(1234));
        await fixture.Main.StopSelectedCommand.ExecuteAsync();
        await fixture.Main.PlaySelectedCommand.ExecuteAsync();
        await fixture.Main.StopSelectedCommand.ExecuteAsync();
        await fixture.Main.PlaySelectedCommand.ExecuteAsync();
        while (callbacks.TryDequeue(out var callback)) callback();
        await TestWait.UntilAsync(() => tab.Status == PlaybackStatus.Playing && !tab.IsBusy, Timeout);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal(1, fixture.Streamlink.StartCount);
        Assert.Equal(1, fixture.Playback.CreateCount);
    });

    private static Task SuspendedHealthWorkAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var callbacks = 0;
        var healthSamples = 0;
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var factory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            PlaybackHealthOverride = () =>
            {
                var sample = Interlocked.Increment(ref healthSamples);
                return new PlaybackHealth(1, PlaybackEngineState.Playing, sample * 1000, sample, sample, sample);
            }
        });
        await using var tab = TestViewModels.CreateTab(Target(), "best", new FakeStreamlinkService(), factory,
            new FakeChatClientFactory(), new MemoryLogger(), action =>
            {
                Interlocked.Increment(ref callbacks);
                dispatcher.BeginInvoke(action);
            });
        var settings = new AppSettings { StreamlinkPath = "streamlink.exe", VlcDirectory = @"C:\VLC" };
        settings.Chat.ConnectAutomatically = false;
        settings.Replay.Enabled = false;
        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);
        await tab.PauseForTabSwitchAsync();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Interlocked.Exchange(ref callbacks, 0);
        await Task.Delay(TimeSpan.FromMilliseconds(2200));
        var suspendedCallbacks = Volatile.Read(ref callbacks);
        Console.WriteLine($"Suspended live tab: {suspendedCallbacks} dispatcher callbacks during 2.2 seconds.");
        Assert.Equal(0, suspendedCallbacks);
        Assert.Equal(0, Volatile.Read(ref healthSamples));
        await tab.ResumeFromTabSwitchAsync();
        await TestWait.UntilAsync(() => Volatile.Read(ref healthSamples) > 0, Timeout);
        Assert.Equal(PlaybackStatus.Playing, tab.Status);
    });

    private static StreamTarget Target(string channel = "watching") => StreamInputParser.Parse(channel, PlatformKind.Twitch);
    private static ReplaySessionInfo UnavailableReplay => ReplaySessionInfo.Unavailable(PlatformKind.Twitch, "watching", "No replay yet.");
    private static ReplaySessionInfo AvailableReplay => new(PlatformKind.Twitch, "watching", "https://www.twitch.tv/videos/123456",
        "123456", DateTimeOffset.UtcNow - TimeSpan.FromHours(1), TimeSpan.FromHours(1), true, "");

    private sealed class PendingReplayResolver : IReplayResolver
    {
        public TaskCompletionSource<CancellationToken> Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ReplaySessionInfo> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IgnoreCancellation { get; init; }

        public Task<ReplaySessionInfo> ResolveCurrentReplayAsync(StreamTarget target, string quality, AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            Requested.TrySetResult(cancellationToken);
            return IgnoreCancellation ? Release.Task : Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly IReplayResolver? replay;
        private readonly MemoryLogger logger = new();
        private readonly Action<Action> dispatch;
        public AppSettings Settings { get; } = new() { StreamlinkPath = "streamlink.exe", VlcDirectory = @"C:\Program Files\VideoLAN\VLC" };
        public FakeStreamlinkService Streamlink { get; } = new();
        public FakePlaybackEngineFactory Playback { get; }
        public FakeChatClientFactory Chat { get; } = new();
        public MainViewModel Main { get; }

        public Fixture(IReplayResolver? replay = null, bool connectChat = false, FakePlaybackEngine? engine = null,
            Func<Action, bool>? tryDispatch = null, Action<Action>? dispatch = null)
        {
            this.replay = replay;
            this.dispatch = dispatch ?? (action => action());
            Settings.Chat.ConnectAutomatically = connectChat;
            Settings.Chat.Layout = ChatLayout.Docked;
            Playback = new FakePlaybackEngineFactory(engine is null ? null : () => engine);
            Main = new MainViewModel(new MainViewModelDependencies
            {
                Settings = Settings,
                SettingsService = new FakeSettingsService(Settings),
                StreamlinkService = Streamlink,
                PlaybackFactory = Playback,
                ChatFactory = Chat,
                Logger = logger,
                Dispatch = this.dispatch,
                ReplayResolver = replay,
                TryDispatch = tryDispatch
            });
        }

        public StreamTabViewModel CreateTab()
        {
            var tab = TestViewModels.CreateTab(Target(), "best", Streamlink, Playback, Chat, logger, dispatch, replayResolver: replay);
            Main.Tabs.Add(tab);
            Main.SelectedTab = tab;
            tab.SetVideoHandle(new IntPtr(1234));
            return tab;
        }

        public ValueTask DisposeAsync() => Main.DisposeAsync();
    }
}

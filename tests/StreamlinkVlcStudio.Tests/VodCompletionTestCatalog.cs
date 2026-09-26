internal static class VodCompletionTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("VOD finished: Twitch reaches the finished screen and can seek back", () => FinishAndSeekAsync(PlatformKind.Twitch)),
        ("VOD finished: Kick reaches the finished screen and can seek back", () => FinishAndSeekAsync(PlatformKind.Kick)),
        ("VOD finished: completion works without history, duration metadata or replay", WithoutReplayAsync),
        ("VOD finished: paused, buffering, stopped and failed playback are not completion", OtherStatesAsync),
        ("VOD finished: live playback is excluded", LiveAsync),
        ("VOD finished: queued completion cannot replace a seek or restart", StaleCompletionAsync),
        ("VOD finished: end reached before seek submission still completes", EndBeforeSeekAsync),
        ("VOD finished: failed in-place seek restores playing state", () => FailedSeekStateAsync(false)),
        ("VOD finished: failed in-place seek restores paused state", () => FailedSeekStateAsync(true)),
        ("VOD finished: cancelled in-place seek restores playback state", CancelledSeekStateAsync)
    ];

    private static StreamTabViewModel Tab(StreamTarget target, FakePlaybackEngineFactory factory,
        Action<Action>? dispatch = null) => new(new StreamTabViewModelDependencies
        {
            Target = target,
            Quality = "best",
            StreamlinkService = new FakeStreamlinkService(),
            PlaybackFactory = factory,
            ChatFactory = new FakeChatClientFactory(),
            Logger = new MemoryLogger(),
            Dispatch = dispatch ?? (action => action())
        });

    private static async Task StartAsync(StreamTabViewModel tab, bool replay = true)
    {
        tab.SetVideoHandle(new IntPtr(123));
        await tab.StartAsync(VodResumeTestCatalog.Settings(replay));
        Assert.Equal(PlaybackStatus.Playing, tab.Status);
    }

    private static async Task FinishAndSeekAsync(PlatformKind platform)
    {
        var factory = new FakePlaybackEngineFactory();
        await using var tab = Tab(VodResumeTestCatalog.Target(platform), factory);
        await StartAsync(tab);
        var engine = factory.Engine!;
        engine.PlaybackHealthOverride = () => new(1,
            engine.PlayCount == 1 ? PlaybackEngineState.Ended : PlaybackEngineState.Playing, 0, 0, 0, 0);
        await TestWait.UntilAsync(() => tab.IsVodFinished, TimeSpan.FromSeconds(3));
        Assert.Equal("VOD finished", tab.StatusText);
        Assert.Equal(tab.ReplaySeekMaximum, tab.ReplaySeekValue);

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        Assert.Equal(false, tab.IsVodFinished);
        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        Assert.Equal(2, engine.PlayCount);
        Assert.Equal(TimeSpan.FromMinutes(10), engine.LastStartPosition!.Value);

        engine.PlaybackHealthOverride = () => new(2, PlaybackEngineState.Ended, 0, 0, 0, 0);
        tab.CheckVodPlaybackCompletion();
        Assert.True(tab.IsVodFinished);
        await StartAsync(tab);
        Assert.Equal(false, tab.IsVodFinished);
        factory.Engine!.PlaybackHealthOverride = () => new(3, PlaybackEngineState.Ended, 0, 0, 0, 0);
        tab.CheckVodPlaybackCompletion();
        await tab.StopAsync();
        Assert.Equal(false, tab.IsVodFinished);
        Assert.Equal(PlaybackStatus.Stopped, tab.Status);
    }

    private static async Task WithoutReplayAsync()
    {
        var factory = new FakePlaybackEngineFactory();
        await using var tab = Tab(VodResumeTestCatalog.Target() with { MediaDuration = TimeSpan.Zero }, factory);
        await StartAsync(tab, replay: false);
        factory.Engine!.PlaybackHealthOverride = () => new(1, PlaybackEngineState.Ended, 0, 0, 0, 0);
        await TestWait.UntilAsync(() => tab.IsVodFinished, TimeSpan.FromSeconds(3));
        Assert.Equal("VOD finished", tab.StatusText);
    }

    private static async Task OtherStatesAsync()
    {
        var factory = new FakePlaybackEngineFactory();
        await using var tab = Tab(VodResumeTestCatalog.Target(), factory);
        await StartAsync(tab);
        foreach (var state in Enum.GetValues<PlaybackEngineState>().Where(state => state != PlaybackEngineState.Ended))
        {
            factory.Engine!.PlaybackHealthOverride = () => new(1, state, 7200000, 0, 0, 0);
            tab.CheckVodPlaybackCompletion();
            Assert.Equal(false, tab.IsVodFinished);
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
        }
        await tab.PauseOrResumeAsync();
        tab.CheckVodPlaybackCompletion();
        Assert.Equal(false, tab.IsVodFinished);
        Assert.Equal(PlaybackStatus.Paused, tab.Status);
    }

    private static async Task LiveAsync()
    {
        var factory = new FakePlaybackEngineFactory();
        await using var tab = Tab(VodResumeTestCatalog.Target() with { Kind = StreamTargetKind.Live }, factory);
        await StartAsync(tab);
        factory.Engine!.PlaybackHealthOverride = () => new(1, PlaybackEngineState.Ended, 0, 0, 0, 0);
        tab.CheckVodPlaybackCompletion();
        Assert.Equal(false, tab.IsVodFinished);
    }

    private static async Task StaleCompletionAsync()
    {
        var pending = new ConcurrentQueue<Action>();
        var defer = false;
        var factory = new FakePlaybackEngineFactory();
        await using var tab = Tab(VodResumeTestCatalog.Target(), factory,
            action => { if (defer) pending.Enqueue(action); else action(); });
        await StartAsync(tab);
        defer = true;
        factory.Engine!.PlaybackHealthOverride = () => new(1, PlaybackEngineState.Ended, 0, 0, 0, 0);
        tab.CheckVodPlaybackCompletion();
        factory.Engine.PlaybackHealthOverride = null;
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(5));
        while (pending.TryDequeue(out var action)) action();
        Assert.Equal(false, tab.IsVodFinished);
        Assert.Equal(PlaybackStatus.Playing, tab.Status);

        factory.Engine.PlaybackHealthOverride = () => new(1, PlaybackEngineState.Ended, 0, 0, 0, 0);
        tab.CheckVodPlaybackCompletion();
        await StartAsync(tab);
        while (pending.TryDequeue(out var action)) action();
        Assert.Equal(false, tab.IsVodFinished);
        Assert.Equal(PlaybackStatus.Playing, tab.Status);
    }

    private static async Task EndBeforeSeekAsync()
    {
        var factory = new FakePlaybackEngineFactory();
        await using var tab = Tab(VodResumeTestCatalog.Target(), factory);
        await StartAsync(tab);
        var engine = factory.Engine!;
        engine.FailingSeekCount = 1;
        // Reproduce EOF before the engine can submit the seek. This is not a
        // successful seek, but the terminal decoder state must remain observable.
        engine.PlaybackHealthOverride = () => new(1,
            engine.SeekCount > 0 ? PlaybackEngineState.Ended : PlaybackEngineState.Playing, 0, 0, 0, 0);
        await tab.SeekReplayAsync(tab.Target.MediaDuration);
        await TestWait.UntilAsync(() => tab.IsVodFinished, TimeSpan.FromSeconds(3));
        Assert.Equal(tab.ReplaySeekMaximum, tab.ReplaySeekValue);
    }

    private static async Task FailedSeekStateAsync(bool paused)
    {
        var factory = new FakePlaybackEngineFactory();
        await using var tab = Tab(VodResumeTestCatalog.Target(), factory);
        await StartAsync(tab);
        if (paused) await tab.PauseOrResumeAsync();
        factory.Engine!.FailingSeekCount = 1;
        await tab.SeekReplayAsync(tab.Target.MediaDuration);
        Assert.Equal(paused ? PlaybackStatus.Paused : PlaybackStatus.Playing, tab.Status);
        Assert.Equal(false, tab.IsVodFinished);
        Assert.Equal(false, tab.IsBusy);
        Assert.Equal(false, tab.IsReplaySeekInProgress);
    }

    private static async Task CancelledSeekStateAsync()
    {
        var pendingSeek = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine { SeekCompletion = pendingSeek.Task });
        await using var tab = Tab(VodResumeTestCatalog.Target(), factory);
        await StartAsync(tab);
        using var cancellation = new CancellationTokenSource();
        var seek = tab.SeekReplayAsync(tab.Target.MediaDuration, cancellation.Token);
        await factory.Engine!.SeekStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await seek.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        Assert.Equal(false, tab.IsVodFinished);
        Assert.Equal(false, tab.IsReplaySeekInProgress);
    }
}

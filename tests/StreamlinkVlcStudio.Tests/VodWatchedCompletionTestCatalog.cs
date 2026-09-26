internal static class VodWatchedCompletionTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("VOD watched completion: Twitch EOF saves despite a missing final clock", () => MissingFinalClockAsync(PlatformKind.Twitch)),
        ("VOD watched completion: Kick EOF saves despite a missing final clock", () => MissingFinalClockAsync(PlatformKind.Kick)),
        ("VOD watched completion: successful end seek needs no intermediate bookmark", EndSeekAsync),
        ("VOD watched completion: startup EOF completes a restored bookmark", StartupEndAsync),
        ("VOD watched completion: decoder duration works without browse metadata or replay", NoMetadataAsync),
        ("VOD watched completion: accepted EOF survives an unavailable second health read", OneHealthSampleAsync),
        ("VOD watched completion: a queued EOF cannot mark a newer seek watched", () => StaleEndAsync("seek")),
        ("VOD watched completion: a queued EOF cannot mark restarted playback watched", () => StaleEndAsync("restart")),
        ("VOD watched completion: a queued EOF cannot mark stopped playback watched", () => StaleEndAsync("stop")),
        ("VOD watched completion: a seek during health sampling invalidates EOF", ChangedDuringSampleAsync),
        ("VOD watched completion: near-end clocks and non-EOF states remain unwatched", NonEndStatesAsync)
    ];

    private static StreamTabViewModel Tab(StreamTarget target, IVodPlaybackHistory history,
        FakePlaybackEngineFactory factory, Action<Action>? dispatch = null) => new(new StreamTabViewModelDependencies
        {
            Target = target,
            Quality = "best",
            StreamlinkService = new FakeStreamlinkService(),
            PlaybackFactory = factory,
            ChatFactory = new FakeChatClientFactory(),
            Logger = new MemoryLogger(),
            Dispatch = dispatch ?? (action => action()),
            VodPlaybackHistory = history
        });

    private static async Task StartAsync(StreamTabViewModel tab, bool replay = true)
    {
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(VodResumeTestCatalog.Settings(replay));
        await StopPollingAsync(tab);
    }

    // Drive samples explicitly so the ordering regressions do not depend on the timer's phase.
    private static Task StopPollingAsync(StreamTabViewModel tab) => (Task)typeof(StreamTabViewModel)
        .GetMethod("StopReplayClockPollingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tab, null)!;

    private static PlaybackHealth Ended() => new(1, PlaybackEngineState.Ended, 0, 0, 0, 0);

    private static async Task MissingFinalClockAsync(PlatformKind platform)
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var history = files.Create();
        await using var main = VodWatchProgressTestCatalog.CreateMain(history, platform);
        await main.SearchTwitchVodsCommand.ExecuteAsync();
        var card = main.TwitchVods[0];
        var factory = new FakePlaybackEngineFactory();
        await using var tab = Tab(card.Target, history, factory);
        await StartAsync(tab);
        await tab.SeekReplayAsync(TimeSpan.FromSeconds(7193.118));
        await StopPollingAsync(tab);
        var engine = factory.Engine!;
        engine.PlaybackClockOverride = _ => (false, new(TimeSpan.Zero, null, false));
        engine.PlaybackHealthOverride = Ended;
        tab.CheckVodPlaybackCompletion();
        Assert.True(tab.IsVodFinished);
        Assert.True(card.IsWatched, "EOF must mark the existing card watched even when the final sample was missed.");
        Assert.Equal(false, card.HasWatchProgress);
        await AssertPersistedAsync(files, history, card.Target);
        await tab.DisposeAsync();
        var reopenedFactory = new FakePlaybackEngineFactory();
        await using var reopened = Tab(card.Target, files.Create(), reopenedFactory);
        await StartAsync(reopened);
        Assert.Equal<TimeSpan?>(null, reopenedFactory.Engine!.LastStartPosition);
    }

    private static async Task EndSeekAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var history = files.Create();
        var factory = new FakePlaybackEngineFactory();
        await using var tab = Tab(VodResumeTestCatalog.Target(), history, factory);
        await StartAsync(tab);
        var engine = factory.Engine!;
        engine.PlaybackHealthOverride = () => engine.SeekCount > 0 ? Ended() : new(1, PlaybackEngineState.Playing, 0, 0, 0, 0);
        engine.PlaybackClockOverride = _ => (false, new(TimeSpan.Zero, null, false));
        await tab.SeekReplayAsync(tab.Target.MediaDuration);
        await StopPollingAsync(tab);
        tab.CheckVodPlaybackCompletion();
        Assert.True((await history.GetAsync(tab.Target)) is { Completed: true, HasBeenWatched: true });
        await tab.DisposeAsync();
        await AssertPersistedAsync(files, history, tab.Target);
    }

    private static async Task StartupEndAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var history = files.Create();
        var target = VodResumeTestCatalog.Target() with { MediaDuration = TimeSpan.Zero };
        history.Remember(target, VodWatchProgressTestCatalog.Bookmark(7199.75));
        await history.SaveAsync();
        var factory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            PlaybackHealthOverride = Ended,
            PlaybackClockOverride = _ => (false, new(TimeSpan.Zero, null, false))
        });
        await using var tab = Tab(target, history, factory);
        await StartAsync(tab, replay: false);
        tab.CheckVodPlaybackCompletion();
        Assert.True((await history.GetAsync(target)) is { Completed: true, HasBeenWatched: true });
        await AssertPersistedAsync(files, history, target);
    }

    private static async Task NoMetadataAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var history = files.Create();
        var factory = new FakePlaybackEngineFactory();
        var target = VodResumeTestCatalog.Target() with { MediaDuration = TimeSpan.Zero };
        await using var tab = Tab(target, history, factory);
        await StartAsync(tab, replay: false);
        factory.Engine!.PlaybackHealthOverride = Ended;
        // VLC may retain its duration but reset its clock/seekability at EOF.
        factory.Engine.PlaybackClockOverride = _ => (true, new(TimeSpan.Zero, TimeSpan.FromHours(2), false));
        tab.CaptureVodResumePosition(closing: true);
        await tab.DisposeAsync();
        await AssertPersistedAsync(files, history, target);
    }

    private static async Task OneHealthSampleAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var history = files.Create();
        var factory = new FakePlaybackEngineFactory();
        var pending = new Queue<Action>();
        var defer = false;
        await using var tab = Tab(VodResumeTestCatalog.Target(), history, factory,
            action => { if (defer) pending.Enqueue(action); else action(); });
        await StartAsync(tab);
        factory.Engine!.PlaybackHealthOverride = Ended;
        defer = true;
        tab.CheckVodPlaybackCompletion();
        // The native gate can be busy on a second read. The already accepted sample is sufficient.
        factory.Engine.PlaybackHealthOverride = null;
        while (pending.TryDequeue(out var action)) action();
        Assert.True(tab.IsVodFinished);
        Assert.True((await history.GetAsync(tab.Target)) is { Completed: true, HasBeenWatched: true });
    }

    private static async Task StaleEndAsync(string transition)
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var history = files.Create();
        var factory = new FakePlaybackEngineFactory();
        var pending = new ConcurrentQueue<Action>();
        var defer = false;
        await using var tab = Tab(VodResumeTestCatalog.Target(), history, factory,
            action => { if (defer) pending.Enqueue(action); else action(); });
        await StartAsync(tab);
        factory.Engine!.PlaybackHealthOverride = Ended;
        defer = true;
        tab.CheckVodPlaybackCompletion();
        factory.Engine.PlaybackHealthOverride = null;
        if (transition == "seek") await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        else if (transition == "restart") await tab.StartAsync(VodResumeTestCatalog.Settings());
        else await tab.StopAsync();
        await StopPollingAsync(tab);
        while (pending.TryDequeue(out var action)) action();
        Assert.Equal(false, tab.IsVodFinished);
        Assert.Equal(false, (await history.GetAsync(tab.Target))?.HasBeenWatched == true);
    }

    private static async Task ChangedDuringSampleAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var history = files.Create();
        var factory = new FakePlaybackEngineFactory();
        await using var tab = Tab(VodResumeTestCatalog.Target(), history, factory);
        await StartAsync(tab);
        await tab.SeekReplayAsync(TimeSpan.FromSeconds(7198));
        await StopPollingAsync(tab);
        factory.Engine!.PlaybackHealthOverride = () =>
        {
            // Simulate the seek generation changing while the native read was in flight.
            var version = typeof(StreamTabViewModel).GetField("replaySeekOperationVersion", BindingFlags.Instance | BindingFlags.NonPublic)!;
            version.SetValue(tab, (long)version.GetValue(tab)! + 1);
            return Ended();
        };
        tab.CaptureVodResumePosition();
        Assert.Equal(false, (await history.GetAsync(tab.Target))!.HasBeenWatched);
        factory.Engine.PlaybackHealthOverride = null;
    }

    private static async Task NonEndStatesAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var history = files.Create();
        var factory = new FakePlaybackEngineFactory();
        await using var tab = Tab(VodResumeTestCatalog.Target(), history, factory);
        await StartAsync(tab);
        await tab.SeekReplayAsync(tab.Target.MediaDuration);
        await StopPollingAsync(tab);
        foreach (var state in Enum.GetValues<PlaybackEngineState>().Where(state => state != PlaybackEngineState.Ended))
        {
            factory.Engine!.PlaybackHealthOverride = () => new(1, state, 7200000, 0, 0, 0);
            tab.CaptureVodResumePosition();
            tab.CheckVodPlaybackCompletion();
            Assert.Equal(false, tab.IsVodFinished);
            Assert.Equal(false, (await history.GetAsync(tab.Target))!.HasBeenWatched);
        }
    }

    internal static async Task AssertPersistedAsync(VodResumeTestCatalog.HistoryFiles files,
        IVodPlaybackHistory history, StreamTarget target)
    {
        // A read on the existing history waits for the save it has already queued, without
        // requesting another write or holding the destination open against atomic replacement.
        // No checkpoint timer or tab close should be needed to start that save.
        await history.GetAsync(target);
        var saved = await files.Create().GetAsync(target);
        Assert.True(saved is { Completed: true, HasBeenWatched: true });
        Assert.Equal(saved!.Duration, saved.Position);
    }
}

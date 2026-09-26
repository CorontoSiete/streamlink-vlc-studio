internal static class VodResumeTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("VOD resume: Twitch close and fresh application restore the final clock and chat", () => ReopenAsync(PlatformKind.Twitch)),
        ("VOD resume: Kick close and fresh application restore despite a refreshed URL", () => ReopenAsync(PlatformKind.Kick)),
        ("VOD resume: stop and quality reload preserve position", StopAndReloadAsync),
        ("VOD resume: checkpoints persist while the VOD stays open", CheckpointAsync),
        ("VOD resume: pause saves the actual clock without adding paused time", PauseAsync),
        ("VOD resume: backward seeks and an intentional restart replace older progress", BackwardAsync),
        ("VOD resume: previews and failed seeks cannot replace confirmed progress", FailedSeekAsync),
        ("VOD resume: invalid and buffering clocks cannot overwrite a bookmark", InvalidClocksAsync),
        ("VOD resume: failed restore keeps the bookmark and stops playback", FailedRestoreAsync),
        ("VOD resume: closing during startup keeps the old bookmark", CloseDuringStartupAsync),
        ("VOD resume: closing during a seek keeps the last confirmed position", CloseDuringSeekAsync),
        ("VOD resume: immediate reopen cannot be overwritten by detached cleanup", ImmediateReopenAsync),
        ("VOD resume: application shutdown saves all final VOD clocks", ApplicationShutdownAsync),
        ("VOD resume: missing metadata and disabled replay still record progress", DisabledReplayAsync),
        ("VOD resume: only confirmed end of media restarts a completed VOD", CompletionAsync),
        ("VOD resume: history separates video IDs and platforms and excludes live", IdentityAsync),
        ("VOD resume: corrupt history is preserved and invalid entries are ignored", CorruptHistoryAsync),
        ("VOD resume: concurrent saves preserve every VOD and bound history size", ConcurrentHistoryAsync),
        ("VOD resume: failed disk writes retain progress for retry", WriteFailureAsync),
        ("VOD resume: unreadable history cannot be overwritten by a fresh start", ReadFailureAsync),
        ("VOD resume: near-end progress is preserved despite stale duration metadata", DurationChangeAsync)
    ];

    internal static StreamTarget Target(PlatformKind platform = PlatformKind.Twitch, string id = "12345") => new(
        platform, "streamer", platform == PlatformKind.Twitch ? $"https://www.twitch.tv/videos/{id}" : "https://example.com/vod.m3u8",
        platform == PlatformKind.Twitch ? StreamTargetKind.TwitchVod : StreamTargetKind.KickVod,
        id, MediaDuration: TimeSpan.FromHours(2));

    internal static AppSettings Settings(bool replay = true)
    {
        var settings = new AppSettings { StreamlinkPath = "streamlink.exe", VlcDirectory = @"C:\VLC" };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Docked;
        settings.Replay.Enabled = replay;
        return settings;
    }

    internal static StreamTabViewModel Tab(StreamTarget target, IVodPlaybackHistory history,
        IPlaybackEngineFactory factory, IVodChatProvider? chat = null, IStreamlinkService? streamlink = null) => new(
        new StreamTabViewModelDependencies
        {
            Target = target,
            Quality = "best",
            StreamlinkService = streamlink ?? new FakeStreamlinkService(),
            PlaybackFactory = factory,
            ChatFactory = new FakeChatClientFactory(),
            Logger = new MemoryLogger(),
            Dispatch = action => action(),
            VodPlaybackHistory = history,
            VodChatProvider = chat
        });

    private static async Task StartAsync(StreamTabViewModel tab, AppSettings? settings = null)
    {
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(settings ?? Settings());
        Assert.Equal(PlaybackStatus.Playing, tab.Status);
    }

    private static async Task ReopenAsync(PlatformKind platform)
    {
        using var files = new HistoryFiles();
        var target = Target(platform);
        var factory = new FakePlaybackEngineFactory();
        await using (var tab = Tab(target, files.Create(), factory))
        {
            await StartAsync(tab);
            Assert.Equal<TimeSpan?>(null, factory.Engine!.LastStartPosition);
            await tab.SeekReplayAsync(TimeSpan.FromHours(1));
            factory.Engine.PlaybackClockOverride = _ => (true, new(TimeSpan.FromSeconds(3601.25), target.MediaDuration, true));
            // Closing must sample the final clock even between the 500 ms polling ticks.
        }
        var chat = new OffsetChatProvider();
        var reopenedFactory = new FakePlaybackEngineFactory();
        await using var reopened = Tab(target with { Url = target.Url + "?fresh=1", DisplayTitle = "New title" },
            files.Create(), reopenedFactory, chat);
        await StartAsync(reopened);
        Assert.Equal(TimeSpan.FromSeconds(3601.25), reopenedFactory.Engine!.LastStartPosition!.Value);
        await reopened.VodChatIdleTask;
        // Chat intentionally backfills 30 seconds around the restored position.
        Assert.Equal(TimeSpan.FromSeconds(3571.25), chat.Offsets.First());
        Assert.True(reopened.ReplaySeekValue >= 3601);
    }

    private static async Task StopAndReloadAsync()
    {
        using var files = new HistoryFiles();
        var factory = new FakePlaybackEngineFactory();
        await using var tab = Tab(Target(), files.Create(), factory);
        await StartAsync(tab);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(40));
        await tab.StopAsync();
        await StartAsync(tab);
        Assert.Equal(TimeSpan.FromMinutes(40), factory.Engine!.LastStartPosition!.Value);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(20));
        await StartAsync(tab);
        Assert.Equal(TimeSpan.FromMinutes(20), factory.Engine!.LastStartPosition!.Value);
    }

    private static async Task CheckpointAsync()
    {
        using var files = new HistoryFiles();
        var factory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            PlaybackClockOverride = _ => (true, new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), true))
        });
        await using var tab = Tab(Target(), files.Create(), factory);
        await StartAsync(tab);
        await TestWait.UntilAsync(() => File.Exists(files.Path), TimeSpan.FromSeconds(8));
        Assert.Equal(TimeSpan.FromMinutes(30), (await files.Create().GetAsync(Target()))!.Position);
        Assert.Equal(false, factory.Engine!.Stopped);
    }

    private static async Task PauseAsync()
    {
        using var files = new HistoryFiles();
        var factory = new FakePlaybackEngineFactory();
        await using var tab = Tab(Target(), files.Create(), factory);
        await StartAsync(tab);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(45));
        await tab.PauseOrResumeAsync();
        await Task.Delay(650);
        Assert.Equal(TimeSpan.FromMinutes(45), (await files.Create().GetAsync(Target()))!.Position);
        tab.CaptureVodResumePosition(closing: true);
        Assert.Equal(PlaybackStatus.Paused, tab.Status);
    }

    private static async Task BackwardAsync()
    {
        using var files = new HistoryFiles();
        var history = files.Create();
        await using var tab = Tab(Target(), history, new FakePlaybackEngineFactory());
        await StartAsync(tab);
        await tab.SeekReplayAsync(TimeSpan.FromHours(1));
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromMinutes(5), (await files.Create().GetAsync(Target()))!.Position);
        await tab.SeekReplayAsync(TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, (await files.Create().GetAsync(Target()))!.Position);
    }

    private static async Task FailedSeekAsync()
    {
        using var files = new HistoryFiles();
        var history = files.Create();
        var factory = new FakePlaybackEngineFactory();
        await using var tab = Tab(Target(), history, factory);
        await StartAsync(tab);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(30));
        tab.BeginReplaySeekPreview(5000);
        tab.CaptureVodResumePosition();
        Assert.Equal(TimeSpan.FromMinutes(30), (await history.GetAsync(Target()))!.Position);
        tab.CancelReplaySeekPreview();
        factory.Engine!.FailingSeekCount = 1;
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(70));
        tab.CaptureVodResumePosition(closing: true);
        Assert.Equal(TimeSpan.FromMinutes(30), (await history.GetAsync(Target()))!.Position);
    }

    private static async Task InvalidClocksAsync()
    {
        using var files = new HistoryFiles();
        var history = files.Create();
        var factory = new FakePlaybackEngineFactory();
        await using var tab = Tab(Target(), history, factory);
        await StartAsync(tab);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(30));
        foreach (var seconds in new[] { 0d, -1d, 8000d, 5000d })
        {
            factory.Engine!.PlaybackClockOverride = _ => (true, new(TimeSpan.FromSeconds(seconds), TimeSpan.FromHours(2), true));
            tab.CaptureVodResumePosition();
        }
        factory.Engine!.PlaybackClockOverride = _ => (false, new(TimeSpan.Zero, null, false));
        tab.CaptureVodResumePosition();
        factory.Engine.PlaybackClockOverride = _ => (true, new(TimeSpan.FromSeconds(1801), TimeSpan.FromHours(2), false));
        tab.CaptureVodResumePosition();
        factory.Engine.PlaybackClockOverride = _ => (true, new(TimeSpan.FromSeconds(1801), TimeSpan.FromHours(2), true));
        factory.Engine.PlaybackHealthOverride = () => new(1, PlaybackEngineState.Buffering, 0, 0, 0, 0);
        tab.CaptureVodResumePosition(closing: true);
        Assert.Equal(TimeSpan.FromMinutes(30), (await history.GetAsync(Target()))!.Position);
    }

    private static async Task FailedRestoreAsync()
    {
        using var files = new HistoryFiles();
        var history = await files.SeedAsync();
        var factory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine { FailingSeekCount = 1 });
        await using var tab = Tab(Target(), history, factory);
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(Settings());
        Assert.Equal(PlaybackStatus.Error, tab.Status);
        Assert.Equal(true, factory.Engine!.Stopped);
        Assert.Equal(TimeSpan.FromHours(1), (await files.Create().GetAsync(Target()))!.Position);
    }

    private static async Task CloseDuringStartupAsync()
    {
        using var files = new HistoryFiles();
        var history = await files.SeedAsync();
        var engine = new FakePlaybackEngine { PlayCompletion = new TaskCompletionSource().Task };
        var tab = Tab(Target(), history, new FakePlaybackEngineFactory(() => engine));
        tab.SetVideoHandle(new IntPtr(42));
        var starting = tab.StartAsync(Settings());
        await engine.PlayStarted.Task;
        await tab.DisposeAsync();
        await starting;
        Assert.Equal(TimeSpan.FromHours(1), (await files.Create().GetAsync(Target()))!.Position);
    }

    private static async Task CloseDuringSeekAsync()
    {
        using var files = new HistoryFiles();
        var history = files.Create();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakePlaybackEngine
        {
            SeekCompletion = release.Task,
            PlaybackClockOverride = _ => (true, new(TimeSpan.FromMinutes(20), TimeSpan.FromHours(2), true))
        };
        await using var tab = Tab(Target(), history, new FakePlaybackEngineFactory(() => engine));
        await StartAsync(tab);
        tab.CaptureVodResumePosition();
        var seeking = tab.SeekReplayAsync(TimeSpan.FromMinutes(50));
        try
        {
            await engine.SeekStarted.Task;
            tab.CaptureVodResumePosition(closing: true);
        }
        finally { release.TrySetResult(); }
        await seeking;
        Assert.Equal(TimeSpan.FromMinutes(20), (await history.GetAsync(Target()))!.Position);
    }

    private static async Task ImmediateReopenAsync()
    {
        using var files = new HistoryFiles();
        var history = files.Create();
        var factory = new FakePlaybackEngineFactory();
        await using var main = new MainViewModel(new MainViewModelDependencies
        {
            Settings = Settings(),
            SettingsService = new FakeSettingsService(Settings()),
            StreamlinkService = new FakeStreamlinkService(),
            PlaybackFactory = factory,
            ChatFactory = new FakeChatClientFactory(),
            Logger = new MemoryLogger(),
            Dispatch = action => action(),
            VodPlaybackHistory = history
        });
        var createTab = typeof(MainViewModel).GetMethod("CreateTab", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var old = (StreamTabViewModel)createTab.Invoke(main, [Target()])!;
        main.SelectedTab = old;
        // This fixture dispatches synchronously on worker threads. Drain the Home-to-tab
        // policy transition before asserting startup state, as the WPF dispatcher does.
        await main.InactivePlaybackPolicyIdleTask;
        await StartAsync(old);
        await old.SeekReplayAsync(TimeSpan.FromMinutes(30));
        var oldEngine = factory.Engine!;
        Assert.True(main.CloseTab(old));
        var reopened = (StreamTabViewModel)createTab.Invoke(main, [Target()])!;
        main.SelectedTab = reopened;
        await main.InactivePlaybackPolicyIdleTask;
        await StartAsync(reopened);
        Assert.Equal(TimeSpan.FromMinutes(30), factory.Engine!.LastStartPosition!.Value);
        await reopened.SeekReplayAsync(TimeSpan.FromMinutes(10));
        oldEngine.PlaybackClockOverride = _ => (true, new(TimeSpan.FromSeconds(1802), TimeSpan.FromHours(2), true));
        await old.DisposeAsync();
        Assert.Equal(TimeSpan.FromMinutes(10), (await files.Create().GetAsync(Target()))!.Position);
    }

    private static async Task DisabledReplayAsync()
    {
        using var files = new HistoryFiles();
        var factory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        { PlaybackClockOverride = _ => (true, new(TimeSpan.FromMinutes(20), TimeSpan.FromHours(2), true)) });
        await using (var tab = Tab(Target() with { MediaDuration = TimeSpan.Zero }, files.Create(), factory))
            await StartAsync(tab, Settings(replay: false));
        Assert.Equal(TimeSpan.FromMinutes(20), (await files.Create().GetAsync(Target()))!.Position);
    }

    private static async Task ApplicationShutdownAsync()
    {
        using var files = new HistoryFiles();
        var settings = Settings();
        settings.KeepInactiveTabsRunning = true;
        var factory = new FakePlaybackEngineFactory();
        await using var main = new MainViewModel(new MainViewModelDependencies
        {
            Settings = settings,
            SettingsService = new FakeSettingsService(settings),
            StreamlinkService = new FakeStreamlinkService(),
            PlaybackFactory = factory,
            ChatFactory = new FakeChatClientFactory(),
            Logger = new MemoryLogger(),
            Dispatch = action => action(),
            VodPlaybackHistory = files.Create()
        });
        var createTab = typeof(MainViewModel).GetMethod("CreateTab", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var i = 1; i <= 2; i++)
        {
            var target = Target(id: i.ToString());
            var tab = (StreamTabViewModel)createTab.Invoke(main, [target])!;
            main.SelectedTab = tab;
            await StartAsync(tab, settings);
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(i * 20));
            var finalPosition = TimeSpan.FromSeconds(i * 1200 + 1);
            factory.Engine!.PlaybackClockOverride = _ => (true, new(finalPosition, TimeSpan.FromHours(2), true));
        }
        await main.DisposeAsync();
        var reloaded = files.Create();
        Assert.Equal(TimeSpan.FromSeconds(1201), (await reloaded.GetAsync(Target(id: "1")))!.Position);
        Assert.Equal(TimeSpan.FromSeconds(2401), (await reloaded.GetAsync(Target(id: "2")))!.Position);
    }

    private static async Task CompletionAsync()
    {
        using var files = new HistoryFiles();
        var history = files.Create();
        var factory = new FakePlaybackEngineFactory();
        await using var tab = Tab(Target(), history, factory);
        await StartAsync(tab);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(30));
        // Decoder errors are not completion. An actual EOF is authoritative even if
        // the previous resume sample was far from the end or no final sample arrived.
        factory.Engine!.PlaybackHealthOverride = () => new(1, PlaybackEngineState.Error, 0, 0, 0, 0);
        tab.CaptureVodResumePosition();
        Assert.Equal(false, (await history.GetAsync(Target()))!.Completed);
        factory.Engine.PlaybackHealthOverride = null;
        await tab.SeekReplayAsync(TimeSpan.FromSeconds(7198));
        Assert.Equal(false, (await history.GetAsync(Target()))!.Completed);
        factory.Engine.PlaybackHealthOverride = () => new(1, PlaybackEngineState.Ended, 7200000, 0, 0, 0);
        tab.CaptureVodResumePosition(closing: true);
        await tab.DisposeAsync();
        var reopenedFactory = new FakePlaybackEngineFactory();
        await using var reopened = Tab(Target(), files.Create(), reopenedFactory);
        await StartAsync(reopened);
        Assert.Equal<TimeSpan?>(null, reopenedFactory.Engine!.LastStartPosition);
    }

    private static async Task IdentityAsync()
    {
        using var files = new HistoryFiles();
        var history = await files.SeedAsync();
        Assert.Equal<VodPlaybackBookmark?>(null, await history.GetAsync(Target(id: "999")));
        Assert.Equal<VodPlaybackBookmark?>(null, await history.GetAsync(Target(PlatformKind.Kick)));
        var live = Target() with { Kind = StreamTargetKind.Live };
        history.Remember(live, Bookmark(99));
        Assert.Equal<VodPlaybackBookmark?>(null, await history.GetAsync(live));
        Assert.Equal(TimeSpan.FromHours(1), (await history.GetAsync(Target() with { Channel = "renamed", Url = "https://example.com/new" }))!.Position);
    }

    private static async Task CorruptHistoryAsync()
    {
        using var files = new HistoryFiles();
        await File.WriteAllTextAsync(files.Path, "{broken");
        Assert.Equal<VodPlaybackBookmark?>(null, await files.Create().GetAsync(Target()));
        Assert.Equal("{broken", await File.ReadAllTextAsync(Directory.GetFiles(files.Directory, "*.invalid-*").Single()));
        await File.WriteAllTextAsync(files.Path, """[{"Platform":0,"MediaId":"12345","Bookmark":{"Position":"-01:00:00","Duration":"02:00:00","UpdatedAtUtc":"2026-09-26T00:00:00Z"}}]""");
        Assert.Equal<VodPlaybackBookmark?>(null, await files.Create().GetAsync(Target()));
        await files.SeedAsync();
        Assert.Equal(TimeSpan.FromHours(1), (await files.Create().GetAsync(Target()))!.Position);
    }

    private static async Task ConcurrentHistoryAsync()
    {
        using var files = new HistoryFiles();
        var history = files.Create();
        await Task.WhenAll(Enumerable.Range(1, 20).Select(async index =>
        {
            var target = Target(id: index.ToString());
            await history.GetAsync(target);
            history.Remember(target, Bookmark(index));
            await history.SaveAsync();
        }));
        var reloaded = files.Create();
        for (var i = 1; i <= 20; i++) Assert.Equal(TimeSpan.FromSeconds(i), (await reloaded.GetAsync(Target(id: i.ToString())))!.Position);
        for (var i = 21; i <= 1100; i++) history.Remember(Target(id: i.ToString()), Bookmark(i));
        await history.SaveAsync();
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(files.Path));
        Assert.Equal(1000, document.RootElement.GetArrayLength());
    }

    private static async Task WriteFailureAsync()
    {
        using var files = new HistoryFiles();
        var history = await files.SeedAsync();
        using (var held = new FileStream(files.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            history.Remember(Target(), Bookmark(200));
            var failed = false;
            try { await history.SaveAsync(); } catch (IOException) { failed = true; }
            Assert.True(failed);
            Assert.Equal(TimeSpan.FromSeconds(200), (await history.GetAsync(Target()))!.Position);
        }
        await history.SaveAsync();
        Assert.Equal(TimeSpan.FromSeconds(200), (await files.Create().GetAsync(Target()))!.Position);
        Assert.Equal(0, Directory.GetFiles(files.Directory, "*.tmp").Length);
    }

    private static async Task ReadFailureAsync()
    {
        using var files = new HistoryFiles();
        await files.SeedAsync();
        var factory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        { PlaybackClockOverride = _ => (true, new(TimeSpan.FromSeconds(30), TimeSpan.FromHours(2), true)) });
        await using (var tab = Tab(Target(), files.Create(), factory))
        {
            using (var held = new FileStream(files.Path, FileMode.Open, FileAccess.Read, FileShare.None))
                await StartAsync(tab);
            tab.CaptureVodResumePosition();
        }
        Assert.Equal(TimeSpan.FromHours(1), (await files.Create().GetAsync(Target()))!.Position);
    }

    private static async Task DurationChangeAsync()
    {
        using var files = new HistoryFiles();
        var history = files.Create();
        await history.GetAsync(Target());
        history.Remember(Target(), Bookmark(7198));
        await history.SaveAsync();
        var factory = new FakePlaybackEngineFactory();
        await using (var tab = Tab(Target(), history, factory))
        {
            await StartAsync(tab);
            Assert.Equal(TimeSpan.FromSeconds(7198), factory.Engine!.LastStartPosition!.Value);
        }
        factory = new FakePlaybackEngineFactory();
        await using var staleMetadata = Tab(Target() with { MediaDuration = TimeSpan.FromHours(1) }, files.Create(), factory);
        await StartAsync(staleMetadata);
        Assert.Equal(TimeSpan.FromSeconds(7198), factory.Engine!.LastStartPosition!.Value);
        Assert.True(staleMetadata.ReplaySeekValue >= 7198);
        Assert.Equal(7200d, staleMetadata.ReplaySeekMaximum);
    }

    private static VodPlaybackBookmark Bookmark(double seconds) => new(TimeSpan.FromSeconds(seconds), TimeSpan.FromHours(2), DateTimeOffset.UtcNow);

    internal sealed class HistoryFiles : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StreamStudioTests", Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "vod-history.json");
        internal HistoryFiles() => System.IO.Directory.CreateDirectory(Directory);
        internal JsonVodPlaybackHistory Create() => new(Path, new MemoryLogger());
        internal async Task<JsonVodPlaybackHistory> SeedAsync()
        {
            var history = Create();
            await history.GetAsync(Target());
            history.Remember(Target(), Bookmark(3600));
            await history.SaveAsync();
            return history;
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed class OffsetChatProvider : IVodChatProvider
    {
        internal ConcurrentQueue<TimeSpan> Offsets { get; } = new();
        public Task<VodChatFetchResult> FetchAsync(ReplaySessionInfo replay, AppSettings settings,
            TimeSpan startOffset, CancellationToken cancellationToken = default)
        {
            Offsets.Enqueue(startOffset);
            return Task.FromResult(VodChatFetchResult.Completed([], replay.Duration));
        }
    }
}

internal static class StreamOpenWorkflowTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("stream open workflow: new playback starts before optional metadata completes", ImmediatePlaybackAsync),
        ("stream open workflow: late metadata preserves Home navigation", () => MetadataHomeNavigationAsync(background: false)),
        ("stream open workflow: a new background tab keeps Home selected while metadata loads", () => MetadataHomeNavigationAsync(background: true)),
        ("stream open workflow: late metadata preserves the latest selected tab and quality", MetadataSelectionAsync),
        ("stream open workflow: closing a tab cancels its optional metadata", () => ClosedTabMetadataAsync(ignoreCancellation: false)),
        ("stream open workflow: a closed tab ignores metadata even when its provider ignores cancellation", () => ClosedTabMetadataAsync(ignoreCancellation: true)),
        ("stream open workflow: metadata failure does not prevent playback", FailedMetadataAsync),
        ("stream open workflow: late opening metadata preserves the current live category", () => PolledCategoryAsync("Sports")),
        ("stream open workflow: late opening metadata preserves a cleared live category", () => PolledCategoryAsync("")),
        ("stream open workflow: late metadata preserves Recent watch order", () => RecentMetadataAsync(deleteFirst: false)),
        ("stream open workflow: late metadata does not restore a deleted Recent card", () => RecentMetadataAsync(deleteFirst: true)),
        ("stream open workflow: existing tabs bypass slow metadata requests", ExistingTabMetadataAsync),
        ("stream open workflow: reopening a manually paused live stream preserves playback", () => ReopenPausedAsync(vod: false, background: false)),
        ("stream open workflow: reopening a manually paused VOD preserves playback", () => ReopenPausedAsync(vod: true, background: false)),
        ("stream open workflow: background opening preserves manual pause and Home", () => ReopenPausedAsync(vod: false, background: true)),
        ("stream open workflow: reopening an automatically paused tab lets selection resume it", ReopenAutomaticallyPausedAsync),
        ("stream open workflow: overlapping metadata completions still share one tab and startup", OverlappingMetadataAsync),
        ("stream open workflow: a late startup preserves the next search", () => StartupSearchAsync(changeQuery: true)),
        ("stream open workflow: editing away and back still preserves the new search", () => StartupSearchAsync(changeQuery: true, returnToOriginal: true)),
        ("stream open workflow: successful startup clears its unchanged search", () => StartupSearchAsync(changeQuery: false)),
        ("stream open workflow: metadata delays cannot transfer ownership of a newer search", MetadataSearchAsync),
        ("stream open workflow: reopening a playing tab clears its search without restarting", ReopenSearchAsync),
        ("stream open workflow: reopening a stopped tab starts it and clears its search", ReopenStoppedAsync),
        ("stream open workflow: failed startup preserves its search for retry", FailedStartupAsync)
    ];

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(3);

    private static StreamTarget LiveTarget => StreamInputParser.Parse("streamer", PlatformKind.Twitch);

    private static StreamMetadataResult LoadedMetadata => new(StreamMetadataState.Available,
        "https://example.invalid/thumbnail.jpg", "Streamer", "loaded", "Games", "https://example.invalid/avatar.jpg");

    private static Task ImmediatePlaybackAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var metadata = new PendingMetadataService();
        await using var fixture = new Fixture(metadata);
        var open = fixture.Main.OpenStreamAsync(LiveTarget);
        Assert.True(open.IsCompletedSuccessfully);
        var tab = fixture.Main.Tabs.Single();
        tab.Title = "My stream";
        tab.SetVideoHandle(new IntPtr(1234));
        await TestWait.UntilAsync(() => tab.Status == PlaybackStatus.Playing && fixture.Main.RecentStreams.Count == 1, WaitTimeout);
        Assert.Equal(1, metadata.CallCount);
        Assert.Equal("", tab.CategoryName);
        metadata.Release.SetResult(LoadedMetadata);
        await TestWait.UntilAsync(() => tab.CategoryName == "Games" && fixture.Main.RecentStreams[0].ThumbnailUrl == LoadedMetadata.ThumbnailUrl, WaitTimeout);
        Assert.Equal(LoadedMetadata.ProfileImageUrl, tab.ProfileImageUrl);
        Assert.Equal("Games", fixture.Main.TabStripItems.Single().SubtitleText);
        Assert.Equal("My stream", tab.Title);
        Assert.Equal(1, metadata.CallCount);
        Assert.Equal(1, fixture.Streamlink.StartCount);
    });

    private static Task MetadataHomeNavigationAsync(bool background) => TestSta.RunOffscreenAsync(async () =>
    {
        var metadata = new PendingMetadataService();
        await using var fixture = new Fixture(metadata);
        var open = fixture.Main.OpenStreamAsync(LiveTarget, selectOpenedTab: !background);
        if (!background) fixture.Main.SelectHomeCommand.Execute(null);
        Assert.Equal(true, fixture.Main.IsHomeSelected);
        metadata.Release.SetResult(LoadedMetadata);
        await open;
        await TestWait.UntilAsync(() => fixture.Main.Tabs.Single().CategoryName == "Games", WaitTimeout);
        Assert.Equal(true, fixture.Main.IsHomeSelected);
        Assert.Equal<StreamTabViewModel?>(null, fixture.Main.SelectedTab);
    });

    private static Task MetadataSelectionAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var metadata = new PendingMetadataService();
        await using var fixture = new Fixture(metadata);
        fixture.Main.SelectedQuality = "720p";
        var firstOpen = fixture.Main.OpenStreamAsync(LiveTarget);
        fixture.Main.SelectHomeCommand.Execute(null);
        fixture.Main.SelectedQuality = "1080p";
        var secondTarget = StreamInputParser.Parse("anotherstream", PlatformKind.Kick) with
        {
            CategoryName = "Sports",
            ProfileImageUrl = "https://example.invalid/other.jpg"
        };
        await fixture.Main.OpenStreamAsync(secondTarget);
        var latest = fixture.Main.SelectedTab;
        metadata.Release.SetResult(LoadedMetadata);
        await firstOpen;
        await TestWait.UntilAsync(() => fixture.Main.Tabs.Any(tab => tab.CategoryName == "Games"), WaitTimeout);
        Assert.Equal(latest, fixture.Main.SelectedTab);
        Assert.Equal("1080p", fixture.Main.SelectedQuality);
        Assert.Equal("720p", fixture.Main.Tabs[0].Quality);
        Assert.Equal(LiveTarget.TabIdentityKey, fixture.Main.Tabs[0].Target.TabIdentityKey);
        Assert.Equal(secondTarget.TabIdentityKey, fixture.Main.Tabs[1].Target.TabIdentityKey);
        Assert.Equal(1, metadata.CallCount);
    });

    private static Task ClosedTabMetadataAsync(bool ignoreCancellation) => TestSta.RunOffscreenAsync(async () =>
    {
        var metadata = new PendingMetadataService { IgnoreCancellation = ignoreCancellation };
        await using var fixture = new Fixture(metadata);
        var open = fixture.Main.OpenStreamAsync(LiveTarget);
        Assert.True(open.IsCompletedSuccessfully);
        var tab = fixture.Main.Tabs.Single();
        Assert.True(fixture.Main.CloseTab(tab));
        await TestWait.UntilAsync(() => metadata.Token.IsCancellationRequested, WaitTimeout);
        if (ignoreCancellation) Assert.True(metadata.Token.WaitHandle.WaitOne(0));
        metadata.Release.SetResult(LoadedMetadata);
        await open;
        await fixture.Main.DisposeAsync().AsTask().WaitAsync(WaitTimeout);
        Assert.Equal(0, fixture.Main.Tabs.Count);
        Assert.Equal(true, fixture.Main.IsHomeSelected);
        Assert.Equal("", tab.CategoryName);
        Assert.Equal("", tab.ProfileImageUrl);
        Assert.Equal(0, fixture.Streamlink.StartCount);
    });

    private static Task FailedMetadataAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var metadata = new PendingMetadataService();
        await using var fixture = new Fixture(metadata);
        var open = fixture.Main.OpenStreamAsync(LiveTarget);
        Assert.True(open.IsCompletedSuccessfully);
        var tab = fixture.Main.Tabs.Single();
        tab.SetVideoHandle(new IntPtr(1234));
        metadata.Release.SetException(new InvalidOperationException("Metadata offline"));
        await TestWait.UntilAsync(() => tab.Status == PlaybackStatus.Playing, WaitTimeout);
        Assert.Equal(tab, fixture.Main.SelectedTab);
        Assert.Equal(1, fixture.Streamlink.StartCount);
    });

    private static Task PolledCategoryAsync(string category) => TestSta.RunOffscreenAsync(async () =>
    {
        var metadata = new PendingMetadataService();
        var viewers = new FakeViewerCountService
        {
            Responder = _ => new(ViewerCountState.Available, 222, "current", category)
        };
        await using var fixture = new Fixture(metadata, viewers: viewers);
        await fixture.Main.OpenStreamAsync(LiveTarget);
        var tab = fixture.Main.Tabs.Single();
        tab.SetVideoHandle(new IntPtr(1234));
        await TestWait.UntilAsync(() => tab.ViewerCountText == "222", WaitTimeout);
        metadata.Release.SetResult(LoadedMetadata);
        await TestWait.UntilAsync(() => tab.HasProfileImage, WaitTimeout);
        Assert.Equal(category, tab.CategoryName);
    });

    private static Task RecentMetadataAsync(bool deleteFirst) => TestSta.RunOffscreenAsync(async () =>
    {
        var metadata = new PendingMetadataService();
        await using var fixture = new Fixture(metadata);
        await fixture.Main.OpenStreamAsync(LiveTarget);
        var first = fixture.Main.Tabs.Single();
        first.SetVideoHandle(new IntPtr(1234));
        await TestWait.UntilAsync(() => fixture.Main.RecentStreams.Count == 1, WaitTimeout);
        var firstWatchedAt = fixture.Main.Settings.RecentStreams[0].LastWatchedAtUtc;
        if (deleteFirst) await fixture.Main.RecentStreams[0].DeleteCommand.ExecuteAsync();

        var secondTarget = StreamInputParser.Parse("nextstream", PlatformKind.Kick);
        await fixture.Main.OpenStreamAsync(secondTarget);
        fixture.Main.SelectedTab!.SetVideoHandle(new IntPtr(5678));
        await TestWait.UntilAsync(() => fixture.Main.RecentStreams.Count == (deleteFirst ? 1 : 2), WaitTimeout);
        metadata.Release.SetResult(LoadedMetadata);
        await TestWait.UntilAsync(() => fixture.Main.RecentStreams.All(card => card.ThumbnailUrl == LoadedMetadata.ThumbnailUrl), WaitTimeout);
        // Drain the first enrichment too before checking that it did not resurrect a deleted row.
        await fixture.Main.DisposeAsync().AsTask().WaitAsync(WaitTimeout);
        Assert.SequenceEqual(deleteFirst ? new[] { "nextstream" } : new[] { "nextstream", "streamer" },
            fixture.Main.Settings.RecentStreams.Select(stream => stream.Channel));
        if (!deleteFirst)
            Assert.Equal(firstWatchedAt, fixture.Main.Settings.RecentStreams[1].LastWatchedAtUtc);
        Assert.Equal(2, metadata.CallCount);
    });

    private static Task ExistingTabMetadataAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var metadata = new PendingMetadataService();
        await using var fixture = new Fixture(metadata);
        var tab = await fixture.AddPlayingTabAsync(LiveTarget);
        fixture.Main.SelectHomeCommand.Execute(null);
        var open = fixture.Main.OpenStreamAsync(LiveTarget with { Channel = "STREAMER" });
        try
        {
            Assert.True(open.IsCompletedSuccessfully);
            Assert.Equal(0, metadata.CallCount);
            Assert.Equal(tab, fixture.Main.SelectedTab);
            Assert.Equal(1, fixture.Main.Tabs.Count);
            Assert.Equal(1, fixture.Playback.CreateCount);
        }
        finally
        {
            metadata.Release.TrySetResult(new(StreamMetadataState.Available, "", "", "loaded"));
            await open;
        }
    });

    private static Task ReopenPausedAsync(bool vod, bool background) => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new Fixture();
        var target = vod
            ? new StreamTarget(PlatformKind.Twitch, "streamer", "https://www.twitch.tv/videos/12345",
                StreamTargetKind.TwitchVod, "12345", "A replay", MediaDuration: TimeSpan.FromHours(2))
            : LiveTarget;
        var tab = await fixture.AddPlayingTabAsync(target);
        var engine = fixture.Playback.Engine!;
        await engine.SeekAsync(TimeSpan.FromMinutes(10));
        await tab.PauseOrResumeAsync();
        Assert.Equal(PlaybackStatus.Paused, tab.Status);
        fixture.Main.SelectHomeCommand.Execute(null);
        await fixture.Main.InactivePlaybackPolicyIdleTask.WaitAsync(WaitTimeout);

        await fixture.Main.OpenStreamAsync(target, selectOpenedTab: !background);
        await TestWait.UntilAsync(() => fixture.Main.StatusMessage != $"Starting {target.DisplayName}", WaitTimeout);
        await fixture.Main.InactivePlaybackPolicyIdleTask.WaitAsync(WaitTimeout);

        Assert.Equal(PlaybackStatus.Paused, tab.Status);
        Assert.Equal(false, tab.PausedByTabSwitch);
        Assert.Equal(true, engine.Paused);
        Assert.Equal(TimeSpan.FromMinutes(10), engine.Position);
        Assert.Equal(1, fixture.Playback.CreateCount);
        Assert.Equal(1, engine.PlayCount);
        Assert.Equal(background, fixture.Main.IsHomeSelected);
        Assert.Equal(background ? null : tab, fixture.Main.SelectedTab);
    });

    private static Task StartupSearchAsync(bool changeQuery, bool returnToOriginal = false) => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakePlaybackEngine { PlayCompletion = release.Task };
        await using var fixture = new Fixture(engine: engine);
        fixture.Main.NewStreamText = "streamer";
        await fixture.Main.OpenStreamAsync(LiveTarget, clearInputOnSuccess: true);
        var tab = fixture.Main.Tabs.Single();
        tab.SetVideoHandle(new IntPtr(1234));
        await engine.PlayStarted.Task.WaitAsync(WaitTimeout);
        if (changeQuery)
        {
            fixture.Main.NewStreamText = "next-stream";
            if (returnToOriginal) fixture.Main.NewStreamText = "streamer";
        }
        release.TrySetResult();
        await TestWait.UntilAsync(() => fixture.Main.StatusMessage == $"{tab.Target.DisplayName} playing", WaitTimeout);
        Assert.Equal(changeQuery ? returnToOriginal ? "streamer" : "next-stream" : "", fixture.Main.NewStreamText);
    });

    private static Task ReopenAutomaticallyPausedAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new Fixture();
        var tab = await fixture.AddPlayingTabAsync(LiveTarget);
        fixture.Main.SelectHomeCommand.Execute(null);
        await fixture.Main.InactivePlaybackPolicyIdleTask.WaitAsync(WaitTimeout);
        Assert.Equal(PlaybackStatus.Paused, tab.Status);
        Assert.Equal(true, tab.PausedByTabSwitch);

        await fixture.Main.OpenStreamAsync(LiveTarget);
        await fixture.Main.InactivePlaybackPolicyIdleTask.WaitAsync(WaitTimeout);

        Assert.Equal(tab, fixture.Main.SelectedTab);
        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        Assert.Equal(false, tab.PausedByTabSwitch);
        Assert.Equal(1, fixture.Playback.CreateCount);
        Assert.Equal(1, fixture.Streamlink.StartCount);
    });

    private static Task OverlappingMetadataAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var metadata = new PendingMetadataService();
        await using var fixture = new Fixture(metadata);
        var first = fixture.Main.OpenStreamAsync(LiveTarget);
        var second = fixture.Main.OpenStreamAsync(LiveTarget with { Channel = "STREAMER" });
        Assert.True(first.IsCompletedSuccessfully && second.IsCompletedSuccessfully);
        Assert.Equal(1, metadata.CallCount);
        metadata.Release.SetResult(new(StreamMetadataState.Available, "", "", "loaded"));
        await Task.WhenAll(first, second);
        var tab = fixture.Main.Tabs.Single();
        tab.SetVideoHandle(new IntPtr(1234));
        await TestWait.UntilAsync(() => tab.Status == PlaybackStatus.Playing, WaitTimeout);
        Assert.Equal(1, fixture.Playback.CreateCount);
        Assert.Equal(1, fixture.Streamlink.StartCount);
    });

    private static Task MetadataSearchAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var metadata = new PendingMetadataService();
        await using var fixture = new Fixture(metadata);
        fixture.Main.NewStreamText = "streamer";
        var open = fixture.Main.OpenStreamAsync(LiveTarget, clearInputOnSuccess: true);
        Assert.Equal(1, metadata.CallCount);
        fixture.Main.NewStreamText = "next-stream";
        metadata.Release.SetResult(new(StreamMetadataState.Available, "", "", "loaded", "Games"));
        await open;
        var tab = fixture.Main.Tabs.Single();
        tab.SetVideoHandle(new IntPtr(1234));
        await TestWait.UntilAsync(() => fixture.Main.StatusMessage == $"{tab.Target.DisplayName} playing", WaitTimeout);
        Assert.Equal("next-stream", fixture.Main.NewStreamText);
    });

    private static Task ReopenSearchAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new Fixture();
        var tab = await fixture.AddPlayingTabAsync(LiveTarget);
        fixture.Main.NewStreamText = "streamer";
        await fixture.Main.OpenStreamAsync(LiveTarget, clearInputOnSuccess: true);
        Assert.Equal("", fixture.Main.NewStreamText);
        Assert.Equal(tab, fixture.Main.SelectedTab);
        Assert.Equal(1, fixture.Playback.CreateCount);
        Assert.Equal(1, fixture.Streamlink.StartCount);
    });

    private static Task ReopenStoppedAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new Fixture();
        var tab = await fixture.AddPlayingTabAsync(LiveTarget);
        await tab.StopAsync();
        fixture.Main.NewStreamText = "streamer";
        await fixture.Main.OpenStreamAsync(LiveTarget, clearInputOnSuccess: true);
        await TestWait.UntilAsync(() => fixture.Main.StatusMessage == $"{tab.Target.DisplayName} playing", WaitTimeout);
        Assert.Equal("", fixture.Main.NewStreamText);
        Assert.Equal(1, fixture.Main.Tabs.Count);
        Assert.Equal(2, fixture.Streamlink.StartCount);
        Assert.Equal(PlaybackStatus.Playing, tab.Status);
    });

    private static Task FailedStartupAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new Fixture();
        fixture.Streamlink.StartExternalHttpOverride = (_, _) => throw new InvalidOperationException("Cannot connect");
        fixture.Main.NewStreamText = "streamer";
        await fixture.Main.OpenStreamAsync(LiveTarget, clearInputOnSuccess: true);
        fixture.Main.Tabs.Single().SetVideoHandle(new IntPtr(1234));
        await TestWait.UntilAsync(() => fixture.Main.StatusMessage.Contains("Cannot connect", StringComparison.Ordinal), WaitTimeout);
        Assert.Equal("streamer", fixture.Main.NewStreamText);
    });

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly AppSettings settings = new()
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        private readonly FakeChatClientFactory chat = new();
        private readonly MemoryLogger logger = new();

        public FakeStreamlinkService Streamlink { get; } = new();
        public FakePlaybackEngineFactory Playback { get; }
        public MainViewModel Main { get; }

        public Fixture(IStreamMetadataService? metadata = null, FakePlaybackEngine? engine = null,
            IViewerCountService? viewers = null)
        {
            settings.Chat.ConnectAutomatically = false;
            Playback = new FakePlaybackEngineFactory(engine is null ? null : () => engine);
            Main = TestViewModels.CreateMain(settings, new FakeSettingsService(settings), Streamlink,
                Playback, chat, logger, action => action(), streamMetadataService: metadata,
                streamSearchDebounceInterval: TimeSpan.FromHours(1), viewerCountService: viewers);
        }

        public async Task<StreamTabViewModel> AddPlayingTabAsync(StreamTarget target)
        {
            var tab = TestViewModels.CreateTab(target, "best", Streamlink, Playback, chat, logger, action => action());
            Main.Tabs.Add(tab);
            Main.SelectedTab = tab;
            await Main.InactivePlaybackPolicyIdleTask.WaitAsync(WaitTimeout);
            tab.SetVideoHandle(new IntPtr(1234));
            await tab.StartAsync(settings);
            await Main.InactivePlaybackPolicyIdleTask.WaitAsync(WaitTimeout);
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            return tab;
        }

        public ValueTask DisposeAsync() => Main.DisposeAsync();
    }

    private sealed class PendingMetadataService : IStreamMetadataService
    {
        public TaskCompletionSource<StreamMetadataResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }
        public CancellationToken Token { get; private set; }
        public bool IgnoreCancellation { get; init; }

        public Task<StreamMetadataResult> GetLiveStreamMetadataAsync(StreamTarget target, AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Token = cancellationToken;
            return IgnoreCancellation ? Release.Task : Release.Task.WaitAsync(cancellationToken);
        }
    }
}

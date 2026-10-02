using System.Windows.Threading;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> InactiveVodPlaybackTests =>
    [
        ("inactive VOD playback: Twitch follows its own toggle independently of live tabs", () => InactiveVodPolicyAsync(PlatformKind.Twitch)),
        ("inactive VOD playback: Kick follows its own toggle independently of live tabs", () => InactiveVodPolicyAsync(PlatformKind.Kick)),
        ("inactive VOD playback: offline Twitch follows its own toggle", () => InactiveVodPolicyAsync(PlatformKind.Twitch, offline: true)),
        ("inactive VOD playback: offline Kick follows its own toggle", () => InactiveVodPolicyAsync(PlatformKind.Kick, offline: true)),
        ("inactive VOD playback: setting changes immediately pause and resume hidden VODs", InactiveVodSettingChangesAsync),
        ("inactive VOD playback: manual pauses survive setting changes and tab selection", InactiveVodManualPauseAsync),
        ("inactive VOD playback: visible grid and picture-in-picture VODs keep playing", InactiveVodVisibleAsync),
        ("inactive VOD playback: Never mute protects a hidden VOD", InactiveVodNeverMuteAsync),
        ("inactive VOD playback: enabling during startup pauses the completed player", () => InactiveVodLateStartupAsync(enable: true)),
        ("inactive VOD playback: disabling during startup keeps the completed player running", () => InactiveVodLateStartupAsync(enable: false)),
        ("inactive VOD playback: disabling during a pending pause wins after it completes", InactiveVodPendingPauseAsync),
        ("inactive VOD playback: older settings preserve their policy and explicit values survive reload", InactiveVodSettingsMigrationAsync),
        ("inactive VOD playback: Playback toggle binds and fits wide and compact layouts", InactiveVodSettingsLayoutAsync),
        .. string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ? [] :
            new (string, Func<Task>)[]
            {
                ("inactive VOD playback: native MP4 pauses and resumes at the same position", () => InactiveVodNativeAsync(hls: false)),
                ("inactive VOD playback: native HLS pauses and resumes at the same position", () => InactiveVodNativeAsync(hls: true)),
                ("inactive VOD playback: native growing HLS pauses and resumes at the same position", () => InactiveVodNativeAsync(hls: true, growing: true))
            }
    ];

    private static Task InactiveVodPolicyAsync(PlatformKind platform, bool offline = false) => TestSta.RunOffscreenAsync(async () =>
    {
        foreach (var keepLiveTabsRunning in new[] { false, true })
            foreach (var pauseVodTabs in new[] { false, true })
            {
                await using var fixture = new InactiveVodPolicyFixture();
                fixture.Settings.KeepInactiveTabsRunning = keepLiveTabsRunning;
                fixture.Settings.PauseInactiveVodTabs = pauseVodTabs;
                var vod = fixture.AddVod(platform, offline);
                var live = fixture.AddLive();
                await fixture.StartSelectedAsync(vod.Tab);
                await vod.Tab.SeekReplayAsync(TimeSpan.FromMinutes(14));
                await fixture.StartSelectedAsync(live.Tab);

                Assert.Equal(pauseVodTabs ? PlaybackStatus.Paused : PlaybackStatus.Playing, vod.Tab.Status);
                Assert.Equal(pauseVodTabs, vod.Tab.PausedByTabSwitch);
                Assert.Equal(pauseVodTabs, vod.Engine.Paused);
                Assert.Equal(pauseVodTabs, vod.Tab.IsBackgroundResourceServicesSuspended);
                Assert.Equal(PlaybackAudioState.Muted, vod.Engine.AudioState);
                Assert.Equal(PlaybackStatus.Playing, live.Tab.Status);

                fixture.Main.SelectedTab = vod.Tab;
                await fixture.WaitForPolicyAsync();
                Assert.Equal(PlaybackStatus.Playing, vod.Tab.Status);
                Assert.Equal(false, vod.Tab.PausedByTabSwitch);
                Assert.Equal(TimeSpan.FromMinutes(14), vod.Engine.Position);
                Assert.Equal(1, vod.Factory.CreateCount);
                Assert.Equal(0, vod.Engine.StopCount);
                Assert.Equal(PlaybackAudioState.Audible, vod.Engine.AudioState);
                Assert.Equal(keepLiveTabsRunning ? PlaybackStatus.Playing : PlaybackStatus.Paused, live.Tab.Status);
            }
    });

    private static Task InactiveVodSettingChangesAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new InactiveVodPolicyFixture();
        fixture.Settings.KeepInactiveTabsRunning = true;
        var vod = fixture.AddVod(PlatformKind.Twitch);
        var live = fixture.AddLive();
        await fixture.StartSelectedAsync(vod.Tab);
        await vod.Tab.SeekReplayAsync(TimeSpan.FromMinutes(14));
        await fixture.StartSelectedAsync(live.Tab);
        Assert.Equal(PlaybackStatus.Paused, vod.Tab.Status);

        fixture.Settings.PauseInactiveVodTabs = false;
        await fixture.WaitForPolicyAsync();
        Assert.Equal(PlaybackStatus.Playing, vod.Tab.Status);
        Assert.Equal(TimeSpan.FromMinutes(14), vod.Engine.Position);
        Assert.Equal(false, vod.Tab.PausedByTabSwitch);
        Assert.Equal(false, vod.Tab.IsBackgroundResourceServicesSuspended);

        fixture.Settings.PauseInactiveVodTabs = true;
        await fixture.WaitForPolicyAsync();
        Assert.Equal(PlaybackStatus.Paused, vod.Tab.Status);
        fixture.Settings.KeepInactiveTabsRunning = false;
        await fixture.WaitForPolicyAsync();
        Assert.Equal(PlaybackStatus.Paused, vod.Tab.Status);
        fixture.Settings.PauseInactiveVodTabs = false;
        await fixture.WaitForPolicyAsync();
        Assert.Equal(PlaybackStatus.Playing, vod.Tab.Status);
        Assert.Equal(PlaybackAudioState.Muted, vod.Engine.AudioState);
        Assert.Equal(PlaybackStatus.Playing, live.Tab.Status);
        Assert.Equal(PlaybackAudioState.Audible, live.Engine.AudioState);
        Assert.Equal(live.Tab, fixture.Main.SelectedTab);
    });

    private static Task InactiveVodManualPauseAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new InactiveVodPolicyFixture();
        var vod = fixture.AddVod(PlatformKind.Kick);
        var live = fixture.AddLive();
        await fixture.StartSelectedAsync(vod.Tab);
        await vod.Tab.PauseOrResumeAsync();
        Assert.Equal(false, vod.Tab.PausedByTabSwitch);
        await fixture.StartSelectedAsync(live.Tab);
        foreach (var pauseVodTabs in new[] { false, true, false })
        {
            fixture.Settings.PauseInactiveVodTabs = pauseVodTabs;
            await fixture.WaitForPolicyAsync();
            Assert.Equal(PlaybackStatus.Paused, vod.Tab.Status);
            Assert.Equal(false, vod.Tab.PausedByTabSwitch);
        }

        fixture.Main.SelectedTab = vod.Tab;
        await fixture.WaitForPolicyAsync();
        Assert.Equal(PlaybackStatus.Paused, vod.Tab.Status);
        Assert.Equal(true, vod.Engine.Paused);
        Assert.Equal(0, vod.Engine.ResumeCount);
    });

    private static Task InactiveVodVisibleAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new InactiveVodPolicyFixture();
        fixture.Settings.MultiStreamEnabled = true;
        var vod = fixture.AddVod(PlatformKind.Twitch);
        var live = fixture.AddLive();
        await fixture.StartSelectedAsync(vod.Tab);
        await fixture.StartSelectedAsync(live.Tab);
        Assert.Equal(true, vod.Tab.IsVideoVisible);
        Assert.Equal(false, vod.Tab.IsSelected);
        Assert.Equal(PlaybackStatus.Playing, vod.Tab.Status);

        fixture.Settings.MultiStreamEnabled = false;
        await fixture.WaitForPolicyAsync();
        Assert.Equal(PlaybackStatus.Paused, vod.Tab.Status);
        vod.Tab.SetDetached(true);
        await fixture.WaitForPolicyAsync();
        Assert.Equal(PlaybackStatus.Playing, vod.Tab.Status);
        Assert.Equal(false, vod.Tab.PausedByTabSwitch);
        fixture.Main.SelectHomeCommand.Execute(null);
        await fixture.WaitForPolicyAsync();
        Assert.Equal(PlaybackStatus.Playing, vod.Tab.Status);

        vod.Tab.SetDetached(false);
        await fixture.WaitForPolicyAsync();
        Assert.Equal(PlaybackStatus.Paused, vod.Tab.Status);
        Assert.Equal(true, vod.Tab.PausedByTabSwitch);
    });

    private static Task InactiveVodNeverMuteAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new InactiveVodPolicyFixture();
        var vod = fixture.AddVod(PlatformKind.Twitch);
        var live = fixture.AddLive();
        vod.Tab.NeverMute = true;
        await fixture.StartSelectedAsync(vod.Tab);
        await fixture.StartSelectedAsync(live.Tab);
        Assert.Equal(PlaybackStatus.Playing, vod.Tab.Status);
        Assert.Equal(PlaybackAudioState.Audible, vod.Engine.AudioState);
        vod.Tab.NeverMute = false;
        await fixture.WaitForPolicyAsync();
        Assert.Equal(PlaybackStatus.Paused, vod.Tab.Status);
        Assert.Equal(true, vod.Tab.PausedByTabSwitch);
    });

    private static Task InactiveVodLateStartupAsync(bool enable) => TestSta.RunOffscreenAsync(async () =>
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakePlaybackEngine { PlayCompletion = finish.Task, PreservesReplayPositionOnResume = true };
        await using var fixture = new InactiveVodPolicyFixture();
        fixture.Settings.KeepInactiveTabsRunning = true;
        fixture.Settings.PauseInactiveVodTabs = !enable;
        var vod = fixture.AddVod(PlatformKind.Twitch, engine: engine);
        var live = fixture.AddLive();
        await fixture.StartSelectedAsync(live.Tab);
        var starting = vod.Tab.StartAsync(fixture.Settings);
        try
        {
            await engine.PlayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            fixture.Settings.PauseInactiveVodTabs = enable;
        }
        finally { finish.TrySetResult(); }
        await starting;
        await fixture.WaitForPolicyAsync();
        Assert.Equal(enable ? PlaybackStatus.Paused : PlaybackStatus.Playing, vod.Tab.Status);
        Assert.Equal(enable, vod.Tab.PausedByTabSwitch);
        Assert.Equal(enable, engine.Paused);
        Assert.Equal(PlaybackStatus.Playing, live.Tab.Status);
        Assert.Equal(PlaybackAudioState.Audible, live.Engine.AudioState);
    });

    private static Task InactiveVodPendingPauseAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakePlaybackEngine { PauseCompletion = finish.Task, PreservesReplayPositionOnResume = true };
        await using var fixture = new InactiveVodPolicyFixture();
        fixture.Settings.KeepInactiveTabsRunning = true;
        var vod = fixture.AddVod(PlatformKind.Twitch, engine: engine);
        var live = fixture.AddLive();
        await fixture.StartSelectedAsync(vod.Tab);
        fixture.Main.SelectedTab = live.Tab;
        try
        {
            await TestWait.UntilAsync(() => engine.PauseCount > 0, TimeSpan.FromSeconds(2));
            fixture.Settings.PauseInactiveVodTabs = false;
        }
        finally { finish.TrySetResult(); }
        await fixture.WaitForPolicyAsync();
        Assert.Equal(PlaybackStatus.Playing, vod.Tab.Status);
        Assert.Equal(false, vod.Tab.PausedByTabSwitch);
        Assert.Equal(false, engine.Paused);
        Assert.Equal(false, vod.Tab.IsBackgroundResourceServicesSuspended);
        Assert.Equal(PlaybackAudioState.Muted, engine.AudioState);
    });

    private static async Task InactiveVodSettingsMigrationAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var path = Path.Combine(files.Directory, "settings.json");
        var service = new JsonSettingsService(path);
        Assert.Equal(true, new AppSettings().PauseInactiveVodTabs);
        Assert.Equal(true, (await service.LoadAsync()).PauseInactiveVodTabs);
        foreach (var keepLiveTabsRunning in new[] { false, true })
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { keepInactiveTabsRunning = keepLiveTabsRunning }));
            var legacy = await service.LoadAsync();
            Assert.Equal(keepLiveTabsRunning, legacy.KeepInactiveTabsRunning);
            Assert.Equal(!keepLiveTabsRunning, legacy.PauseInactiveVodTabs);
            legacy.PauseInactiveVodTabs = keepLiveTabsRunning;
            await service.SaveAsync(legacy);
            var reloaded = await new JsonSettingsService(path).LoadAsync();
            Assert.Equal(keepLiveTabsRunning, reloaded.KeepInactiveTabsRunning);
            Assert.Equal(keepLiveTabsRunning, reloaded.PauseInactiveVodTabs);
        }

        await File.WriteAllTextAsync(path, """{"KeepInactiveTabsRunning":true,"pauseInactiveVodTabs":true}""");
        Assert.Equal(true, (await service.LoadAsync()).PauseInactiveVodTabs);
    }

    private static Task InactiveVodSettingsLayoutAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new InactiveVodPolicyFixture();
        fixture.Main.IsSettingsOpen = true;
        fixture.Main.SelectedSettingsCategory = SettingsCategory.Playback;
        var window = new MainWindow { DataContext = fixture.Main };
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, fixture.Main);
        try
        {
            var toggle = (CheckBox)window.FindName("PauseInactiveVodTabsCheckBox");
            var page = (FrameworkElement)window.FindName("PlaybackSettingsPage");
            foreach (var size in new[] { new Size(1320, 820), new Size(960, 720), new Size(780, 640) })
            {
                LayoutStudioPolishWindow(window, size);
                Assert.Equal(true, toggle.IsChecked);
                Assert.Equal("Pause unselected VOD tabs", toggle.Content);
                Assert.Equal(Visibility.Visible, toggle.Visibility);
                Assert.Equal(Visibility.Visible, page.Visibility);
                var bounds = toggle.TransformToAncestor(page).TransformBounds(new Rect(toggle.RenderSize));
                Assert.True(bounds.Width > 100 && bounds.Height > 0);
                Assert.True(bounds.Left >= -1 && bounds.Right <= page.ActualWidth + 1,
                    $"The VOD toggle must fit the Playback page at {size}.");
                var gridToggle = FindVisualDescendants<CheckBox>(page).Single(control => Equals(control.Content, "Multi-stream grid"));
                var gridBounds = gridToggle.TransformToAncestor(page).TransformBounds(new Rect(gridToggle.RenderSize));
                Assert.True(bounds.Bottom <= gridBounds.Top + 1, "The VOD toggle must not overlap the grid toggle.");

                var artifactDirectory = Environment.GetEnvironmentVariable("SVS_TEST_ARTIFACT_DIR");
                if (!string.IsNullOrWhiteSpace(artifactDirectory))
                {
                    Directory.CreateDirectory(artifactDirectory);
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render((Visual)window.Content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(artifactDirectory, $"inactive-vod-settings-{size.Width:0}.png"));
                    encoder.Save(output);
                }
            }

            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            Assert.Equal(false, fixture.Settings.PauseInactiveVodTabs);
            fixture.Settings.PauseInactiveVodTabs = true;
            Assert.Equal(true, toggle.IsChecked);
        }
        finally { window.Close(); }
    });

    private static Task InactiveVodNativeAsync(bool hls, bool growing = false) => TestSta.RunOffscreenAsync(async () =>
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var history = files.Create();
        var mediaPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-colors.mp4");
        await using var growingServer = growing
            ? new ReplayFixtureServer(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-audio"),
                segmentDelay: TimeSpan.FromMilliseconds(200))
            : null;
        await using var hlsFixture = hls && !growing ? new FastVodFixture() : null;
        var source = growingServer?.Uri ?? (hls ? FastVodFixture.MediaUri : new Uri(mediaPath));
        IPlaybackMediaSourceGateway? mediaGateway = growing ? new LiveReplayFixtureGateway() : hlsFixture?.Gateway;
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(source, "Fixture"))
        };
        var settings = VodResumeTestCatalog.Settings();
        settings.VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
        settings.VideoRendererMode = VideoRendererMode.Gdi;
        settings.KeepInactiveTabsRunning = true;
        var logger = new MemoryLogger();
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            await using var fixture = new InactiveVodPolicyFixture(settings, streamlink, logger);
            var factory = new VodResumeNativeFactory(settings.Chat, mediaGateway, nativeOverlay: growing, logger: logger);
            var target = VodResumeTestCatalog.Target() with { MediaDuration = TimeSpan.FromSeconds(60) };
            history.Remember(target, new VodPlaybackBookmark(TimeSpan.FromSeconds(25), target.MediaDuration, DateTimeOffset.UtcNow));
            var vod = fixture.AddTarget(target, factory, history);
            vod.SetVideoHandle(handle);
            vod.IsMuted = true;
            var live = fixture.AddLive();
            await fixture.StartSelectedAsync(vod);
            var engine = factory.Engine!;
            var reportedReadyState = false;
            await TestWait.UntilAsync(() =>
            {
                var hasClock = engine.TryGetPlaybackClock(out var clock);
                var hasHealth = engine.TryGetPlaybackHealth(out var health);
                if (hasClock && hasHealth && !reportedReadyState)
                {
                    Console.WriteLine($"Native VOD readiness: position={clock.Position}, seekable={clock.IsSeekable}, health={health}");
                    reportedReadyState = true;
                }
                return hasClock && hasHealth && health.State == PlaybackEngineState.Playing && health.DisplayedPictures > 0;
            }, TimeSpan.FromSeconds(8));
            Assert.True(!hls || (growing ? growingServer!.Requests : hlsFixture!.Requests)
                .Any(name => name.EndsWith(".ts", StringComparison.Ordinal)),
                "Native HLS playback must decode the local fixture segments.");
            Assert.True(engine.TryGetPlaybackClock(out var sought) && sought.Position >= TimeSpan.FromSeconds(23) &&
                sought.Position < TimeSpan.FromSeconds(29),
                $"The fixture must reach the requested VOD position before pausing: clock={sought?.Position}, status={vod.Status}, error={vod.ErrorMessage}.");
            await fixture.StartSelectedAsync(live.Tab);
            Assert.Equal(PlaybackStatus.Paused, vod.Status);
            Assert.Equal(true, vod.PausedByTabSwitch);
            await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var health) && health.State == PlaybackEngineState.Paused,
                TimeSpan.FromSeconds(3));
            await Task.Delay(250);
            Assert.True(engine.TryGetPlaybackClock(out var paused));
            await Task.Delay(600);
            Assert.True(engine.TryGetPlaybackClock(out var stillPaused));
            Assert.True((stillPaused.Position - paused.Position).Duration() < TimeSpan.FromMilliseconds(250),
                "A hidden VOD must stop advancing in VLC.");

            fixture.Main.SelectedTab = vod;
            await fixture.WaitForPolicyAsync();
            Assert.Equal(PlaybackStatus.Playing, vod.Status);
            Assert.Equal(false, vod.PausedByTabSwitch);
            await TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var clock) && clock.Position > stillPaused.Position + TimeSpan.FromMilliseconds(200),
                TimeSpan.FromSeconds(4));
            Assert.True(engine.TryGetPlaybackClock(out var resumed));
            Console.WriteLine($"Inactive VOD native {(growing ? "growing HLS" : hls ? "HLS" : "MP4")}: paused={paused.Position}, held={stillPaused.Position}, resumed={resumed.Position}");
            Assert.True(resumed.Position >= stillPaused.Position - TimeSpan.FromSeconds(1) &&
                resumed.Position < stillPaused.Position + TimeSpan.FromSeconds(4), "VLC must resume near the paused VOD position.");

            fixture.Main.SelectedTab = live.Tab;
            await fixture.WaitForPolicyAsync();
            settings.PauseInactiveVodTabs = false;
            await fixture.WaitForPolicyAsync();
            Assert.Equal(PlaybackStatus.Playing, vod.Status);
            await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var health) && health.State == PlaybackEngineState.Playing,
                TimeSpan.FromSeconds(4));
        }
        catch
        {
            foreach (var entry in logger.Entries.TakeLast(15))
                Console.WriteLine($"Native VOD diagnostic {entry.Source}: {entry.Message}");
            if (hlsFixture is not null)
                foreach (var entry in hlsFixture.Logger.Entries.TakeLast(10))
                    Console.WriteLine($"Native VOD gateway diagnostic {entry.Source}: {entry.Message}");
            throw;
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });

    private sealed record InactiveVodPolicyTab(StreamTabViewModel Tab, FakePlaybackEngineFactory Factory)
    {
        internal FakePlaybackEngine Engine => Factory.Engine!;
    }

    private sealed class InactiveVodPolicyFixture : IAsyncDisposable
    {
        private readonly IStreamlinkService streamlink;
        private readonly Action<Action> dispatch;
        internal AppSettings Settings { get; }
        internal MainViewModel Main { get; }
        private MemoryLogger Logger { get; }

        internal InactiveVodPolicyFixture(AppSettings? settings = null, IStreamlinkService? streamlinkService = null, MemoryLogger? logger = null)
        {
            Settings = settings ?? VodResumeTestCatalog.Settings();
            Logger = logger ?? new MemoryLogger();
            streamlink = streamlinkService ?? new FakeStreamlinkService();
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatch = action => dispatcher.BeginInvoke(action);
            Main = TestViewModels.CreateMain(Settings, new FakeSettingsService(Settings), streamlink,
                new FakePlaybackEngineFactory(), new FakeChatClientFactory(), Logger, dispatch);
        }

        internal InactiveVodPolicyTab AddVod(PlatformKind platform, bool offline = false, FakePlaybackEngine? engine = null)
        {
            var target = VodResumeTestCatalog.Target(platform);
            if (offline)
                target = target with { LocalMediaPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-colors.mp4") };
            var factory = new FakePlaybackEngineFactory(() => engine ?? new FakePlaybackEngine { PreservesReplayPositionOnResume = true });
            return new InactiveVodPolicyTab(AddTarget(target, factory), factory);
        }

        internal InactiveVodPolicyTab AddLive()
        {
            var factory = new FakePlaybackEngineFactory();
            return new InactiveVodPolicyTab(AddTarget(StreamInputParser.Parse("livefixture", PlatformKind.Twitch), factory), factory);
        }

        internal StreamTabViewModel AddTarget(StreamTarget target, IPlaybackEngineFactory factory, IVodPlaybackHistory? history = null)
        {
            var tab = TestViewModels.CreateTab(target, "best", streamlink, factory, new FakeChatClientFactory(), Logger, dispatch,
                vodPlaybackHistory: history);
            tab.SetVideoHandle(new IntPtr(1234));
            Main.Tabs.Add(tab);
            return tab;
        }

        internal async Task StartSelectedAsync(StreamTabViewModel tab)
        {
            Main.SelectedTab = tab;
            await WaitForPolicyAsync();
            await tab.StartAsync(Settings);
            await WaitForPolicyAsync();
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
        }

        internal Task WaitForPolicyAsync() => Main.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(10));

        public ValueTask DisposeAsync() => Main.DisposeAsync();
    }
}

internal static class NeverMuteTabTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("never mute clears manual mute and rejects subsequent mute requests", OverridesManualMuteAsync),
        ("never mute isolates protected tabs across selection and Home", PreservesBackgroundPlaybackAsync),
        ("never mute disabling restores inactive playback without restoring manual mute", DisablingRestoresPolicyAsync),
        ("never mute resumes an automatically paused tab", ResumesAutomaticallyPausedTabAsync),
        ("never mute preserves a manually paused tab", PreservesManualPauseAsync),
        ("never mute remains audible throughout background reload", PreservesBackgroundReloadAsync),
        ("never mute supersedes a queued inactive pause during startup", SupersedesQueuedPauseAsync),
        ("never mute supersedes suspended resource policy at late startup", SupersedesLateStartupPolicyAsync),
        ("never mute prevents stale automatic pause restoration after overlay restart", () => PreservesOverlayRestartAsync(manualPause: false)),
        ("never mute preserves manual pause restoration after overlay restart", () => PreservesOverlayRestartAsync(manualPause: true)),
        ("never mute toolbar toggle follows selected tab and Home", ApplicationTestCatalog.NeverMuteToolbarToggleAsync),
        ("never mute tab indicators update realized tabs and compact choices", ApplicationTestCatalog.NeverMuteTabIndicatorsAsync),
        ("never mute group indicators follow inactive members and rebuilt groups", ApplicationTestCatalog.NeverMuteGroupIndicatorsAsync)
    ];

    private static async Task OverridesManualMuteAsync()
    {
        var calls = new ConcurrentQueue<FakePlaybackAudioCall>();
        var factory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine { AudioCallLog = calls });
        await using var tab = CreateTab("albralelie", factory);
        Assert.Equal(false, tab.NeverMute);
        await tab.StartAsync(CreateSettings());
        tab.IsMuted = true;
        Assert.Equal(PlaybackAudioState.HardMuted, factory.Engine!.AudioState);

        calls.Clear();
        tab.NeverMute = true;
        Assert.Equal(false, tab.IsMuted);
        tab.IsMuted = true;
        tab.SetSelectedForAudio(false);
        tab.Volume = 47;
        tab.ReapplyAudio();
        factory.Engine.SimulateAudioStateReapplied();

        AssertAudible(tab, factory.Engine);
        Assert.True(calls.Count > 0);
        Assert.True(calls.All(call => call.AudioState == PlaybackAudioState.Audible),
            "Manual mute, selection changes and engine convergence must not send protected playback a mute request.");

        tab.SetSelectedForAudio(true);
        tab.NeverMute = false;
        AssertAudible(tab, factory.Engine);
        tab.IsMuted = true;
        Assert.Equal(PlaybackAudioState.HardMuted, factory.Engine.AudioState);
    }

    private static async Task PreservesBackgroundPlaybackAsync()
    {
        await using var fixture = new MainFixture();
        var protectedTab = fixture.AddTab("albralelie");
        var ordinaryTab = fixture.AddTab("summit1g");
        var selectedTab = fixture.AddTab("aceu");
        protectedTab.Tab.NeverMute = true;

        await fixture.StartSelectedAsync(protectedTab);
        await fixture.StartSelectedAsync(ordinaryTab);
        await fixture.StartSelectedAsync(selectedTab);

        AssertRunningAudible(protectedTab);
        Assert.Equal(false, ordinaryTab.Tab.NeverMute);
        AssertAutoPausedMuted(ordinaryTab);
        AssertRunningAudible(selectedTab);

        fixture.Main.SelectHomeCommand.Execute(null);
        await fixture.WaitForPolicyAsync();
        Assert.True(fixture.Main.IsHomeSelected);
        AssertRunningAudible(protectedTab);
        AssertAutoPausedMuted(ordinaryTab);
        AssertAutoPausedMuted(selectedTab);

        fixture.Main.SelectedTab = ordinaryTab.Tab;
        await fixture.WaitForPolicyAsync();
        AssertRunningAudible(protectedTab);
        AssertRunningAudible(ordinaryTab);
        AssertAutoPausedMuted(selectedTab);
    }

    private static async Task DisablingRestoresPolicyAsync()
    {
        await using var fixture = new MainFixture();
        var protectedTab = fixture.AddTab("albralelie");
        await fixture.StartSelectedAsync(protectedTab);
        protectedTab.Tab.IsMuted = true;
        protectedTab.Tab.NeverMute = true;
        fixture.Main.SelectHomeCommand.Execute(null);
        await fixture.WaitForPolicyAsync();
        AssertRunningAudible(protectedTab);

        protectedTab.Tab.NeverMute = false;
        await fixture.WaitForPolicyAsync();
        Assert.Equal(false, protectedTab.Tab.IsMuted);
        AssertAutoPausedMuted(protectedTab);

        fixture.Main.SelectedTab = protectedTab.Tab;
        await fixture.WaitForPolicyAsync();
        AssertRunningAudible(protectedTab);
        Assert.Equal(false, protectedTab.Tab.IsMuted);
    }

    private static async Task ResumesAutomaticallyPausedTabAsync()
    {
        await using var fixture = new MainFixture();
        var background = fixture.AddTab("albralelie");
        var selected = fixture.AddTab("summit1g");
        await fixture.StartSelectedAsync(background);
        await fixture.StartSelectedAsync(selected);
        AssertAutoPausedMuted(background);

        background.Tab.NeverMute = true;
        await fixture.WaitForPolicyAsync();
        AssertRunningAudible(background);
        AssertRunningAudible(selected);
        Assert.Equal(selected.Tab, fixture.Main.SelectedTab);
    }

    private static async Task PreservesManualPauseAsync()
    {
        await using var fixture = new MainFixture();
        var background = fixture.AddTab("albralelie");
        var selected = fixture.AddTab("summit1g");
        await fixture.StartSelectedAsync(background);
        await background.Tab.PauseOrResumeAsync();
        Assert.Equal(false, background.Tab.PausedByTabSwitch);
        await fixture.StartSelectedAsync(selected);
        background.Tab.NeverMute = true;
        await fixture.WaitForPolicyAsync();

        Assert.Equal(PlaybackStatus.Paused, background.Tab.Status);
        Assert.Equal(true, background.Engine.Paused);
        Assert.Equal(false, background.Tab.PausedByTabSwitch);
        Assert.Equal(false, background.Tab.IsBackgroundResourceServicesSuspended);
        AssertAudible(background.Tab, background.Engine);
        AssertRunningAudible(selected);

        await background.Tab.PauseOrResumeAsync();
        await fixture.WaitForPolicyAsync();
        AssertRunningAudible(background);
    }

    private static async Task PreservesBackgroundReloadAsync()
    {
        await using var fixture = new MainFixture();
        var calls = new ConcurrentQueue<FakePlaybackAudioCall>();
        var factory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine { AudioCallLog = calls });
        var background = fixture.AddTab("albralelie", factory);
        var selected = fixture.AddTab("summit1g");
        background.Tab.NeverMute = true;
        await fixture.StartSelectedAsync(background);
        background.Tab.Volume = 53;
        await fixture.StartSelectedAsync(selected);

        calls.Clear();
        await background.Tab.StartAsync(fixture.Settings);
        await fixture.WaitForPolicyAsync();

        Assert.Equal(2, factory.CreateCount);
        Assert.True(background.Tab.NeverMute);
        AssertRunningAudible(background);
        Assert.Equal(53, background.Engine.Volume);
        AssertRunningAudible(selected);
        Assert.True(calls.Count > 0);
        Assert.True(calls.All(call => call.AudioState == PlaybackAudioState.Audible),
            "Reloading an already protected background tab must create and configure only audible players.");
    }

    private static async Task SupersedesQueuedPauseAsync()
    {
        var releasePlayback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakePlaybackEngine { PlayCompletion = releasePlayback.Task };
        var factory = new FakePlaybackEngineFactory(() => engine);
        await using var tab = CreateTab("albralelie", factory);
        tab.SetSelectedForAudio(false);
        var start = tab.StartAsync(CreateSettings());
        Task? queuedPause = null;
        try
        {
            await engine.PlayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            queuedPause = tab.PauseForTabSwitchAsync();
            Assert.Equal(false, queuedPause.IsCompleted);
            tab.NeverMute = true;
        }
        finally
        {
            releasePlayback.TrySetResult();
            await start.WaitAsync(TimeSpan.FromSeconds(2));
            if (queuedPause is not null)
                await queuedPause.WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        Assert.Equal(false, tab.PausedByTabSwitch);
        Assert.Equal(false, tab.IsBackgroundResourceServicesSuspended);
        Assert.Equal(false, engine.Paused);
        AssertAudible(tab, engine);
    }

    private static async Task SupersedesLateStartupPolicyAsync()
    {
        var releasePlayback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakePlaybackEngine { PlayCompletion = releasePlayback.Task };
        await using var tab = CreateTab("albralelie", new FakePlaybackEngineFactory(() => engine));
        tab.SetSelectedForAudio(false);
        await tab.PauseForTabSwitchAsync();
        Assert.True(tab.IsBackgroundResourceServicesSuspended);
        var start = tab.StartAsync(CreateSettings());
        try
        {
            await engine.PlayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            tab.NeverMute = true;
        }
        finally
        {
            releasePlayback.TrySetResult();
            await start.WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        Assert.Equal(false, tab.PausedByTabSwitch);
        Assert.Equal(false, engine.Paused);
        AssertAudible(tab, engine);
    }

    private static async Task PreservesOverlayRestartAsync(bool manualPause)
    {
        var releasePlayback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementEngine = new FakePlaybackEngine { PlayCompletion = releasePlayback.Task };
        var engines = new Queue<FakePlaybackEngine>([new FakePlaybackEngine(), replacementEngine]);
        await using var tab = CreateTab("albralelie", new FakePlaybackEngineFactory(() => engines.Dequeue()));
        var settings = CreateSettings();
        await tab.StartAsync(settings);
        if (manualPause)
            await tab.PauseOrResumeAsync();
        else
            await tab.PauseForTabSwitchAsync();
        Assert.Equal(PlaybackStatus.Paused, tab.Status);
        Assert.Equal(!manualPause, tab.PausedByTabSwitch);

        settings.Chat.Layout = ChatLayout.Overlay;
        Assert.True(tab.ShouldRestartPlaybackForChatOverlaySettings(settings));
        var restart = tab.ReconfigurePlaybackForChatOverlaySettingsAsync(settings);
        try
        {
            await replacementEngine.PlayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            tab.NeverMute = true;
        }
        finally
        {
            releasePlayback.TrySetResult();
            await restart.WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(manualPause ? PlaybackStatus.Paused : PlaybackStatus.Playing, tab.Status);
        Assert.Equal(manualPause, replacementEngine.Paused);
        Assert.Equal(false, tab.PausedByTabSwitch);
        AssertAudible(tab, replacementEngine);

        if (!manualPause)
        {
            tab.NeverMute = false;
            tab.SetSelectedForAudio(false);
            await tab.PauseForTabSwitchAsync();
            Assert.Equal(PlaybackStatus.Paused, tab.Status);
            Assert.Equal(true, tab.PausedByTabSwitch);
            Assert.Equal(true, replacementEngine.Paused);
            Assert.Equal(PlaybackAudioState.Muted, replacementEngine.AudioState);
        }
    }

    private static void AssertAudible(StreamTabViewModel tab, FakePlaybackEngine engine)
    {
        Assert.Equal(false, tab.IsMuted);
        Assert.Equal(false, tab.IsAutoMuted);
        Assert.Equal(PlaybackAudioState.Audible, engine.AudioState);
        Assert.Equal(false, engine.Muted);
        Assert.Equal(tab.Volume, engine.Volume);
        Assert.Equal(true, engine.AudioTrackEnabled);
    }

    private static void AssertRunningAudible(TabFixture fixture)
    {
        Assert.Equal(PlaybackStatus.Playing, fixture.Tab.Status);
        Assert.Equal(false, fixture.Tab.PausedByTabSwitch);
        Assert.Equal(false, fixture.Tab.IsBackgroundResourceServicesSuspended);
        Assert.Equal(false, fixture.Engine.Paused);
        AssertAudible(fixture.Tab, fixture.Engine);
    }

    private static void AssertAutoPausedMuted(TabFixture fixture)
    {
        Assert.Equal(PlaybackStatus.Paused, fixture.Tab.Status);
        Assert.Equal(true, fixture.Tab.PausedByTabSwitch);
        Assert.Equal(true, fixture.Engine.Paused);
        Assert.Equal(true, fixture.Tab.IsAutoMuted);
        Assert.Equal(PlaybackAudioState.Muted, fixture.Engine.AudioState);
        Assert.Equal(true, fixture.Engine.Muted);
    }

    private static AppSettings CreateSettings()
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Docked;
        return settings;
    }

    private static StreamTabViewModel CreateTab(string channel, FakePlaybackEngineFactory factory)
    {
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse(channel, PlatformKind.Twitch), "best",
            new FakeStreamlinkService(), factory, new FakeChatClientFactory(), new MemoryLogger(), action => action());
        tab.SetVideoHandle(new IntPtr(1234));
        return tab;
    }

    internal sealed record TabFixture(StreamTabViewModel Tab, FakePlaybackEngineFactory Factory)
    {
        internal FakePlaybackEngine Engine => Factory.Engine!;
    }

    internal sealed class MainFixture : IAsyncDisposable
    {
        internal AppSettings Settings { get; } = CreateSettings();
        internal MainViewModel Main { get; }

        internal MainFixture()
        {
            Main = TestViewModels.CreateMain(Settings, new FakeSettingsService(Settings),
                new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
                new MemoryLogger(), action => action());
        }

        internal TabFixture AddTab(string channel, FakePlaybackEngineFactory? factory = null)
        {
            factory ??= new FakePlaybackEngineFactory();
            var tab = CreateTab(channel, factory);
            Main.Tabs.Add(tab);
            return new TabFixture(tab, factory);
        }

        internal async Task StartSelectedAsync(TabFixture fixture)
        {
            Main.SelectedTab = fixture.Tab;
            await WaitForPolicyAsync();
            await fixture.Tab.StartAsync(Settings);
            await WaitForPolicyAsync();
        }

        internal Task WaitForPolicyAsync() => Main.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(2));

        public ValueTask DisposeAsync() => Main.DisposeAsync();
    }
}

internal static partial class ApplicationTestCatalog
{
    internal static Task NeverMuteToolbarToggleAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new NeverMuteTabTestCatalog.MainFixture();
        var first = fixture.AddTab("albralelie").Tab;
        var second = fixture.AddTab("summit1g").Tab;
        fixture.Main.SelectedTab = first;
        var window = new MainWindow { DataContext = fixture.Main };
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, fixture.Main);
        try
        {
            void UpdateBindings() => window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            UpdateBindings();
            var toggle = window.FindName("TopNeverMuteButton") as ToggleButton;
            var mute = window.FindName("TopMuteButton") as Button;
            Assert.NotNull(toggle);
            Assert.NotNull(mute);
            Assert.Equal(BindingMode.TwoWay, BindingOperations.GetBinding(toggle!, ToggleButton.IsCheckedProperty)!.Mode);
            Assert.Equal(false, toggle!.IsChecked);
            Assert.Equal(Visibility.Visible, toggle.Visibility);
            Assert.Equal(true, mute!.IsEnabled);

            var peer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(toggle);
            ((System.Windows.Automation.Provider.IToggleProvider)peer).Toggle();
            UpdateBindings();
            Assert.Equal(true, first.NeverMute);
            Assert.Equal(false, second.NeverMute);
            Assert.Equal(false, mute.IsEnabled);
            Assert.True(mute.ToolTip?.ToString()?.Contains("never mute", StringComparison.OrdinalIgnoreCase) == true,
                "The disabled mute control must explain the Never mute tab setting.");

            fixture.Main.SelectedTab = second;
            UpdateBindings();
            Assert.Equal(false, toggle.IsChecked);
            Assert.Equal(true, mute.IsEnabled);
            fixture.Main.SelectedTab = first;
            UpdateBindings();
            Assert.Equal(true, toggle.IsChecked);
            ((System.Windows.Automation.Provider.IToggleProvider)peer).Toggle();
            UpdateBindings();
            Assert.Equal(false, first.NeverMute);
            Assert.Equal(true, mute.IsEnabled);

            fixture.Main.SelectHomeCommand.Execute(null);
            UpdateBindings();
            Assert.Equal(Visibility.Collapsed, toggle.Visibility);
        }
        finally
        {
            window.Close();
        }
    });
}

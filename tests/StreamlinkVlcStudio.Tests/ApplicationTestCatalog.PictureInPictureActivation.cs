internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> PictureInPictureActivationTests { get; } =
    [
        ("picture-in-picture activation keeps main audio without transient mute", PictureInPictureActivationMainAudioAsync),
        ("picture-in-picture audio preserves main playback and follows main tab changes", PictureInPictureActivationPlaybackPolicyAsync),
        ("picture-in-picture activation preserves manual mute and never mute", PictureInPictureActivationMutePreferencesAsync),
        ("picture-in-picture active audio falls back when its tab closes", () => PictureInPictureActivationRemovedOwnerAsync(close: true)),
        ("picture-in-picture active audio falls back when its tab reattaches", () => PictureInPictureActivationRemovedOwnerAsync(close: false)),
        ("picture-in-picture native clicks activation and fullscreen preserve main audio surface multiview and Home", PictureInPictureActivationWindowWiringAsync)
    ];

    private static async Task PictureInPictureActivationMainAudioAsync()
    {
        await using var fixture = new PictureInPictureActivationFixture();
        var pip = fixture.AddTab("albralelie");
        var main = fixture.AddTab("summit1g");
        await fixture.StartSelectedAsync(pip);
        fixture.Detach(pip);
        await fixture.StartSelectedAsync(main);

        fixture.AudioCalls.Clear();
        fixture.Main.ActivatePictureInPictureTab(pip.Tab);
        await fixture.WaitForPolicyAsync();
        Assert.Equal(main.Tab, fixture.Main.SelectedTab);
        Assert.Equal(true, main.Tab.IsSelected);
        Assert.Equal(false, pip.Tab.IsSelected);
        fixture.AssertAudioOwner(main, pip);
        fixture.AssertNeverMuted(main);

        foreach (var reapply in new Action[] { pip.Tab.ReapplyAudio, pip.Engine.SimulateAudioStateReapplied })
        {
            fixture.AudioCalls.Clear();
            reapply();
            await fixture.WaitForPolicyAsync();
            var calls = fixture.AudioCalls.ToArray();
            Assert.True(calls.Length >= 2);
            Assert.Equal(pip.Engine, calls[0].Engine);
            Assert.Equal(PlaybackAudioState.Muted, calls[0].AudioState);
            Assert.Equal(main.Engine, calls[^1].Engine);
            Assert.Equal(PlaybackAudioState.Audible, calls[^1].AudioState);
            fixture.AssertAudioOwner(main, pip);
            fixture.AssertNeverMuted(main);
        }

        fixture.AudioCalls.Clear();
        for (var iteration = 0; iteration < 16; iteration++)
        {
            fixture.Main.ActivateMainWindowAudio();
            fixture.Main.ActivatePictureInPictureTab(pip.Tab);
        }
        await fixture.WaitForPolicyAsync();
        fixture.AssertAudioOwner(main, pip);
        fixture.AssertNeverMuted(main);
        Assert.Equal(main.Tab, fixture.Main.SelectedTab);
    }

    private static async Task PictureInPictureActivationPlaybackPolicyAsync()
    {
        await using var fixture = new PictureInPictureActivationFixture();
        var background = fixture.AddTab("aceu");
        var pip = fixture.AddTab("albralelie");
        var main = fixture.AddTab("summit1g");
        await fixture.StartSelectedAsync(background);
        await fixture.StartSelectedAsync(pip);
        fixture.Detach(pip);
        await fixture.StartSelectedAsync(main);
        fixture.Main.ActivatePictureInPictureTab(pip.Tab);
        await fixture.WaitForPolicyAsync();

        fixture.AudioCalls.Clear();
        // Policy passes and incidental tab strip changes must preserve main audio
        // while the PiP window is focused.
        fixture.Main.Settings.KeepInactiveTabsRunning = true;
        await fixture.WaitForPolicyAsync();
        Assert.Equal(PlaybackStatus.Playing, background.Tab.Status);
        fixture.AssertAudioOwner(main, pip, background);
        fixture.Main.Settings.KeepInactiveTabsRunning = false;
        await fixture.WaitForPolicyAsync();

        fixture.Main.Tabs.Move(0, 1);
        await fixture.WaitForPolicyAsync();
        Assert.Equal(main.Tab, fixture.Main.SelectedTab);

        foreach (var visible in new[] { pip, main })
        {
            Assert.Equal(PlaybackStatus.Playing, visible.Tab.Status);
            Assert.Equal(false, visible.Engine.Paused);
            Assert.Equal(false, visible.Tab.PausedByTabSwitch);
            Assert.Equal(false, visible.Tab.IsBackgroundResourceServicesSuspended);
        }
        Assert.Equal(PlaybackStatus.Paused, background.Tab.Status);
        Assert.Equal(true, background.Tab.PausedByTabSwitch);
        Assert.Equal(true, background.Engine.Paused);
        fixture.AssertAudioOwner(main, pip, background);
        fixture.AssertNeverMuted(main);

        // Explicit main tab changes still transfer audio, even after PiP focus.
        fixture.Main.SelectedTab = background.Tab;
        fixture.Main.ActivatePictureInPictureTab(pip.Tab);
        await fixture.WaitForPolicyAsync();
        fixture.AssertAudioOwner(background, main, pip);
        Assert.Equal(PlaybackStatus.Playing, background.Tab.Status);

        fixture.Main.SelectedTab = main.Tab;
        fixture.Main.ActivatePictureInPictureTab(pip.Tab);
        fixture.Main.SelectedTab = main.Tab;
        await fixture.WaitForPolicyAsync();
        fixture.AssertAudioOwner(main, pip, background);

        fixture.Main.SelectHomeCommand.Execute(null);
        await fixture.WaitForPolicyAsync();
        fixture.Main.ActivatePictureInPictureTab(pip.Tab);
        await fixture.WaitForPolicyAsync();
        Assert.True(fixture.Main.IsHomeSelected);
        Assert.Equal<StreamTabViewModel?>(null, fixture.Main.SelectedTab);
        fixture.AssertAudioOwner(pip, main, background);
        Assert.Equal(PlaybackStatus.Playing, pip.Tab.Status);
        Assert.Equal(PlaybackStatus.Paused, main.Tab.Status);

        fixture.Main.ActivateMainWindowAudio();
        await fixture.WaitForPolicyAsync();
        Assert.True(fixture.Main.IsHomeSelected);
        Assert.Equal(PlaybackAudioState.Muted, pip.Engine.AudioState);
        Assert.Equal(PlaybackStatus.Playing, pip.Tab.Status);
    }

    private static async Task PictureInPictureActivationMutePreferencesAsync()
    {
        await using var fixture = new PictureInPictureActivationFixture();
        var pip = fixture.AddTab("albralelie");
        var main = fixture.AddTab("summit1g");
        await fixture.StartSelectedAsync(pip);
        fixture.Detach(pip);
        await fixture.StartSelectedAsync(main);
        pip.Tab.IsMuted = true;
        fixture.Main.ActivatePictureInPictureTab(pip.Tab);
        await fixture.WaitForPolicyAsync();
        Assert.True(pip.Tab.IsMuted);
        Assert.Equal(PlaybackAudioState.HardMuted, pip.Engine.AudioState);
        Assert.Equal(PlaybackAudioState.Audible, main.Engine.AudioState);

        fixture.Main.ActivateMainWindowAudio();
        await fixture.WaitForPolicyAsync();
        Assert.True(pip.Tab.IsMuted);
        Assert.Equal(PlaybackAudioState.HardMuted, pip.Engine.AudioState);
        Assert.Equal(PlaybackAudioState.Audible, main.Engine.AudioState);

        main.Tab.IsMuted = true;
        fixture.Main.ActivatePictureInPictureTab(pip.Tab);
        fixture.Main.ActivateMainWindowAudio();
        await fixture.WaitForPolicyAsync();
        Assert.True(main.Tab.IsMuted);
        Assert.Equal(PlaybackAudioState.HardMuted, main.Engine.AudioState);
        Assert.Equal(PlaybackAudioState.HardMuted, pip.Engine.AudioState);
        main.Tab.IsMuted = false;

        pip.Tab.NeverMute = true;
        await fixture.WaitForPolicyAsync();
        fixture.Main.ActivatePictureInPictureTab(pip.Tab);
        fixture.Main.ActivateMainWindowAudio();
        await fixture.WaitForPolicyAsync();
        Assert.Equal(false, pip.Tab.IsMuted);
        Assert.Equal(false, pip.Tab.IsAutoMuted);
        Assert.Equal(PlaybackAudioState.Audible, pip.Engine.AudioState);
        Assert.Equal(PlaybackAudioState.Audible, main.Engine.AudioState);
        Assert.Equal(main.Tab, fixture.Main.SelectedTab);

        pip.Tab.NeverMute = false;
        fixture.Main.ActivatePictureInPictureTab(pip.Tab);
        await fixture.WaitForPolicyAsync();
        fixture.AssertAudioOwner(main, pip);
    }

    private static async Task PictureInPictureActivationRemovedOwnerAsync(bool close)
    {
        await using var fixture = new PictureInPictureActivationFixture();
        var firstPip = fixture.AddTab("albralelie");
        var secondPip = fixture.AddTab("aceu");
        var main = fixture.AddTab("summit1g");
        await fixture.StartSelectedAsync(firstPip);
        fixture.Detach(firstPip);
        await fixture.StartSelectedAsync(secondPip);
        fixture.Detach(secondPip);
        await fixture.StartSelectedAsync(main);
        fixture.Main.ActivatePictureInPictureTab(firstPip.Tab);
        fixture.Main.ActivatePictureInPictureTab(secondPip.Tab);
        await fixture.WaitForPolicyAsync();
        Assert.Equal(main.Tab, fixture.Main.SelectedTab);
        fixture.AssertAudioOwner(main, firstPip, secondPip);

        // Without an attached main selection, PiP can still own audio. Exercise
        // removal of that actual owner and reject subsequent stale notifications.
        fixture.Main.SelectHomeCommand.Execute(null);
        await fixture.WaitForPolicyAsync();
        fixture.Main.ActivatePictureInPictureTab(firstPip.Tab);
        fixture.Main.ActivatePictureInPictureTab(secondPip.Tab);
        await fixture.WaitForPolicyAsync();
        fixture.AssertAudioOwner(secondPip, firstPip, main);

        if (close)
            Assert.True(fixture.Main.CloseTab(secondPip.Tab));
        else
            Assert.True(fixture.Main.SetTabsDetached([secondPip.Tab], detached: false));
        await fixture.WaitForPolicyAsync();
        Assert.Equal<StreamTabViewModel?>(null, fixture.Main.SelectedTab);
        Assert.True(fixture.Main.IsHomeSelected);
        Assert.Equal(PlaybackAudioState.Muted, main.Engine.AudioState);
        Assert.Equal(PlaybackAudioState.Muted, firstPip.Engine.AudioState);
        if (!close)
            Assert.Equal(PlaybackAudioState.Muted, secondPip.Engine.AudioState);

        // A stale notification from a removed or reattached window cannot steal audio.
        fixture.Main.ActivatePictureInPictureTab(secondPip.Tab);
        await fixture.WaitForPolicyAsync();
        Assert.Equal(PlaybackAudioState.Muted, main.Engine.AudioState);
        Assert.Equal(PlaybackAudioState.Muted, firstPip.Engine.AudioState);
        fixture.Main.SelectedTab = main.Tab;
        await fixture.WaitForPolicyAsync();
        fixture.AssertAudioOwner(main, firstPip);
    }

    private static Task PictureInPictureActivationWindowWiringAsync() => TestSta.RunAsync(async () =>
    {
        await using var fixture = new PictureInPictureActivationFixture();
        var pip = fixture.AddTab("albralelie");
        var main = fixture.AddTab("summit1g");
        var peer = fixture.AddTab("aceu");
        await fixture.StartSelectedAsync(pip);
        await fixture.StartSelectedAsync(main);
        await fixture.StartSelectedAsync(peer);
        var window = new MainWindow { DataContext = fixture.Main, Left = 50, Top = 60, Width = 1100, Height = 650 };
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, fixture.Main);
        DetachedVideoWindow? detached = null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            window.Show();
            PumpPictureInPictureActivationLayout(window);
            SetMainWindowHandle(window);
            var detach = typeof(MainWindow).GetMethod("DetachTabToPictureInPicture", flags);
            Assert.NotNull(detach);
            detach!.Invoke(window, [pip.Tab, new Point(350, 250), false]);
            var detachedWindows = (IDictionary<StreamTabViewModel, DetachedVideoWindow>)typeof(MainWindow)
                .GetField("detachedWindows", flags)!.GetValue(window)!;
            detached = detachedWindows[pip.Tab];
            Assert.True(pip.Tab.IsDetached);
            Assert.True(fixture.Main.TryMergeTabsIntoMultiView([peer.Tab], main.Tab));
            fixture.Main.SelectedTab = main.Tab;
            await fixture.WaitForPolicyAsync();
            PumpPictureInPictureActivationLayout(window, detached);

            var mainSurface = FindVisualDescendants<VideoSurface>(window).Single(surface => ReferenceEquals(surface.Tag, main.Tab));
            var mainHandle = mainSurface.Handle;
            var mountedTabs = fixture.Main.VideoTabs.ToArray();
            var selectedGroup = fixture.Main.SelectedTabStripItem;
            Assert.NotNull(selectedGroup);
            Assert.Equal(2, selectedGroup!.Tabs.Count);
            var groupTabs = selectedGroup.Tabs.ToArray();
            var grid = (fixture.Main.VideoGridRows, fixture.Main.VideoGridColumns);
            var placements = new[] { main.Tab, peer.Tab }.Select(tab =>
                (tab.IsVideoVisible, tab.VideoGridRow, tab.VideoGridColumn, tab.VideoGridRowSpan, tab.VideoGridColumnSpan)).ToArray();
            void AssertMainViewUnchanged()
            {
                PumpPictureInPictureActivationLayout(window, detached);
                Assert.True(ReferenceEquals(main.Tab, fixture.Main.SelectedTab),
                    $"PiP interaction changed the main tab from {main.Tab.Target.Channel} to {fixture.Main.SelectedTab?.Target.Channel}.");
                Assert.Equal(false, fixture.Main.IsHomeSelected);
                Assert.Equal(true, main.Tab.IsSelected);
                Assert.Equal(false, pip.Tab.IsSelected);
                Assert.Equal(selectedGroup, fixture.Main.SelectedTabStripItem);
                Assert.SequenceEqual(groupTabs, selectedGroup.Tabs);
                Assert.SequenceEqual(mountedTabs, fixture.Main.VideoTabs);
                Assert.Equal(grid, (fixture.Main.VideoGridRows, fixture.Main.VideoGridColumns));
                Assert.SequenceEqual(placements, new[] { main.Tab, peer.Tab }.Select(tab =>
                    (tab.IsVideoVisible, tab.VideoGridRow, tab.VideoGridColumn, tab.VideoGridRowSpan, tab.VideoGridColumnSpan)));
                Assert.Equal(mainSurface, FindVisualDescendants<VideoSurface>(window).Single(surface => ReferenceEquals(surface.Tag, main.Tab)));
                Assert.Equal(mainHandle, mainSurface.Handle);
                Assert.Equal(mainHandle, main.Engine.VideoHandle);
                fixture.AssertAudioOwner(main, pip, peer);
                fixture.AssertNeverMuted(main);
            }

            // Send the same Win32 mouse message delivered to the actual native video host.
            fixture.AudioCalls.Clear();
            var pipSurface = FindVisualDescendants<VideoSurface>(detached).Single();
            var center = pipSurface.PointToScreen(new Point(pipSurface.ActualWidth / 2, pipSurface.ActualHeight / 2));
            NativeWindowTest.SendMessage(pipSurface.Handle, 0x0201, new IntPtr(1),
                NativeWindowTest.MakeMouseLParamFromScreenPoint(pipSurface.Handle, center));
            NativeWindowTest.SendMessage(pipSurface.Handle, 0x0202, IntPtr.Zero,
                NativeWindowTest.MakeMouseLParamFromScreenPoint(pipSurface.Handle, center));
            await fixture.WaitForPolicyAsync();
            AssertMainViewUnchanged();

            // Raise WPF's protected event entry point to exercise the registered handlers
            // without depending on Windows foreground-activation permission in the runner.
            var onActivated = typeof(Window).GetMethod("OnActivated", flags);
            Assert.NotNull(onActivated);
            onActivated!.Invoke(window, [EventArgs.Empty]);
            await fixture.WaitForPolicyAsync();
            AssertMainViewUnchanged();
            onActivated.Invoke(detached, [EventArgs.Empty]);
            await fixture.WaitForPolicyAsync();
            AssertMainViewUnchanged();

            detached.CancelVideoMoveCandidate();
            typeof(DetachedVideoWindow).GetMethod("ResetStreamDoubleClickTracking", flags)!.Invoke(detached, []);
            var pointType = typeof(MainWindow).GetNestedType("NativePoint", BindingFlags.NonPublic)!;
            var nativePoint = Activator.CreateInstance(pointType, [(int)Math.Round(center.X), (int)Math.Round(center.Y)]);
            var doubleClick = typeof(MainWindow).GetMethod("TryToggleDetachedStreamFullscreenFromVideoDoubleClick", flags)!;
            Assert.Equal(false, doubleClick.Invoke(window, [nativePoint]));
            Assert.Equal(true, doubleClick.Invoke(window, [nativePoint]));
            await fixture.WaitForPolicyAsync();
            Assert.True(detached.IsStreamFullscreen);
            AssertMainViewUnchanged();

            fixture.Main.SelectHomeCommand.Execute(null);
            await fixture.WaitForPolicyAsync();
            PumpPictureInPictureActivationLayout(window, detached);
            var homeMountedTabs = fixture.Main.VideoTabs.ToArray();
            onActivated.Invoke(detached, [EventArgs.Empty]);
            await fixture.WaitForPolicyAsync();
            PumpPictureInPictureActivationLayout(window, detached);
            Assert.True(fixture.Main.IsHomeSelected);
            Assert.Equal<StreamTabViewModel?>(null, fixture.Main.SelectedTab);
            Assert.SequenceEqual(homeMountedTabs, fixture.Main.VideoTabs);
            fixture.AssertAudioOwner(pip, main, peer);
        }
        finally
        {
            detached?.CloseForTabDisposal();
            window.Close();
        }
    });

    private static void PumpPictureInPictureActivationLayout(params Window[] windows)
    {
        foreach (var window in windows)
        {
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }
    }

    private sealed class PictureInPictureActivationFixture : IAsyncDisposable
    {
        private readonly NeverMuteTabTestCatalog.MainFixture fixture = new();
        internal MainViewModel Main => fixture.Main;
        internal ConcurrentQueue<FakePlaybackAudioCall> AudioCalls { get; } = new();
        internal FakeSharedAudioState SharedAudio { get; } = new();

        internal NeverMuteTabTestCatalog.TabFixture AddTab(string channel) => fixture.AddTab(channel,
            new FakePlaybackEngineFactory(() => new FakePlaybackEngine { SharedAudioState = SharedAudio, AudioCallLog = AudioCalls }));

        internal Task StartSelectedAsync(NeverMuteTabTestCatalog.TabFixture tab) => fixture.StartSelectedAsync(tab);
        internal Task WaitForPolicyAsync() => fixture.WaitForPolicyAsync();

        internal void Detach(NeverMuteTabTestCatalog.TabFixture tab)
        {
            Main.SetPictureInPictureTabGroup([tab.Tab]);
            Main.SetPictureInPictureVisibleTabGroup([tab.Tab]);
            Assert.True(Main.SetTabsDetached([tab.Tab], detached: true));
        }

        internal void AssertAudioOwner(NeverMuteTabTestCatalog.TabFixture owner, params NeverMuteTabTestCatalog.TabFixture[] muted)
        {
            Assert.Equal(false, owner.Tab.IsAutoMuted);
            Assert.Equal(PlaybackAudioState.Audible, owner.Engine.AudioState);
            Assert.Equal(owner.Tab.Volume, owner.Engine.Volume);
            foreach (var tab in muted)
            {
                Assert.Equal(true, tab.Tab.IsAutoMuted);
                Assert.Equal(PlaybackAudioState.Muted, tab.Engine.AudioState);
                Assert.Equal(0, tab.Engine.Volume);
            }
            Assert.Equal(false, SharedAudio.Muted);
            Assert.Equal(PlaybackAudioState.Audible, SharedAudio.AudioState);
            Assert.Equal(owner.Tab.Volume, SharedAudio.Volume);
        }

        internal void AssertNeverMuted(NeverMuteTabTestCatalog.TabFixture tab)
        {
            Assert.True(AudioCalls.Where(call => ReferenceEquals(call.Engine, tab.Engine))
                .All(call => call.AudioState == PlaybackAudioState.Audible),
                "Focusing PiP must not send the selected main stream even a transient mute request.");
        }

        public ValueTask DisposeAsync() => fixture.DisposeAsync();
    }
}

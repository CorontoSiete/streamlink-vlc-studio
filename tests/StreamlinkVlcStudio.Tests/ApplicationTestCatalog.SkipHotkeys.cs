using System.Windows.Threading;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> SkipHotkeyTests { get; } =
    [
        ("skip hotkeys: defaults survive older settings and independent amounts save reload and reset", SkipHotkeyPersistenceAsync),
        ("skip hotkeys: numpad arrows stay distinct from the arrow cluster with Num Lock off", SkipHotkeyNumpadIdentityAsync),
        ("skip hotkeys: selected VOD uses the current clock custom amounts and both boundaries", SkipHotkeyVodAsync),
        ("skip hotkeys: live replay rewinds and returns to live at the forward boundary", SkipHotkeyLiveAsync),
        ("skip hotkeys: input guards remapping recording and existing navigation remain intact", SkipHotkeyInputGuardsAsync),
        ("skip hotkeys: opposite directions cannot queue stale seeks while playback is busy", SkipHotkeyBusyAsync),
        ("skip hotkeys: settings sliders record swap reset and save through the real bindings", SkipHotkeySettingsAsync),
        ("skip hotkeys: detached player and replay overlay dispatch the configured shortcuts", SkipHotkeyOtherSurfacesAsync),
        ("skip hotkeys: physical numpad input seeks with Num Lock on and off and records correctly", SkipHotkeyPhysicalKeyboardAsync),
        ("skip hotkeys: physical right click opens the slider and dragging updates only its direction", SkipHotkeyPhysicalSliderAsync),
        .. string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY"))
            ? Array.Empty<(string, Func<Task>)>()
            : [
                ("skip hotkeys: physical numpad seeks real Direct3D11 VLC after clicking the video", () => SkipHotkeyNativeVlcAsync(VideoRendererMode.Direct3D11)),
                ("skip hotkeys: physical numpad seeks real GDI VLC after clicking the video", () => SkipHotkeyNativeVlcAsync(VideoRendererMode.Gdi))
            ]
    ];

    private static async Task SkipHotkeyPersistenceAsync()
    {
        var directory = Directory.CreateTempSubdirectory("svs-skip-hotkeys-");
        var path = Path.Combine(directory.FullName, "settings.json");
        try
        {
            await File.WriteAllTextAsync(path, """{"Hotkeys":{"PreviousTab":"Ctrl+Left"},"DefaultQuality":"480p"}""");
            var service = new JsonSettingsService(path);
            var settings = await service.LoadAsync();
            Assert.Equal("NumPad4", settings.Hotkeys.SkipBackward);
            Assert.Equal("NumPad6", settings.Hotkeys.SkipForward);
            Assert.Equal(30, settings.Hotkeys.SkipBackwardSeconds);
            Assert.Equal(30, settings.Hotkeys.SkipForwardSeconds);
            Assert.Equal("Ctrl+Left", settings.Hotkeys.PreviousTab);
            Assert.Equal("480p", settings.DefaultQuality);
            settings.Hotkeys.SkipBackward = " Ctrl+Mouse5 ";
            settings.Hotkeys.SkipForward = " F8 ";
            settings.Hotkeys.SkipBackwardSeconds = 75;
            settings.Hotkeys.SkipForwardSeconds = 15;
            await service.SaveAsync(settings);
            settings = await new JsonSettingsService(path).LoadAsync();
            Assert.Equal("Ctrl+Mouse5", settings.Hotkeys.SkipBackward);
            Assert.Equal("F8", settings.Hotkeys.SkipForward);
            Assert.Equal(75, settings.Hotkeys.SkipBackwardSeconds);
            Assert.Equal(15, settings.Hotkeys.SkipForwardSeconds);
            settings.Hotkeys.SkipBackwardSeconds = int.MinValue;
            settings.Hotkeys.SkipForwardSeconds = int.MaxValue;
            Assert.Equal(1, settings.Hotkeys.SkipBackwardSeconds);
            Assert.Equal(300, settings.Hotkeys.SkipForwardSeconds);
            settings.Hotkeys.ResetToDefaults();
            await service.SaveAsync(settings);
            settings = await new JsonSettingsService(path).LoadAsync();
            Assert.Equal("NumPad4", settings.Hotkeys.SkipBackward);
            Assert.Equal("NumPad6", settings.Hotkeys.SkipForward);
            Assert.Equal(30, settings.Hotkeys.SkipBackwardSeconds);
            Assert.Equal(30, settings.Hotkeys.SkipForwardSeconds);
        }
        finally { directory.Delete(recursive: true); }
    }

    private static Task SkipHotkeyNumpadIdentityAsync()
    {
        var settings = new HotkeySettings();
        foreach (var (arrow, number, virtualKey, scanCode, action) in new[]
        {
            (Key.Left, Key.NumPad4, 0x25, 0x4B, AppHotkeyAction.SkipBackward),
            (Key.Right, Key.NumPad6, 0x27, 0x4D, AppHotkeyAction.SkipForward)
        })
        {
            foreach (var flags in new[] { 0L, 0x40000000L, 0xC0000000L })
            {
                var data = ((long)scanCode << 16) | flags | 1;
                Assert.Equal(number, HotkeyGesture.NormalizeNumpadArrow(arrow, virtualKey, data));
                Assert.Equal(arrow, HotkeyGesture.NormalizeNumpadArrow(arrow, virtualKey, data | 0x01000000));
            }
            Assert.Equal(arrow, HotkeyGesture.NormalizeNumpadArrow(arrow, virtualKey, 1));
            Assert.Equal(number, HotkeyGesture.NormalizeNumpadArrow(number, (int)KeyInterop.VirtualKeyFromKey(number), 1));
            Assert.True(HotkeyBindingPolicy.Matches(settings, action, number, ModifierKeys.None));
            Assert.Equal(false, HotkeyBindingPolicy.Matches(settings, action, arrow, ModifierKeys.None));
            Assert.Equal(false, HotkeyBindingPolicy.Matches(settings, action, number, ModifierKeys.Control));
        }
        return Task.CompletedTask;
    }

    private static Task SkipHotkeyVodAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new SkipHotkeyFixture();
        await fixture.StartAsync();
        await fixture.PositionAsync(600);
        fixture.First.ReplaySeekValue = 20; // Deliberately stale UI clock.
        await fixture.PressAndWaitAsync(Key.NumPad4, 570);
        await fixture.PressAndWaitAsync(Key.NumPad6, 600);
        fixture.Settings.Hotkeys.SkipBackwardSeconds = 75;
        fixture.Settings.Hotkeys.SkipForwardSeconds = 15;
        await fixture.PressAndWaitAsync(Key.NumPad4, 525);
        await fixture.PressAndWaitAsync(Key.NumPad6, 540);
        Assert.Equal(0, fixture.OtherEngine.SeekCount);
        Assert.Equal(TimeSpan.Zero, fixture.OtherEngine.Position);

        await fixture.PositionAsync(10);
        await fixture.PressAndWaitAsync(Key.NumPad4, 0);
        await fixture.PositionAsync(3595);
        await fixture.PressAndWaitAsync(Key.NumPad6, 3600);
        Assert.True(fixture.First.IsReplayMode);
        Assert.True(ReferenceEquals(fixture.First, fixture.Main.SelectedTab));
    });

    private static Task SkipHotkeyLiveAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new SkipHotkeyFixture(live: true);
        await fixture.StartAsync();
        Assert.Equal(false, fixture.First.IsReplayMode);
        await fixture.PressAndWaitAsync(Key.NumPad4, 3570);
        Assert.True(fixture.First.IsBehindLive);
        var starts = fixture.Streamlink.StartCount;
        Assert.True(fixture.Press(Key.NumPad6).Handled);
        await TestWait.UntilAsync(() => !fixture.First.IsReplayMode && fixture.Streamlink.StartCount > starts,
            TimeSpan.FromSeconds(3));
        Assert.Equal(false, fixture.First.IsBehindLive);
        Assert.Equal("Live", fixture.First.ReplayLiveStateText);
        await TestWait.UntilAsync(() => fixture.First.SkipBackwardCommand.CanExecute(null), TimeSpan.FromSeconds(3));
        fixture.Settings.Hotkeys.SkipBackwardSeconds = 1;
        fixture.Settings.Hotkeys.SkipForwardSeconds = 1;
        await fixture.PressAndWaitAsync(Key.NumPad4, 3599);
        await fixture.PressAndWaitAsync(Key.NumPad4, 3598);
        await fixture.PressAndWaitAsync(Key.NumPad6, 3599);
        Assert.True(fixture.First.IsReplayMode);
        starts = fixture.Streamlink.StartCount;
        Assert.True(fixture.Press(Key.NumPad6).Handled);
        await TestWait.UntilAsync(() => !fixture.First.IsReplayMode && fixture.Streamlink.StartCount > starts,
            TimeSpan.FromSeconds(3));
    });

    private static Task SkipHotkeyInputGuardsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new SkipHotkeyFixture();
        Assert.Equal(false, fixture.Press(Key.NumPad4).Handled);
        await fixture.StartAsync();
        await fixture.PositionAsync(600);
        var initialSeeks = fixture.Engine.SeekCount;
        foreach (var editor in new IInputElement[] { new TextBox(), new RichTextBox(), new PasswordBox() })
        {
            Assert.Equal(false, fixture.Window.TryExecutePlaybackHotkey(Key.NumPad4, ModifierKeys.None, editor));
            Assert.Equal(false, fixture.Window.TryExecutePlaybackHotkey(Key.NumPad6, ModifierKeys.None, editor));
        }
        Assert.True(fixture.Window.TryExecutePlaybackHotkey(Key.NumPad4, ModifierKeys.None, null, isRepeat: true));
        Assert.Equal(initialSeeks, fixture.Engine.SeekCount);
        fixture.Main.IsSettingsOpen = true;
        Assert.Equal(false, fixture.Press(Key.NumPad4).Handled);
        fixture.Main.IsSettingsOpen = false;
        fixture.Main.SelectHomeCommand.Execute(null);
        Assert.Equal(false, fixture.Press(Key.NumPad6).Handled);
        fixture.Main.SelectedTab = fixture.First;
        var recorder = (HotkeyRecorderButton)fixture.Window.FindName("SkipBackwardHotkeyRecorder");
        InvokeSkipRecorder(recorder, "OnClick");
        Assert.Equal(false, fixture.Window.TryExecutePlaybackHotkey(Key.NumPad4, ModifierKeys.None, recorder));
        InvokeSkipRecorder(recorder, "OnPreviewKeyDown", fixture.KeyEvent(Key.F8));
        InvokeSkipRecorder(recorder, "OnPreviewKeyUp", fixture.KeyEvent(Key.F8));
        fixture.Flush();
        Assert.Equal("F8", fixture.Settings.Hotkeys.SkipBackward);
        Assert.Equal(false, fixture.Press(Key.NumPad4).Handled);
        await fixture.PressAndWaitAsync(Key.F8, 570);

        fixture.Settings.Hotkeys.SkipForward = "Ctrl+Mouse5";
        Assert.Equal(false, fixture.Window.TryExecuteMouseHotkey(MouseButton.XButton2, ModifierKeys.None, null));
        var count = fixture.Engine.SeekCount;
        Assert.True(fixture.Window.TryExecuteMouseHotkey(MouseButton.XButton2, ModifierKeys.Control, null));
        await fixture.WaitForSeekAsync(count, 600);

        fixture.Settings.Hotkeys.ResetToDefaults();
        fixture.Settings.Hotkeys.PreviousTab = "NumPad4";
        fixture.Main.SelectedTab = fixture.Other;
        Assert.True(fixture.Press(Key.NumPad4).Handled);
        Assert.True(ReferenceEquals(fixture.First, fixture.Main.SelectedTab));
        fixture.Settings.Hotkeys.PreviousTab = "Left";
        fixture.Settings.Hotkeys.GoBack = "NumPad4";
        Assert.True(fixture.Press(Key.NumPad4).Handled);
        Assert.True(ReferenceEquals(fixture.Other, fixture.Main.SelectedTab));
        Assert.Equal(0, fixture.OtherEngine.SeekCount);
    });

    private static Task SkipHotkeyBusyAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new SkipHotkeyFixture(seekCompletion: release.Task);
        try
        {
            await fixture.StartAsync();
            Assert.True(fixture.Press(Key.NumPad6).Handled);
            // The opposite press arrives even before the first seek's Task.Yield resumes.
            Assert.True(fixture.Press(Key.NumPad4).Handled);
            await fixture.Engine.SeekStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(fixture.First.IsReplaySeekInProgress);
            Assert.Equal(false, fixture.First.SkipBackwardCommand.CanExecute(null));
            Assert.Equal(false, fixture.First.SkipForwardCommand.CanExecute(null));
            Assert.True(fixture.Press(Key.NumPad6).Handled);
            Assert.Equal(1, fixture.Engine.SeekCount);
        }
        finally { release.TrySetResult(); }
        await fixture.WaitForSeekAsync(0, 30);
        await fixture.PressAndWaitAsync(Key.NumPad4, 0);
    });

    private static Task SkipHotkeySettingsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Directory.CreateTempSubdirectory("svs-skip-hotkey-ui-");
        try
        {
            var service = new JsonSettingsService(Path.Combine(directory.FullName, "settings.json"));
            await using var fixture = new SkipHotkeyFixture(settingsService: service);
            fixture.Main.IsSettingsOpen = true;
            fixture.Main.ShowHotkeysSettingsCommand.Execute(null);
            fixture.Layout();
            var backward = (HotkeyRecorderButton)fixture.Window.FindName("SkipBackwardHotkeyRecorder");
            var forward = (HotkeyRecorderButton)fixture.Window.FindName("SkipForwardHotkeyRecorder");
            var backwardSlider = PrepareSkipAmountMenu(backward);
            var forwardSlider = PrepareSkipAmountMenu(forward);
            Assert.Equal(30d, backwardSlider.Value);
            Assert.Equal(30d, forwardSlider.Value);
            Assert.True(backward.ToolTip.ToString()!.Contains("Right-click", StringComparison.Ordinal));
            backwardSlider.SetCurrentValue(Slider.ValueProperty, 75d);
            forwardSlider.SetCurrentValue(Slider.ValueProperty, 15d);
            Assert.Equal(75, fixture.Settings.Hotkeys.SkipBackwardSeconds);
            Assert.Equal(15, fixture.Settings.Hotkeys.SkipForwardSeconds);
            InvokeSkipRecorder(backward, "OnClick");
            InvokeSkipRecorder(backward, "OnPreviewKeyDown", fixture.KeyEvent(Key.NumPad6));
            InvokeSkipRecorder(backward, "OnPreviewKeyUp", fixture.KeyEvent(Key.NumPad6));
            fixture.Flush();
            Assert.Equal("NumPad6", fixture.Settings.Hotkeys.SkipBackward);
            Assert.Equal("NumPad4", fixture.Settings.Hotkeys.SkipForward);
            Assert.Equal("Num 4 (Left)", forward.Content.ToString());
            Assert.Equal(75, fixture.Settings.Hotkeys.SkipBackwardSeconds);
            Assert.Equal(15, fixture.Settings.Hotkeys.SkipForwardSeconds);
            await fixture.Main.SaveSettingsCommand.ExecuteAsync();
            var loaded = await service.LoadAsync();
            Assert.Equal("NumPad6", loaded.Hotkeys.SkipBackward);
            Assert.Equal("NumPad4", loaded.Hotkeys.SkipForward);
            Assert.Equal(75, loaded.Hotkeys.SkipBackwardSeconds);
            Assert.Equal(15, loaded.Hotkeys.SkipForwardSeconds);
            ((Button)fixture.Window.FindName("ResetHotkeysButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            fixture.Flush();
            Assert.Equal("Num 4 (Left)", backward.Content.ToString());
            Assert.Equal("Num 6 (Right)", forward.Content.ToString());
            Assert.Equal(30d, backwardSlider.Value);
            Assert.Equal(30d, forwardSlider.Value);
            fixture.Settings.Hotkeys = new HotkeySettings { SkipBackwardSeconds = 41, SkipForwardSeconds = 62 };
            fixture.Flush();
            Assert.Equal(41d, backwardSlider.Value);
            Assert.Equal(62d, forwardSlider.Value);
            foreach (var width in new[] { 1320d, 700d })
            {
                fixture.Layout(width);
                SaveResponsiveWindowImage(fixture.Window, $"skip-hotkey-settings-{width}");
                Assert.True(backward.ActualWidth > 0 && backward.ActualWidth <= 178);
                Assert.True(forward.ActualWidth > 0 && forward.ActualWidth <= 178);
            }
        }
        finally { directory.Delete(recursive: true); }
    });

    private static Task SkipHotkeyOtherSurfacesAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new SkipHotkeyFixture();
        await fixture.StartAsync();
        await fixture.PositionAsync(600);
        var detached = new DetachedVideoWindow(fixture.First);
        try
        {
            var count = fixture.Engine.SeekCount;
            var args = fixture.KeyEvent(Key.NumPad4);
            detached.RaiseEvent(args);
            Assert.True(args.Handled);
            await fixture.WaitForSeekAsync(count, 570);
            var overlay = new ReplaySeekOverlay { DataContext = fixture.First };
            fixture.Flush();
            var chrome = (FrameworkElement)overlay.FindName("OverlayChrome");
            count = fixture.Engine.SeekCount;
            args = fixture.KeyEvent(Key.NumPad6);
            chrome.RaiseEvent(args);
            Assert.True(args.Handled);
            await fixture.WaitForSeekAsync(count, 600);
        }
        finally { detached.CloseForTabDisposal(); }
    });

    private static Slider PrepareSkipAmountMenu(HotkeyRecorderButton recorder)
    {
        var menu = recorder.ContextMenu;
        Assert.NotNull(menu);
        menu!.PlacementTarget = recorder;
        menu.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        menu.Measure(new Size(300, 260));
        menu.Arrange(new Rect(0, 0, menu.DesiredSize.Width, menu.DesiredSize.Height));
        menu.UpdateLayout();
        var slider = FindVisualDescendants<Slider>(menu).Single();
        Assert.Equal(1d, slider.Minimum);
        Assert.Equal(300d, slider.Maximum);
        Assert.True(slider.IsSnapToTickEnabled);
        Assert.Equal(1d, slider.TickFrequency);
        return slider;
    }

    private static void InvokeSkipRecorder(HotkeyRecorderButton recorder, string method, params object[] args) =>
        typeof(HotkeyRecorderButton).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(recorder, args);

    private sealed class SkipHotkeyFixture : IAsyncDisposable
    {
        private readonly FakePlaybackEngineFactory playback;
        private readonly FakePlaybackEngineFactory otherPlayback;
        private readonly SkipHotkeyPresentationSource source;

        internal SkipHotkeyFixture(bool live = false, Task? seekCompletion = null, ISettingsService? settingsService = null,
            Task? pauseCompletion = null)
        {
            Settings = new AppSettings { StreamlinkPath = "streamlink.exe", VlcDirectory = @"C:\VLC", KeepInactiveTabsRunning = true };
            Settings.Chat.ConnectAutomatically = false;
            Settings.Chat.Layout = ChatLayout.Hidden;
            Streamlink = new FakeStreamlinkService();
            playback = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                Duration = TimeSpan.FromHours(1),
                PauseCompletion = pauseCompletion ?? Task.CompletedTask,
                SeekCompletion = seekCompletion ?? Task.CompletedTask
            });
            otherPlayback = new FakePlaybackEngineFactory(() => new FakePlaybackEngine { Duration = TimeSpan.FromHours(1) });
            var chats = new FakeChatClientFactory();
            var logger = new MemoryLogger();
            Main = TestViewModels.CreateMain(Settings, settingsService ?? new FakeSettingsService(Settings),
                Streamlink, playback, chats, logger, action => action());
            StreamTabViewModel CreateTab(string id, FakePlaybackEngineFactory factory) => TestViewModels.CreateTab(
                live ? StreamInputParser.Parse("streamer" + id, PlatformKind.Twitch) :
                    new StreamTarget(PlatformKind.Twitch, "streamer" + id, "https://www.twitch.tv/videos/" + id,
                        StreamTargetKind.TwitchVod, MediaId: id, MediaDuration: TimeSpan.FromHours(1)),
                "best", Streamlink, factory, chats, logger, action => action(),
                replayResolver: new FakeReplayResolver(new ReplaySessionInfo(PlatformKind.Twitch, "streamer" + id,
                    "https://www.twitch.tv/videos/" + id, id, null, TimeSpan.FromHours(1), true, "")),
                vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
            First = CreateTab("123", playback);
            Other = CreateTab("456", otherPlayback);
            Main.Tabs.Add(First);
            Main.Tabs.Add(Other);
            Main.SelectedTab = First;
            Window = new MainWindow { DataContext = Main };
            RemoveMainWindowAutomaticStartup(Window);
            SetMainWindowViewModel(Window, Main);
            source = new SkipHotkeyPresentationSource { RootVisual = Window };
            Flush();
        }

        internal AppSettings Settings { get; }
        internal FakeStreamlinkService Streamlink { get; }
        internal MainViewModel Main { get; }
        internal MainWindow Window { get; }
        internal StreamTabViewModel First { get; }
        internal StreamTabViewModel Other { get; }
        internal FakePlaybackEngine Engine => playback.Engine!;
        internal FakePlaybackEngine OtherEngine => otherPlayback.Engine!;

        internal async Task StartAsync()
        {
            First.SetVideoHandle(new IntPtr(42));
            await First.StartAsync(Settings);
            Other.SetVideoHandle(new IntPtr(43));
            await Other.StartAsync(Settings);
            await TestWait.UntilAsync(() => First.CanSeekReplay && Other.CanSeekReplay, TimeSpan.FromSeconds(3));
            await StopReplayClockPollingAsync(First);
            await StopReplayClockPollingAsync(Other);
        }

        internal async Task PositionAsync(double seconds)
        {
            await First.SeekReplayAsync(TimeSpan.FromSeconds(seconds));
            MarkReplayClockSeekConfirmed(First);
        }

        internal KeyEventArgs KeyEvent(Key key) => new(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };

        internal KeyEventArgs Press(Key key)
        {
            var args = KeyEvent(key);
            Window.RaiseEvent(args);
            return args;
        }

        internal async Task PressAndWaitAsync(Key key, double expectedSeconds)
        {
            var count = Engine.SeekCount;
            Assert.True(Press(key).Handled);
            await WaitForSeekAsync(count, expectedSeconds);
        }

        internal async Task WaitForSeekAsync(int previousCount, double expectedSeconds)
        {
            await TestWait.UntilAsync(() => Engine.SeekCount > previousCount && !First.IsReplaySeekInProgress &&
                First.SkipBackwardCommand.CanExecute(null) && First.SkipForwardCommand.CanExecute(null), TimeSpan.FromSeconds(3));
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), Engine.Position);
            MarkReplayClockSeekConfirmed(First);
        }

        internal void Flush() => Window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

        internal void Layout(double width = 1320)
        {
            var root = (FrameworkElement)Window.Content;
            root.Measure(new Size(width, 900));
            root.Arrange(new Rect(0, 0, width, 900));
            root.UpdateLayout();
            Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            root.UpdateLayout();
        }

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await Main.DisposeAsync();
        }
    }

    private sealed class SkipHotkeyPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}

using System.Windows.Interop;
using System.Windows.Threading;

internal static partial class ApplicationTestCatalog
{
    private static Task SkipHotkeyPhysicalKeyboardAsync() => TestSta.RunAsync(async () =>
    {
        await using var fixture = new SkipHotkeyFixture();
        var originalNumLock = Keyboard.IsKeyToggled(Key.NumLock);
        try
        {
            await fixture.StartAsync();
            fixture.Window.Width = 1200;
            fixture.Window.Height = 900;
            fixture.Window.Topmost = true;
            fixture.Window.Show();
            fixture.Window.UpdateLayout();
            await NativeWindowTest.RequireForegroundAsync(new WindowInteropHelper(fixture.Window).Handle,
                TimeSpan.FromSeconds(2), "Physical numpad hotkey input");
            Assert.True(fixture.Window.Focus());
            foreach (var numLock in new[] { true, false })
            {
                await SetSkipHotkeyNumLockAsync(numLock);
                await fixture.PositionAsync(600);
                var count = fixture.Engine.SeekCount;
                NativeWindowTest.SendScanCode(0x4B); // Physical keypad 4/Left.
                await fixture.WaitForSeekAsync(count, 570);
                count = fixture.Engine.SeekCount;
                NativeWindowTest.SendScanCode(0x4D); // Physical keypad 6/Right.
                await fixture.WaitForSeekAsync(count, 600);
                Assert.True(ReferenceEquals(fixture.First, fixture.Main.SelectedTab));

                NativeWindowTest.SendScanCode(0x4D, extended: true); // Separate Right arrow.
                await TestWait.UntilAsync(() => ReferenceEquals(fixture.Other, fixture.Main.SelectedTab), TimeSpan.FromSeconds(2));
                NativeWindowTest.SendScanCode(0x4B, extended: true);
                await TestWait.UntilAsync(() => ReferenceEquals(fixture.First, fixture.Main.SelectedTab), TimeSpan.FromSeconds(2));
                Assert.Equal(0, fixture.OtherEngine.SeekCount);

                fixture.Main.IsSettingsOpen = true;
                fixture.Main.ShowHotkeysSettingsCommand.Execute(null);
                fixture.Window.UpdateLayout();
                var recorder = (HotkeyRecorderButton)fixture.Window.FindName("SkipBackwardHotkeyRecorder");
                recorder.BringIntoView();
                fixture.Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                InvokeSkipRecorder(recorder, "OnClick");
                Assert.True(recorder.IsKeyboardFocused);
                NativeWindowTest.SendScanCode(0x4D);
                await TestWait.UntilAsync(() => !recorder.IsCapturingInput, TimeSpan.FromSeconds(2));
                Assert.Equal("NumPad6", fixture.Settings.Hotkeys.SkipBackward);
                Assert.Equal("NumPad4", fixture.Settings.Hotkeys.SkipForward);
                Assert.Equal("Left", fixture.Settings.Hotkeys.PreviousTab);
                Assert.Equal("Right", fixture.Settings.Hotkeys.NextTab);
                fixture.Settings.Hotkeys.ResetToDefaults();
                fixture.Main.IsSettingsOpen = false;
                fixture.Window.UpdateLayout();
                fixture.Window.Focus();
            }
        }
        finally { await SetSkipHotkeyNumLockAsync(originalNumLock); }
    });

    private static Task SkipHotkeyPhysicalSliderAsync() => TestSta.RunAsync(async () =>
    {
        await using var fixture = new SkipHotkeyFixture();
        if (!NativeWindowTest.TryGetCursorPosition(out var originalCursor))
            throw new InteractiveDesktopTestSkippedException("Cannot preserve the pointer for skip slider input tests.");
        ContextMenu? openMenu = null;
        try
        {
            fixture.Main.IsSettingsOpen = true;
            fixture.Main.ShowHotkeysSettingsCommand.Execute(null);
            fixture.Window.Width = 1320;
            fixture.Window.Height = 950;
            fixture.Window.Topmost = true;
            fixture.Window.Show();
            fixture.Window.UpdateLayout();
            await NativeWindowTest.RequireForegroundAsync(new WindowInteropHelper(fixture.Window).Handle,
                TimeSpan.FromSeconds(2), "Physical skip slider input");
            foreach (var backward in new[] { true, false })
            {
                var recorder = (HotkeyRecorderButton)fixture.Window.FindName(backward ?
                    "SkipBackwardHotkeyRecorder" : "SkipForwardHotkeyRecorder");
                var originalGesture = recorder.Gesture;
                recorder.BringIntoView();
                fixture.Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                fixture.Window.UpdateLayout();
                // Opening the context menu must cancel an unfinished shortcut recording.
                InvokeSkipRecorder(recorder, "OnClick");
                var point = recorder.PointToScreen(new Point(recorder.ActualWidth / 2, recorder.ActualHeight / 2));
                NativeWindowTest.SetCursorPosition((int)Math.Round(point.X), (int)Math.Round(point.Y));
                await SendPictureInPictureContextMenuRightClickAsync(busy: false, afterInjection: null);
                openMenu = recorder.ContextMenu!;
                await TestWait.UntilAsync(() => openMenu.IsOpen, TimeSpan.FromSeconds(2));
                Assert.Equal(false, recorder.IsCapturingInput);
                openMenu.UpdateLayout();
                var slider = FindVisualDescendants<Slider>(openMenu).Single();
                Assert.Equal(30d, slider.Value);
                var track = (Track)slider.Template.FindName("PART_Track", slider);
                var thumb = track.Thumb;
                point = thumb.PointToScreen(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2));
                NativeWindowTest.SetCursorPosition((int)Math.Round(point.X), (int)Math.Round(point.Y));
                await TestWait.UntilAsync(() => thumb.IsMouseOver, TimeSpan.FromSeconds(2),
                    $"The pointer must reach the skip slider thumb at {point}; menu={new WindowInteropHelper(fixture.Window).Handle}, " +
                    $"hit={NativeWindowHitTester.Instance.WindowFromPoint((int)point.X, (int)point.Y)}, thumb={thumb.ActualWidth}x{thumb.ActualHeight}.");
                ReplayVlcMouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
                await TestWait.UntilAsync(() => thumb.IsDragging, TimeSpan.FromSeconds(2));
                point = track.PointToScreen(new Point(track.ActualWidth * 0.6, track.ActualHeight / 2));
                NativeWindowTest.SetCursorPosition((int)Math.Round(point.X), (int)Math.Round(point.Y));
                await TestWait.UntilAsync(() => slider.Value > 100, TimeSpan.FromSeconds(2));
                ReplayVlcMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                await TestWait.UntilAsync(() => !thumb.IsDragging, TimeSpan.FromSeconds(2));
                Assert.True(openMenu.IsOpen, "Dragging the amount must keep the context menu open.");
                Assert.Equal(Math.Round(slider.Value), slider.Value);
                Assert.Equal((int)slider.Value, backward ? fixture.Settings.Hotkeys.SkipBackwardSeconds : fixture.Settings.Hotkeys.SkipForwardSeconds);
                if (backward) Assert.Equal(30, fixture.Settings.Hotkeys.SkipForwardSeconds);
                slider.Focus();
                var beforeArrow = slider.Value;
                NativeWindowTest.SendScanCode(0x4D, extended: true);
                await TestWait.UntilAsync(() => slider.Value == beforeArrow + 1, TimeSpan.FromSeconds(2));
                SaveSkipHotkeyMenuImage(openMenu, backward ? "skip-backward-menu" : "skip-forward-menu");
                NativeWindowTest.SendVirtualKeySequence((0x1B, false), (0x1B, true));
                await TestWait.UntilAsync(() => !openMenu.IsOpen, TimeSpan.FromSeconds(2));
                Assert.Equal(originalGesture, recorder.Gesture);
                Assert.Equal(false, recorder.IsCapturingInput);
                openMenu = null;
            }
        }
        finally
        {
            ReplayVlcMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
            if (openMenu is not null) openMenu.IsOpen = false;
            NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
        }
    });

    private static async Task SetSkipHotkeyNumLockAsync(bool enabled)
    {
        if (Keyboard.IsKeyToggled(Key.NumLock) == enabled) return;
        NativeWindowTest.SendVirtualKeySequence((0x90, false), (0x90, true));
        await TestWait.UntilAsync(() => Keyboard.IsKeyToggled(Key.NumLock) == enabled, TimeSpan.FromSeconds(2));
    }

    private static Task SkipHotkeyNativeVlcAsync(VideoRendererMode rendererMode) => TestSta.RunAsync(async () =>
    {
        var settings = VodResumeTestCatalog.Settings();
        settings.VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
        settings.VideoRendererMode = rendererMode;
        settings.Chat.Layout = ChatLayout.Hidden;
        var media = new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-colors.mp4"));
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(media, "Local test video"))
        };
        var logger = new MemoryLogger();
        var factory = new VodResumeNativeFactory(settings.Chat);
        var chats = new FakeChatClientFactory();
        await using var main = TestViewModels.CreateMain(settings, new FakeSettingsService(settings), streamlink,
            factory, chats, logger, action => action());
        var tab = TestViewModels.CreateTab(VodResumeTestCatalog.Target() with { MediaDuration = TimeSpan.FromSeconds(60) },
            "best", streamlink, factory, chats, logger, action => action(),
            vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
        main.Tabs.Add(tab);
        main.SelectedTab = tab;
        var window = new MainWindow { DataContext = main, Width = 1000, Height = 720, Topmost = true };
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, main);
        var originalNumLock = Keyboard.IsKeyToggled(Key.NumLock);
        var restoreCursor = NativeWindowTest.TryGetCursorPosition(out var originalCursor);
        try
        {
            window.Show();
            window.UpdateLayout();
            var surface = FindVisualDescendants<VideoSurface>(window).Single();
            Assert.True(surface.Handle != IntPtr.Zero);
            tab.SetVideoHandle(surface.Handle);
            tab.IsMuted = true;
            await tab.StartAsync(settings);
            var engine = factory.Engine!;
            await TestWait.UntilAsync(() => tab.CanSeekReplay && engine.TryGetPlaybackHealth(out var health) &&
                health.DisplayedPictures > 0, TimeSpan.FromSeconds(10));
            Assert.Equal(rendererMode, ((LibVlcPlaybackEngine)engine).RendererMode);
            await NativeWindowTest.RequireForegroundAsync(new WindowInteropHelper(window).Handle,
                TimeSpan.FromSeconds(2), "Native VLC skip hotkey input");
            Assert.True(window.Focus());
            var point = surface.PointToScreen(new Point(surface.ActualWidth / 2, surface.ActualHeight / 2));
            NativeWindowTest.SendLeftClick((int)Math.Round(point.X), (int)Math.Round(point.Y));

            foreach (var numLock in new[] { false, true })
            {
                await SetSkipHotkeyNumLockAsync(numLock);
                settings.Hotkeys.SkipBackwardSeconds = 30;
                settings.Hotkeys.SkipForwardSeconds = 30;
                await tab.SeekReplayAsync(TimeSpan.FromSeconds(45));
                await WaitForNativePositionAsync(45);
                NativeWindowTest.SendScanCode(0x4B);
                await WaitForNativePositionAsync(15);
                NativeWindowTest.SendScanCode(0x4D);
                await WaitForNativePositionAsync(45);
                settings.Hotkeys.SkipBackwardSeconds = 7;
                settings.Hotkeys.SkipForwardSeconds = 11;
                NativeWindowTest.SendScanCode(0x4B);
                await WaitForNativePositionAsync(38);
                NativeWindowTest.SendScanCode(0x4D);
                await WaitForNativePositionAsync(49);
            }
            Assert.Equal(false, logger.Entries.Any(entry => entry.Message.Contains("Replay seek failed", StringComparison.Ordinal)));

            Task WaitForNativePositionAsync(double expected) => TestWait.UntilAsync(() =>
                tab.SkipBackwardCommand.CanExecute(null) && tab.SkipForwardCommand.CanExecute(null) &&
                engine.TryGetPlaybackClock(out var clock) && Math.Abs(clock.Position.TotalSeconds - expected) < 3,
                TimeSpan.FromSeconds(8), $"{rendererMode}: native playback did not reach {expected}s after numpad input.");
        }
        finally
        {
            await SetSkipHotkeyNumLockAsync(originalNumLock);
            if (restoreCursor) NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
            window.Close();
        }
    });

    private static void SaveSkipHotkeyMenuImage(ContextMenu menu, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SVS_RESPONSIVE_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(WpfVisualTest.Render(menu)));
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }
}

using System.Windows.Interop;

internal static partial class ApplicationTestCatalog
{
    private static Task PauseHotkeyNativeVlcAsync(VideoRendererMode rendererMode) => TestSta.RunAsync(async () =>
    {
        var settings = VodResumeTestCatalog.Settings();
        settings.VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
        settings.VideoRendererMode = rendererMode;
        settings.Chat.Layout = ChatLayout.Hidden;
        var media = new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-colors.mp4"));
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(media, "Local pause hotkey video"))
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
        var restoreCursor = NativeWindowTest.TryGetCursorPosition(out var originalCursor);
        var spaceKey = checked((ushort)KeyInterop.VirtualKeyFromKey(Key.Space));
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
                TimeSpan.FromSeconds(2), "Native VLC pause hotkey input");
            Assert.True(window.Focus());
            var point = surface.PointToScreen(new Point(surface.ActualWidth / 2, surface.ActualHeight / 3));
            NativeWindowTest.SendLeftClick((int)Math.Round(point.X), (int)Math.Round(point.Y));

            NativeWindowTest.SendScanCode(0x39); // Physical Space, including its release.
            await WaitForStateAsync(PlaybackStatus.Paused);
            await AssertNativeClockPausedAsync();
            var heldPosition = ReadNativeClock();
            NativeWindowTest.SendScanCode(0x39);
            await WaitForStateAsync(PlaybackStatus.Playing);
            await TestWait.UntilAsync(() => ReadNativeClock() > heldPosition + TimeSpan.FromMilliseconds(150),
                TimeSpan.FromSeconds(3), "Native playback must advance after Space resumes it.");

            // Separate repeated key-downs from key-up to exercise WPF's real IsRepeat flag.
            NativeWindowTest.SendVirtualKeySequence((spaceKey, false));
            await WaitForStateAsync(PlaybackStatus.Paused);
            for (var repeat = 0; repeat < 3; repeat++)
                NativeWindowTest.SendVirtualKeySequence((spaceKey, false));
            await AssertNativeClockPausedAsync();
            Assert.Equal(PlaybackStatus.Paused, tab.Status);
            NativeWindowTest.SendVirtualKeySequence((spaceKey, true));

            ToggleMainWindowFullscreen(window, "StreamOnly");
            PumpResponsiveLayout(window);
            await NativeWindowTest.RequireForegroundAsync(new WindowInteropHelper(window).Handle,
                TimeSpan.FromSeconds(2), "Fullscreen pause hotkey input");
            Assert.True(window.Focus());
            NativeWindowTest.SendScanCode(0x39);
            await WaitForStateAsync(PlaybackStatus.Playing);
            NativeWindowTest.SendScanCode(0x39);
            await WaitForStateAsync(PlaybackStatus.Paused);
            await AssertNativeClockPausedAsync();
            ExitMainWindowFullscreenIfActive(window);
            PumpResponsiveLayout(window);

            main.IsSettingsOpen = true;
            main.ShowHotkeysSettingsCommand.Execute(null);
            PumpResponsiveLayout(window);
            var recorder = (HotkeyRecorderButton)window.FindName("TogglePauseHotkeyRecorder");
            recorder.BringIntoView();
            PumpResponsiveLayout(window);
            var recorderPoint = recorder.PointToScreen(new Point(recorder.ActualWidth / 2, recorder.ActualHeight / 2));
            NativeWindowTest.SendLeftClick((int)Math.Round(recorderPoint.X), (int)Math.Round(recorderPoint.Y));
            await TestWait.UntilAsync(() => recorder.IsCapturing, TimeSpan.FromSeconds(2));
            NativeWindowTest.SendScanCode(0x39);
            await TestWait.UntilAsync(() => !recorder.IsCapturingInput, TimeSpan.FromSeconds(2));
            Assert.Equal("Space", settings.Hotkeys.TogglePause);
            Assert.Equal(PlaybackStatus.Paused, tab.Status);
            NativeWindowTest.SendLeftClick((int)Math.Round(recorderPoint.X), (int)Math.Round(recorderPoint.Y));
            await TestWait.UntilAsync(() => recorder.IsCapturing, TimeSpan.FromSeconds(2));
            var remappedKey = checked((ushort)KeyInterop.VirtualKeyFromKey(Key.F8));
            NativeWindowTest.SendVirtualKeySequence((remappedKey, false), (remappedKey, true));
            await TestWait.UntilAsync(() => !recorder.IsCapturingInput, TimeSpan.FromSeconds(2));
            Assert.Equal("F8", settings.Hotkeys.TogglePause);
            main.IsSettingsOpen = false;
            PumpResponsiveLayout(window);
            Assert.True(window.Focus());
            NativeWindowTest.SendScanCode(0x39);
            await AssertNativeClockPausedAsync();
            Assert.Equal(PlaybackStatus.Paused, tab.Status);
            NativeWindowTest.SendVirtualKeySequence((remappedKey, false), (remappedKey, true));
            await WaitForStateAsync(PlaybackStatus.Playing);
            Assert.Equal(false, logger.Entries.Any(entry => entry.Level == AppLogLevel.Error));

            Task WaitForStateAsync(PlaybackStatus state) => TestWait.UntilAsync(() => tab.Status == state &&
                tab.PauseOrResumeCommand.CanExecute(null) && main.PauseSelectedCommand.CanExecute(null),
                TimeSpan.FromSeconds(5), $"{rendererMode}: physical pause input did not reach {state}.");

            TimeSpan ReadNativeClock()
            {
                Assert.True(engine.TryGetPlaybackClock(out var clock));
                return clock.Position;
            }

            async Task AssertNativeClockPausedAsync()
            {
                await Task.Delay(150);
                var position = ReadNativeClock();
                await Task.Delay(350);
                Assert.True((ReadNativeClock() - position).Duration() < TimeSpan.FromMilliseconds(100),
                    $"{rendererMode}: Space must freeze the real VLC clock while paused.");
            }
        }
        finally
        {
            NativeWindowTest.SendVirtualKeySequence((spaceKey, true));
            ExitMainWindowFullscreenIfActive(window);
            if (restoreCursor) NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
            window.Close();
        }
    });
}

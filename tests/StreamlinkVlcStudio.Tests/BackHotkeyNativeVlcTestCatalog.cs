using System.Windows.Interop;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> BackHotkeyNativeVlcTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA"))
            ? []
            :
            [
                ("Back hotkey physical side buttons navigate once over real Direct3D11 VLC with a busy dispatcher",
                    () => BackHotkeyNativeVlcAsync(VideoRendererMode.Direct3D11)),
                ("Back hotkey physical side buttons navigate once over real GDI VLC with a busy dispatcher",
                    () => BackHotkeyNativeVlcAsync(VideoRendererMode.Gdi))
            ];

    private static Task BackHotkeyNativeVlcAsync(VideoRendererMode rendererMode) =>
        WithResponsiveWindowAsync(withVideo: true, async (window, viewModel) =>
        {
            var vlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
            var mediaPath = Path.GetFullPath(Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA")!);
            Assert.True(File.Exists(Path.Combine(vlcDirectory, "libvlc.dll")), "Configured libVLC is missing.");
            Assert.True(File.Exists(mediaPath), "Configured deterministic video fixture is missing.");
            if (!NativeWindowTest.TryGetCursorPosition(out var originalCursor))
                throw new InteractiveDesktopTestSkippedException("Cannot preserve the cursor for native Back input tests.");

            var selected = viewModel.SelectedTab!;
            // Give Back distinguishable consecutive destinations so a double dispatch
            // cannot pass merely because the first press reached Home.
            viewModel.SelectHomeCommand.Execute(null);
            viewModel.ShowRecentHomePageCommand.Execute(null);
            viewModel.SelectedTab = selected;
            window.Topmost = true;
            PumpResponsiveLayout(window);
            foreach (var overlay in FindVisualDescendants<ReplaySeekOverlay>(window))
                overlay.IsOverlayEnabled = false;
            var handle = new WindowInteropHelper(window).Handle;
            var surface = FindVisualDescendants<VideoSurface>(window).Single();
            var factory = new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings());
            using var engine = await factory.CreateAsync(vlcDirectory,
                enableNativeOverlay: false, rendererMode: rendererMode);
            try
            {
                await NativeWindowTest.RequireForegroundAsync(handle, TimeSpan.FromSeconds(2),
                    "Native Back input requires an interactive foreground main window");
                engine.SetVideoHandle(surface.Handle);
                await engine.PlayAsync(new Uri(mediaPath), 0, PlaybackAudioState.HardMuted);
                await TestWait.UntilAsync(() => engine.TryGetVideoSize(out var width, out var height) &&
                    width > 0 && height > 0 && engine.TryGetPlaybackClock(out var clock) &&
                    clock.Position > TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(8));
                Assert.Equal(rendererMode, ((LibVlcPlaybackEngine)engine).RendererMode);
                PumpResponsiveLayout(window);

                PlaceBackHotkeyPointerOnVlc(surface);
                // Send actual desktop input to the renderer's HWND, not a managed
                // routed event or a direct call to the command-dispatch helper.
                await PressBackHotkeySideButtonAsync(1);
                await AssertBackHotkeyRecentDestinationAsync(viewModel);

                viewModel.SelectedTab = selected;
                viewModel.Settings.Hotkeys.GoBack = "Mouse5";
                PumpResponsiveLayout(window);
                PlaceBackHotkeyPointerOnVlc(surface);
                await PressBackHotkeySideButtonAsync(1);
                Assert.Equal(false, viewModel.IsHomeSelected);
                Assert.True(ReferenceEquals(selected, viewModel.SelectedTab),
                    "Mouse4 must stop navigating after Back is remapped to Mouse5.");

                await AssertBackHotkeyOtherWindowIgnoredAsync(window, viewModel, selected);
                await NativeWindowTest.RequireForegroundAsync(handle, TimeSpan.FromSeconds(2),
                    "Fullscreen Back input requires the main window to regain foreground");
                ToggleMainWindowFullscreen(window, "StreamOnly");
                PumpResponsiveLayout(window);
                Assert.True(viewModel.IsStreamOnlyFullscreenActive);
                PlaceBackHotkeyPointerOnVlc(surface);

                using var senderReady = new ManualResetEventSlim();
                using var dispatcherBlocked = new ManualResetEventSlim();
                using var inputSent = new ManualResetEventSlim();
                var sender = Task.Run(() =>
                {
                    senderReady.Set();
                    Assert.True(dispatcherBlocked.Wait(TimeSpan.FromSeconds(2)),
                        "The native input sender did not observe the dispatcher stall.");
                    try
                    {
                        BackHotkeyMouseEvent(0x0080, 0, 0, 2, UIntPtr.Zero); // XBUTTONDOWN
                        Thread.Sleep(40);
                    }
                    finally
                    {
                        BackHotkeyMouseEvent(0x0100, 0, 0, 2, UIntPtr.Zero); // XBUTTONUP
                        inputSent.Set();
                    }
                });
                // This deliberately exceeds the former hook's 25 ms deadline.
                // Input is sent independently while the WPF dispatcher cannot run.
                Assert.True(senderReady.Wait(TimeSpan.FromSeconds(2)), "The native input sender did not start.");
                dispatcherBlocked.Set();
                Thread.Sleep(180);
                Assert.True(inputSent.IsSet, "The physical side-button gesture did not finish during the UI stall.");
                await sender.WaitAsync(TimeSpan.FromSeconds(2));
                await AssertBackHotkeyRecentDestinationAsync(viewModel);
                Assert.Equal(false, viewModel.IsStreamOnlyFullscreenActive);
                Assert.Equal(false, viewModel.IsVideoFullscreenActive);

                viewModel.SelectedTab = selected;
                PumpResponsiveLayout(window);
                var replayOverlay = FindVisualDescendants<ReplaySeekOverlay>(window).Single();
                replayOverlay.IsOverlayEnabled = true;
                replayOverlay.ProcessPointerSample(new Point(100, 100), true, Environment.TickCount64);
                PumpResponsiveLayout(window);
                Assert.True(replayOverlay.IsOverlayOpen);
                var chrome = (FrameworkElement)replayOverlay.FindName("OverlayChrome");
                var overlayPoint = chrome.PointToScreen(new Point(chrome.ActualWidth / 2, chrome.ActualHeight / 2));
                Assert.True(ReplaySeekOverlay.IsReplayOverlayWindow(
                    NativeWindowHitTester.Instance.WindowFromPoint((int)overlayPoint.X, (int)overlayPoint.Y)));
                NativeWindowTest.SetCursorPosition((int)overlayPoint.X, (int)overlayPoint.Y);
                await PressBackHotkeySideButtonAsync(2);
                await AssertBackHotkeyRecentDestinationAsync(viewModel);
                Console.WriteLine($"Native {rendererMode} Back: Mouse4, remapped Mouse5, foreign-window isolation, " +
                    "replay-overlay input, and a 180 ms fullscreen dispatcher stall each preserved the expected navigation state.");
            }
            finally
            {
                ExitMainWindowFullscreenIfActive(window);
                NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
                await engine.StopAsync();
            }
        });

    private static void PlaceBackHotkeyPointerOnVlc(VideoSurface surface)
    {
        surface.SyncNativeBounds();
        var point = surface.PointToScreen(new Point(surface.ActualWidth / 2, surface.ActualHeight / 3));
        var x = (int)Math.Round(point.X);
        var y = (int)Math.Round(point.Y);
        var hit = NativeWindowHitTester.Instance.WindowFromPoint(x, y);
        Assert.True(hit != IntPtr.Zero && hit != surface.Handle &&
            NativeWindowHitTester.Instance.IsChild(surface.Handle, hit),
            "Physical Back input must hit a real VLC renderer descendant: " +
            NativeWindowTest.DescribeWindowAtPoint(x, y));
        NativeWindowTest.SetCursorPosition(x, y);
    }

    private static async Task PressBackHotkeySideButtonAsync(uint button)
    {
        try
        {
            BackHotkeyMouseEvent(0x0080, 0, 0, button, UIntPtr.Zero);
            await Task.Delay(40);
        }
        finally
        {
            BackHotkeyMouseEvent(0x0100, 0, 0, button, UIntPtr.Zero);
        }
        await Task.Delay(180);
    }

    private static async Task AssertBackHotkeyRecentDestinationAsync(MainViewModel viewModel)
    {
        await TestWait.UntilAsync(() => viewModel.IsHomeSelected && viewModel.IsRecentHomePageSelected,
            TimeSpan.FromSeconds(2));
        await Task.Delay(180); // Catch a second dispatch from the same down/up sequence.
        Assert.True(viewModel.IsHomeSelected && viewModel.IsRecentHomePageSelected,
            "One side-button click must restore exactly the previous Recent page.");
        Assert.True(viewModel.CanGoBack, "The test must retain earlier history to detect duplicate navigation.");
    }

    private static async Task AssertBackHotkeyOtherWindowIgnoredAsync(
        MainWindow main, MainViewModel viewModel, StreamTabViewModel selected)
    {
        var other = new Window
        {
            Title = "Back hotkey unrelated window test",
            Width = 280,
            Height = 180,
            Left = main.Left + 100,
            Top = main.Top + 100,
            Topmost = true,
            ShowInTaskbar = false,
            Content = new Border { Background = Brushes.DimGray }
        };
        var receivedRelease = false;
        other.PreviewMouseUp += (_, e) => receivedRelease |= e.ChangedButton == MouseButton.XButton2;
        try
        {
            other.Show();
            other.UpdateLayout();
            var handle = new WindowInteropHelper(other).Handle;
            await NativeWindowTest.RequireForegroundAsync(handle, TimeSpan.FromSeconds(2),
                "Back isolation requires the unrelated window to receive physical input");
            var point = ((FrameworkElement)other.Content).PointToScreen(new Point(60, 60));
            Assert.True(NativeWindowTest.IsRootWindowAtPoint(handle, (int)point.X, (int)point.Y));
            NativeWindowTest.SetCursorPosition((int)point.X, (int)point.Y);
            await PressBackHotkeySideButtonAsync(2);
            await TestWait.UntilAsync(() => receivedRelease, TimeSpan.FromSeconds(1));
            Assert.Equal(false, viewModel.IsHomeSelected);
            Assert.True(ReferenceEquals(selected, viewModel.SelectedTab),
                "A side button in another window must not navigate the main window.");
        }
        finally
        {
            other.Close();
        }
    }

    [DllImport("user32", EntryPoint = "mouse_event")]
    private static extern void BackHotkeyMouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}

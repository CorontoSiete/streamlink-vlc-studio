using System.ComponentModel;
using System.Windows.Interop;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> VolumeWheelInputTests =>
    [
        ("volume wheel routes by its screen point without VLC geometry or cursor queries", VolumeWheelWithoutNativeGeometryAsync),
        ("volume wheel busy dispatcher preserves normal WPF scrolling and context menu input exactly once", VolumeWheelDefaultWpfScrollAsync),
        ("volume wheel busy dispatcher leaves replay seek overlay input to its native child", VolumeWheelSeekOverlayAsync),
        .. VolumeWheelNativeVlcTests
    ];

    private static IReadOnlyList<(string Name, Func<Task> Run)> VolumeWheelNativeVlcTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA"))
            ? []
            :
            [
                ("volume wheel preserves physical bursts over real Direct3D11 VLC in main fullscreen and detached windows",
                    () => VolumeWheelRealVlcAsync(VideoRendererMode.Direct3D11)),
                ("volume wheel preserves physical bursts over real GDI VLC in main fullscreen and detached windows",
                    () => VolumeWheelRealVlcAsync(VideoRendererMode.Gdi))
            ];

    private static Task VolumeWheelWithoutNativeGeometryAsync() =>
        WithResponsiveWindowAsync(withVideo: true, async (window, viewModel) =>
        {
            var tab = viewModel.SelectedTab!;
            // This tab has no playback engine. Neither a fresh geometry query nor
            // libVLC's most recent cursor position can participate in hit testing.
            Assert.Equal(false, tab.TryGetVideoSize(out _, out _));
            Assert.Equal(false, tab.TryGetVideoCursor(out _, out _));
            window.Topmost = true;
            PumpResponsiveLayout(window);
            await NativeWindowTest.RequireForegroundAsync(new WindowInteropHelper(window).Handle,
                TimeSpan.FromSeconds(2), "Volume input without native geometry");
            var surface = FindVisualDescendants<VideoSurface>(window).Single();
            var point = VolumeWheelPoint(surface);
            tab.Volume = 60;
            Assert.True(window.TryRouteMouseWheel(point.X, point.Y, 120),
                "A visible video surface must accept volume input even while VLC geometry is unavailable.");
            Assert.Equal(65, tab.Volume);
            Assert.True(window.TryRouteMouseWheel(point.X, point.Y, -240));
            Assert.Equal(55, tab.Volume);

            PumpResponsiveLayout(window);
            var osd = (VolumeOverlay)window.FindName("VolumeOsd");
            var popup = (Popup)osd.FindName("Popup");
            Assert.True(popup.IsOpen);
            var chrome = (FrameworkElement)popup.Child;
            var popupPoint = chrome.PointToScreen(new Point(chrome.ActualWidth / 2, chrome.ActualHeight / 2));
            Assert.True(window.TryRouteMouseWheel((int)Math.Round(popupPoint.X), (int)Math.Round(popupPoint.Y), 120),
                "The volume indicator must not block the next wheel input.");
            Assert.Equal(60, tab.Volume);

            if (!NativeWindowTest.TryGetCursorPosition(out var originalCursor))
                throw new InteractiveDesktopTestSkippedException("Cannot preserve the cursor for physical volume OSD input.");
            var osdSource = (HwndSource)PresentationSource.FromVisual(chrome)!;
            var x = (int)Math.Round(popupPoint.X);
            var y = (int)Math.Round(popupPoint.Y);
            // The indicator fades in and is hit-test-invisible. Depending on the
            // native transparency state, Windows can hit its registered popup or
            // the registered video surface behind it; both must retain rotation.
            await TestWait.UntilAsync(() =>
            {
                var hit = NativeWindowHitTester.Instance.WindowFromPoint(x, y);
                return hit == osdSource.Handle || hit == surface.Handle ||
                    NativeWindowHitTester.Instance.IsChild(surface.Handle, hit);
            }, TimeSpan.FromMilliseconds(600), "Volume popup must be above a native video input target.");
            Assert.NotNull(NativeMouseWheelTarget.CaptureFallback(new LowLevelMouseHookEvent(
                LowLevelMouseHookEvent.WmMouseWheel, x, y, 120 << 16)));
            var observed = new List<int>();
            void RecordVolume(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(StreamTabViewModel.Volume)) observed.Add(tab.Volume);
            }
            tab.PropertyChanged += RecordVolume;
            try
            {
                await StartVolumeWheelTestHookAsync(window);
                NativeWindowTest.SetCursorPosition(x, y);
                await Task.Delay(80);
                await SendVolumeWheelBurstDuringUiStallAsync([-120, 120, -120, 120]);
                await TestWait.UntilAsync(() => observed.Count >= 4, TimeSpan.FromSeconds(2));
                await Task.Delay(200);
                Assert.SequenceEqual(new[] { 55, 60, 55, 60 }, observed);
            }
            finally
            {
                StopVolumeWheelTestHook(window);
                tab.PropertyChanged -= RecordVolume;
                NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
            }
        });

    private static Task VolumeWheelDefaultWpfScrollAsync() => TestSta.RunAsync(async () =>
    {
        if (!NativeWindowTest.TryGetCursorPosition(out var originalCursor))
            throw new InteractiveDesktopTestSkippedException("Cannot preserve the cursor for WPF wheel fallback input.");

        var owner = new MainWindow(false);
        RemoveMainWindowAutomaticStartup(owner);
        var content = new StackPanel();
        for (var index = 0; index < 100; index++)
            content.Children.Add(new TextBlock { Text = $"Scroll item {index}", Height = 30 });
        var viewer = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Background = Brushes.White
        };
        var window = new Window
        {
            Width = 400,
            Height = 300,
            Left = 160,
            Top = 160,
            Topmost = true,
            ShowInTaskbar = false,
            Content = viewer
        };
        var received = new List<int>();
        var nativeReceived = new List<int>();
        HwndSource? source = null;
        IntPtr ObserveWheel(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == LowLevelMouseHookEvent.WmMouseWheel)
                nativeReceived.Add(unchecked((short)(wParam.ToInt64() >> 16)));
            return IntPtr.Zero;
        }
        viewer.PreviewMouseWheel += (_, e) => received.Add(e.Delta);
        try
        {
            window.Show();
            window.UpdateLayout();
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!;
            source.AddHook(ObserveWheel);
            await NativeWindowTest.RequireForegroundAsync(new WindowInteropHelper(window).Handle,
                TimeSpan.FromSeconds(2), "WPF wheel fallback requires a foreground scroll viewer");
            await StartVolumeWheelTestHookAsync(owner);
            var point = viewer.PointToScreen(new Point(80, 80));
            Assert.True(NativeMouseWheelTarget.CaptureFallback(new LowLevelMouseHookEvent(
                LowLevelMouseHookEvent.WmMouseWheel, (int)point.X, (int)point.Y, -120 << 16)) is null,
                "Ordinary WPF windows must retain Windows' wheel routing without capture by the video hook.");
            NativeWindowTest.SetCursorPosition((int)point.X, (int)point.Y);
            await Task.Delay(80);
            int[] deltas = [-120, -120, -120];
            await SendVolumeWheelBurstDuringUiStallAsync(deltas);
            // Windows combines adjacent native wheel packets while a normal WPF
            // window is busy (three -120 inputs arrive here as one -360 message).
            // These unregistered windows deliberately retain that OS behavior.
            // Preserve all rotation and deliver each native message exactly once.
            await TestWait.UntilAsync(() => received.Sum() <= deltas.Sum(), TimeSpan.FromSeconds(2));
            await Task.Delay(200);
            Assert.Equal(deltas.Sum(), nativeReceived.Sum());
            Assert.True(nativeReceived.All(delta => delta < 0));
            Assert.SequenceEqual(nativeReceived, received);
            Assert.True(viewer.VerticalOffset > 0,
                "Unhandled app-owned wheel input must still perform the ScrollViewer's default scrolling.");

            var menu = new ContextMenu { PlacementTarget = viewer, Placement = PlacementMode.Center, MaxHeight = 180 };
            for (var index = 0; index < 30; index++)
                menu.Items.Add(new MenuItem { Header = $"Menu item {index}" });
            var menuDeltas = new List<int>();
            var menuNativeDeltas = new List<int>();
            HwndSource? menuSource = null;
            IntPtr ObserveMenuWheel(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                if (message == LowLevelMouseHookEvent.WmMouseWheel)
                    menuNativeDeltas.Add(unchecked((short)(wParam.ToInt64() >> 16)));
                return IntPtr.Zero;
            }
            menu.PreviewMouseWheel += (_, e) => menuDeltas.Add(e.Delta);
            try
            {
                menu.IsOpen = true;
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                menu.UpdateLayout();
                var menuPoint = menu.PointToScreen(new Point(menu.ActualWidth / 2, menu.ActualHeight / 2));
                menuSource = (HwndSource)PresentationSource.FromVisual(menu)!;
                menuSource.AddHook(ObserveMenuWheel);
                // An opening popup can still be transparent to native hit testing
                // during its animation. Start physical input only after it owns
                // the point, just as the visible settled menu does for the user.
                await TestWait.UntilAsync(() =>
                {
                    menuPoint = menu.PointToScreen(new Point(menu.ActualWidth / 2, menu.ActualHeight / 2));
                    var menuHit = NativeWindowHitTester.Instance.WindowFromPoint((int)menuPoint.X, (int)menuPoint.Y);
                    return menuHit == menuSource.Handle || NativeWindowHitTester.Instance.IsChild(menuSource.Handle, menuHit);
                }, TimeSpan.FromSeconds(1), "Context-menu wheel input must land in its separate popup HWND.");
                Assert.True(NativeMouseWheelTarget.CaptureFallback(new LowLevelMouseHookEvent(
                    LowLevelMouseHookEvent.WmMouseWheel, (int)menuPoint.X, (int)menuPoint.Y, -120 << 16)) is null,
                    "Context menus must retain native wheel routing without capture by the video hook.");
                NativeWindowTest.SetCursorPosition((int)menuPoint.X, (int)menuPoint.Y);
                await Task.Delay(80);
                await SendVolumeWheelBurstDuringUiStallAsync(deltas);
                await TestWait.UntilAsync(() => menuDeltas.Sum() <= deltas.Sum(), TimeSpan.FromSeconds(2));
                await Task.Delay(200);
                Assert.Equal(deltas.Sum(), menuNativeDeltas.Sum());
                Assert.True(menuNativeDeltas.All(delta => delta < 0));
                Assert.SequenceEqual(menuNativeDeltas, menuDeltas);
                Assert.True(menu.IsOpen, "Wheel fallback must preserve the open context menu.");
            }
            finally
            {
                menuSource?.RemoveHook(ObserveMenuWheel);
                menu.IsOpen = false;
            }
        }
        finally
        {
            StopVolumeWheelTestHook(owner);
            source?.RemoveHook(ObserveWheel);
            window.Close();
            owner.Close();
            NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
        }
    });

    private static Task VolumeWheelSeekOverlayAsync() => TestSta.RunAsync(async () =>
    {
        if (!NativeWindowTest.TryGetCursorPosition(out var originalCursor))
            throw new InteractiveDesktopTestSkippedException("Cannot preserve the cursor for replay overlay wheel input.");

        await using var session = await ReplayOverlayTestSession.CreateAsync();
        var main = new MainWindow(false);
        RemoveMainWindowAutomaticStartup(main);
        var detached = new DetachedVideoWindow([session.Tab], session.Tab, showTopBar: false)
        {
            Width = 720,
            Height = 420,
            Left = 160,
            Top = 160,
            Topmost = true,
            ShowInTaskbar = false
        };
        var detachedWindows = VolumeWheelDetachedWindows(main);
        detachedWindows.Add(session.Tab, detached);
        try
        {
            ShowPictureInPictureResizeWindow(detached);
            await NativeWindowTest.RequireForegroundAsync(new WindowInteropHelper(detached).Handle,
                TimeSpan.FromSeconds(2), "Replay overlay wheel ownership");
            var overlay = FindVisualDescendants<ReplaySeekOverlay>(detached).Single();
            var surface = FindVisualDescendants<VideoSurface>(detached).Single();
            overlay.IsOverlayEnabled = true;
            StopReplayOverlayPointerSampling(overlay);
            var surfacePoint = surface.PointToScreen(new Point(surface.ActualWidth / 2, surface.ActualHeight / 2));
            overlay.ProcessPointerSample(surfacePoint, true, Environment.TickCount64);
            PumpPictureInPictureResize(detached);
            Assert.True(overlay.IsOverlayOpen);
            var chrome = (FrameworkElement)overlay.FindName("OverlayChrome");
            // Reveal animates from transparent. Windows hit testing skips a layered child
            // until its first nontransparent frame has actually been presented.
            await TestWait.UntilAsync(() => chrome.Opacity >= 0.99, TimeSpan.FromSeconds(1));
            var point = chrome.PointToScreen(new Point(chrome.ActualWidth / 2, chrome.ActualHeight / 2));
            await TestWait.UntilAsync(() => ReplaySeekOverlay.IsReplayOverlayWindow(
                NativeWindowHitTester.Instance.WindowFromPoint((int)point.X, (int)point.Y)), TimeSpan.FromSeconds(1));
            var received = new List<int>();
            chrome.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, e) =>
            {
                received.Add(e.Delta);
                e.Handled = true;
            }), handledEventsToo: true);
            var initialVolume = session.Tab.Volume;
            var initialSeekCount = session.SeekCount;
            await StartVolumeWheelTestHookAsync(main);
            NativeWindowTest.SetCursorPosition((int)point.X, (int)point.Y);
            await Task.Delay(80);
            int[] deltas = [-120, 120, -120];
            await SendVolumeWheelBurstDuringUiStallAsync(deltas);
            await TestWait.UntilAsync(() => received.Count >= deltas.Length, TimeSpan.FromSeconds(2));
            await Task.Delay(200);
            Assert.SequenceEqual(deltas, received);
            Assert.Equal(initialVolume, session.Tab.Volume);
            Assert.Equal(initialSeekCount, session.SeekCount);
        }
        finally
        {
            StopVolumeWheelTestHook(main);
            detachedWindows.Clear();
            detached.CloseForTabDisposal();
            main.Close();
            NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
        }
    });

    private static Task VolumeWheelRealVlcAsync(VideoRendererMode rendererMode) =>
        WithResponsiveWindowAsync(withVideo: true, async (window, viewModel) =>
        {
            var vlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
            var mediaPath = Path.GetFullPath(Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA")!);
            Assert.True(File.Exists(Path.Combine(vlcDirectory, "libvlc.dll")), "Configured libVLC is missing.");
            Assert.True(File.Exists(mediaPath), "Configured deterministic video fixture is missing.");
            if (!NativeWindowTest.TryGetCursorPosition(out var originalCursor))
                throw new InteractiveDesktopTestSkippedException("Cannot preserve the cursor for native VLC wheel input.");

            var tab = viewModel.SelectedTab!;
            window.Topmost = true;
            PumpResponsiveLayout(window);
            foreach (var overlay in FindVisualDescendants<ReplaySeekOverlay>(window))
            {
                overlay.IsOverlayEnabled = false;
                StopReplayOverlayPointerSampling(overlay);
            }
            var handle = new WindowInteropHelper(window).Handle;
            var surface = FindVisualDescendants<VideoSurface>(window).Single();
            var factory = new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings());
            using var engine = await factory.CreateAsync(vlcDirectory,
                enableNativeOverlay: false, rendererMode: rendererMode);
            try
            {
                await NativeWindowTest.RequireForegroundAsync(handle, TimeSpan.FromSeconds(2), "Native VLC volume wheel");
                engine.SetVideoHandle(surface.Handle);
                await engine.PlayAsync(new Uri(mediaPath), 0, PlaybackAudioState.HardMuted);
                await TestWait.UntilAsync(() => engine.TryGetVideoSize(out var width, out var height) &&
                    width > 0 && height > 0 && engine.TryGetPlaybackClock(out var clock) &&
                    clock.Position > TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(8));
                Assert.Equal(rendererMode, ((LibVlcPlaybackEngine)engine).RendererMode);
                await StartVolumeWheelTestHookAsync(window);

                PumpResponsiveLayout(window);
                await AssertVolumeWheelNativeBurstAsync(tab, surface, $"{rendererMode} main");
                ToggleMainWindowFullscreen(window, "StreamOnly");
                PumpResponsiveLayout(window);
                Assert.True(viewModel.IsStreamOnlyFullscreenActive);
                await AssertVolumeWheelNativeBurstAsync(tab, surface, $"{rendererMode} fullscreen");
                ExitMainWindowFullscreenIfActive(window);
                PumpResponsiveLayout(window);
                await engine.StopAsync();

                // Use a separate tab for the detached surface so main and detached
                // routing cannot accidentally satisfy each other's assertions.
                await using var detachedTab = CreateTestStreamTab();
                var detached = new DetachedVideoWindow([detachedTab], detachedTab, showTopBar: false)
                {
                    Width = 640,
                    Height = 360,
                    Left = 180,
                    Top = 180,
                    Topmost = true,
                    ShowInTaskbar = false
                };
                var detachedWindows = VolumeWheelDetachedWindows(window);
                detachedWindows.Add(detachedTab, detached);
                try
                {
                    ShowPictureInPictureResizeWindow(detached);
                    await NativeWindowTest.RequireForegroundAsync(new WindowInteropHelper(detached).Handle,
                        TimeSpan.FromSeconds(2), "Detached native VLC volume wheel");
                    foreach (var overlay in FindVisualDescendants<ReplaySeekOverlay>(detached))
                    {
                        overlay.IsOverlayEnabled = false;
                        StopReplayOverlayPointerSampling(overlay);
                    }
                    var detachedSurface = FindVisualDescendants<VideoSurface>(detached).Single();
                    engine.SetVideoHandle(detachedSurface.Handle);
                    await engine.PlayAsync(new Uri(mediaPath), 0, PlaybackAudioState.HardMuted);
                    await TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var clock) &&
                        clock.Position > TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(8));
                    PumpPictureInPictureResize(detached);
                    var mainVolume = tab.Volume;
                    await AssertVolumeWheelNativeBurstAsync(detachedTab, detachedSurface, $"{rendererMode} detached");
                    Assert.Equal(mainVolume, tab.Volume);
                }
                finally
                {
                    await engine.StopAsync();
                    detachedWindows.Remove(detachedTab);
                    detached.CloseForTabDisposal();
                }
            }
            finally
            {
                StopVolumeWheelTestHook(window);
                ExitMainWindowFullscreenIfActive(window);
                await engine.StopAsync();
                NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
            }
        });

    private static async Task AssertVolumeWheelNativeBurstAsync(StreamTabViewModel tab, VideoSurface surface, string context)
    {
        surface.SyncNativeBounds();
        var point = VolumeWheelPoint(surface);
        var renderer = NativeWindowHitTester.Instance.WindowFromPoint(point.X, point.Y);
        Assert.True(renderer != IntPtr.Zero && renderer != surface.Handle &&
            NativeWindowHitTester.Instance.IsChild(surface.Handle, renderer),
            $"{context}: wheel must hit a real VLC descendant: " + NativeWindowTest.DescribeWindowAtPoint(point.X, point.Y));
        NativeWindowTest.SetCursorPosition(point.X, point.Y);
        await Task.Delay(80);
        tab.Volume = 60;
        int[] deltas = [120, 120, -120, 240, -120, -240, 120, -120];
        int[] expectedVolumes = [65, 70, 65, 75, 70, 60, 65, 60];
        var observedVolumes = new List<int>();
        void RecordVolume(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(StreamTabViewModel.Volume)) observedVolumes.Add(tab.Volume);
        }
        tab.PropertyChanged += RecordVolume;
        try
        {
            var outsideSurface = surface.PointToScreen(new Point(-20, -20));
            await SendVolumeWheelBurstDuringUiStallAsync(deltas, () =>
                NativeWindowTest.SetCursorPosition((int)outsideSurface.X, (int)outsideSurface.Y));
            await TestWait.UntilAsync(() => observedVolumes.Count >= deltas.Length, TimeSpan.FromSeconds(2));
            await Task.Delay(250); // Drain native messages as well, exposing late duplicate delivery.
            Assert.SequenceEqual(expectedVolumes, observedVolumes);
            Assert.Equal(60, tab.Volume);
            Console.WriteLine($"{context}: all {deltas.Length} physical wheel packets applied once and in order after a 300 ms UI stall.");
        }
        finally
        {
            tab.PropertyChanged -= RecordVolume;
        }
    }

    private static async Task SendVolumeWheelBurstDuringUiStallAsync(IReadOnlyList<int> deltas, Action? afterInjection = null)
    {
        using var senderReady = new ManualResetEventSlim();
        using var dispatcherBlocked = new ManualResetEventSlim();
        using var inputSent = new ManualResetEventSlim();
        var sender = Task.Run(() =>
        {
            senderReady.Set();
            Assert.True(dispatcherBlocked.Wait(TimeSpan.FromSeconds(2)), "Wheel sender did not observe the UI stall.");
            foreach (var delta in deltas)
            {
                VolumeWheelMouseEvent(0x0800, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
                Thread.Sleep(8);
            }
            // Queued wheel routing must retain each packet's position even when
            // the pointer leaves the video before the dispatcher can route it.
            afterInjection?.Invoke();
            inputSent.Set();
        });
        Assert.True(senderReady.Wait(TimeSpan.FromSeconds(2)), "Wheel sender did not start.");
        dispatcherBlocked.Set();
        Thread.Sleep(300);
        var completedDuringStall = inputSent.IsSet;
        await sender.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(completedDuringStall, "Physical wheel injection must finish while WPF is still blocked.");
    }

    private static async Task StartVolumeWheelTestHookAsync(MainWindow main)
    {
        typeof(MainWindow).GetMethod("InstallMouseWheelHook", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
        var pump = (LowLevelMouseHookPump?)typeof(MainWindow)
            .GetField("mouseHookPump", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
        Assert.NotNull(pump);
        var handleField = typeof(LowLevelMouseHookPump).GetField("hookHandle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await TestWait.UntilAsync(() => (IntPtr)handleField.GetValue(pump)! != IntPtr.Zero, TimeSpan.FromSeconds(2));
    }

    private static void StopVolumeWheelTestHook(MainWindow main) =>
        typeof(MainWindow).GetMethod("UninstallMouseWheelHook", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);

    private static IDictionary<StreamTabViewModel, DetachedVideoWindow> VolumeWheelDetachedWindows(MainWindow main) =>
        (IDictionary<StreamTabViewModel, DetachedVideoWindow>)typeof(MainWindow)
            .GetField("detachedWindows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;

    private static (int X, int Y) VolumeWheelPoint(VideoSurface surface)
    {
        var point = surface.PointToScreen(new Point(surface.ActualWidth / 2, surface.ActualHeight / 3));
        return ((int)Math.Round(point.X), (int)Math.Round(point.Y));
    }

    [DllImport("user32", EntryPoint = "mouse_event")]
    private static extern void VolumeWheelMouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}

using System.Windows.Interop;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> PictureInPictureResizeVlcTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA"))
            ? []
            :
            [
                ("picture-in-picture resize physical edges and cursors work over real Direct3D11 VLC descendants",
                    () => PictureInPictureResizeRealVlcAsync(VideoRendererMode.Direct3D11)),
                ("picture-in-picture resize physical edges and cursors work over real GDI VLC descendants",
                    () => PictureInPictureResizeRealVlcAsync(VideoRendererMode.Gdi))
            ];

    private static Task PictureInPictureResizeRealVlcAsync(VideoRendererMode rendererMode) => TestSta.RunAsync(async () =>
    {
        var vlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
        var mediaPath = Path.GetFullPath(Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA")!);
        Assert.True(File.Exists(Path.Combine(vlcDirectory, "libvlc.dll")), "Configured libVLC is missing.");
        Assert.True(File.Exists(mediaPath), "Configured deterministic video fixture is missing.");
        if (!NativeWindowTest.TryGetCursorPosition(out var originalCursor))
            throw new InteractiveDesktopTestSkippedException("Cannot preserve the cursor for real VLC PiP resize tests.");

        await using var session = await ReplayOverlayTestSession.CreateAsync();
        var window = new DetachedVideoWindow([session.Tab], session.Tab, showTopBar: false)
        {
            Left = 160,
            Top = 160,
            Width = 640,
            Height = 360,
            Topmost = true,
            ShowInTaskbar = false
        };
        var main = new MainWindow(false);
        RemoveMainWindowAutomaticStartup(main);
        var detachedWindows = (IDictionary<StreamTabViewModel, DetachedVideoWindow>)typeof(MainWindow)
            .GetField("detachedWindows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        detachedWindows.Add(session.Tab, window);
        var logger = new MemoryLogger();
        using var mouseHook = new LowLevelMouseHookPump(new LowLevelMouseHookDispatcher(
            main.Dispatcher, main.RouteLowLevelMouseHookEvent, main.HasActiveLowLevelMouseMoveRoute), () => logger);
        var factory = new LibVlcPlaybackEngineFactory(logger, new ChatSettings());
        using var engine = await factory.CreateAsync(vlcDirectory, enableNativeOverlay: false, rendererMode: rendererMode);
        HwndSource? source = null;
        var enterCount = 0;
        var exitCount = 0;
        var sizingCount = 0;
        var lastSizingEdge = 0;
        IntPtr ObserveNativeSizing(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0231) Interlocked.Increment(ref enterCount); // WM_ENTERSIZEMOVE
            if (message == 0x0232) Interlocked.Increment(ref exitCount); // WM_EXITSIZEMOVE
            if (message == PictureInPictureWindowSizing.WmSizing)
            {
                Interlocked.Increment(ref sizingCount);
                Volatile.Write(ref lastSizingEdge, wParam.ToInt32());
            }
            return IntPtr.Zero;
        }

        try
        {
            ShowPictureInPictureResizeWindow(window);
            var handle = new WindowInteropHelper(window).Handle;
            source = HwndSource.FromHwnd(handle)!;
            source.AddHook(ObserveNativeSizing);
            await NativeWindowTest.RequireForegroundAsync(handle, TimeSpan.FromSeconds(2), "Real VLC PiP resize");
            Assert.True(WindowInteropHelpers.TryGetMonitorInfo(handle, out var monitor));
            var original = NativeWindowTest.GetWindowBounds(handle);
            var area = monitor.WorkArea;
            if (original.Width + 160 > area.Right - area.Left || original.Height + 160 > area.Bottom - area.Top)
                throw new InteractiveDesktopTestSkippedException("Real VLC PiP resize requires room around the test window.");
            original.X = area.Left + (area.Right - area.Left - original.Width) / 2;
            original.Y = area.Top + (area.Bottom - area.Top - original.Height) / 2;
            NativeWindowTest.SetWindowBounds(handle, original.X, original.Y, original.Width, original.Height);
            PumpPictureInPictureResize(window);

            var surface = FindVisualDescendants<VideoSurface>(window).Single();
            var overlay = FindVisualDescendants<ReplaySeekOverlay>(window).Single();
            overlay.IsOverlayEnabled = false;
            StopReplayOverlayPointerSampling(overlay);
            engine.SetVideoHandle(surface.Handle);
            await engine.PlayAsync(new Uri(mediaPath), 0, PlaybackAudioState.HardMuted);
            await TestWait.UntilAsync(() => engine.TryGetVideoSize(out var width, out var height) &&
                width > 0 && height > 0 && engine.TryGetPlaybackClock(out var clock) &&
                clock.Position > TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(8));
            Assert.Equal(rendererMode, ((LibVlcPlaybackEngine)engine).RendererMode);
            PumpPictureInPictureResize(window);
            using (var initialVideo = CaptureReplayVlcSurface(surface)) AssertReplayVlcVideoPixels(initialVideo);

            mouseHook.Start();
            var hookHandle = typeof(LowLevelMouseHookPump).GetField("hookHandle", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await TestWait.UntilAsync(() => (IntPtr)hookHandle.GetValue(mouseHook)! != IntPtr.Zero, TimeSpan.FromSeconds(2));

            foreach (var direction in Enumerable.Range(10, 8))
            {
                NativeWindowTest.SetWindowBounds(handle, original.X, original.Y, original.Width, original.Height);
                PumpPictureInPictureResize(window);
                await Task.Delay(80);
                var before = NativeWindowTest.GetWindowBounds(handle);
                var edge = PictureInPictureResizePoints(before).Single(point => point.Hit == direction);
                var renderer = NativeWindowHitTester.Instance.WindowFromPoint(edge.X, edge.Y);
                Assert.True(renderer != surface.Handle && NativeWindowHitTester.Instance.IsChild(surface.Handle, renderer),
                    $"Hit {direction} must land on a real VLC descendant, not managed chrome: " +
                    NativeWindowTest.DescribeWindowAtPoint(edge.X, edge.Y));

                NativeWindowTest.SetCursorPosition(edge.X, edge.Y);
                await Task.Delay(80);
                var expectedCursor = direction switch
                {
                    10 or 11 => 32644,
                    12 or 15 => 32645,
                    13 or 17 => 32642,
                    _ => 32643
                };
                var cursorInfo = new PictureInPictureVlcCursorInfo { Size = Marshal.SizeOf<PictureInPictureVlcCursorInfo>() };
                Assert.True(PictureInPictureVlcGetCursorInfo(ref cursorInfo), "Could not inspect the visible native cursor.");
                Assert.True(cursorInfo.Cursor == PictureInPictureLoadCursor(IntPtr.Zero, new IntPtr(expectedCursor)),
                    $"Real {rendererMode} renderer cursor at hit {direction} did not indicate the resize direction.");

                var dx = direction is 10 or 13 or 16 ? -48 : direction is 11 or 14 or 17 ? 48 : 0;
                var dy = direction is 12 or 13 or 14 ? -32 : direction is 15 or 16 or 17 ? 32 : 0;
                var entersBefore = Volatile.Read(ref enterCount);
                var exitsBefore = Volatile.Read(ref exitCount);
                var sizingBefore = Volatile.Read(ref sizingCount);
                // The sender must be independent of the WPF dispatcher: WM_NCLBUTTONDOWN
                // runs a native sizing loop until release. This also exercises the production
                // low-level hook's synchronous timeout against a real VLC capture target.
                var whilePressed = await Task.Run(() =>
                {
                    try
                    {
                        ReplayVlcMouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
                        Thread.Sleep(160);
                        for (var step = 1; step <= 6; step++)
                        {
                            NativeWindowTest.SetCursorPosition(edge.X + dx * step / 6, edge.Y + dy * step / 6);
                            Thread.Sleep(35);
                        }
                        return NativeWindowTest.GetWindowBounds(handle);
                    }
                    finally
                    {
                        ReplayVlcMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                    }
                });
                await Task.Delay(100);
                PumpPictureInPictureResize(window);
                var after = NativeWindowTest.GetWindowBounds(handle);
                Assert.True(whilePressed.Width > before.Width + 8 && whilePressed.Height > before.Height + 8,
                    $"Real {rendererMode} hit {direction} did not resize while held: {before} -> {whilePressed}.");
                Assert.True(after.Width > before.Width + 8 && after.Height > before.Height + 8,
                    $"Real {rendererMode} hit {direction} lost its resized bounds on release: {before} -> {after}.");
                Assert.Equal(entersBefore + 1, Volatile.Read(ref enterCount));
                Assert.Equal(exitsBefore + 1, Volatile.Read(ref exitCount));
                Assert.True(Volatile.Read(ref sizingCount) > sizingBefore, "Physical resize produced no WM_SIZING messages.");
                Assert.Equal(direction - 9, Volatile.Read(ref lastSizingEdge));
                if (dx < 0) AssertNear(before.Right, after.Right, 2);
                if (dx > 0) AssertNear(before.Left, after.Left, 2);
                if (dy < 0) AssertNear(before.Bottom, after.Bottom, 2);
                if (dy > 0) AssertNear(before.Top, after.Top, 2);
                var video = (FrameworkElement)window.FindName("VideoHost");
                AssertNear(window.ContentAspectRatio, video.ActualWidth / video.ActualHeight, 0.015);
                Assert.Equal(false, window.HasActiveWindowMove);
                Assert.Equal(false, window.HasVideoMoveCandidate);
                Assert.Equal(false, window.IsStreamFullscreen);
                using var resizedVideo = CaptureReplayVlcSurface(surface);
                AssertReplayVlcVideoPixels(resizedVideo);
                SaveReplayVlcArtifact(resizedVideo, rendererMode, $"pip-resize-{direction}");
            }
        }
        finally
        {
            ReplayVlcMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
            NativeWindowTest.ReleaseCapture();
            mouseHook.Dispose();
            source?.RemoveHook(ObserveNativeSizing);
            NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
            await engine.StopAsync();
            detachedWindows.Clear();
            window.CloseForTabDisposal();
            main.Close();
        }
    });

    [StructLayout(LayoutKind.Sequential)]
    private struct PictureInPictureVlcCursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public int X;
        public int Y;
    }

    [DllImport("user32", EntryPoint = "GetCursorInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PictureInPictureVlcGetCursorInfo(ref PictureInPictureVlcCursorInfo info);
}

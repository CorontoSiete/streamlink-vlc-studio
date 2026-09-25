internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> ResizeFlashTests =>
    [
        ("resize flashing never moves video across the title bar during native sizing", () =>
            WithResponsiveWindowAsync(withVideo: true, (window, _) =>
            {
                var surface = FindVisualDescendants<VideoSurface>(window).Single();
                using var recorder = new TabSwitchBoundsRecorder(surface.Handle);
                var violations = new List<string>();
                var changes = 0;
                foreach (var size in new[] { (1100, 760), (900, 650), (700, 800), (500, 650), (1100, 760) })
                {
                    recorder.VisibleBounds.Clear();
                    ResizeResponsiveWindow(window, size.Item1, size.Item2);
                    var toolbar = GetResponsiveScreenBounds((FrameworkElement)window.FindName("TopControlsBar"));
                    var expected = GetResponsiveScreenBounds(surface);
                    var actual = NativeWindowTest.GetWindowBounds(surface.Handle);
                    changes += recorder.VisibleBounds.Count;
                    foreach (var bounds in recorder.VisibleBounds.Where(bounds => bounds.Top < toolbar.Bottom - 1))
                        violations.Add($"{size}: native video {bounds} overlaps chrome ending at {toolbar.Bottom}.");
                    AssertNear(expected.Left, actual.Left, 1);
                    AssertNear(expected.Top, actual.Top, 1);
                    AssertNear(expected.Width, actual.Width, 1);
                    AssertNear(expected.Height, actual.Height, 1);
                }
                Console.WriteLine($"Resize trace: {changes} visible native position events, {violations.Count} chrome overlaps.");
                Assert.True(changes > 0, "The resize trace did not observe any native changes.");
                Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
                return Task.CompletedTask;
            })),
        .. ResizeFlashVlcTests
    ];

    private static IReadOnlyList<(string Name, Func<Task> Run)> ResizeFlashVlcTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA"))
            ? []
            :
            [
                ("resize flashing real GDI video stays visible throughout continuous sizing", () => ResizeFlashVlcAsync(VideoRendererMode.Gdi)),
                ("resize flashing real Direct3D11 video stays visible throughout continuous sizing", () => ResizeFlashVlcAsync(VideoRendererMode.Direct3D11)),
                ("resize flashing native chat and real GDI video stay visible throughout continuous sizing", () => ResizeFlashVlcAsync(VideoRendererMode.Gdi, nativeOverlay: true))
            ];

    private static Task ResizeFlashVlcAsync(VideoRendererMode mode, bool nativeOverlay = false) =>
        WithResponsiveWindowAsync(withVideo: true, async (window, _) =>
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
                throw new InteractiveDesktopTestSkippedException("Window capture requires Windows 10 1903 or newer.");
            var surface = FindVisualDescendants<VideoSurface>(window).Single();
            var factory = new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings());
            using var engine = await factory.CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!,
                enableNativeOverlay: nativeOverlay, rendererMode: mode);
            var root = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            window.Topmost = true;
            await NativeWindowTest.RequireForegroundAsync(root, TimeSpan.FromSeconds(2), "Video resize capture");
            try
            {
                engine.SetVideoHandle(surface.Handle);
                await engine.PlayAsync(new Uri(Path.GetFullPath(Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA")!)),
                    0, PlaybackAudioState.HardMuted);
                await TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var clock) &&
                    clock.Position > TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(8));
                AssertActualVideoRenderer(surface, mode == VideoRendererMode.Gdi ? "WinGDI output" : "Direct3D11 output");
                Assert.Equal(nativeOverlay, engine.UsesNativeOverlay);
                if (nativeOverlay) await AssertNativeOverlayComposedAsync(engine, surface, "resize-before");
                var frames = 0;
                var blankFrames = 0;
                using var finished = new CancellationTokenSource();
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var sampling = Task.Run(async () =>
                {
                    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)) return;
                    using var capture = new WindowCaptureTestSession(root);
                    using (var initial = await capture.NextFrameAsync())
                    {
                        Assert.True(IsVideoVisible(initial), "The capture must contain bright video before resizing.");
                        SaveReplayVlcArtifact(initial, mode, "resize-initial");
                    }
                    ready.SetResult();
                    while (!finished.IsCancellationRequested)
                    {
                        using var frame = await capture.NextFrameAsync(continuous: true);
                        frames++;
                        if (!IsVideoVisible(frame))
                        {
                            blankFrames++;
                            if (blankFrames <= 3) SaveReplayVlcArtifact(frame, mode, $"resize-blank-{blankFrames}");
                        }
                    }
                });
                try
                {
                    if (await Task.WhenAny(ready.Task, sampling) == sampling) await sampling;
                    await ready.Task;
                    for (var step = 0; step < 80; step++)
                    {
                        var amount = (1 - Math.Cos(step * Math.PI / 40)) / 2;
                        ResizeResponsiveWindow(window, 1100 - (int)(300 * amount), 760 - (int)(180 * amount));
                        await Task.Delay(16);
                    }
                    ResizeResponsiveWindow(window, 1100, 760);
                    Assert.True(NativeWindowTest.TryGetCursorPosition(out var originalCursor));
                    var initialBounds = NativeWindowTest.GetWindowBounds(root);
                    var enters = 0;
                    var exits = 0;
                    IntPtr ObserveSizing(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
                    {
                        if (message == 0x0231) enters++;
                        if (message == 0x0232) exits++;
                        return IntPtr.Zero;
                    }
                    var source = System.Windows.Interop.HwndSource.FromHwnd(root)!;
                    source.AddHook(ObserveSizing);
                    try
                    {
                        await Task.Run(() =>
                        {
                            try
                            {
                                var x = initialBounds.Right - 2;
                                var y = initialBounds.Bottom - 2;
                                NativeWindowTest.SetCursorPosition(x, y);
                                Thread.Sleep(100);
                                ReplayVlcMouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
                                Thread.Sleep(100);
                                for (var step = 0; step <= 120; step++)
                                {
                                    var amount = (1 - Math.Cos(step * Math.PI / 30)) / 2;
                                    NativeWindowTest.SetCursorPosition(x - (int)(300 * amount), y - (int)(180 * amount));
                                    Thread.Sleep(16);
                                }
                            }
                            finally
                            {
                                ReplayVlcMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                                NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
                            }
                        });
                        await Task.Delay(100);
                        Assert.Equal(1, enters);
                        Assert.Equal(1, exits);
                    }
                    finally { source.RemoveHook(ObserveSizing); }
                }
                finally
                {
                    finished.Cancel();
                    await sampling;
                }
                Console.WriteLine($"{mode} continuous resize: {frames} captured frames, {blankFrames} blank video frames.");
                Assert.True(frames >= 30, $"Only {frames} frames were captured during resize.");
                Assert.Equal(0, blankFrames);
                Assert.True(engine.TryGetPlaybackClock(out var finalClock) && finalClock.Position > TimeSpan.FromSeconds(3),
                    "Playback must keep advancing while the window resizes.");
                if (nativeOverlay) await AssertNativeOverlayComposedAsync(engine, surface, "resize-after");
            }
            finally
            {
                await engine.StopAsync();
            }

            static bool IsVideoVisible(System.Drawing.Bitmap frame)
            {
                // This rectangle is inside the video at every tested window size.
                // Fixed coordinates also catch brief moves out of the player viewport.
                foreach (var y in new[] { 300, 350, 400 })
                    foreach (var x in new[] { 350, 400, 450 })
                    {
                        var pixel = frame.GetPixel(x, y);
                        if (Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) < 100) return false;
                    }
                return true;
            }

        });
}

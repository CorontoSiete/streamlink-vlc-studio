using System.Windows.Interop;

internal static partial class ApplicationTestCatalog
{
    private static Task PictureInPictureResizeWindowEdges() => TestSta.RunAsync(async () =>
    {
        var failures = new List<string>();
        foreach (var showTopBar in new[] { true, false })
        {
            var window = CreatePictureInPictureResizeWindow(showTopBar);
            // A white window underneath exposes transparent gaps as well as painted borders.
            var backdrop = new Window
            {
                Left = 150,
                Top = 150,
                Width = 800,
                Height = 600,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Background = Brushes.White,
                ShowInTaskbar = false
            };
            try
            {
                backdrop.Show();
                ShowPictureInPictureResizeWindow(window);
                var handle = new WindowInteropHelper(window).Handle;
                var backdropHandle = new WindowInteropHelper(backdrop).Handle;
                await NativeWindowTest.RequireForegroundAsync(handle, TimeSpan.FromSeconds(2), "PiP edge capture");
                var surface = FindVisualDescendants<VideoSurface>(window).Single();
                var overlay = FindVisualDescendants<ReplaySeekOverlay>(window).Single();
                overlay.IsOverlayEnabled = false;
                StopReplayOverlayPointerSampling(overlay);
                var scale = PresentationSource.FromVisual(window)!.CompositionTarget!.TransformToDevice;
                // Consecutive physical widths cover both rounding directions; reverse the
                // sequence to exercise shrinking as well as growing. Capture the whole HWND,
                // since checking only the middle of the video misses a one-pixel frame.
                foreach (var width in Enumerable.Range(630, 17).Concat(Enumerable.Range(630, 17).Reverse()))
                {
                    var height = (int)Math.Round(width / window.ContentAspectRatio) +
                        (showTopBar ? (int)Math.Round(34 * scale.M22) : 0);
                    NativeWindowTest.SetWindowBounds(handle, 160, 160, width, height);
                    PumpPictureInPictureResize(window);
                    await NativeWindowTest.RequireForegroundAsync(width % 2 == 0 ? backdropHandle : handle,
                        TimeSpan.FromSeconds(2), "PiP active/inactive edge capture");
                    window.Title = $"PiP resize {width}";
                    await Task.Delay(60);
                    var bounds = NativeWindowTest.GetWindowBounds(handle);
                    var videoBounds = NativeWindowTest.GetWindowBounds(surface.Handle);
                    Assert.Equal(bounds.Left, videoBounds.Left);
                    Assert.Equal(bounds.Right, videoBounds.Right);
                    using var frame = CapturePictureInPictureWindow(handle);
                    var brightEdges = PictureInPictureEdgePixels(frame)
                        .Where(sample => sample.Color.R > 60 || sample.Color.G > 60 || sample.Color.B > 60)
                        .ToArray();
                    if (brightEdges.Length > 0)
                    {
                        failures.Add($"chrome={showTopBar}, size={bounds.Size}, edges={string.Join(",", brightEdges)}");
                        SaveReplayVlcArtifact(frame, VideoRendererMode.Automatic, $"pip-edge-{showTopBar}-{width}");
                    }
                    else if (width == 640)
                        SaveReplayVlcArtifact(frame, VideoRendererMode.Automatic, $"pip-edge-{showTopBar}-{width}");
                }
            }
            finally
            {
                window.CloseForTabDisposal();
                backdrop.Close();
            }
        }
        Console.WriteLine($"PiP edge capture: 68 resized windows, {failures.Count} bright borders.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    });

    private static System.Drawing.Bitmap CapturePictureInPictureWindow(IntPtr handle)
    {
        var bounds = NativeWindowTest.GetWindowBounds(handle);
        var frame = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
        try
        {
            using var graphics = System.Drawing.Graphics.FromImage(frame);
            graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    private static IEnumerable<(int X, int Y, System.Drawing.Color Color)> PictureInPictureEdgePixels(System.Drawing.Bitmap frame)
    {
        foreach (var fraction in new[] { 0.25, 0.5, 0.75 })
        {
            var x = (int)(frame.Width * fraction);
            var y = (int)(frame.Height * fraction);
            yield return (0, y, frame.GetPixel(0, y));
            yield return (frame.Width - 1, y, frame.GetPixel(frame.Width - 1, y));
            yield return (x, 0, frame.GetPixel(x, 0));
            yield return (x, frame.Height - 1, frame.GetPixel(x, frame.Height - 1));
        }
    }

    private static bool HasUnexpectedWhitePictureInPictureEdge(System.Drawing.Bitmap frame) =>
        PictureInPictureEdgePixels(frame).Any(sample =>
        {
            var adjacent = frame.GetPixel(Math.Clamp(sample.X, 4, frame.Width - 5),
                Math.Clamp(sample.Y, 4, frame.Height - 5));
            return sample.Color.R >= 230 && sample.Color.G >= 230 && sample.Color.B >= 230 &&
                Math.Abs(sample.Color.R - adjacent.R) + Math.Abs(sample.Color.G - adjacent.G) +
                Math.Abs(sample.Color.B - adjacent.B) > 80;
        });

    private static Task PictureInPictureResizeVideoFrames(VideoRendererMode mode) => TestSta.RunAsync(async () =>
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
            throw new InteractiveDesktopTestSkippedException("Window capture requires Windows 10 1903 or newer.");
        foreach (var showTopBar in new[] { true, false })
        {
            var window = CreatePictureInPictureResizeWindow(showTopBar);
            var factory = new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings());
            using var engine = await factory.CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!,
                enableNativeOverlay: false, rendererMode: mode);
            try
            {
                ShowPictureInPictureResizeWindow(window);
                var handle = new WindowInteropHelper(window).Handle;
                await NativeWindowTest.RequireForegroundAsync(handle, TimeSpan.FromSeconds(2), "PiP video edge capture");
                var surface = FindVisualDescendants<VideoSurface>(window).Single();
                var overlay = FindVisualDescendants<ReplaySeekOverlay>(window).Single();
                overlay.IsOverlayEnabled = false;
                StopReplayOverlayPointerSampling(overlay);
                engine.SetVideoHandle(surface.Handle);
                await engine.PlayAsync(new Uri(Path.GetFullPath(Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA")!)),
                    0, PlaybackAudioState.HardMuted);
                await TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var clock) &&
                    clock.Position > TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(8));
                AssertActualVideoRenderer(surface, mode == VideoRendererMode.Gdi ? "WinGDI output" : "Direct3D11 output");

                var frames = 0;
                var blankFrames = 0;
                var whiteEdgeFrames = 0;
                using var finished = new CancellationTokenSource();
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var sampling = Task.Run(async () =>
                {
                    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)) return;
                    using var capture = new WindowCaptureTestSession(handle);
                    using (var initial = await capture.NextFrameAsync()) AssertReplayVlcVideoPixels(initial);
                    ready.SetResult();
                    while (!finished.IsCancellationRequested)
                    {
                        using var frame = await capture.NextFrameAsync(continuous: true);
                        frames++;
                        var center = frame.GetPixel(frame.Width / 2, frame.Height / 2);
                        if (Math.Max(center.R, Math.Max(center.G, center.B)) < 100)
                        {
                            blankFrames++;
                            if (blankFrames <= 3) SaveReplayVlcArtifact(frame, mode, $"pip-blank-{showTopBar}-{blankFrames}");
                        }
                        if (HasUnexpectedWhitePictureInPictureEdge(frame))
                        {
                            whiteEdgeFrames++;
                            if (whiteEdgeFrames <= 3) SaveReplayVlcArtifact(frame, mode, $"pip-white-edge-{showTopBar}-{whiteEdgeFrames}");
                        }
                    }
                });
                try
                {
                    if (await Task.WhenAny(ready.Task, sampling) == sampling) await sampling;
                    await ready.Task;
                    var scale = PresentationSource.FromVisual(window)!.CompositionTarget!.TransformToDevice;
                    for (var step = 0; step < 80; step++)
                    {
                        var width = 640 - (int)Math.Round(96 * (1 - Math.Cos(step * Math.PI / 20)) / 2);
                        var height = (int)Math.Round(width / window.ContentAspectRatio) +
                            (showTopBar ? (int)Math.Round(34 * scale.M22) : 0);
                        NativeWindowTest.SetWindowBounds(handle, 160, 160, width, height);
                        PumpPictureInPictureResize(window);
                        await Task.Delay(16);
                    }
                    await Task.Delay(100);
                    using var final = CapturePictureInPictureWindow(handle);
                    Assert.True(!HasUnexpectedWhitePictureInPictureEdge(final), "Settled PiP retained a white edge.");
                    SaveReplayVlcArtifact(final, mode, $"pip-resized-{showTopBar}");
                }
                finally
                {
                    finished.Cancel();
                    await sampling;
                }

                Console.WriteLine($"PiP {mode}, chrome={showTopBar}: {frames} continuous frames, {blankFrames} blank, {whiteEdgeFrames} white edges.");
                Assert.True(frames >= 30, $"Only {frames} frames were captured during PiP resizing.");
                Assert.Equal(0, blankFrames);
                Assert.Equal(0, whiteEdgeFrames);
                Assert.True(engine.TryGetPlaybackClock(out var finalClock) && finalClock.Position > TimeSpan.FromSeconds(1),
                    "Playback must continue advancing throughout PiP resizing.");
            }
            finally
            {
                await engine.StopAsync();
                window.CloseForTabDisposal();
            }
        }
    });
}

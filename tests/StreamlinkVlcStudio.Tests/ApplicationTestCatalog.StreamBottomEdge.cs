using System.Windows.Interop;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> StreamBottomEdgeTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA"))
            ? []
            : [("main stream video edges stay opaque through resize and window modes", StreamBottomEdgeAsync)];

    private static Task StreamBottomEdgeAsync() => WithResponsiveWindowAsync(withVideo: true, async (window, viewModel) =>
    {
        var tab = viewModel.SelectedTab!;
        var surface = FindVisualDescendants<VideoSurface>(window).Single();
        var viewport = (FrameworkElement)window.FindName("VideoViewport");
        var windowHandle = new WindowInteropHelper(window).Handle;
        window.Topmost = true;
        await NativeWindowTest.RequireForegroundAsync(windowHandle, TimeSpan.FromSeconds(2),
            "Stream edge capture requires an interactive desktop");

        var factory = new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings());
        using var engine = await factory.CreateAsync(
            Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!,
            enableNativeOverlay: true);
        try
        {
            engine.SetVideoHandle(surface.Handle);
            await engine.PlayAsync(new Uri(Path.GetFullPath(Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA")!)),
                0, PlaybackAudioState.HardMuted);
            await TestWait.UntilAsync(() => engine.TryGetVideoSize(out var width, out var height) &&
                width > 0 && height > 0 && engine.TryGetPlaybackClock(out var clock) &&
                clock.Position > TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(8));
            await Task.Delay(750);
            using (var videoFrame = CaptureReplayVlcSurface(surface))
            {
                AssertReplayVlcVideoPixels(videoFrame);
            }

            // Narrow video touches the bottom, fitted video touches all sides, and wide
            // video touches the left/right sides. These reproduce the three reported layouts.
            foreach (var (width, height, aspectScale) in new[]
                     { (1320, 820, 0.95), (1297, 820, 1.0), (1297, 902, 1.15) })
            {
                ResizeResponsiveWindow(window, width, height);
                SetAspectForViewport(aspectScale);
                await Task.Delay(150);
                PumpResponsiveLayout(window);
                AssertVideoEdges($"{width}x{height}, aspect scale {aspectScale}", aspectScale);
            }

            ToggleMainWindowFullscreen(window, "StreamOnly");
            PumpResponsiveLayout(window);
            ExitMainWindowFullscreenIfActive(window);
            ResizeResponsiveWindow(window, 1297, 820);
            SetAspectForViewport(1.0);
            AssertVideoEdges("after fullscreen", 1.0);

            window.WindowState = WindowState.Maximized;
            PumpResponsiveLayout(window);
            SetAspectForViewport(1.0);
            AssertVideoEdges("maximized", 1.0);
            window.WindowState = WindowState.Normal;
            ResizeResponsiveWindow(window, 1297, 820);
            SetAspectForViewport(1.0);
            AssertVideoEdges("after maximize restore", 1.0);
        }
        finally
        {
            await engine.StopAsync();
        }

        void SetAspectForViewport(double scale)
        {
            SetVideoAspectRatio(tab, viewport.ActualWidth / viewport.ActualHeight * scale);
            PumpResponsiveLayout(window);
        }

        void AssertVideoEdges(string stage, double aspectScale)
        {
            var windowBounds = NativeWindowTest.GetWindowBounds(windowHandle);
            var videoBounds = NativeWindowTest.GetWindowBounds(surface.Handle);
            if (aspectScale <= 1)
            {
                Assert.Equal(windowBounds.Bottom, videoBounds.Bottom);
            }
            if (aspectScale >= 1)
            {
                Assert.Equal(windowBounds.Left, videoBounds.Left);
                Assert.Equal(windowBounds.Right, videoBounds.Right);
            }

            using var frame = CaptureMainFullscreenWindow(windowBounds);
            var left = videoBounds.Left - windowBounds.Left;
            var right = videoBounds.Right - windowBounds.Left - 1;
            var top = videoBounds.Top - windowBounds.Top;
            var bottom = videoBounds.Bottom - windowBounds.Top - 1;
            foreach (var fraction in new[] { 0.25, 0.5, 0.75 })
            {
                var x = left + (int)(videoBounds.Width * fraction);
                var y = top + (int)(videoBounds.Height * fraction);
                AssertNotWhite(frame.GetPixel(left, y), $"{stage} left ({left},{y})");
                AssertNotWhite(frame.GetPixel(right, y), $"{stage} right ({right},{y})");
                AssertNotWhite(frame.GetPixel(x, top), $"{stage} top ({x},{top})");
                AssertNotWhite(frame.GetPixel(x, bottom), $"{stage} bottom ({x},{bottom})");
            }
            Console.WriteLine($"{stage}: outer={windowBounds}, video={videoBounds}; no white edges.");
        }

        static void AssertNotWhite(System.Drawing.Color pixel, string location) =>
            Assert.True(pixel.R < 240 || pixel.G < 240 || pixel.B < 240,
                $"Video edge {location} was white: {pixel}.");
    });
}

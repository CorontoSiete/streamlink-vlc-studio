internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> WindowSharingVlcTests =>
    [
        ("window sharing renderer binding rejects unverified VLC ABIs before native calls", () =>
        {
            foreach (var version in new Version?[] { null, new(2, 2, 8), new(4, 0) })
                Assert.Throws<NotSupportedException>(() => LibVlcVideoOutputBinding.Bind(
                    IntPtr.Zero, IntPtr.Zero, VideoRendererMode.Gdi, version, usesNativeOverlay: true));
            return Task.CompletedTask;
        }),
        .. WindowSharingDesktopTests
    ];

    private static IReadOnlyList<(string Name, Func<Task> Run)> WindowSharingDesktopTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA"))
            ? []
            :
            [
                ("window sharing GDI request uses the actual WinGDI output", () => WindowSharingRendererAsync(false, VideoRendererMode.Gdi)),
                ("window sharing native overlay remains visible across GDI output rebinds", () => WindowSharingRendererAsync(true, VideoRendererMode.Automatic)),
                ("window sharing explicit Direct3D11 still selects its actual output", () => WindowSharingRendererAsync(false, VideoRendererMode.Direct3D11)),
                ("window sharing retains chrome and video across Home resize and rebind", WindowSharingCaptureAsync)
            ];

    private static Task WindowSharingRendererAsync(bool nativeOverlay, VideoRendererMode mode) =>
        WithResponsiveWindowAsync(withVideo: true, async (window, _) =>
        {
            var surface = FindVisualDescendants<VideoSurface>(window).Single();
            var factory = new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings());
            using var engine = await factory.CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!,
                enableNativeOverlay: nativeOverlay, rendererMode: mode);
            try
            {
                Assert.Equal(nativeOverlay, engine.UsesNativeOverlay);
                Assert.Equal(nativeOverlay, ((LibVlcPlaybackEngine)engine).HardwareOverlayComposition);
                engine.SetVideoHandle(surface.Handle);
                await engine.PlayAsync(new Uri(Path.GetFullPath(
                    Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA")!)), 0, PlaybackAudioState.HardMuted);
                await TestWait.UntilAsync(() => engine.TryGetVideoSize(out var width, out var height) &&
                    width > 0 && height > 0 && engine.TryGetPlaybackClock(out var clock) &&
                    clock.Position > TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(8));
                AssertActualVideoRenderer(surface, mode == VideoRendererMode.Direct3D11 && !nativeOverlay
                    ? "Direct3D11 output" : "WinGDI output");
                if (nativeOverlay)
                {
                    window.Topmost = true;
                    window.Activate();
                    await AssertNativeOverlayComposedAsync(engine, surface, "initial");
                    var detachedSurface = new VideoSurface();
                    var detached = new Window
                    {
                        Content = detachedSurface,
                        Width = 640,
                        Height = 400,
                        ShowInTaskbar = false,
                        Topmost = true
                    };
                    try
                    {
                        detached.Show();
                        detached.UpdateLayout();
                        await RebindAsync(detachedSurface);
                        await AssertNativeOverlayComposedAsync(engine, detachedSurface, "detached");
                        await RebindAsync(surface);
                        detached.Close();
                        window.Activate();
                        await AssertNativeOverlayComposedAsync(engine, surface, "reattached");
                    }
                    finally
                    {
                        detached.Close();
                    }

                    async Task RebindAsync(VideoSurface target)
                    {
                        var rebound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        EventHandler handler = (_, _) => rebound.TrySetResult();
                        engine.VideoOutputRebound += handler;
                        try
                        {
                            engine.SetVideoHandle(target.Handle);
                            await rebound.Task.WaitAsync(TimeSpan.FromSeconds(8));
                            // The rebound notification can precede WinGDI's window title
                            // initialization. Require the actual renderer within a bounded
                            // wait rather than sampling the temporary "VLC Video Output" title.
                            await TestWait.UntilAsync(() => ReadVideoOutputTitles(target)
                                .Any(title => title.Contains("WinGDI output", StringComparison.Ordinal)),
                                TimeSpan.FromSeconds(8));
                            AssertActualVideoRenderer(target, "WinGDI output");
                        }
                        finally
                        {
                            engine.VideoOutputRebound -= handler;
                        }
                    }
                }
            }
            finally
            {
                await engine.StopAsync();
            }
        });

    private static async Task AssertNativeOverlayComposedAsync(IPlaybackEngine engine, VideoSurface surface, string phase)
    {
        // Send a local, opaque green frame through the real plugin. A connected pipe
        // and a running video output alone do not prove that chat is visible.
        var message = NativeOverlayProtocolCodec.CreateFrameMessage(320, 200);
        for (var offset = NativeOverlayProtocolCodec.HeaderSize; offset < message.Length; offset += 4)
        {
            message[offset + 1] = 255;
            message[offset + 3] = 255;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var pipe = new NamedPipeClientStream(".", engine.NativeOverlayPipeName!,
            PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token);
        await pipe.WriteAsync(message, deadline.Token);
        await pipe.FlushAsync(deadline.Token);
        await Task.Delay(500);
        using var frame = CaptureReplayVlcSurface(surface);
        SaveReplayVlcArtifact(frame, VideoRendererMode.Gdi, $"native-chat-{phase}");
        var greenPixels = 0;
        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
            {
                var pixel = frame.GetPixel(x, y);
                if (pixel.G > 220 && pixel.R < 30 && pixel.B < 30) greenPixels++;
            }
        Assert.True(greenPixels > 500,
            $"{phase}: the native chat frame never appeared over VLC video ({greenPixels} green pixels).");
    }

    private static Task WindowSharingCaptureAsync() =>
        WithResponsiveWindowAsync(withVideo: true, async (window, viewModel) =>
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
                throw new InteractiveDesktopTestSkippedException("Window capture requires Windows 10 1903 or newer.");
            var selected = viewModel.SelectedTab;
            var surface = FindVisualDescendants<VideoSurface>(window).Single();
            var root = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            viewModel.SelectHomeCommand.Execute(null);
            PumpResponsiveLayout(window);
            using var capture = new WindowCaptureTestSession(root);
            await AssertCaptureAsync("home", false);
            viewModel.SelectedTab = selected;
            PumpResponsiveLayout(window);
            var factory = new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings());
            using var engine = await factory.CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!,
                enableNativeOverlay: true, rendererMode: VideoRendererMode.Automatic);
            var detachedSurface = new VideoSurface();
            var detached = new Window { Content = detachedSurface, Width = 640, Height = 400, ShowInTaskbar = false };
            try
            {
                engine.SetVideoHandle(surface.Handle);
                await engine.PlayAsync(new Uri(Path.GetFullPath(
                    Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA")!)), 0, PlaybackAudioState.HardMuted);
                await WaitForVideoAsync(surface);
                await AssertCaptureAsync("playing", true);
                viewModel.SelectHomeCommand.Execute(null);
                PumpResponsiveLayout(window);
                await AssertCaptureAsync("home-during-playback", false);
                viewModel.SelectedTab = selected;
                PumpResponsiveLayout(window);
                await AssertCaptureAsync("returned-to-stream", true);

                window.Width = 850;
                window.Height = 600;
                PumpResponsiveLayout(window);
                await Task.Delay(200);
                capture.Resize();
                await AssertCaptureAsync("resized", true);

                detached.Show();
                detached.UpdateLayout();
                engine.SetVideoHandle(detachedSurface.Handle);
                await WaitForVideoAsync(detachedSurface);
                engine.SetVideoHandle(surface.Handle);
                await WaitForVideoAsync(surface);
                detached.Close();
                await AssertCaptureAsync("reattached", true);
            }
            finally
            {
                await engine.StopAsync();
                detached.Close();
            }

            async Task WaitForVideoAsync(VideoSurface target)
            {
                await TestWait.UntilAsync(() => ReadVideoOutputTitles(target).Any(title => title.Contains("WinGDI output")) &&
                    engine.TryGetPlaybackClock(out var clock) && clock.Position > TimeSpan.FromMilliseconds(500),
                    TimeSpan.FromSeconds(8));
                AssertActualVideoRenderer(target, "WinGDI output");
                await Task.Delay(150);
            }

            async Task AssertCaptureAsync(string phase, bool expectVideo)
            {
                await Task.Delay(150);
                using var frame = await capture.NextFrameAsync();
                SaveReplayVlcArtifact(frame, VideoRendererMode.Automatic, $"window-sharing-{phase}");
                var bounds = NativeWindowTest.GetWindowBounds(root);
                Assert.True(Math.Abs(frame.Width - bounds.Width) <= 16 && Math.Abs(frame.Height - bounds.Height) <= 16,
                    $"{phase}: capture cropped to {frame.Width}x{frame.Height}; app is {bounds.Width}x{bounds.Height}.");
                Assert.True(CountBrightCapturePixels(frame, 0, 45) > 40, $"{phase}: title bar text is missing.");
                Assert.True(CountBrightCapturePixels(frame, 45, 95) > 40,
                    $"{phase}: navigation/playback toolbar is missing.");
                if (expectVideo)
                {
                    var video = NativeWindowTest.GetWindowBounds(surface.Handle);
                    var pixel = frame.GetPixel(video.Left - bounds.Left + video.Width / 2,
                        video.Top - bounds.Top + video.Height / 2);
                    Assert.True(Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) >= 100,
                        $"{phase}: video was not composed into the captured app: {pixel}.");
                }
                Console.WriteLine($"Window capture {phase}: {frame.Width}x{frame.Height}, chrome present, video={expectVideo}.");
            }
        });

    private static int CountBrightCapturePixels(System.Drawing.Bitmap frame, int startY, int endY)
    {
        var count = 0;
        for (var y = startY; y < endY; y++)
            for (var x = 0; x < frame.Width; x++)
            {
                var pixel = frame.GetPixel(x, y);
                if (pixel.R > 160 && pixel.G > 160 && pixel.B > 160) count++;
            }
        return count;
    }

    private static void AssertActualVideoRenderer(VideoSurface surface, string expected)
    {
        var titles = ReadVideoOutputTitles(surface);
        Console.WriteLine($"Actual VLC output: {string.Join("; ", titles)}");
        Assert.True(titles.Any(title => title.Contains(expected, StringComparison.Ordinal)),
            $"Expected {expected}; actual output: {string.Join("; ", titles)}");
        if (expected == "WinGDI output")
            Assert.True(!titles.Any(title => title.Contains("Direct3D", StringComparison.Ordinal)),
                "GDI playback must not create a separate Direct3D presentation target.");
    }

    private static List<string> ReadVideoOutputTitles(VideoSurface surface)
    {
        var titles = new List<string>();
        CaptureEnumChildWindows(surface.Handle, (hwnd, _) =>
        {
            var text = new StringBuilder(256);
            CaptureGetWindowText(hwnd, text, text.Capacity);
            if (text.Length > 0) titles.Add(text.ToString());
            return true;
        }, IntPtr.Zero);
        return titles;
    }

    private delegate bool CaptureEnumWindowProc(IntPtr hwnd, IntPtr parameter);

    [DllImport("user32", EntryPoint = "EnumChildWindows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CaptureEnumChildWindows(IntPtr parent, CaptureEnumWindowProc callback, IntPtr parameter);

    [DllImport("user32", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int CaptureGetWindowText(IntPtr hwnd, StringBuilder text, int count);
}

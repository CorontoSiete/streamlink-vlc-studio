internal static partial class ApplicationTestCatalog
{
    // These tests exercise the composed desktop and the installed native decoder. Keep
    // them opt-in so the ordinary suite does not need VLC or a particular media fixture.
    private static IReadOnlyList<(string Name, Func<Task> Run)> ReplaySeekOverlayVlcTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA"))
            ? []
            :
            [
                ("replay seek overlay composes and receives physical input over real Direct3D11 VLC video",
                    () => ReplaySeekOverlayVlcCompositionAsync(VideoRendererMode.Direct3D11)),
                ("replay seek overlay composes and receives physical input over real GDI VLC video",
                    () => ReplaySeekOverlayVlcCompositionAsync(VideoRendererMode.Gdi))
            ];

    private static Task ReplaySeekOverlayVlcCompositionAsync(VideoRendererMode rendererMode) => TestSta.RunAsync(async () =>
    {
        var vlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
        var mediaPath = Path.GetFullPath(Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA")!);
        Assert.True(File.Exists(Path.Combine(vlcDirectory, "libvlc.dll")), "Configured libVLC is missing.");
        Assert.True(File.Exists(mediaPath), "Configured deterministic video fixture is missing.");
        if (!NativeWindowTest.TryGetCursorPosition(out var originalCursor))
            throw new InteractiveDesktopTestSkippedException("Cannot preserve the cursor for VLC input tests.");

        await using var session = await ReplayOverlayTestSession.CreateAsync();
        using var fixture = new ReplayOverlayTestHost(session.Tab);
        var window = Window.GetWindow(fixture.Overlay)!;
        window.Topmost = true;
        window.Width = Math.Min(800, SystemParameters.WorkArea.Width - 80);
        window.Height = window.Width * 9 / 16;
        window.Left = SystemParameters.WorkArea.Left + 24;
        window.Top = SystemParameters.WorkArea.Top + 24;
        fixture.Overlay.IsOverlayEnabled = false;
        fixture.FlushBindings();
        await NativeWindowTest.RequireForegroundAsync(fixture.OwnerHandle, TimeSpan.FromSeconds(1),
            "Real VLC overlay input requires the test owner to be active");
        var logger = new MemoryLogger();
        var factory = new LibVlcPlaybackEngineFactory(logger, new ChatSettings());
        using var engine = await factory.CreateAsync(vlcDirectory,
            enableNativeOverlay: false, rendererMode: rendererMode);
        try
        {
            engine.SetVideoHandle(fixture.Target.Handle);
            await engine.PlayAsync(new Uri(mediaPath), 0, PlaybackAudioState.HardMuted);
            await TestWait.UntilAsync(() => engine.TryGetVideoSize(out var width, out var height) &&
                width > 0 && height > 0 && engine.TryGetPlaybackClock(out var clock) &&
                clock.Position > TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(8));
            Assert.Equal(rendererMode, ((LibVlcPlaybackEngine)engine).RendererMode);
            fixture.FlushBindings();
            var surfaceBounds = NativeWindowTest.GetWindowBounds(fixture.Target.Handle);
            NativeWindowTest.SetCursorPosition(surfaceBounds.Left + 4, surfaceBounds.Top + 4);

            // Capture the actual desktop, including native Direct3D and layered-child
            // composition. RenderTargetBitmap alone cannot prove either is on screen.
            using var videoOnly = CaptureReplayVlcSurface(fixture.Target);
            AssertReplayVlcVideoPixels(videoOnly);
            SaveReplayVlcArtifact(videoOnly, rendererMode, "video");
            var videoHit = NativeWindowHitTester.Instance.WindowFromPoint(
                surfaceBounds.Left + surfaceBounds.Width / 2,
                surfaceBounds.Top + surfaceBounds.Height / 3);
            Assert.True(videoHit != fixture.Target.Handle &&
                NativeWindowHitTester.Instance.IsChild(fixture.Target.Handle, videoHit),
                "The playing video must have a real native VLC renderer under the pointer.");

            fixture.Overlay.IsOverlayEnabled = true;
            fixture.StopPointerSampling();
            fixture.Overlay.ProcessPointerSample(new Point(100, 100), true, Environment.TickCount64);
            await Task.Delay(220); // Let the production reveal animation reach full opacity.
            fixture.FlushBindings();
            fixture.StopPointerSampling();
            Assert.True(fixture.Overlay.IsOverlayOpen);
            using var withControls = CaptureReplayVlcSurface(fixture.Target);
            AssertReplayVlcComposition(fixture, videoOnly, withControls);
            SaveReplayVlcArtifact(withControls, rendererMode, "controls");

            var sourceHandle = fixture.NativeOverlayHandle;
            var trackStart = fixture.Slider.PointToScreen(
                new Point(fixture.Slider.ActualWidth * 0.55, fixture.Slider.ActualHeight / 2));
            var trackEnd = fixture.Slider.PointToScreen(
                new Point(fixture.Slider.ActualWidth * 0.72, fixture.Slider.ActualHeight / 2));
            Assert.Equal(sourceHandle, NativeWindowHitTester.Instance.WindowFromPoint(
                (int)Math.Round(trackStart.X), (int)Math.Round(trackStart.Y)));
            var hoverImage = CreateSeekHoverTestImage(0xA0);
            fixture.Overlay.PreviewImageLoader = (_, _, _) => Task.FromResult<BitmapSource?>(hoverImage);
            NativeWindowTest.SetCursorPosition((int)Math.Round(trackStart.X), (int)Math.Round(trackStart.Y));
            await TestWait.UntilAsync(() => fixture.Overlay.IsSeekHoverOpen &&
                ReferenceEquals(((Image)fixture.Overlay.FindName("SeekPreviewImage")).Source, hoverImage),
                TimeSpan.FromSeconds(2));
            fixture.FlushBindings();
            using (var withHover = CaptureReplayVlcSurface(fixture.Target))
            {
                // The preview intentionally covers some of the video samples used above.
                // Check the composed image itself and a separate, uncovered video point.
                var displayedImage = (Image)fixture.Overlay.FindName("SeekPreviewImage");
                var imageCenter = displayedImage.PointToScreen(new Point(
                    displayedImage.ActualWidth / 2, displayedImage.ActualHeight / 2));
                var targetBounds = NativeWindowTest.GetWindowBounds(fixture.Target.Handle);
                var actual = withHover.GetPixel((int)Math.Round(imageCenter.X) - targetBounds.Left,
                    (int)Math.Round(imageCenter.Y) - targetBounds.Top);
                Assert.True(Math.Abs(actual.R - 96) <= 5 && Math.Abs(actual.G - 108) <= 5 && Math.Abs(actual.B - 160) <= 5,
                    $"Expected the preview sprite to be composed above VLC, got {actual}.");
                var videoPixel = withHover.GetPixel(withHover.Width / 4, withHover.Height / 4);
                var originalPixel = videoOnly.GetPixel(videoOnly.Width / 4, videoOnly.Height / 4);
                Assert.Equal(originalPixel, videoPixel);
                SaveReplayVlcArtifact(withHover, rendererMode, "hover");
            }
            await AssertReplayVlcHoverStabilityAsync(fixture, videoOnly, hoverImage, rendererMode);
            var beforeMouseSeek = session.SeekCount;
            var focusTrace = new List<string>();
            fixture.Slider.GotKeyboardFocus += (_, e) => focusTrace.Add($"got:{e.NewFocus?.GetType().Name}");
            fixture.Slider.LostKeyboardFocus += (_, e) => focusTrace.Add($"lost:{e.NewFocus?.GetType().Name}");
            fixture.Slider.IsEnabledChanged += (_, e) => focusTrace.Add($"enabled:{e.NewValue}");
            NativeWindowTest.SetCursorPosition((int)Math.Round(trackStart.X), (int)Math.Round(trackStart.Y));
            ReplayVlcMouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero); // Physical left down.
            await TestWait.UntilAsync(() => session.Tab.IsReplaySeekPreviewActive,
                TimeSpan.FromSeconds(1));
            NativeWindowTest.SetCursorPosition((int)Math.Round(trackEnd.X), (int)Math.Round(trackEnd.Y));
            await Task.Delay(100);
            Assert.Equal(beforeMouseSeek, session.SeekCount);
            var expectedPointer = RoundedReplayPointer(trackEnd);
            AssertReplayPointerPreview(session, fixture, expectedPointer, "scrubbing over VLC video");
            ReplayVlcMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero); // Physical left up.
            await TestWait.UntilAsync(() => session.SeekCount == beforeMouseSeek + 1 &&
                !session.Tab.IsReplaySeekPreviewActive && session.Tab.CanSeekReplay,
                TimeSpan.FromSeconds(2));
            await Task.Delay(100);
            Assert.Equal(beforeMouseSeek + 1, session.SeekCount);
            Assert.True(Math.Abs(session.PlaybackFactory.Engine!.Position.TotalSeconds -
                ExpectedReplayPointerValue(fixture, expectedPointer)) < 0.001,
                "Releasing over VLC video must commit the time under the pointer.");

            // Do not raise routed key events or assign keyboard focus here. The mouse
            // gesture must have focused the child input root, and native Right input
            // must arrive there even though its parent HWND belongs to the video host.
            Assert.True(fixture.Slider.IsKeyboardFocused,
                $"Physical slider input must focus the child HwndSource for keyboard seeking. " +
                $"Focused={Keyboard.FocusedElement?.GetType().Name}; enabled={fixture.Slider.IsEnabled}; " +
                $"events={string.Join(", ", focusTrace)}");
            var beforeKeyboardSeek = session.SeekCount;
            var beforeKeyboardValue = session.Tab.ReplaySeekValue;
            ReplayVlcKeyboardEvent(0x27, 0, 0, UIntPtr.Zero); // Physical Right down.
            await TestWait.UntilAsync(() => session.Tab.IsReplaySeekPreviewActive,
                TimeSpan.FromSeconds(1));
            Assert.Equal(beforeKeyboardSeek, session.SeekCount);
            ReplayVlcKeyboardEvent(0x27, 0, 0x0002, UIntPtr.Zero); // Physical Right up.
            await TestWait.UntilAsync(() => session.SeekCount == beforeKeyboardSeek + 1 &&
                !session.Tab.IsReplaySeekPreviewActive, TimeSpan.FromSeconds(2));
            Assert.Equal(beforeKeyboardValue + fixture.Slider.SmallChange, session.Tab.ReplaySeekValue);

            fixture.StopPointerSampling();
            var ownerBefore = NativeWindowTest.GetWindowBounds(fixture.OwnerHandle);
            var controlsBefore = NativeWindowTest.GetWindowBounds(sourceHandle);
            NativeWindowTest.SetWindowBounds(fixture.OwnerHandle,
                ownerBefore.Left + 31, ownerBefore.Top + 23, ownerBefore.Width, ownerBefore.Height);
            var controlsAfter = NativeWindowTest.GetWindowBounds(sourceHandle);
            Assert.Equal(controlsBefore.Left + 31, controlsAfter.Left);
            Assert.Equal(controlsBefore.Top + 23, controlsAfter.Top);
            using (var immediateMove = CaptureReplayVlcSurface(fixture.Target))
                SaveReplayVlcArtifact(immediateMove, rendererMode, "move-immediate");
            await Task.Delay(150); // Inspect the next composed desktop frame as well.
            using var afterMove = CaptureReplayVlcSurface(fixture.Target);
            AssertReplayVlcComposition(fixture, videoOnly, afterMove);
            SaveReplayVlcArtifact(afterMove, rendererMode, "move-settled");
            Assert.Equal(beforeKeyboardSeek + 1, session.SeekCount);
        }
        catch
        {
            try
            {
                using var failed = CaptureReplayVlcSurface(fixture.Target);
                SaveReplayVlcArtifact(failed, rendererMode, "failure");
            }
            catch { /* Preserve the original assertion if desktop capture also failed. */ }
            throw;
        }
        finally
        {
            ReplayVlcMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
            ReplayVlcKeyboardEvent(0x27, 0, 0x0002, UIntPtr.Zero);
            NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
            await engine.StopAsync();
        }
    });

    private static async Task AssertReplayVlcHoverStabilityAsync(ReplayOverlayTestHost fixture,
        System.Drawing.Bitmap videoOnly, BitmapSource initialImage, VideoRendererMode rendererMode)
    {
        // Keep production pointer polling running: a single screenshot with polling
        // stopped cannot detect repeating flashes or an incorrect idle dismissal.
        var timer = (System.Windows.Threading.DispatcherTimer)typeof(ReplaySeekOverlay)
            .GetField("pointerTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Overlay)!;
        var preview = (Border)fixture.Overlay.FindName("SeekPreviewChrome");
        var image = (Image)fixture.Overlay.FindName("SeekPreviewImage");
        var previewSource = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(preview);
        var controlsSource = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(fixture.Chrome);
        var nativeChanges = 0;
        IntPtr ObservePlacement(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0046) nativeChanges++;
            return IntPtr.Zero;
        }
        controlsSource.AddHook(ObservePlacement);
        previewSource.AddHook(ObservePlacement);
        timer.Start();
        try
        {
            for (var sample = 0; sample < 48; sample++)
            {
                await Task.Delay(50);
                AssertFrame($"stationary-{sample:00}");
            }
            Assert.Equal(0, nativeChanges);

            var pendingImage = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Overlay.PreviewImageLoader = (_, _, _) => pendingImage.Task;
            for (var sample = 0; sample < 12; sample++)
            {
                var point = fixture.Slider.PointToScreen(new Point(
                    fixture.Slider.ActualWidth * (0.2 + sample * 0.05), fixture.Slider.ActualHeight / 2));
                NativeWindowTest.SetCursorPosition((int)Math.Round(point.X), (int)Math.Round(point.Y));
                await Task.Delay(50);
                AssertFrame($"moving-{sample:00}");
            }
            pendingImage.SetResult(initialImage);
            fixture.Overlay.PreviewImageLoader = (_, _, _) => Task.FromResult<BitmapSource?>(initialImage);
            Console.WriteLine($"{rendererMode}: 60 composed hover frames remained visible with physical input and polling enabled.");
        }
        finally
        {
            timer.Stop();
            controlsSource.RemoveHook(ObservePlacement);
            previewSource.RemoveHook(ObservePlacement);
        }

        void AssertFrame(string phase)
        {
            Assert.True(fixture.Overlay.IsOverlayOpen && fixture.Overlay.IsSeekHoverOpen,
                $"Both overlays must stay open during {phase}.");
            Assert.True(ReferenceEquals(controlsSource, PresentationSource.FromVisual(fixture.Chrome)) &&
                ReferenceEquals(previewSource, PresentationSource.FromVisual(preview)),
                "Hovering must not recreate either native window.");
            Assert.True(ReferenceEquals(initialImage, image.Source), "A pending thumbnail must not blank the preview.");
            using var frame = CaptureReplayVlcSurface(fixture.Target);
            AssertReplayVlcComposition(fixture, videoOnly, frame);
            var center = image.PointToScreen(new Point(image.ActualWidth / 2, image.ActualHeight / 2));
            var bounds = NativeWindowTest.GetWindowBounds(fixture.Target.Handle);
            var actual = frame.GetPixel((int)Math.Round(center.X) - bounds.Left, (int)Math.Round(center.Y) - bounds.Top);
            Assert.True(Math.Abs(actual.R - 96) <= 5 && Math.Abs(actual.G - 108) <= 5 && Math.Abs(actual.B - 160) <= 5,
                $"The composed thumbnail must remain visible during {phase}; got {actual}.");
            if (phase is "stationary-47" or "moving-11") SaveReplayVlcArtifact(frame, rendererMode, phase);
        }
    }

    private static System.Drawing.Bitmap CaptureReplayVlcSurface(VideoSurface surface)
    {
        var bounds = NativeWindowTest.GetWindowBounds(surface.Handle);
        var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            var screenDc = ReplayVlcGetDc(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                    "Failed to acquire the desktop DC for composed VLC capture.");
            try
            {
                var bitmapDc = graphics.GetHdc();
                try
                {
                    const uint sourceCopyWithLayeredWindows = 0x00CC0020 | 0x40000000;
                    if (!ReplayVlcBitBlt(bitmapDc, 0, 0, bounds.Width, bounds.Height,
                        screenDc, bounds.Left, bounds.Top, sourceCopyWithLayeredWindows))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                            "Failed to capture the composed VLC video and layered seek controls.");
                }
                finally
                {
                    graphics.ReleaseHdc(bitmapDc);
                }
            }
            finally
            {
                _ = ReplayVlcReleaseDc(IntPtr.Zero, screenDc);
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static void AssertReplayVlcVideoPixels(System.Drawing.Bitmap bitmap)
    {
        // The configured fixture must be a steady, bright frame (the smoke fixture is
        // uniform blue). Sampling several regions rejects a black or
        // empty renderer even when libVLC reports a valid playback clock.
        foreach (var y in new[] { bitmap.Height / 4, bitmap.Height / 2, bitmap.Height * 3 / 4 })
            foreach (var x in new[] { bitmap.Width / 4, bitmap.Width / 2, bitmap.Width * 3 / 4 })
            {
                var pixel = bitmap.GetPixel(x, y);
                Assert.True(Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) >= 100,
                    $"Expected visible bright video at ({x},{y}), got {pixel}.");
            }
    }

    private static void AssertReplayVlcComposition(ReplayOverlayTestHost fixture,
        System.Drawing.Bitmap videoOnly, System.Drawing.Bitmap composed)
    {
        var surfaceBounds = NativeWindowTest.GetWindowBounds(fixture.Target.Handle);
        var localSample = new Point(Math.Floor(fixture.Chrome.ActualWidth / 2), 4);
        var screenSample = fixture.Chrome.PointToScreen(localSample);
        var x = (int)Math.Round(screenSample.X) - surfaceBounds.Left;
        var y = (int)Math.Round(screenSample.Y) - surfaceBounds.Top;
        var background = videoOnly.GetPixel(x, y);
        var actual = composed.GetPixel(x, y);

        // Inspect the isolated visual only to obtain its premultiplied foreground and
        // alpha. The decisive assertion compares that alpha blend with desktop pixels.
        var foreground = WpfVisualTest.Render(fixture.Chrome);
        var pixel = new byte[4];
        foreground.CopyPixels(new Int32Rect((int)localSample.X, (int)localSample.Y, 1, 1), pixel, 4, 0);
        Assert.True(pixel[3] is >= 210 and < 250, "The sampled seekbar backing must be translucent.");
        var remainingAlpha = 1 - pixel[3] / 255d;
        var expected = System.Drawing.Color.FromArgb(
            (int)Math.Round(pixel[2] + background.R * remainingAlpha),
            (int)Math.Round(pixel[1] + background.G * remainingAlpha),
            (int)Math.Round(pixel[0] + background.B * remainingAlpha));
        Assert.True(Math.Abs(expected.R - actual.R) <= 10 &&
            Math.Abs(expected.G - actual.G) <= 10 && Math.Abs(expected.B - actual.B) <= 10,
            $"Desktop seekbar pixel {actual} must blend its background {background}; expected {expected}.");
        Assert.True(Math.Abs(actual.R - background.R) + Math.Abs(actual.G - background.G) +
            Math.Abs(actual.B - background.B) > 70,
            "The control backing must be visible above the actual video renderer.");
        var brightestChannel = background.R >= background.G && background.R >= background.B ? 2 :
            background.G >= background.B ? 1 : 0;
        var actualChannel = brightestChannel == 2 ? actual.R : brightestChannel == 1 ? actual.G : actual.B;
        Assert.True(actualChannel >= pixel[brightestChannel] + 6,
            "A bright video channel must remain visible through the seekbar's translucent backing.");
    }

    private static void SaveReplayVlcArtifact(System.Drawing.Bitmap bitmap, VideoRendererMode mode, string phase)
    {
        var directory = Environment.GetEnvironmentVariable("SVS_TEST_ARTIFACT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        bitmap.Save(Path.Combine(directory, $"replay-overlay-{mode}-{phase}.png"),
            System.Drawing.Imaging.ImageFormat.Png);
    }

    [DllImport("user32", EntryPoint = "mouse_event")]
    private static extern void ReplayVlcMouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32", EntryPoint = "keybd_event")]
    private static extern void ReplayVlcKeyboardEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32", EntryPoint = "GetDC", SetLastError = true)]
    private static extern IntPtr ReplayVlcGetDc(IntPtr window);

    [DllImport("user32", EntryPoint = "ReleaseDC")]
    private static extern int ReplayVlcReleaseDc(IntPtr window, IntPtr dc);

    [DllImport("gdi32", EntryPoint = "BitBlt", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReplayVlcBitBlt(IntPtr destinationDc, int destinationX, int destinationY,
        int width, int height, IntPtr sourceDc, int sourceX, int sourceY, uint rasterOperation);
}

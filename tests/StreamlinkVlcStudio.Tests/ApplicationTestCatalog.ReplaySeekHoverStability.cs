using System.Windows.Interop;

internal static partial class ApplicationTestCatalog
{
    private static Task ReplaySeekHoverNativeStabilityAsync() => TestSta.RunAsync(async () =>
    {
        await using var session = await ReplayOverlayTestSession.CreateAsync();
        using var fixture = new ReplayOverlayTestHost(session.Tab);
        fixture.Overlay.ProcessPointerSample(new Point(100, 100), true, Environment.TickCount64);
        fixture.FlushBindings();
        var point = new Point(fixture.Slider.ActualWidth / 2, 14);
        fixture.Overlay.UpdateSeekHover(point);
        await Task.Delay(250);
        fixture.FlushBindings();
        var preview = (Border)fixture.Overlay.FindName("SeekPreviewChrome");
        var controlsSource = (HwndSource)PresentationSource.FromVisual(fixture.Chrome);
        var previewSource = (HwndSource)PresentationSource.FromVisual(preview);
        var controlsBefore = NativeWindowTest.GetWindowBounds(controlsSource.Handle);
        var previewBefore = NativeWindowTest.GetWindowBounds(previewSource.Handle);
        var topBefore = NativeWindowTest.GetTopChildWindow(fixture.Target.Handle);
        var controlsPlacements = 0;
        var previewPlacements = 0;
        IntPtr ObservePlacement(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0046) // WM_WINDOWPOSCHANGING, before Windows removes redundant flags.
            {
                if (hwnd == controlsSource.Handle) controlsPlacements++;
                if (hwnd == previewSource.Handle) previewPlacements++;
            }
            return IntPtr.Zero;
        }

        controlsSource.AddHook(ObservePlacement);
        previewSource.AddHook(ObservePlacement);
        try
        {
            for (var sample = 0; sample < 5; sample++)
            {
                fixture.Overlay.UpdateSeekHover(point);
                // Include the video host's periodic renderer-discovery pass, which must
                // preserve two overlays' relative order when nothing has changed.
                await Task.Delay(280);
                fixture.Target.SyncNativeBounds();
                fixture.FlushBindings();
            }
            Assert.True(controlsPlacements == 0 && previewPlacements == 0,
                $"Stationary hover must not reposition/reorder unchanged native windows; " +
                $"seekbar={controlsPlacements}, preview={previewPlacements}.");
            Assert.Equal(topBefore, NativeWindowTest.GetTopChildWindow(fixture.Target.Handle));
            Assert.Equal(controlsBefore, NativeWindowTest.GetWindowBounds(controlsSource.Handle));
            Assert.Equal(previewBefore, NativeWindowTest.GetWindowBounds(previewSource.Handle));

            fixture.Overlay.UpdateSeekHover(new Point(point.X + 50, point.Y));
            fixture.FlushBindings();
            Assert.Equal(controlsBefore, NativeWindowTest.GetWindowBounds(controlsSource.Handle));
            Assert.True(NativeWindowTest.GetWindowBounds(previewSource.Handle).Left > previewBefore.Left,
                "The preview must still follow actual pointer movement.");
            Assert.Equal(topBefore, NativeWindowTest.GetTopChildWindow(fixture.Target.Handle));

            var renderer = NativeWindowTest.CreateVisibleChildWindow(
                fixture.Target.Handle, "StreamlinkVlcStudioVideoSurface");
            try
            {
                fixture.FlushBindings();
                var controlsPoint = fixture.Chrome.PointToScreen(new Point(20, 20));
                Assert.Equal(controlsSource.Handle, NativeWindowHitTester.Instance.WindowFromPoint(
                    (int)controlsPoint.X, (int)controlsPoint.Y));
                var siblings = new List<IntPtr>();
                for (var sibling = NativeWindowTest.GetTopChildWindow(fixture.Target.Handle);
                     sibling != IntPtr.Zero; sibling = ReplayHoverGetWindow(sibling, 2)) // GW_HWNDNEXT
                    siblings.Add(sibling);
                Assert.True(siblings.Contains(controlsSource.Handle) && siblings.Contains(previewSource.Handle) &&
                    siblings.IndexOf(controlsSource.Handle) < siblings.IndexOf(renderer) &&
                    siblings.IndexOf(previewSource.Handle) < siblings.IndexOf(renderer),
                    "A new VLC renderer must remain below both overlays, regardless of their relative order.");
            }
            finally { NativeWindowTest.DestroyWindow(renderer); }
        }
        finally
        {
            controlsSource.RemoveHook(ObservePlacement);
            previewSource.RemoveHook(ObservePlacement);
        }
    });

    private static Task ReplaySeekHoverImageStabilityAsync() => TestSta.RunAsync(async () =>
    {
        await using var session = await ReplayOverlayTestSession.CreateAsync();
        using var fixture = new ReplayOverlayTestHost(session.Tab);
        var firstImage = CreateSeekHoverTestImage(0x40);
        var nextImage = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        fixture.Overlay.PreviewImageLoader = (_, _, _) =>
            Interlocked.Increment(ref requests) == 1 ? Task.FromResult<BitmapSource?>(firstImage) : nextImage.Task;
        fixture.Overlay.ProcessPointerSample(new Point(100, 100), true, Environment.TickCount64);
        fixture.FlushBindings();
        fixture.Overlay.UpdateSeekHover(new Point(fixture.Slider.ActualWidth / 4, 14));
        var image = (Image)fixture.Overlay.FindName("SeekPreviewImage");
        var frame = (Border)fixture.Overlay.FindName("SeekPreviewImageFrame");
        var timestamp = (TextBlock)fixture.Overlay.FindName("SeekPreviewTimestamp");
        var preview = (Border)fixture.Overlay.FindName("SeekPreviewChrome");
        await TestWait.UntilAsync(() => ReferenceEquals(firstImage, image.Source), TimeSpan.FromSeconds(2));
        fixture.FlushBindings();
        var source = (HwndSource)PresentationSource.FromVisual(preview);
        var height = NativeWindowTest.GetWindowBounds(source.Handle).Height;
        var initialTimestamp = timestamp.Text;
        var seeks = session.SeekCount;
        for (var move = 0; move < 8; move++)
        {
            fixture.Overlay.UpdateSeekHover(new Point(fixture.Slider.ActualWidth * (0.5 + move * 0.01), 14));
            fixture.FlushBindings();
            Assert.True(ReferenceEquals(firstImage, image.Source),
                "Moving the hover must retain the displayed frame until its replacement is ready.");
            Assert.Equal(Visibility.Visible, frame.Visibility);
            Assert.Equal(height, NativeWindowTest.GetWindowBounds(source.Handle).Height);
            Assert.True(ReferenceEquals(source, PresentationSource.FromVisual(preview)));
            await Task.Delay(30); // Keep moving inside the production image debounce.
        }
        Assert.True(timestamp.Text != initialTimestamp, "Timestamp feedback must not wait for image loading.");
        await TestWait.UntilAsync(() => Volatile.Read(ref requests) >= 2, TimeSpan.FromSeconds(2));
        var replacement = CreateSeekHoverTestImage(0xD0);
        nextImage.SetResult(replacement);
        await TestWait.UntilAsync(() => ReferenceEquals(replacement, image.Source), TimeSpan.FromSeconds(2));
        fixture.FlushBindings();
        Assert.Equal(height, NativeWindowTest.GetWindowBounds(source.Handle).Height);
        Assert.Equal(seeks, session.SeekCount);

        // A completed unavailable result must still become timestamp-only; retention
        // applies only while waiting and must never leak into another hover session.
        fixture.Overlay.PreviewImageLoader = (_, _, _) => Task.FromResult<BitmapSource?>(null);
        fixture.Overlay.UpdateSeekHover(new Point(fixture.Slider.ActualWidth * 0.9, 14));
        await TestWait.UntilAsync(() => image.Source is null, TimeSpan.FromSeconds(2));
        Assert.Equal(Visibility.Collapsed, frame.Visibility);
        fixture.Overlay.UpdateSeekHover(new Point(-1, 14));
        Assert.Equal(false, fixture.Overlay.IsSeekHoverOpen);
        fixture.Overlay.UpdateSeekHover(new Point(fixture.Slider.ActualWidth / 4, 14));
        Assert.True(image.Source is null);
    });

    [DllImport("user32", EntryPoint = "GetWindow")]
    private static extern IntPtr ReplayHoverGetWindow(IntPtr hwnd, uint command);
}

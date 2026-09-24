using System.Windows.Interop;
using System.Windows.Threading;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> PictureInPictureResizeTests { get; } =
    [
        ("picture-in-picture resize border scales all edges and corner arms on negative monitors", PictureInPictureResizeDpiBoundaries),
        ("picture-in-picture resize border rejects exterior and invalid geometry", PictureInPictureResizeInvalidGeometry),
        ("picture-in-picture resize native hit tests cover all edges with either chrome setting", PictureInPictureResizeNativeHitTests),
        ("picture-in-picture resize native video cursor matches every edge and corner", PictureInPictureResizeNativeCursors),
        ("picture-in-picture resize native clicks and renderer fallback outrank dragging and fullscreen", PictureInPictureResizeNativeInitiation),
        ("picture-in-picture resize disables in fullscreen maximized and fixed states and restores", PictureInPictureResizeStateTransitions),
        ("picture-in-picture resize physical drags change bounds from every edge and corner", PictureInPictureResizePhysicalDrags)
    ];

    private static Task PictureInPictureResizeDpiBoundaries()
    {
        var bounds = new NativeRectangle { Left = -1920, Top = -1080, Right = -1120, Bottom = -580 };
        // The expected device-pixel widths are deliberately explicit, including fractional
        // rounding and unequal axes, so this checks the DIP contract rather than copying it.
        (double X, double Y, int EdgeX, int EdgeY, int Bottom, int CornerX, int CornerY)[] scales =
        [
            (1, 1, 6, 6, 10, 24, 24),
            (1.25, 1.25, 8, 8, 13, 30, 30),
            (1.5, 1.5, 9, 9, 15, 36, 36),
            (2, 2, 12, 12, 20, 48, 48),
            (2, 1.5, 12, 9, 15, 48, 36)
        ];
        foreach (var scale in scales)
        {
            void Hit(int x, int y, int expected)
            {
                var accepted = PictureInPictureWindowResize.TryHitTest(bounds, x, y, scale.X, scale.Y, out var actual);
                Assert.Equal(expected != 0, accepted);
                Assert.True(actual == expected,
                    $"At scale {scale.X}/{scale.Y}, ({x},{y}) returned {actual}, expected {expected}.");
            }

            var middleX = (bounds.Left + bounds.Right) / 2;
            var middleY = (bounds.Top + bounds.Bottom) / 2;
            Hit(bounds.Left + scale.EdgeX - 1, middleY, 10);
            Hit(bounds.Left + scale.EdgeX, middleY, 0);
            Hit(bounds.Right - scale.EdgeX, middleY, 11);
            Hit(bounds.Right - scale.EdgeX - 1, middleY, 0);
            Hit(middleX, bounds.Top + scale.EdgeY - 1, 12);
            Hit(middleX, bounds.Top + scale.EdgeY, 0);
            Hit(middleX, bounds.Bottom - scale.Bottom, 15);
            Hit(middleX, bounds.Bottom - scale.Bottom - 1, 0);

            Hit(bounds.Left + scale.CornerX - 1, bounds.Top, 13);
            Hit(bounds.Left + scale.CornerX, bounds.Top, 12);
            Hit(bounds.Left, bounds.Top + scale.CornerY - 1, 13);
            Hit(bounds.Left, bounds.Top + scale.CornerY, 10);
            Hit(bounds.Right - scale.CornerX, bounds.Top, 14);
            Hit(bounds.Right - scale.CornerX - 1, bounds.Top, 12);
            Hit(bounds.Right - 1, bounds.Top + scale.CornerY - 1, 14);
            Hit(bounds.Right - 1, bounds.Top + scale.CornerY, 11);
            Hit(bounds.Left + scale.CornerX - 1, bounds.Bottom - 1, 16);
            Hit(bounds.Left + scale.CornerX, bounds.Bottom - 1, 15);
            Hit(bounds.Left, bounds.Bottom - scale.CornerY, 16);
            Hit(bounds.Left, bounds.Bottom - scale.CornerY - 1, 10);
            Hit(bounds.Right - scale.CornerX, bounds.Bottom - 1, 17);
            Hit(bounds.Right - scale.CornerX - 1, bounds.Bottom - 1, 15);
            Hit(bounds.Right - 1, bounds.Bottom - scale.CornerY, 17);
            Hit(bounds.Right - 1, bounds.Bottom - scale.CornerY - 1, 11);

            // Corner targets are L-shaped: their interior must remain playable video.
            Hit(bounds.Left + scale.EdgeX, bounds.Top + scale.EdgeY, 0);
            Hit(bounds.Right - scale.EdgeX - 1, bounds.Top + scale.EdgeY, 0);
            Hit(bounds.Left + scale.EdgeX, bounds.Bottom - scale.Bottom - 1, 0);
            Hit(bounds.Right - scale.EdgeX - 1, bounds.Bottom - scale.Bottom - 1, 0);
        }

        return Task.CompletedTask;
    }

    private static Task PictureInPictureResizeInvalidGeometry()
    {
        var bounds = new NativeRectangle { Left = -800, Top = -600, Right = 0, Bottom = 0 };
        foreach (var point in new[] { (-801, -300), (0, -300), (-400, -601), (-400, 0), (-400, -300) })
        {
            Assert.Equal(false, PictureInPictureWindowResize.TryHitTest(bounds, point.Item1, point.Item2, 1, 1, out var hit));
            Assert.Equal(0, hit);
        }

        foreach (var invalid in new[] { 0, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Equal(false, PictureInPictureWindowResize.TryHitTest(bounds, -800, -600, invalid, 1, out _));
            Assert.Equal(false, PictureInPictureWindowResize.TryHitTest(bounds, -800, -600, 1, invalid, out _));
        }

        Assert.Equal(false, PictureInPictureWindowResize.TryHitTest(default, 0, 0, 1, 1, out _));
        var enormous = new NativeRectangle { Left = int.MinValue, Top = int.MinValue, Right = int.MaxValue, Bottom = int.MaxValue };
        Assert.True(PictureInPictureWindowResize.TryHitTest(enormous, int.MinValue, 0, 1, 1, out var left));
        Assert.Equal(10, left);
        Assert.True(PictureInPictureWindowResize.TryHitTest(enormous, int.MaxValue - 1, 0, 1, 1, out var right));
        Assert.Equal(11, right);
        Assert.Equal(false, PictureInPictureWindowResize.TryHitTest(enormous, 0, 0, 1, 1, out _));
        return Task.CompletedTask;
    }

    private static Task PictureInPictureResizeNativeHitTests() => TestSta.RunAsync(() =>
    {
        foreach (var shown in new[] { true, false })
        {
            var window = CreatePictureInPictureResizeWindow(shown);
            try
            {
                ShowPictureInPictureResizeWindow(window);
                AssertPictureInPictureNativeResizeHits(window);
                var handle = new WindowInteropHelper(window).Handle;
                var surface = FindVisualDescendants<VideoSurface>(window).Single();
                var center = surface.PointToScreen(new Point(surface.ActualWidth / 2, surface.ActualHeight / 2));
                Assert.Equal(new IntPtr(1), NativeWindowTest.SendMessage(handle, 0x0084, IntPtr.Zero,
                    NativeWindowTest.MakeMouseLParam((int)center.X, (int)center.Y)));
                if (shown)
                {
                    foreach (var name in new[] { "HideTopBarButton", "TopmostButton", "FullscreenButton" })
                    {
                        var button = (Button)window.FindName(name);
                        var point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                        Assert.Equal(new IntPtr(1), NativeWindowTest.SendMessage(handle, 0x0084, IntPtr.Zero,
                            NativeWindowTest.MakeMouseLParam((int)point.X, (int)point.Y)));
                    }
                }
            }
            finally
            {
                window.CloseForTabDisposal();
            }
        }
    });

    private static Task PictureInPictureResizeNativeCursors() => TestSta.RunAsync(() =>
    {
        var window = CreatePictureInPictureResizeWindow(showTopBar: false);
        var restoreCursor = NativeWindowTest.TryGetCursorPosition(out var cursor);
        try
        {
            ShowPictureInPictureResizeWindow(window);
            var surface = FindVisualDescendants<VideoSurface>(window).Single();
            var bounds = NativeWindowTest.GetWindowBounds(new WindowInteropHelper(window).Handle);
            foreach (var edge in PictureInPictureResizePoints(bounds))
            {
                NativeWindowTest.SetCursorPosition(edge.X, edge.Y);
                var result = NativeWindowTest.SendMessage(surface.Handle, 0x0020, surface.Handle,
                    NativeWindowTest.MakeMouseLParam(1, 0x0200));
                Assert.Equal(new IntPtr(1), result);
                var expectedCursor = edge.Hit switch
                {
                    10 or 11 => 32644, // IDC_SIZEWE
                    12 or 15 => 32645, // IDC_SIZENS
                    13 or 17 => 32642, // IDC_SIZENWSE
                    _ => 32643 // IDC_SIZENESW
                };
                Assert.True(PictureInPictureGetCursor() == PictureInPictureLoadCursor(IntPtr.Zero, new IntPtr(expectedCursor)),
                    $"Native video cursor for hit {edge.Hit} did not match resize direction.");
            }
        }
        finally
        {
            if (restoreCursor)
            {
                NativeWindowTest.SetCursorPosition(cursor.X, cursor.Y);
            }

            window.CloseForTabDisposal();
        }
    });

    private static Task PictureInPictureResizeNativeInitiation() => TestSta.RunAsync(() =>
    {
        var window = CreatePictureInPictureResizeWindow(showTopBar: false);
        HwndSource? source = null;
        IntPtr renderer = IntPtr.Zero;
        var resizeRequests = new List<(int Hit, IntPtr Point)>();
        IntPtr ObserveResize(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x00A1 && wParam.ToInt32() is >= 10 and <= 17)
            {
                resizeRequests.Add((wParam.ToInt32(), lParam));
                // Observe the actual OS resize command without entering its modal loop.
                // The separate physical-drag test exercises that loop and resulting bounds.
                handled = true;
            }

            return IntPtr.Zero;
        }

        try
        {
            ShowPictureInPictureResizeWindow(window);
            var handle = new WindowInteropHelper(window).Handle;
            source = HwndSource.FromHwnd(handle);
            Assert.NotNull(source);
            source!.AddHook(ObserveResize);
            var surface = FindVisualDescendants<VideoSurface>(window).Single();
            var bounds = NativeWindowTest.GetWindowBounds(handle);
            var center = surface.PointToScreen(new Point(surface.ActualWidth / 2, surface.ActualHeight / 2));
            foreach (var edge in PictureInPictureResizePoints(bounds))
            {
                foreach (var message in new[] { 0x0201, 0x0203 }) // Native click and double-click.
                {
                    Assert.True(window.TryBeginVideoMoveFromScreenClick((int)center.X, (int)center.Y));
                    Assert.True(window.HasVideoMoveCandidate);
                    var before = resizeRequests.Count;
                    NativeWindowTest.SendMessage(surface.Handle, message, new IntPtr(1),
                        NativeWindowTest.MakeMouseLParamFromScreenPoint(surface.Handle, new Point(edge.X, edge.Y)));
                    Assert.Equal(before + 1, resizeRequests.Count);
                    Assert.Equal((edge.Hit, NativeWindowTest.MakeMouseLParam(edge.X, edge.Y)), resizeRequests[^1]);
                    Assert.Equal(false, window.HasVideoMoveCandidate);
                    Assert.Equal(false, window.HasActiveWindowMove);
                    Assert.Equal(false, window.IsStreamFullscreen);
                    Assert.Equal(false, window.TryBeginVideoMoveFromScreenClick(edge.X, edge.Y));
                    Assert.Equal(false, window.TryToggleStreamFullscreenFromScreenClick(edge.X, edge.Y));
                }
            }

            // VLC creates a child HWND inside VideoSurface. Its mouse messages bypass the
            // host, so the screen-click fallback must still recognize its parent's border.
            // A button class consumes input like a renderer; the static class can be
            // transparent to WindowFromPoint and would not prove descendant ownership.
            renderer = NativeWindowTest.CreateVisibleChildWindow(surface.Handle, className: "button");
            var surfaceBounds = NativeWindowTest.GetWindowBounds(surface.Handle);
            NativeWindowTest.SetWindowBounds(renderer, 0, 0, surfaceBounds.Width, surfaceBounds.Height);
            PumpPictureInPictureResize(window);
            foreach (var edge in PictureInPictureResizePoints(bounds))
            {
                Assert.Equal(renderer, NativeWindowHitTester.Instance.WindowFromPoint(edge.X, edge.Y));
                var before = resizeRequests.Count;
                Assert.True(window.TryBeginResizeFromScreenClick(edge.X, edge.Y));
                Assert.Equal(before + 1, resizeRequests.Count);
                Assert.Equal((edge.Hit, NativeWindowTest.MakeMouseLParam(edge.X, edge.Y)), resizeRequests[^1]);
                Assert.Equal(false, window.HasVideoMoveCandidate);
            }

            Assert.Equal(false, window.TryBeginResizeFromScreenClick((int)center.X, (int)center.Y));
        }
        finally
        {
            source?.RemoveHook(ObserveResize);
            if (renderer != IntPtr.Zero)
            {
                NativeWindowTest.DestroyWindow(renderer);
            }

            window.CancelVideoMoveCandidate();
            window.CloseForTabDisposal();
        }
    });

    private static Task PictureInPictureResizeStateTransitions() => TestSta.RunAsync(() =>
    {
        foreach (var showTopBar in new[] { true, false })
        {
            var window = CreatePictureInPictureResizeWindow(showTopBar);
            try
            {
                ShowPictureInPictureResizeWindow(window);
                foreach (var fixedMode in new[] { ResizeMode.NoResize, ResizeMode.CanMinimize })
                {
                    window.ResizeMode = fixedMode;
                    PumpPictureInPictureResize(window);
                    AssertPictureInPictureResizeDisabled(window);
                    window.ResizeMode = ResizeMode.CanResize;
                    PumpPictureInPictureResize(window);
                    AssertPictureInPictureNativeResizeHits(window);
                }

                window.WindowState = WindowState.Maximized;
                PumpPictureInPictureResize(window);
                AssertPictureInPictureResizeDisabled(window);
                window.WindowState = WindowState.Normal;
                PumpPictureInPictureResize(window);
                AssertPictureInPictureNativeResizeHits(window);

                window.EnterStreamFullscreen();
                PumpPictureInPictureResize(window);
                AssertPictureInPictureResizeDisabled(window);
                window.ExitStreamFullscreen();
                PumpPictureInPictureResize(window);
                Assert.Equal(showTopBar, window.IsTopBarShown);
                AssertPictureInPictureNativeResizeHits(window);
            }
            finally
            {
                window.CloseForTabDisposal();
            }
        }
    });

    private static Task PictureInPictureResizePhysicalDrags() => TestSta.RunAsync(async () =>
    {
        var window = CreatePictureInPictureResizeWindow(showTopBar: false);
        var restoreCursor = NativeWindowTest.TryGetCursorPosition(out var cursor);
        try
        {
            ShowPictureInPictureResizeWindow(window);
            var handle = new WindowInteropHelper(window).Handle;
            await NativeWindowTest.RequireForegroundAsync(handle, TimeSpan.FromSeconds(2), "PiP physical resize");
            Assert.True(WindowInteropHelpers.TryGetMonitorInfo(handle, out var monitor));
            var original = NativeWindowTest.GetWindowBounds(handle);
            var area = monitor.WorkArea;
            if (original.Width + 160 > area.Right - area.Left || original.Height + 160 > area.Bottom - area.Top)
            {
                throw new InteractiveDesktopTestSkippedException("PiP physical resize requires room around the test window");
            }

            original.X = area.Left + (area.Right - area.Left - original.Width) / 2;
            original.Y = area.Top + (area.Bottom - area.Top - original.Height) / 2;
            foreach (var direction in Enumerable.Range(10, 8))
            {
                NativeWindowTest.SetWindowBounds(handle, original.X, original.Y, original.Width, original.Height);
                PumpPictureInPictureResize(window);
                var before = NativeWindowTest.GetWindowBounds(handle);
                var edge = PictureInPictureResizePoints(before).Single(candidate => candidate.Hit == direction);
                Assert.True(NativeWindowTest.IsRootWindowAtPoint(handle, edge.X, edge.Y),
                    "Physical resize point is covered: " + NativeWindowTest.DescribeWindowAtPoint(edge.X, edge.Y));
                var dx = direction is 10 or 13 or 16 ? -48 : direction is 11 or 14 or 17 ? 48 : 0;
                var dy = direction is 12 or 13 or 14 ? -32 : direction is 15 or 16 or 17 ? 32 : 0;
                await Task.Run(() =>
                {
                    NativeWindowTest.SetCursorPosition(edge.X, edge.Y);
                    Thread.Sleep(80);
                    try
                    {
                        PictureInPictureMouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
                        Thread.Sleep(180);
                        for (var step = 1; step <= 6; step++)
                        {
                            NativeWindowTest.SetCursorPosition(edge.X + dx * step / 6, edge.Y + dy * step / 6);
                            Thread.Sleep(30);
                        }
                    }
                    finally
                    {
                        PictureInPictureMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                    }
                });
                await Task.Delay(80);
                PumpPictureInPictureResize(window);
                var after = NativeWindowTest.GetWindowBounds(handle);
                Assert.True(after.Width > before.Width + 8 && after.Height > before.Height + 8,
                    $"Dragging native hit {direction} failed to enlarge the PiP: {before} -> {after}.");
                if (dx < 0) AssertNear(before.Right, after.Right, 2);
                if (dx > 0) AssertNear(before.Left, after.Left, 2);
                if (dy < 0) AssertNear(before.Bottom, after.Bottom, 2);
                if (dy > 0) AssertNear(before.Top, after.Top, 2);
                var video = (FrameworkElement)window.FindName("VideoHost");
                AssertNear(window.ContentAspectRatio, video.ActualWidth / video.ActualHeight, 0.015);
                Assert.Equal(false, window.HasActiveWindowMove);
                Assert.Equal(false, window.HasVideoMoveCandidate);
                Assert.Equal(false, window.IsStreamFullscreen);
            }
        }
        finally
        {
            PictureInPictureMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
            NativeWindowTest.ReleaseCapture();
            if (restoreCursor)
            {
                NativeWindowTest.SetCursorPosition(cursor.X, cursor.Y);
            }

            window.CloseForTabDisposal();
        }
    });

    private static DetachedVideoWindow CreatePictureInPictureResizeWindow(bool showTopBar)
    {
        var tab = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best",
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
            new MemoryLogger(), action => action());
        return new DetachedVideoWindow([tab], tab, showTopBar)
        {
            Left = 160,
            Top = 160,
            Width = 640,
            Height = showTopBar ? 394 : 360,
            Topmost = true
        };
    }

    private static void ShowPictureInPictureResizeWindow(DetachedVideoWindow window)
    {
        window.Show();
        NativeWindowTest.ActivateWindow(new WindowInteropHelper(window).Handle);
        PumpPictureInPictureResize(window);
    }

    private static void PumpPictureInPictureResize(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static (int Hit, int X, int Y)[] PictureInPictureResizePoints(System.Drawing.Rectangle bounds) =>
    [
        (10, bounds.Left + 2, bounds.Top + bounds.Height / 2),
        (11, bounds.Right - 3, bounds.Top + bounds.Height / 2),
        (12, bounds.Left + bounds.Width / 2, bounds.Top + 2),
        (13, bounds.Left + 2, bounds.Top + 2),
        (14, bounds.Right - 3, bounds.Top + 2),
        (15, bounds.Left + bounds.Width / 2, bounds.Bottom - 3),
        (16, bounds.Left + 2, bounds.Bottom - 3),
        (17, bounds.Right - 3, bounds.Bottom - 3)
    ];

    private static void AssertPictureInPictureNativeResizeHits(DetachedVideoWindow window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var bounds = NativeWindowTest.GetWindowBounds(handle);
        foreach (var edge in PictureInPictureResizePoints(bounds))
        {
            var result = NativeWindowTest.SendMessage(handle, 0x0084, IntPtr.Zero,
                NativeWindowTest.MakeMouseLParam(edge.X, edge.Y));
            Assert.True(result.ToInt32() == edge.Hit,
                $"Native hit at ({edge.X},{edge.Y}) with chrome={window.IsTopBarShown} returned {result}, expected {edge.Hit}.");
        }

        var transform = PresentationSource.FromVisual(window)!.CompositionTarget!.TransformToDevice;
        var cornerX = (int)Math.Round(18 * transform.M11);
        var cornerY = (int)Math.Round(18 * transform.M22);
        foreach (var corner in new[]
        {
            (13, bounds.Left + cornerX, bounds.Top + 2),
            (13, bounds.Left + 2, bounds.Top + cornerY),
            (14, bounds.Right - cornerX, bounds.Top + 2),
            (14, bounds.Right - 3, bounds.Top + cornerY),
            (16, bounds.Left + cornerX, bounds.Bottom - 3),
            (16, bounds.Left + 2, bounds.Bottom - cornerY),
            (17, bounds.Right - cornerX, bounds.Bottom - 3),
            (17, bounds.Right - 3, bounds.Bottom - cornerY)
        })
        {
            Assert.Equal(new IntPtr(corner.Item1), NativeWindowTest.SendMessage(handle, 0x0084, IntPtr.Zero,
                NativeWindowTest.MakeMouseLParam(corner.Item2, corner.Item3)));
        }
    }

    private static void AssertPictureInPictureResizeDisabled(DetachedVideoWindow window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        foreach (var edge in PictureInPictureResizePoints(NativeWindowTest.GetWindowBounds(handle)))
        {
            var hit = NativeWindowTest.SendMessage(handle, 0x0084, IntPtr.Zero,
                NativeWindowTest.MakeMouseLParam(edge.X, edge.Y)).ToInt32();
            Assert.True(hit is < 10 or > 17,
                $"State {window.WindowState}/{window.ResizeMode}/fullscreen={window.IsStreamFullscreen} retained resize hit {hit}.");
            Assert.Equal(false, window.TryBeginResizeFromScreenClick(edge.X, edge.Y));
        }
    }

    [DllImport("user32", EntryPoint = "GetCursor")]
    private static extern IntPtr PictureInPictureGetCursor();

    [DllImport("user32", EntryPoint = "LoadCursorW")]
    private static extern IntPtr PictureInPictureLoadCursor(IntPtr instance, IntPtr name);

    [DllImport("user32", EntryPoint = "mouse_event")]
    private static extern void PictureInPictureMouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}

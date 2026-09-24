using System.Windows.Interop;
using System.Windows.Threading;

internal static class NativeMouseWheelTargetTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Native wheel capture follows video surface ownership and inactive routing policy", VideoSurfaceOwnershipAsync),
        ("Native wheel fallback preserves the captured packet and unregisters cleanly", NativeFallbackPacketAsync),
        ("Native wheel capture never takes input from a foreign process", ForeignProcessPassesThroughAsync)
    ];

    private static Task VideoSurfaceOwnershipAsync() => TestSta.RunAsync(async () =>
    {
        using var surface = new VideoSurface();
        var video = CreateWindow(surface, 20);
        var ordinary = CreateWindow(new Border { Background = Brushes.White }, 340);
        var previousForeground = NativeWindowTest.GetForegroundWindow();
        var child = IntPtr.Zero;
        try
        {
            video.Show();
            ordinary.Show();
            video.UpdateLayout();
            await NativeWindowTest.RequireForegroundAsync(new WindowInteropHelper(video).Handle,
                TimeSpan.FromSeconds(2), "Native wheel video ownership");
            Assert.True(surface.Handle != IntPtr.Zero);
            child = NativeWindowTest.CreateVisibleChildWindow(surface.Handle, "button");
            var point = surface.PointToScreen(new Point(10, 10));
            var wheel = WheelAt(point, 120);
            Assert.Equal(child, NativeWindowHitTester.Instance.WindowFromPoint(wheel.ScreenX, wheel.ScreenY));
            Assert.NotNull(NativeMouseWheelTarget.CaptureFallback(wheel));

            await NativeWindowTest.RequireForegroundAsync(new WindowInteropHelper(ordinary).Handle,
                TimeSpan.FromSeconds(2), "Native wheel inactive main window policy");
            Assert.True(NativeMouseWheelTarget.CaptureFallback(wheel) is null,
                "An inactive main video must leave focus-versus-hover wheel delivery to Windows.");
            var ordinaryPoint = ordinary.PointToScreen(new Point(20, 20));
            Assert.True(NativeMouseWheelTarget.CaptureFallback(WheelAt(ordinaryPoint, 120)) is null,
                "Ordinary WPF windows must not be captured.");

            // Exercise the explicit policy used by detached surfaces while another HWND
            // has focus, using the same real surface and native child as the main case.
            NativeMouseWheelTarget.RegisterWindow(surface.Handle, acceptsInactiveWheel: true);
            Assert.NotNull(NativeMouseWheelTarget.CaptureFallback(wheel));
            NativeMouseWheelTarget.UnregisterWindow(surface.Handle);
            Assert.True(NativeMouseWheelTarget.CaptureFallback(wheel) is null,
                "Unregistering a video host must also stop capturing its native descendants.");
            NativeMouseWheelTarget.RegisterWindow(surface.Handle, acceptsInactiveWheel: false);
        }
        finally
        {
            if (child != IntPtr.Zero) NativeWindowTest.DestroyWindow(child);
            ordinary.Close();
            video.Close();
            NativeWindowTest.ActivateWindow(previousForeground);
        }
    });

    private static Task NativeFallbackPacketAsync() => TestSta.RunAsync(async () =>
    {
        var window = CreateWindow(new Border { Background = Brushes.White }, 20);
        var previousForeground = NativeWindowTest.GetForegroundWindow();
        var received = new List<(IntPtr WParam, IntPtr LParam)>();
        HwndSource? source = null;
        var handle = IntPtr.Zero;
        IntPtr ObserveWheel(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == LowLevelMouseHookEvent.WmMouseWheel)
            {
                received.Add((wParam, lParam));
                handled = true;
            }

            return IntPtr.Zero;
        }

        try
        {
            window.Show();
            window.UpdateLayout();
            handle = new WindowInteropHelper(window).Handle;
            source = HwndSource.FromHwnd(handle)!;
            source.AddHook(ObserveWheel);
            await NativeWindowTest.RequireForegroundAsync(handle, TimeSpan.FromSeconds(2), "Native wheel fallback packet");
            NativeMouseWheelTarget.RegisterWindow(handle, acceptsInactiveWheel: false);
            var wheel = WheelAt(window.PointToScreen(new Point(30, 40)), -240);
            Assert.Equal(handle, NativeWindowHitTester.Instance.WindowFromPoint(wheel.ScreenX, wheel.ScreenY));
            var expectedButtons = ReadMouseKeyFlags();
            var fallback = NativeMouseWheelTarget.CaptureFallback(wheel);
            Assert.NotNull(fallback);
            NativeMouseWheelTarget.UnregisterWindow(handle);
            Assert.True(NativeMouseWheelTarget.CaptureFallback(wheel) is null);

            // The retained packet already owns its delivery even if registration changes.
            fallback!();
            await TestWait.UntilAsync(() => received.Count == 1, TimeSpan.FromSeconds(2));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, received.Count);
            Assert.Equal(wheel.WheelDelta, unchecked((short)(received[0].WParam.ToInt64() >> 16)));
            Assert.Equal(expectedButtons, (int)(received[0].WParam.ToInt64() & 0xFFFF));
            Assert.Equal(wheel.ScreenX, unchecked((short)received[0].LParam.ToInt64()));
            Assert.Equal(wheel.ScreenY, unchecked((short)(received[0].LParam.ToInt64() >> 16)));
        }
        finally
        {
            NativeMouseWheelTarget.UnregisterWindow(handle);
            source?.RemoveHook(ObserveWheel);
            window.Close();
            NativeWindowTest.ActivateWindow(previousForeground);
        }
    });

    private static Task ForeignProcessPassesThroughAsync() => TestSta.RunAsync(() =>
    {
        var area = SystemParameters.WorkArea;
        foreach (var point in new[]
        {
            new Point(area.Left + 2, area.Top + 2),
            new Point(area.Right - 2, area.Bottom - 2),
            new Point(area.Left + area.Width / 2, area.Top + area.Height / 2)
        })
        {
            var wheel = WheelAt(point, -120);
            var target = NativeWindowHitTester.Instance.WindowFromPoint(wheel.ScreenX, wheel.ScreenY);
            if (target == IntPtr.Zero || GetWindowThreadProcessId(target, out var processId) == 0 ||
                processId == (uint)Environment.ProcessId)
                continue;

            // Even a stale or incorrect registry entry may never capture another process.
            NativeMouseWheelTarget.RegisterWindow(target, acceptsInactiveWheel: true);
            try
            {
                Assert.True(NativeMouseWheelTarget.CaptureFallback(wheel) is null);
            }
            finally
            {
                NativeMouseWheelTarget.UnregisterWindow(target);
            }

            return;
        }

        throw new InteractiveDesktopTestSkippedException("No foreign desktop HWND is exposed for the wheel ownership check.");
    });

    private static Window CreateWindow(UIElement content, double horizontalOffset) => new()
    {
        Width = 280,
        Height = 180,
        Left = SystemParameters.WorkArea.Left + horizontalOffset,
        Top = SystemParameters.WorkArea.Top + 40,
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize,
        Topmost = true,
        ShowInTaskbar = false,
        Content = content
    };

    private static LowLevelMouseHookEvent WheelAt(Point point, int delta) => new(
        LowLevelMouseHookEvent.WmMouseWheel, (int)Math.Round(point.X), (int)Math.Round(point.Y), delta << 16);

    private static int ReadMouseKeyFlags()
    {
        var flags = 0;
        foreach (var (key, flag) in new[] { (1, 1), (2, 2), (16, 4), (17, 8), (4, 16), (5, 32), (6, 64) })
            if ((GetAsyncKeyState(key) & 0x8000) != 0) flags |= flag;
        return flags;
    }

    [DllImport("user32")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}

using System.Windows.Interop;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> MaximizedWorkAreaTests { get; } =
    [
        ("maximized placement respects 1440p secondary monitor taskbars in native pixels", () =>
        {
            // A 1920x1080 primary with a 2560x1440 secondary to its right, left, or above.
            // Different taskbar sizes also cover scaled displays without converting to DIPs.
            foreach (var (left, top) in new[] { (1920, 0), (-2560, 0), (0, -1440) })
            foreach (var taskbarPixels in new[] { 40, 60, 72 })
            foreach (var edge in new[] { "bottom", "top", "left", "right" })
            foreach (var geometryFlags in new uint[] { 0, 1, 2 })
            {
                var workArea = new NativeRectangle
                {
                    Left = left + (edge == "left" ? taskbarPixels : 0),
                    Top = top + (edge == "top" ? taskbarPixels : 0),
                    Right = left + 2560 - (edge == "right" ? taskbarPixels : 0),
                    Bottom = top + 1440 - (edge == "bottom" ? taskbarPixels : 0)
                };
                var position = new NativeWindowPosition
                {
                    Window = new IntPtr(123),
                    InsertAfter = new IntPtr(456),
                    X = left - 8,
                    Y = top - 8,
                    Width = 2576,
                    Height = 1456,
                    Flags = 0x0014 | geometryFlags // Preserve NOZORDER and NOACTIVATE.
                };

                Assert.True(WindowInteropHelpers.ConstrainMaximizedWindowPosition(ref position, workArea));
                Assert.Equal(workArea.Left, position.X);
                Assert.Equal(workArea.Top, position.Y);
                Assert.Equal(workArea.Right, position.X + position.Width);
                Assert.Equal(workArea.Bottom, position.Y + position.Height);
                Assert.Equal(0x0014u, position.Flags);
                Assert.Equal(new IntPtr(123), position.Window);
                Assert.Equal(new IntPtr(456), position.InsertAfter);
            }

            return Task.CompletedTask;
        }),
        ("maximized placement ignores z-order-only requests and unavailable work areas", () =>
        {
            var workArea = new NativeRectangle { Left = 1920, Right = 4480, Bottom = 1400 };
            var position = new NativeWindowPosition { X = 5, Y = 6, Width = 7, Height = 8, Flags = 3 };
            Assert.Equal(false, WindowInteropHelpers.ConstrainMaximizedWindowPosition(ref position, workArea));
            Assert.Equal(5, position.X);
            Assert.Equal(6, position.Y);
            Assert.Equal(7, position.Width);
            Assert.Equal(8, position.Height);
            Assert.Equal(3u, position.Flags);

            position.Flags = 0;
            Assert.Equal(false, WindowInteropHelpers.ConstrainMaximizedWindowPosition(ref position, default));
            Assert.Equal(7, position.Width);
            Assert.Equal(8, position.Height);
            return Task.CompletedTask;
        }),
        ("main maximized home window keeps native placement inside each monitor work area", () => TestSta.RunAsync(() =>
        {
            var previousTaskbarController = MainWindow.TaskbarFullscreenController;
            var taskbarController = new FakeTaskbarFullscreenController();
            MainWindow.TaskbarFullscreenController = taskbarController;
            var window = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual };
            RemoveMainWindowAutomaticStartup(window);

            try
            {
                window.Show();
                var handle = new WindowInteropHelper(window).Handle;
                // The test startup helper removes SourceInitialized along with app startup.
                // Install the production message hook without starting tray or input hooks.
                SetMainWindowHandle(window);
                var hook = (HwndSourceHook)Delegate.CreateDelegate(typeof(HwndSourceHook), window,
                    typeof(MainWindow).GetMethod("WindowMessageHook", BindingFlags.Instance | BindingFlags.NonPublic)!);
                HwndSource.FromHwnd(handle)!.AddHook(hook);
                var maximizeButton = (Button)window.FindName("MaximizeRestoreButton");
                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    window.WindowState = WindowState.Normal;
                    NativeWindowTest.SetWindowBounds(handle,
                        screen.WorkingArea.Left + 40, screen.WorkingArea.Top + 40,
                        Math.Min(900, screen.WorkingArea.Width - 80),
                        Math.Min(620, screen.WorkingArea.Height - 80));
                    var restoreBounds = NativeWindowTest.GetWindowBounds(handle);
                    maximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpResponsiveLayout(window);
                    Assert.Equal(WindowState.Maximized, window.WindowState);
                    Assert.Equal(screen.WorkingArea, NativeWindowTest.GetWindowBounds(handle));

                    // Simulate the final oversized placement Windows can produce when
                    // maximizing a borderless window on a larger secondary monitor.
                    NativeWindowTest.SetWindowBounds(handle,
                        screen.Bounds.Left - 8, screen.Bounds.Top - 8,
                        screen.Bounds.Width + 16, screen.Bounds.Height + 16);
                    PumpResponsiveLayout(window);
                    Assert.Equal(screen.WorkingArea, NativeWindowTest.GetWindowBounds(handle));
                    Assert.Equal(WindowState.Maximized, window.WindowState);
                    Assert.Equal(0, taskbarController.Requests.Count);

                    maximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(WindowState.Normal, window.WindowState);
                    Assert.Equal(restoreBounds, NativeWindowTest.GetWindowBounds(handle));
                    NativeWindowTest.SetWindowBounds(handle,
                        screen.WorkingArea.Left + 60, screen.WorkingArea.Top + 60, 640, 400);
                    Assert.Equal(640, NativeWindowTest.GetWindowBounds(handle).Width);
                    Assert.Equal(400, NativeWindowTest.GetWindowBounds(handle).Height);
                }

                maximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    // Move while maximized, as with Win+Shift+Arrow: the destination
                    // monitor must win over the monitor the HWND occupied previously.
                    NativeWindowTest.SetWindowBounds(handle,
                        screen.WorkingArea.Left, screen.WorkingArea.Top,
                        screen.WorkingArea.Width, screen.WorkingArea.Height);
                    PumpResponsiveLayout(window);
                    Assert.Equal(screen.WorkingArea, NativeWindowTest.GetWindowBounds(handle));
                    Assert.Equal(WindowState.Maximized, window.WindowState);
                }

                var fullscreenScreen = System.Windows.Forms.Screen.FromHandle(handle);
                window.WindowState = WindowState.Minimized;
                PumpResponsiveLayout(window);
                Assert.Equal(WindowState.Minimized, window.WindowState);
                window.WindowState = WindowState.Maximized;
                PumpResponsiveLayout(window);
                Assert.Equal(fullscreenScreen.WorkingArea, NativeWindowTest.GetWindowBounds(handle));

                ToggleMainWindowFullscreen(window, "StreamOnly");
                PumpResponsiveLayout(window);
                Assert.Equal(fullscreenScreen.Bounds, NativeWindowTest.GetWindowBounds(handle));
                ExitMainWindowFullscreenIfActive(window);
                PumpResponsiveLayout(window);
                Assert.Equal(WindowState.Maximized, window.WindowState);
                Assert.Equal(fullscreenScreen.WorkingArea, NativeWindowTest.GetWindowBounds(handle));
                Assert.SequenceEqual(new[] { (handle, true), (handle, false) }, taskbarController.Requests.ToArray());
            }
            finally
            {
                window.Close();
                MainWindow.TaskbarFullscreenController = previousTaskbarController;
            }
        }))
    ];
}

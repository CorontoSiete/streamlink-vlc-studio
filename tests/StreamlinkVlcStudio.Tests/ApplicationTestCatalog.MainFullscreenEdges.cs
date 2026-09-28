using System.Windows.Interop;
using System.Windows.Shell;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingColor = System.Drawing.Color;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using DrawingRectangle = System.Drawing.Rectangle;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> MainFullscreenEdgeTests { get; } =
    [
        ("main stream fullscreen covers all monitor edges without showing the window underneath", MainFullscreenEdgePixels)
    ];

    private static Task MainFullscreenEdgePixels() => WithResponsiveWindowAsync(
        withVideo: true,
        async (window, viewModel) =>
        {
            var tab = viewModel.SelectedTab!;
            var surface = FindVisualDescendants<VideoSurface>(window)
                .Single(candidate => ReferenceEquals(candidate.Tag, tab));
            var windowChrome = WindowChrome.GetWindowChrome(window);
            Assert.NotNull(windowChrome);
            var originalCaptionHeight = windowChrome!.CaptionHeight;
            var originalResizeBorderThickness = windowChrome.ResizeBorderThickness;
            var originalGlassFrameThickness = windowChrome.GlassFrameThickness;
            var handle = new WindowInteropHelper(window).Handle;
            Assert.True(handle != IntPtr.Zero);
            Assert.True(surface.Handle != IntPtr.Zero);

            var monitor = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
            SetVideoAspectRatio(tab, monitor.Width / (double)monitor.Height);

            // Different colors make both kinds of leak identifiable: white is the
            // desktop window underneath; magenta is the WPF layer behind the video HWND.
            ((Grid)window.FindName("WorkspaceRoot")).Background = Brushes.Magenta;
            ((DockPanel)window.FindName("PlaybackHost")).Background = Brushes.Transparent;
            ((Grid)window.FindName("VideoAndReplayHost")).Background = Brushes.Transparent;
            ((Grid)window.FindName("VideoViewport")).Background = Brushes.Transparent;
            if (VisualTreeHelper.GetParent(surface) is Grid surfaceHost)
            {
                surfaceHost.Background = Brushes.Transparent;
            }
            PumpResponsiveLayout(window);

            var backdrop = new Window
            {
                Left = 100,
                Top = 100,
                Width = 640,
                Height = 360,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Background = Brushes.White,
                ShowInTaskbar = false
            };

            try
            {
                backdrop.Show();
                var backdropHandle = new WindowInteropHelper(backdrop).Handle;
                NativeWindowTest.SetWindowBounds(
                    backdropHandle,
                    monitor.Left,
                    monitor.Top,
                    monitor.Width,
                    monitor.Height);
                await NativeWindowTest.RequireForegroundAsync(
                    backdropHandle,
                    TimeSpan.FromSeconds(2),
                    "main fullscreen edge backdrop");

                ToggleMainWindowFullscreen(window, "StreamOnly");
                PumpResponsiveLayout(window);
                Assert.True(WindowChrome.GetWindowChrome(window) is null,
                    "WindowChrome must be detached while the main window is fullscreen.");
                await NativeWindowTest.RequireForegroundAsync(
                    handle,
                    TimeSpan.FromSeconds(2),
                    "main fullscreen edge capture");

                var bounds = NativeWindowTest.GetWindowBounds(handle);
                var videoBounds = NativeWindowTest.GetWindowBounds(surface.Handle);
                Console.WriteLine($"Main fullscreen outer={bounds}, video HWND={videoBounds}, monitor={monitor}.");
                Assert.Equal(monitor.Left, bounds.Left);
                Assert.Equal(monitor.Top, bounds.Top);
                Assert.Equal(monitor.Width, bounds.Width);
                Assert.Equal(monitor.Height, bounds.Height);
                Assert.Equal(bounds.Left, videoBounds.Left);
                Assert.Equal(bounds.Top, videoBounds.Top);
                Assert.Equal(bounds.Right, videoBounds.Right);
                Assert.Equal(bounds.Bottom, videoBounds.Bottom);

                using var frame = CaptureMainFullscreenWindow(bounds);
                var exposedLayers = MainFullscreenEdgePixels(frame)
                    .Where(sample => IsExposedWindowOrDesktop(sample.Color))
                    .ToArray();
                Console.WriteLine($"Main fullscreen active={window.IsActive}, foreground={NativeWindowTest.GetForegroundWindow()}, " +
                    $"center hit={NativeWindowTest.DescribeWindowAtPoint(monitor.Left + monitor.Width / 2, monitor.Top + monitor.Height / 2)}, " +
                    $"center pixel={frame.GetPixel(frame.Width / 2, frame.Height / 2)}.");
                Console.WriteLine($"Main fullscreen edge pixels: {string.Join(", ", MainFullscreenEdgePixels(frame))}.");
                Assert.True(
                    exposedLayers.Length == 0,
                    $"Fullscreen exposed the WPF background or the window underneath at {string.Join(", ", exposedLayers)}.");
            }
            finally
            {
                ExitMainWindowFullscreenIfActive(window);
                backdrop.Close();
            }

            Assert.True(ReferenceEquals(windowChrome, WindowChrome.GetWindowChrome(window)),
                "The original WindowChrome instance must be restored after fullscreen.");
            Assert.Equal(originalCaptionHeight, windowChrome.CaptionHeight);
            Assert.Equal(originalResizeBorderThickness, windowChrome.ResizeBorderThickness);
            Assert.Equal(originalGlassFrameThickness, windowChrome.GlassFrameThickness);
        });

    private static DrawingBitmap CaptureMainFullscreenWindow(DrawingRectangle bounds)
    {
        var frame = new DrawingBitmap(bounds.Width, bounds.Height, DrawingPixelFormat.Format32bppArgb);
        try
        {
            using var graphics = DrawingGraphics.FromImage(frame);
            graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    private static IEnumerable<(int X, int Y, DrawingColor Color)> MainFullscreenEdgePixels(DrawingBitmap frame)
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

    private static bool IsExposedWindowOrDesktop(DrawingColor edge) =>
        (edge.R >= 220 && edge.G >= 220 && edge.B >= 220) ||
        (edge.R >= 220 && edge.B >= 220 && edge.G <= 80);

    private static void SetVideoAspectRatio(StreamTabViewModel tab, double ratio)
    {
        var property = typeof(StreamTabViewModel).GetProperty(
            nameof(StreamTabViewModel.VideoAspectRatio),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        var setter = property!.GetSetMethod(nonPublic: true);
        Assert.NotNull(setter);
        setter!.Invoke(tab, [ratio]);
    }
}

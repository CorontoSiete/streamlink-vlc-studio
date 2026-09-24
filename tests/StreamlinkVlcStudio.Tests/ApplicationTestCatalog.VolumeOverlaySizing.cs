internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> VolumeOverlaySizingTests { get; } =
    [
        ("volume popup remains inside a tiny video while its window resizes", VolumePopupFitsResizedTargetAsync)
    ];

    private static Task VolumePopupFitsResizedTargetAsync() => TestSta.RunAsync(() =>
    {
        var target = new Border();
        var overlay = new VolumeOverlay();
        var content = new Grid();
        content.Children.Add(target);
        content.Children.Add(overlay);
        var window = new Window
        {
            Width = 640,
            Height = 360,
            Left = 160,
            Top = 160,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Content = content
        };
        var popup = (System.Windows.Controls.Primitives.Popup)overlay.FindName("Popup");
        var scale = (Viewbox)overlay.FindName("OverlayScale");
        var chrome = (Border)overlay.FindName("OverlayChrome");

        void FlushLayout()
        {
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        void AssertContained()
        {
            FlushLayout();
            Assert.True(popup.IsOpen, "Resizing a visible video must keep the volume indicator available.");
            var source = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(scale)!;
            Assert.NotNull(source);
            var popupBounds = NativeWindowTest.GetWindowBounds(source.Handle);
            var topLeft = target.PointToScreen(new Point());
            var bottomRight = target.PointToScreen(new Point(target.ActualWidth, target.ActualHeight));
            Assert.True(popupBounds.Left >= topLeft.X - 1 && popupBounds.Top >= topLeft.Y - 1 &&
                popupBounds.Right <= bottomRight.X + 1 && popupBounds.Bottom <= bottomRight.Y + 1,
                $"Volume popup {popupBounds} escaped target {topLeft} to {bottomRight}.");
        }

        try
        {
            window.Show();
            FlushLayout();
            overlay.Show(target, 75, muted: false);
            AssertContained();
            Assert.True(Math.Abs(scale.ActualWidth - chrome.ActualWidth) <= 1,
                "The normal-size volume indicator must retain its unscaled appearance.");

            // Keep the popup open throughout both resizes; a second Show call would mask
            // stale native desktop placement and target event subscription failures.
            window.Width = 96;
            window.Height = 52;
            AssertContained();
            Assert.True(scale.ActualWidth < chrome.ActualWidth,
                "The popup chrome must shrink when the video is narrower than the pill.");

            window.Width = 56;
            window.Height = 12;
            AssertContained();
            Assert.True(scale.ActualHeight <= target.ActualHeight);

            target.Visibility = Visibility.Collapsed;
            FlushLayout();
            Assert.Equal(false, popup.IsOpen);

            target.Visibility = Visibility.Visible;
            window.Width = 320;
            window.Height = 180;
            FlushLayout();
            overlay.Show(target, 50, muted: true);
            AssertContained();
            window.Left += 25;
            FlushLayout();
            Assert.True(!popup.IsOpen,
                "A desktop popup must not remain at the old position when its owner moves.");
        }
        finally
        {
            window.Close();
        }
    });
}

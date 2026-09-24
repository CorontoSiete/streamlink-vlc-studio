internal static partial class ApplicationTestCatalog
{
    private static IReadOnlyList<(string Name, Func<Task> Run)> ReplaySeekTrackTests =>
    [
        ("replay seek overlay maps pointer positions independently of pending thumb layout", ReplaySeekTrackPendingLayoutAsync),
        ("replay seek overlay track respects ranges directions and unavailable geometry", ReplaySeekTrackGeometryAsync)
    ];

    private static Task ReplaySeekTrackPendingLayoutAsync() => TestSta.RunOffscreenAsync(() =>
    {
        var overlay = new ReplaySeekOverlay();
        var chrome = (Border)overlay.FindName("OverlayChrome");
        var slider = (Slider)overlay.FindName("ReplaySeekSlider");
        slider.Maximum = 3600;

        foreach (var width in new[] { 180d, 640d, 960d })
        {
            chrome.Width = width;
            slider.Value = 900;
            chrome.Measure(new Size(width, double.PositiveInfinity));
            chrome.Arrange(new Rect(0, 0, width, chrome.DesiredSize.Height));
            chrome.UpdateLayout();
            var track = (Track)slider.Template.FindName("PART_Track", slider);
            var thumbWidth = track.Thumb.ActualWidth;
            var travel = track.ActualWidth - thumbWidth;
            Assert.True(travel > 0);

            // Drive the same conversion used by Slider's mouse-down handler and our
            // captured MouseMove handler without letting WPF arrange the new Value.
            // A stationary pointer must stay at one time even when more input arrives
            // before the next frame, and later points must not depend on earlier ones.
            foreach (var fraction in new[] { 0.7, 0.7, 0.2, 0.85, 0.05, 1.0, 0.0, 0.55 })
            {
                var point = new Point(thumbWidth / 2 + travel * fraction, track.ActualHeight / 2);
                var actual = track.ValueFromPoint(point);
                var expected = slider.Maximum * fraction;
                Assert.True(Math.Abs(actual - expected) < 0.000001,
                    $"At width {width}, pointer fraction {fraction} must map to {expected}s before layout; got {actual}s.");
                slider.SetCurrentValue(Slider.ValueProperty, actual);
                Assert.Equal(actual, track.Value);
            }

            Assert.Equal(slider.Minimum, track.ValueFromPoint(new Point(-100, 14)));
            Assert.Equal(slider.Maximum, track.ValueFromPoint(new Point(track.ActualWidth + 100, 14)));
        }

        return Task.CompletedTask;
    });

    private static Task ReplaySeekTrackGeometryAsync() => TestSta.RunOffscreenAsync(() =>
    {
        foreach (var orientation in new[] { Orientation.Horizontal, Orientation.Vertical })
            foreach (var reversed in new[] { false, true })
            {
                var track = new ReplaySeekTrack
                {
                    Minimum = 120,
                    Maximum = 920,
                    Value = 400,
                    Orientation = orientation,
                    IsDirectionReversed = reversed,
                    Thumb = new Thumb { Width = 12, Height = 12 }
                };
                Assert.Equal(400d, track.ValueFromPoint(new Point(50, 50)));
                var size = orientation == Orientation.Horizontal ? new Size(212, 28) : new Size(28, 212);
                track.Measure(size);
                track.Arrange(new Rect(size));
                var increasing = orientation == Orientation.Horizontal ? !reversed : reversed;
                foreach (var (coordinate, fraction) in new[] { (-20d, 0d), (6d, 0d), (56d, 0.25), (106d, 0.5), (206d, 1d), (250d, 1d) })
                {
                    var point = orientation == Orientation.Horizontal ? new Point(coordinate, 14) : new Point(14, coordinate);
                    var expected = 120 + 800 * (increasing ? fraction : 1 - fraction);
                    Assert.Equal(expected, track.ValueFromPoint(point));
                }
                Assert.Equal(400d, track.ValueFromPoint(new Point(double.NaN, double.NaN)));
                Assert.Equal(400d, track.ValueFromPoint(new Point(double.PositiveInfinity, double.PositiveInfinity)));
                track.Measure(new Size(12, 12));
                track.Arrange(new Rect(0, 0, 12, 12));
                Assert.Equal(400d, track.ValueFromPoint(new Point(6, 6)));
            }

        return Task.CompletedTask;
    });
}

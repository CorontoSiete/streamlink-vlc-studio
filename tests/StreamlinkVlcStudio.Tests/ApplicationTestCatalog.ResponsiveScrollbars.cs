internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> ResponsiveScrollbarTests =>
    [
        ("responsive scrollbars keep tracks and thumbs inside short viewports on both axes", () =>
            TestSta.RunOffscreenAsync(() =>
            {
                var owner = new MainWindow();
                RemoveMainWindowAutomaticStartup(owner);
                try
                {
                    foreach (var resource in new[] { "HomeScrollBarStyle", "DockedChatScrollBarStyle" })
                    {
                        var bar = new ScrollBar
                        {
                            Style = (Style)owner.Resources[resource],
                            Minimum = 0,
                            Maximum = 100,
                            ViewportSize = 10,
                            Value = 25
                        };
                        var host = new Grid();
                        host.Children.Add(bar);
                        // Reuse the same template so that changing orientation must also
                        // restore its sizing, direction, and commands when changed back.
                        foreach (var orientation in new[] { Orientation.Vertical, Orientation.Horizontal, Orientation.Vertical })
                        foreach (var length in new[] { 200d, 60d, 32d, 20d })
                        {
                            bar.Orientation = orientation;
                            var horizontal = orientation == Orientation.Horizontal;
                            var size = horizontal ? new Size(length, 30) : new Size(30, length);
                            host.Measure(size);
                            host.Arrange(new Rect(size));
                            host.UpdateLayout();

                            var track = (Track)bar.Template.FindName("PART_Track", bar);
                            Assert.NotNull(track);
                            Assert.Equal(orientation, track.Orientation);
                            Assert.Equal(!horizontal, track.IsDirectionReversed);
                            var margin = horizontal ? bar.Margin.Left + bar.Margin.Right : bar.Margin.Top + bar.Margin.Bottom;
                            AssertNear(length - margin, horizontal ? bar.ActualWidth : bar.ActualHeight);
                            AssertScrollbarPartContained(track, bar, $"{resource} {orientation} track at {length}");
                            AssertScrollbarPartContained(track.Thumb, track, $"{resource} {orientation} thumb at {length}");
                            Assert.True(track.Thumb.ActualWidth > 0 && track.Thumb.ActualHeight > 0,
                                $"{resource} {orientation} lost its thumb at length {length}.");
                            Assert.True(ReferenceEquals(track.DecreaseRepeatButton.Command,
                                horizontal ? ScrollBar.PageLeftCommand : ScrollBar.PageUpCommand));
                            Assert.True(ReferenceEquals(track.IncreaseRepeatButton.Command,
                                horizontal ? ScrollBar.PageRightCommand : ScrollBar.PageDownCommand));

                            if (length >= 32)
                            {
                                Assert.True(track.ValueFromDistance(horizontal ? 1 : 0, horizontal ? 0 : 1) > 0,
                                    $"Dragging {resource} {orientation} forward must increase the scroll offset.");
                            }
                        }
                    }
                }
                finally
                {
                    owner.Close();
                }
                return Task.CompletedTask;
            })),
        ("responsive scrollbar page buttons scroll the correct content axis", () =>
            TestSta.RunOffscreenAsync(() =>
            {
                var owner = new MainWindow();
                RemoveMainWindowAutomaticStartup(owner);
                try
                {
                    foreach (var resource in new[] { "HomeScrollBarStyle", "DockedChatScrollBarStyle" })
                    {
                        var viewer = new ScrollViewer
                        {
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                            Content = new Border { Width = 600, Height = 400 }
                        };
                        viewer.Resources[typeof(ScrollBar)] = new Style(typeof(ScrollBar), (Style)owner.Resources[resource]);
                        viewer.Measure(new Size(200, 100));
                        viewer.Arrange(new Rect(0, 0, 200, 100));
                        viewer.UpdateLayout();

                        foreach (var orientation in new[] { Orientation.Horizontal, Orientation.Vertical })
                        {
                            var horizontal = orientation == Orientation.Horizontal;
                            var partName = horizontal ? "PART_HorizontalScrollBar" : "PART_VerticalScrollBar";
                            var bar = (ScrollBar)viewer.Template.FindName(partName, viewer);
                            var track = (Track)bar.Template.FindName("PART_Track", bar);
                            var axisLength = horizontal ? bar.ActualWidth : bar.ActualHeight;
                            var margin = horizontal ? bar.Margin.Left + bar.Margin.Right : bar.Margin.Top + bar.Margin.Bottom;
                            AssertNear(horizontal ? viewer.ViewportWidth : viewer.ViewportHeight, axisLength + margin);

                            ((RoutedCommand)track.IncreaseRepeatButton.Command).Execute(null, track.IncreaseRepeatButton);
                            viewer.UpdateLayout();
                            Assert.True((horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset) > 0,
                                $"The {resource} {orientation} page button did not move the content.");
                            AssertNear(0, horizontal ? viewer.VerticalOffset : viewer.HorizontalOffset);

                            ((RoutedCommand)track.DecreaseRepeatButton.Command).Execute(null, track.DecreaseRepeatButton);
                            viewer.UpdateLayout();
                            AssertNear(0, viewer.HorizontalOffset);
                            AssertNear(0, viewer.VerticalOffset);
                        }
                    }
                }
                finally
                {
                    owner.Close();
                }
                return Task.CompletedTask;
            }))
    ];

    private static void AssertScrollbarPartContained(FrameworkElement part, FrameworkElement parent, string description)
    {
        var bounds = part.TransformToAncestor(parent).TransformBounds(new Rect(part.RenderSize));
        const double tolerance = 0.01;
        Assert.True(bounds.Left >= -tolerance && bounds.Top >= -tolerance &&
                    bounds.Right <= parent.ActualWidth + tolerance && bounds.Bottom <= parent.ActualHeight + tolerance,
            $"{description} bounds {bounds} exceed parent {parent.RenderSize}.");
    }
}

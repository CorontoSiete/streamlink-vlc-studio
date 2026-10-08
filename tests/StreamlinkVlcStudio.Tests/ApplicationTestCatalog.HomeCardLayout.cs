using System.Collections.ObjectModel;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> HomeCardLayoutTests { get; } =
    [
        ("home card layout: sparse category results retain normal poster sizes", SparseCategoryCardLayoutAsync),
        ("home card layout: sparse followed streamers retain normal preview sizes", () => SparseStreamerCardLayoutAsync(LiveStreamCardSource.Followed)),
        ("home card layout: sparse browse streamers retain normal preview sizes", () => SparseStreamerCardLayoutAsync(LiveStreamCardSource.Browse))
    ];

    private static Task SparseCategoryCardLayoutAsync() => WithStudioPolishWindowAsync((window, main, _) =>
    {
        var categories = Enumerable.Range(0, 18).Select(index => new BrowseCategoryViewModel(
            new BrowseCategory(index % 2 == 0 ? PlatformKind.Kick : PlatformKind.Twitch,
                index.ToString(CultureInfo.InvariantCulture), "Category " + index, "", [], 12000 + index),
            _ => Task.CompletedTask)).ToArray();
        foreach (var category in categories)
            main.BrowseCategories.Add(category);
        main.ShowBrowseHomePageCommand.Execute(null);

        AssertSparseHomeCardLayout(window, main, main.BrowseCategories, categories, 0.75, "categories");
        return Task.CompletedTask;
    });

    private static Task SparseStreamerCardLayoutAsync(LiveStreamCardSource source) => WithStudioPolishWindowAsync((window, main, _) =>
    {
        var streams = Enumerable.Range(0, 12).Select(index =>
        {
            var platform = index % 2 == 0 ? PlatformKind.Kick : PlatformKind.Twitch;
            var channel = "fixture" + index;
            return new LiveStreamCardViewModel(new LiveStreamCardData(
                source, StreamInputParser.Parse(channel, platform), platform,
                channel, "Studio channel " + index, "A stream title that wraps within its card",
                "Just Chatting", 12500 + index, "", "", DateTimeOffset.UtcNow, false, "en"),
                (_, _) => Task.CompletedTask);
        }).ToArray();
        var results = source == LiveStreamCardSource.Followed ? main.LiveFollowedChannels : main.BrowseStreams;
        foreach (var stream in streams)
            results.Add(stream);

        if (source == LiveStreamCardSource.Followed)
        {
            main.ShowFollowedHomePageCommand.Execute(null);
        }
        else
        {
            main.BrowseCategories.Add(new BrowseCategoryViewModel(
                new BrowseCategory(PlatformKind.Kick, "fixture", "Just Chatting", "", [], 12000),
                _ => Task.CompletedTask));
            main.ShowBrowseHomePageCommand.Execute(null);
            main.SetBrowseStreamsPageSelected(true);
        }

        AssertSparseHomeCardLayout(window, main, results, streams, 16.0 / 9.0, source.ToString());
        return Task.CompletedTask;
    });

    private static void AssertSparseHomeCardLayout<T>(MainWindow window, MainViewModel main,
        ObservableCollection<T> results, IReadOnlyList<T> fixtures, double aspectRatio, string pageName)
    {
        var root = (FrameworkElement)window.Content;
        var viewer = (ScrollViewer)window.FindName("HomeContentScrollViewer");
        // Keep the viewport width constant when the result count changes scrollbar demand.
        viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;

        foreach (var keepRightGap in new[] { false, true })
        {
            main.Settings.KeepHomeCardRightGap = keepRightGap;
            foreach (var size in new[]
            {
                new Size(1920, 1040), new Size(1320, 820), new Size(700, 640),
                new Size(360, 640), new Size(200, 320), new Size(1320, 820)
            })
            {
                SetResultCount(fixtures.Count);
                LayoutStudioPolishWindow(window, size);
                var items = FindVisualDescendants<ItemsControl>(root)
                    .Single(control => ReferenceEquals(control.ItemsSource, results));
                var populatedPanel = FindVisualDescendants<HomeCardWrapPanel>(items).Single();
                var referenceWidth = populatedPanel.Children[0].RenderSize.Width;
                var referencePreviewHeight = FindVisualDescendants<AspectRatioDecorator>(populatedPanel).First().ActualHeight;
                Assert.True(referenceWidth > 0 && referencePreviewHeight > 0,
                    $"The populated {pageName} grid must render at {size}.");

                // Include clearing/repopulation and an incomplete row after a full grid.
                foreach (var count in new[] { 1, 2, 0, 1, fixtures.Count, 3 })
                {
                    SetResultCount(count);
                    viewer.ScrollToTop();
                    LayoutStudioPolishWindow(window, size);
                    if (count == 0)
                    {
                        Assert.Equal(Visibility.Collapsed, items.Visibility);
                        continue;
                    }

                    var panel = FindVisualDescendants<HomeCardWrapPanel>(items).Single();
                    Assert.Equal(count, panel.Children.Count);
                    foreach (UIElement card in panel.Children)
                    {
                        Assert.True(Math.Abs(referenceWidth - card.RenderSize.Width) < 1,
                            $"{pageName} card width changed from {referenceWidth:0.###} to {card.RenderSize.Width:0.###} " +
                            $"with {count} results at {size}, KeepRightGap={keepRightGap}.");
                        var bounds = card.TransformToAncestor(panel).TransformBounds(new Rect(card.RenderSize));
                        Assert.True(bounds.Left >= -1 && bounds.Right <= panel.ActualWidth + 1 &&
                            bounds.Bottom <= panel.ActualHeight + 1,
                            $"{pageName} card {bounds} exceeds its panel {panel.RenderSize}.");
                    }

                    var previews = FindVisualDescendants<AspectRatioDecorator>(panel).ToArray();
                    Assert.Equal(count, previews.Length);
                    AssertNear(referencePreviewHeight, previews[0].ActualHeight, 1);
                    foreach (var preview in previews)
                        AssertNear(preview.ActualWidth, preview.ActualHeight * aspectRatio, 1);

                    if (count is 1 or 2)
                    {
                        ApplyStudioDesignImages(items, posters: aspectRatio < 1);
                        LayoutStudioPolishWindow(window, size);
                        SaveResponsiveWindowImage(window, $"sparse-{pageName}-{count}-{size.Width}-gap-{keepRightGap}");
                    }
                }
            }
        }

        void SetResultCount(int count)
        {
            results.Clear();
            for (var index = 0; index < count; index++)
                results.Add(fixtures[index]);
        }
    }
}

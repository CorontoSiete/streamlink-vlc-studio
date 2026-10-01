using StreamlinkVlcStudio.App.Wpf.Themes;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> StudioDesignTests { get; } =
    [
        ("studio design: page context, artwork and library commands remain usable across window sizes and palettes", StudioDesignLayoutAsync),
        ("studio design: text and thumbnail duration labels retain readable contrast in dark and light themes", StudioDesignContrastAsync)
    ];

    private static Task StudioDesignLayoutAsync() => WithStudioPolishWindowAsync(async (window, main, _) =>
    {
        AddStudioCardFixtures(main);
        var selectedCategory = "";
        var categoryNames = new[] { "Just Chatting", "Grand Theft Auto V", "Apex Legends", "World of Warcraft", "Call of Duty: Warzone" };
        for (var index = 0; index < categoryNames.Length; index++)
        {
            main.BrowseCategories.Add(new BrowseCategoryViewModel(new BrowseCategory(
                PlatformKind.Twitch, index.ToString(CultureInfo.InvariantCulture), categoryNames[index], "", [], 12000 + index),
                category => { selectedCategory = category.Id; return Task.CompletedTask; }));
        }

        var root = (FrameworkElement)window.Content;
        var viewer = (ScrollViewer)window.FindName("HomeContentScrollViewer");
        try
        {
            foreach (var theme in Enum.GetValues<AppTheme>())
            {
                ThemeManager.ApplyTheme(theme);
                main.ShowFollowedHomePageCommand.Execute(null);
                LayoutStudioPolishWindow(window, new Size(1320, 820));
                var context = (FrameworkElement)window.FindName("WorkspacePageContext");
                var search = (FrameworkElement)window.FindName("HomeSearchAnchor");
                var rail = (Border)window.FindName("LibraryRail");
                var caption = FindVisualDescendants<TextBlock>(rail).Single(text => text.Text == "YOUR LIBRARY");
                Assert.True(StudioContrast(((SolidColorBrush)caption.Foreground).Color,
                    ((SolidColorBrush)rail.Background).Color) >= 4.5,
                    $"Library section labels must remain readable in {theme}.");
                Assert.Equal(Visibility.Visible, context.Visibility);
                Assert.True(Math.Abs(context.TransformToAncestor(root).Transform(new Point()).X -
                    search.TransformToAncestor(root).Transform(new Point()).X) < 1,
                    "The page context and content must share an alignment edge.");
                var streams = FindVisualDescendants<ItemsControl>(root)
                    .Single(control => ReferenceEquals(control.ItemsSource, main.LiveFollowedChannels));
                ApplyStudioDesignImages(streams, posters: false);
                LayoutStudioPolishWindow(window, new Size(1320, 820));
                SaveResponsiveWindowImage(window, $"design-following-{theme}");

                main.ShowBrowseHomePageCommand.Execute(null);
                LayoutStudioPolishWindow(window, new Size(1320, 820));
                var categories = FindVisualDescendants<ItemsControl>(root)
                    .Single(control => ReferenceEquals(control.ItemsSource, main.BrowseCategories));
                ApplyStudioDesignImages(categories, posters: true);
                var first = FindVisualDescendants<Button>(categories).Single(button =>
                    ReferenceEquals(button.Command, main.BrowseCategories[0].SelectCommand));
                await ((AsyncRelayCommand)first.Command).ExecuteAsync();
                Assert.Equal("0", selectedCategory);
                foreach (var size in new[] { new Size(1320, 820), new Size(700, 640), new Size(360, 640) })
                {
                    LayoutStudioPolishWindow(window, size);
                    var artwork = FindVisualDescendants<AspectRatioDecorator>(categories).ToArray();
                    Assert.Equal(categoryNames.Length, artwork.Length);
                    Assert.True(artwork.All(poster => poster.ActualHeight > 0 &&
                        Math.Abs(poster.ActualWidth / poster.ActualHeight - 0.75) < 0.01),
                        "Category artwork must retain its portrait proportions.");
                    if (size.Width == 360)
                    {
                        var firstPoster = artwork[0].TransformToAncestor(root).Transform(new Point());
                        var secondPoster = artwork[1].TransformToAncestor(root).Transform(new Point());
                        var compactPanel = FindVisualDescendants<HomeCardWrapPanel>(categories).Single();
                        if (Math.Abs(firstPoster.Y - secondPoster.Y) >= 1)
                            SaveResponsiveWindowImage(window, $"design-discover-{theme}-compact-diagnostic");
                        Assert.True(Math.Abs(firstPoster.Y - secondPoster.Y) < 1,
                            $"Two category posters must fit side by side. Panel={compactPanel.RenderSize}, preferred={compactPanel.ItemWidth}, " +
                            $"viewer={viewer.RenderSize}, viewport={viewer.ViewportWidth}, first={firstPoster}, second={secondPoster}.");
                    }
                    viewer.ScrollToEnd();
                    LayoutStudioPolishWindow(window, size);
                    var panel = FindVisualDescendants<HomeCardWrapPanel>(categories).Single();
                    var last = panel.Children[panel.Children.Count - 1];
                    var bounds = last.TransformToAncestor(viewer).TransformBounds(new Rect(last.RenderSize));
                    Assert.True(bounds.Bottom > 0 && bounds.Bottom <= viewer.ActualHeight + 1,
                        $"The final category is unreachable at {size}: {bounds}.");
                    viewer.ScrollToTop();
                    LayoutStudioPolishWindow(window, size);
                    SaveResponsiveWindowImage(window, $"design-discover-{theme}-{size.Width}");
                }

                // Resize while Home is hidden: stale Home measurements must not pin the wide navigation.
                main.IsSettingsOpen = true;
                foreach (var size in new[] { new Size(1320, 820), new Size(700, 640), new Size(360, 640) })
                {
                    LayoutStudioPolishWindow(window, size);
                    var toolbar = (FrameworkElement)window.FindName("TopControlsBar");
                    var home = FindVisualDescendants<Button>(toolbar).Single(button => ReferenceEquals(button.Command, main.SelectHomeCommand));
                    var bounds = home.TransformToAncestor(toolbar).TransformBounds(new Rect(home.RenderSize));
                    Assert.True(bounds.Left >= 0 && bounds.Right <= toolbar.ActualWidth,
                        "Home must remain reachable while Settings changes between wide and compact layouts.");
                    SaveResponsiveWindowImage(window, $"design-settings-{theme}-{size.Width}");
                }
                main.IsSettingsOpen = false;
            }
        }
        finally
        {
            ThemeManager.ApplyTheme(AppTheme.Dark);
        }
    });

    private static Task StudioDesignContrastAsync() => WithStudioPolishWindowAsync((window, main, _) =>
    {
        main.TwitchVods.Add(new VodViewModel(new TwitchVodItem(
            "12345", "stream", "broadcaster", "fixture", "Studio channel", "A completed broadcast", "",
            "https://www.twitch.tv/videos/12345", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            TimeSpan.FromHours(2), 1234, TwitchVodTypeFilter.Archive), (_, _) => Task.CompletedTask));
        main.ShowTwitchVodsHomePageCommand.Execute(null);
        try
        {
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.ApplyTheme(theme);
                LayoutStudioPolishWindow(window, new Size(1320, 820));
                foreach (var key in new[] { "StudioTextColor", "StudioTextSecondaryColor", "StudioTextMutedColor", "StudioTextPlaceholderColor", "StudioTextDimColor" })
                {
                    var foreground = (Color)window.FindResource(key);
                    foreach (var surface in new[] { "StudioBaseColor", "StudioSurface0Color", "StudioSurfaceCardColor" })
                        Assert.True(StudioContrast(foreground, (Color)window.FindResource(surface)) >= 4.5,
                            $"{key} is not readable on {surface} in {theme}.");
                }
                var root = (FrameworkElement)window.Content;
                var duration = FindVisualDescendants<TextBlock>(root).Single(text =>
                    BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == nameof(VodViewModel.DurationText));
                var badge = (Border)duration.Parent;
                var overlay = ((SolidColorBrush)badge.Background).Color;
                var alpha = overlay.A / 255.0;
                var brightestBackground = Color.FromRgb(
                    (byte)Math.Round(overlay.R * alpha + 255 * (1 - alpha)),
                    (byte)Math.Round(overlay.G * alpha + 255 * (1 - alpha)),
                    (byte)Math.Round(overlay.B * alpha + 255 * (1 - alpha)));
                Assert.True(StudioContrast(((SolidColorBrush)duration.Foreground).Color, brightestBackground) >= 4.5,
                    "Duration text must remain readable over a bright thumbnail in either theme.");
                SaveResponsiveWindowImage(window, $"design-broadcasts-{theme}");
            }
        }
        finally
        {
            ThemeManager.ApplyTheme(AppTheme.Dark);
        }
        return Task.CompletedTask;
    });

    private static double StudioContrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Channel(byte value)
            {
                var normalized = value / 255.0;
                return normalized <= 0.04045 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }
        var one = Luminance(first);
        var two = Luminance(second);
        return (Math.Max(one, two) + 0.05) / (Math.Min(one, two) + 0.05);
    }

    private static void ApplyStudioDesignImages(ItemsControl items, bool posters)
    {
        var fixtureDirectory = Environment.GetEnvironmentVariable("SVS_UI_IMAGE_FIXTURES");
        var streams = new[] { "xqc", "summit1g", "rogue", "naughty", "albralelie", "genburten" };
        var index = 0;
        foreach (var preview in FindVisualDescendants<AspectRatioDecorator>(items))
        {
            var image = FindVisualDescendants<AnimatedEmoteImage>(preview).Single();
            var filename = posters ? $"poster-{index + 1}.jpg" : streams[index % streams.Length] + ".jpg";
            var path = string.IsNullOrWhiteSpace(fixtureDirectory) ? "" : System.IO.Path.Combine(fixtureDirectory, filename);
            if (File.Exists(path))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(System.IO.Path.GetFullPath(path));
                bitmap.EndInit();
                bitmap.Freeze();
                image.Source = bitmap;
            }
            else
            {
                var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                    new byte[] { 110, 80, 45, 255 }, 4);
                bitmap.Freeze();
                image.Source = bitmap;
            }
            image.Visibility = Visibility.Visible;
            index++;
        }
    }
}

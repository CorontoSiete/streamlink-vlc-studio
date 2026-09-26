internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> VodWatchProgressTests =>
    [
        ("VOD watch progress: thumbnail badges and bars render and update in dark theme", () => VodWatchProgressVisualsAsync(AppTheme.Dark)),
        ("VOD watch progress: thumbnail badges and bars render and update in light theme", () => VodWatchProgressVisualsAsync(AppTheme.Light))
    ];

    private static Task VodWatchProgressVisualsAsync(AppTheme theme) => TestSta.RunOffscreenAsync(async () =>
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var history = files.Create();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        await using var main = VodWatchProgressTestCatalog.CreateMain(history,
            dispatch: action => dispatcher.BeginInvoke(action));
        await main.SearchTwitchVodsCommand.ExecuteAsync();
        history.Remember(main.TwitchVods[0].Target, VodWatchProgressTestCatalog.Bookmark(7198, completed: true));
        history.Remember(main.TwitchVods[1].Target, VodWatchProgressTestCatalog.Bookmark(1800));
        StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(theme);
        var owner = new MainWindow { DataContext = main };
        RemoveMainWindowAutomaticStartup(owner);
        try
        {
            var cards = (ItemsControl)owner.FindName("VodCardsItemsControl");
            ((Panel)cards.Parent).Children.Remove(cards);
            var host = new Border { DataContext = main, Resources = owner.Resources, Child = cards };

            void Pump(double width = 1030)
            {
                dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                host.Measure(new Size(width, 400));
                host.Arrange(new Rect(0, 0, width, 400));
                host.UpdateLayout();
            }

            ProgressBar Bar(VodViewModel card) => FindVisualDescendants<ProgressBar>(host).Single(bar =>
                ReferenceEquals(bar.DataContext, card) &&
                System.Windows.Automation.AutomationProperties.GetName(bar) == "VOD watch progress");
            Border Badge(VodViewModel card) => FindVisualDescendants<Border>(host).Single(badge =>
                ReferenceEquals(badge.DataContext, card) &&
                System.Windows.Automation.AutomationProperties.GetName(badge) == "Watched VOD");

            host.Background = WpfVisualTest.PaletteBrush(owner, "StudioSurface0Brush");
            Pump();
            Assert.Equal(Visibility.Visible, Badge(main.TwitchVods[0]).Visibility);
            Assert.Equal(Visibility.Collapsed, Bar(main.TwitchVods[0]).Visibility);
            Assert.Equal(Visibility.Collapsed, Badge(main.TwitchVods[1]).Visibility);
            Assert.Equal(Visibility.Visible, Bar(main.TwitchVods[1]).Visibility);
            Assert.Equal(Visibility.Collapsed, Badge(main.TwitchVods[2]).Visibility);
            Assert.Equal(Visibility.Collapsed, Bar(main.TwitchVods[2]).Visibility);
            var bar = Bar(main.TwitchVods[1]);
            Assert.Equal(25d, bar.Value);
            Assert.Contains("25%", bar.ToolTip.ToString()!);
            AssertBarGeometry(bar, main.TwitchVods[1], 0.25);
            var directory = Environment.GetEnvironmentVariable("SVS_TEST_ARTIFACT_DIR");
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(WpfVisualTest.Render(host)));
                using var output = File.Create(Path.Combine(directory, $"vod-watch-progress-{theme}.png"));
                encoder.Save(output);
            }

            history.Remember(main.TwitchVods[1].Target, VodWatchProgressTestCatalog.Bookmark(5400));
            Pump(690);
            Assert.Equal(75d, Bar(main.TwitchVods[1]).Value);
            AssertBarGeometry(Bar(main.TwitchVods[1]), main.TwitchVods[1], 0.75);
            history.Remember(main.TwitchVods[1].Target, VodWatchProgressTestCatalog.Bookmark(7198, completed: true));
            Pump(690);
            Assert.Equal(Visibility.Visible, Badge(main.TwitchVods[1]).Visibility);
            Assert.Equal(Visibility.Collapsed, Bar(main.TwitchVods[1]).Visibility);

            void AssertBarGeometry(ProgressBar bar, VodViewModel card, double fraction)
            {
                var track = (FrameworkElement)bar.Template.FindName("PART_Track", bar);
                var fill = (FrameworkElement)bar.Template.FindName("PART_Indicator", bar);
                Assert.True(track.ActualWidth > 200);
                Assert.True(Math.Abs(fill.ActualWidth - track.ActualWidth * fraction) < 1);
                var thumbnail = FindVisualDescendants<RoundedClipBorder>(host)
                    .Single(border => ReferenceEquals(border.DataContext, card));
                var bounds = bar.TransformToAncestor(thumbnail).TransformBounds(new Rect(bar.RenderSize));
                Assert.True(Math.Abs(bounds.Bottom - thumbnail.ActualHeight) < 1);
                Assert.True(Math.Abs(bounds.Width - thumbnail.ActualWidth) < 1);
            }
        }
        finally
        {
            owner.Close();
            StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Dark);
        }
    });
}

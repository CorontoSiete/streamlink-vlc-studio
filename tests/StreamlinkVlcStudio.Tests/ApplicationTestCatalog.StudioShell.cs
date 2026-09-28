internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> StudioShellTests { get; } =
    [
        ("studio shell: navigation, account entry, settings and palettes render offscreen", () =>
            TestSta.RunOffscreenAsync(async () =>
            {
                var settings = new AppSettings();
                settings.Chat.ConnectAutomatically = false;
                await using var model = TestViewModels.CreateMain(
                    settings, new FakeSettingsService(settings), new FakeStreamlinkService(),
                    new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
                    new MemoryLogger(), action => action());
                var window = new MainWindow { DataContext = model };
                RemoveMainWindowAutomaticStartup(window);
                var root = (FrameworkElement)window.Content;

                void Layout(double width = 1320, double height = 820)
                {
                    root.Measure(new Size(width, height));
                    root.Arrange(new Rect(0, 0, width, height));
                    root.UpdateLayout();
                    root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    root.UpdateLayout();
                }

                try
                {
                    Layout();
                    SaveResponsiveWindowImage(window, "studio-home-empty");
                    var rail = (FrameworkElement)window.FindName("LibraryRail");
                    var compact = (ToolBar)window.FindName("HomeNavigation");
                    Assert.Equal(Visibility.Visible, rail.Visibility);
                    Assert.Equal(Visibility.Collapsed, compact.Visibility);
                    var discover = FindVisualDescendants<Button>(rail).Single(button => Equals(button.Content, "Discover"));
                    discover.Command.Execute(null);
                    Layout();
                    Assert.True(model.IsBrowseHomePageSelected);
                    WpfVisualTest.AssertSolidBrushColor(
                        WpfVisualTest.PaletteColor(window, "StudioAccentPressedColor"), discover.Background);
                    SaveResponsiveWindowImage(window, "studio-discover");

                    model.ShowTwitchVodsHomePageCommand.Execute(null);
                    Layout();
                    Assert.True(model.IsTwitchVodsHomePageSelected);
                    SaveResponsiveWindowImage(window, "studio-broadcasts");
                    model.ShowRecentHomePageCommand.Execute(null);
                    Layout();
                    Assert.True(model.IsRecentHomePageSelected);
                    SaveResponsiveWindowImage(window, "studio-recent");

                    model.ShowFollowedHomePageCommand.Execute(null);
                    Layout();
                    var accounts = FindVisualDescendants<Button>(root).Single(button => Equals(button.Content, "Connect accounts"));
                    accounts.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Layout();
                    Assert.True(model.IsSettingsOpen);
                    Assert.Equal(SettingsCategory.Accounts, model.SelectedSettingsCategory);
                    foreach (var category in Enum.GetValues<SettingsCategory>())
                    {
                        model.SelectedSettingsCategory = category;
                        Layout();
                        Assert.True(((FrameworkElement)window.FindName(category + "SettingsPage")).ActualHeight > 0);
                        SaveResponsiveWindowImage(window, "studio-settings-" + category);
                    }

                    model.IsSettingsOpen = false;
                    AddStudioCardFixtures(model);
                    foreach (var theme in Enum.GetValues<AppTheme>())
                    {
                        StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(theme);
                        Layout();
                        var liveCards = FindVisualDescendants<ItemsControl>(root).Single(control =>
                            ReferenceEquals(control.ItemsSource, model.LiveFollowedChannels));
                        var cardPanel = FindVisualDescendants<HomeCardWrapPanel>(liveCards).Single();
                        Assert.Equal(6, cardPanel.Children.Count);
                        var lastCard = cardPanel.Children[5];
                        var lastCardBounds = lastCard.TransformToAncestor(cardPanel).TransformBounds(new Rect(lastCard.RenderSize));
                        Assert.True(lastCardBounds.Bottom <= cardPanel.ActualHeight + 1,
                            $"The last row extends beyond the scrollable card panel: {lastCardBounds}, panel height {cardPanel.ActualHeight}.");
                        WpfVisualTest.AssertSolidBrushColor(
                            WpfVisualTest.PaletteColor(window, "StudioSurface0Color"), ((Border)rail).Background);
                        SaveResponsiveWindowImage(window, "studio-populated-" + theme);
                    }
                    StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Dark);
                    Layout(700, 640);
                    Assert.Equal(Visibility.Collapsed, rail.Visibility);
                    Assert.Equal(Visibility.Visible, compact.Visibility);
                    SaveResponsiveWindowImage(window, "studio-compact");
                    Layout();
                    Assert.Equal(Visibility.Visible, rail.Visibility);
                    Assert.Equal(Visibility.Collapsed, compact.Visibility);

                    StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Dark);
                    var setup = new SetupWizardWindow(settings, new FakeSettingsService(settings), new MemoryLogger());
                    try
                    {
                        var setupRoot = (FrameworkElement)setup.Content;
                        setupRoot.Measure(new Size(980, 680));
                        setupRoot.Arrange(new Rect(0, 0, 980, 680));
                        setupRoot.UpdateLayout();
                        SaveResponsiveWindowImage(setup, "studio-setup");
                        Assert.True(FindVisualDescendants<Button>(setupRoot).Any(button => Equals(button.Content, "Next")));
                    }
                    finally
                    {
                        setup.Close();
                    }
                }
                finally
                {
                    window.Close();
                    StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Dark);
                }
            }))
    ];

    private static void AddStudioCardFixtures(MainViewModel model)
    {
        for (var index = 0; index < 6; index++)
        {
            var platform = index % 2 == 0 ? PlatformKind.Twitch : PlatformKind.Kick;
            var channel = "fixture" + index;
            model.LiveFollowedChannels.Add(new LiveStreamCardViewModel(new LiveStreamCardData(
                LiveStreamCardSource.Followed, StreamInputParser.Parse(channel, platform), platform,
                channel, "Studio channel " + (index + 1), "A long stream title that should stay neatly within its card",
                "Just Chatting", 12500 + index, "", "", DateTimeOffset.UtcNow, false, "en"),
                (_, _) => Task.CompletedTask));
        }
    }
}

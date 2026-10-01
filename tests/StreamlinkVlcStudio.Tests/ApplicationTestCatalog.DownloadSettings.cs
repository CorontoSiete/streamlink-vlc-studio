using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Threading;

internal static partial class ApplicationTestCatalog
{
    private static Task DownloadSettingsUiAsync(AppTheme theme) => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(PlatformKind.Twitch);
        var item = await fixture.DownloadAsync();
        var settings = VodResumeTestCatalog.Settings();
        settings.Theme = theme;
        settings.Downloads.Quality = "720p";
        settings.Downloads.BandwidthLimitMegabytesPerSecond = 1.25;
        var service = new JsonSettingsService(Path.Combine(fixture.Root, "settings.json"));
        var dispatcher = Dispatcher.CurrentDispatcher;
        await using var model = new MainViewModel(new MainViewModelDependencies
        {
            Settings = settings,
            SettingsService = service,
            StreamlinkService = fixture.Resolver,
            PlaybackFactory = new FakePlaybackEngineFactory(),
            ChatFactory = new FakeChatClientFactory(),
            Logger = new MemoryLogger(),
            Dispatch = action => dispatcher.BeginInvoke(action),
            VodDownloadService = fixture.Service
        });
        model.Initialize();
        await TestWait.UntilAsync(() => model.VodDownloads.Count == 1, TimeSpan.FromSeconds(3));
        StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(theme);
        var window = new MainWindow { DataContext = model };
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, model);
        try
        {
            model.ShowTwitchVodsHomePageCommand.Execute(null);
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            var toolbar = (ToolBar)window.FindName("PlaybackActionsToolBar");
            var downloadSettingsButtons = toolbar.Items.OfType<Button>()
                .Where(button => ReferenceEquals(button.Command, model.ShowDownloadsSettingsCommand)).ToArray();
            Assert.Equal(1, downloadSettingsButtons.Length);
            var downloadSettingsButton = downloadSettingsButtons[0];
            var settingsButton = toolbar.Items.OfType<Button>()
                .Single(button => ReferenceEquals(button.Command, model.ToggleSettingsCommand));
            Assert.Equal(toolbar.Items.IndexOf(settingsButton) - 1, toolbar.Items.IndexOf(downloadSettingsButton));
            Assert.Equal("Download settings", downloadSettingsButton.Content);
            Assert.Equal("Open VOD download settings", AutomationProperties.GetName(downloadSettingsButton));
            Assert.Equal(1, FindVisualDescendants<Button>((FrameworkElement)window.FindName("HomeViewport"))
                .Count(button => AutomationProperties.GetName(button) == "Open VOD download settings"));
            var root = (FrameworkElement)window.Content;
            foreach (var size in new[] { new Size(1320, 820), new Size(440, 820) })
            {
                model.IsSettingsOpen = false;
                model.SelectedSettingsCategory = SettingsCategory.General;
                LayoutStudioPolishWindow(window, size);
                Assert.Equal(Visibility.Visible, downloadSettingsButton.Visibility);
                Assert.True(downloadSettingsButton.IsEnabled);
                Assert.True(downloadSettingsButton.ActualWidth > 0 && downloadSettingsButton.ActualHeight > 0);
                Assert.True(!ToolBar.GetIsOverflowItem(downloadSettingsButton));
                var downloadBounds = downloadSettingsButton.TransformToAncestor(root)
                    .TransformBounds(new Rect(downloadSettingsButton.RenderSize));
                var settingsBounds = settingsButton.TransformToAncestor(root)
                    .TransformBounds(new Rect(settingsButton.RenderSize));
                Assert.True(downloadBounds.Right <= settingsBounds.Left);
                Assert.True(new Rect(root.RenderSize).Contains(downloadBounds));
                Assert.True(new Rect(root.RenderSize).Contains(settingsBounds));
                SaveResponsiveWindowImage(window, $"past-broadcasts-top-actions-{theme}-{size.Width}");
                var peer = new ButtonAutomationPeer(downloadSettingsButton);
                var provider = (IInvokeProvider?)peer.GetPattern(PatternInterface.Invoke);
                Assert.NotNull(provider);
                provider!.Invoke();
                await TestWait.UntilAsync(() => model.IsSettingsOpen && model.IsDownloadsSettingsSelected,
                    TimeSpan.FromSeconds(3));
            }
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            var page = (FrameworkElement)window.FindName("DownloadsSettingsPage");
            Assert.Equal(Visibility.Visible, page.Visibility);
            var quality = (ComboBox)window.FindName("VodDownloadQualityComboBox");
            Assert.Equal("720p", quality.SelectedValue);
            var playbackQuality = model.SelectedQuality;
            quality.SetCurrentValue(ComboBox.SelectedValueProperty, "480p");
            Assert.Equal("480p", settings.Downloads.Quality);
            Assert.Equal(playbackQuality, model.SelectedQuality);
            var limit = (TextBox)window.FindName("VodDownloadBandwidthLimitTextBox");
            limit.SetCurrentValue(TextBox.TextProperty, "0.75");
            limit.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Assert.Equal(750_000L, settings.Downloads.BandwidthLimitBytesPerSecond);
            foreach (var invalid in new[] { "-1", "NaN", "Infinity", "not a number" })
            {
                limit.SetCurrentValue(TextBox.TextProperty, invalid);
                limit.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Assert.True(Validation.GetHasError(limit));
                Assert.Equal(0.75, settings.Downloads.BandwidthLimitMegabytesPerSecond);
            }
            limit.SetCurrentValue(TextBox.TextProperty, "0");
            limit.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Assert.True(!Validation.GetHasError(limit));
            Assert.Equal(0L, settings.Downloads.BandwidthLimitBytesPerSecond);
            limit.SetCurrentValue(TextBox.TextProperty, "1.25");
            limit.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            var directory = Path.Combine(fixture.Root, "selected download folder 日本語");
            await model.ChangeDownloadDirectoryAsync(directory);
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            Assert.Equal(directory, ((TextBox)window.FindName("VodDownloadDirectoryTextBox")).Text);
            Assert.True(model.CanChangeDownloadDirectory);
            Assert.True(FindVisualDescendants<Button>(page).Single(button => Equals(button.Content, "Browse...")).IsEnabled);
            Assert.Equal(1, model.VodDownloads.Count);
            Assert.True(File.Exists(item.LocalMediaPath));
            Assert.Equal(1, FindVisualDescendants<ComboBox>((FrameworkElement)window.Content)
                .Count(control => AutomationProperties.GetName(control) == "VOD download quality"));
            Assert.True(!FindVisualDescendants<ComboBox>(FindVisualDescendants<VodDownloadsView>((FrameworkElement)window.Content).Single()).Any());
            SaveResponsiveWindowImage(window, $"download-settings-{theme}-wide");
            LayoutStudioPolishWindow(window, new Size(440, 820));
            var categories = (ComboBox)window.FindName("CompactSettingsCategorySelector");
            Assert.True(categories.Items.Contains(SettingsCategory.Downloads));
            Assert.Equal(SettingsCategory.Downloads, categories.SelectedItem);
            Assert.True(limit.ActualWidth > 0 && quality.ActualWidth > 0);
            SaveResponsiveWindowImage(window, $"download-settings-{theme}-compact");
            await model.SaveSettingsCommand.ExecuteAsync();
            var restored = await service.LoadAsync();
            Assert.Equal("480p", restored.Downloads.Quality);
            Assert.Equal(1.25, restored.Downloads.BandwidthLimitMegabytesPerSecond);
            Assert.Equal(directory, restored.Downloads.Directory);
            Assert.SequenceEqual(new[] { fixture.Library }, restored.Downloads.PreviousDirectories);
            Assert.Equal(settings.DefaultQuality, restored.DefaultQuality);
        }
        finally
        {
            window.Close();
            StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Dark);
        }
    });

    private static Task DownloadSettingsButtonVisibilityAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new NavigationFixture();
        var model = fixture.Main;
        var tab = fixture.AddTab("downloadsettings");
        var window = new MainWindow { DataContext = model };
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, model);
        try
        {
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            var toolbar = (ToolBar)window.FindName("PlaybackActionsToolBar");
            var button = toolbar.Items.OfType<Button>()
                .Single(control => ReferenceEquals(control.Command, model.ShowDownloadsSettingsCommand));
            var settingsButton = toolbar.Items.OfType<Button>()
                .Single(control => ReferenceEquals(control.Command, model.ToggleSettingsCommand));
            foreach (var size in new[] { new Size(1320, 820), new Size(440, 820) })
            {
                void AssertVisibility(Visibility expected)
                {
                    LayoutStudioPolishWindow(window, size);
                    Assert.Equal(expected, button.Visibility);
                    Assert.Equal(Visibility.Visible, settingsButton.Visibility);
                }

                model.SelectHomeCommand.Execute(null);
                foreach (var command in new[]
                    { model.ShowFollowedHomePageCommand, model.ShowBrowseHomePageCommand, model.ShowRecentHomePageCommand })
                {
                    command.Execute(null);
                    AssertVisibility(Visibility.Collapsed);
                }
                foreach (var command in new[] { model.ShowTwitchVodsHomePageCommand, model.ShowDownloadsHomePageCommand })
                {
                    command.Execute(null);
                    AssertVisibility(Visibility.Visible);
                    model.ToggleSettingsCommand.Execute(null);
                    AssertVisibility(Visibility.Collapsed);
                    model.ToggleSettingsCommand.Execute(null);
                    AssertVisibility(Visibility.Visible);
                    model.SelectedTab = tab;
                    Assert.True(model.IsTwitchVodsHomePageSelected || model.IsDownloadsHomePageSelected);
                    AssertVisibility(Visibility.Collapsed);
                    model.SelectHomeCommand.Execute(null);
                    AssertVisibility(Visibility.Visible);
                }
            }
        }
        finally
        {
            window.Close();
        }
    });

    private static Task DownloadSettingsReplacementAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new CardDownloadTestService();
        await using var model = CreateDownloadCardsMain(service, [], action => action());
        var original = model.Settings.Downloads;
        original.BandwidthLimitMegabytesPerSecond = 1.5;
        Assert.Equal(1_500_000L, service.BandwidthLimit);
        model.Settings.Downloads = new DownloadSettings { Quality = "720p", BandwidthLimitMegabytesPerSecond = 0.25 };
        Assert.Equal("720p", model.SelectedVodDownloadQuality);
        Assert.Equal(250_000L, service.BandwidthLimit);
        original.BandwidthLimitMegabytesPerSecond = 9;
        original.Quality = "worst";
        Assert.Equal("720p", model.SelectedVodDownloadQuality);
        Assert.Equal(250_000L, service.BandwidthLimit);
        await model.DisposeAsync();
        model.Settings.Downloads.BandwidthLimitMegabytesPerSecond = 10;
        Assert.Equal(250_000L, service.BandwidthLimit);
    });
}

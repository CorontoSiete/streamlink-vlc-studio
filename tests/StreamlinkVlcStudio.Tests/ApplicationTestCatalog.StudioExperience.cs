using System.Windows.Threading;
using StreamlinkVlcStudio.App.Wpf.Themes;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> StudioExperienceTests { get; } =
    [
        ("studio experience: toolbar accents follow palette changes and stream card menus preserve background opening", StudioToolbarAndCardActionsAsync),
        ("studio experience: picture-in-picture title chrome follows every palette", StudioPictureInPicturePalettesAsync),
        ("studio experience: download URL errors are visible, clear on editing and do not overwrite newer input", DownloadInputFeedbackAsync),
        ("studio experience: Enter queues a download once and the empty library updates immediately", DownloadEnterAsync)
    ];

    private static Task StudioToolbarAndCardActionsAsync() => WithStudioPolishWindowAsync(async (window, main, _) =>
    {
        var root = (FrameworkElement)window.Content;
        var home = FindVisualDescendants<Button>(root).Single(button => ReferenceEquals(button.Command, main.SelectHomeCommand));
        var openedInBackground = false;
        var target = StreamInputParser.Parse("menufixture", PlatformKind.Twitch);
        var stream = new LiveStreamCardViewModel(new LiveStreamCardData(LiveStreamCardSource.Followed, target,
            PlatformKind.Twitch, target.Channel, "Menu Fixture", "A broadcast", "Just Chatting", 10, "", "", null, false, "en"),
            (_, background) => { openedInBackground = background; return Task.CompletedTask; });
        main.LiveFollowedChannels.Add(stream);
        try
        {
            var following = FindVisualDescendants<Button>(root).Single(button =>
                ReferenceEquals(button.Command, main.ShowFollowedHomePageCommand) && Equals(button.Content, "Following"));
            var liveBadge = (Border)following.Template.FindName("RailBadge", following);
            Assert.True(main.HasLiveFollowedChannels);
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            Assert.Equal(Visibility.Visible, liveBadge.Visibility);
            Assert.Equal("1", ((TextBlock)following.Template.FindName("RailBadgeText", following)).Text);
            foreach (var theme in Enum.GetValues<AppTheme>())
            {
                ThemeManager.ApplyTheme(theme);
                LayoutStudioPolishWindow(window, new Size(1320, 820));
                WpfVisualTest.AssertSolidBrushColor(WpfVisualTest.PaletteColor(window, "StudioAccentColor"), home.BorderBrush);
            }
            var card = FindVisualDescendants<Button>(root).Single(button => ReferenceEquals(button.Command, stream.OpenCommand));
            var menu = card.ContextMenu!;
            menu.PlacementTarget = card;
            menu.DataContext = card.DataContext;
            var keepBrowsing = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Open and keep browsing"));
            Assert.True(ReferenceEquals(stream.OpenAndStayOnHomeCommand, keepBrowsing.Command));
            await ((AsyncRelayCommand)keepBrowsing.Command).ExecuteAsync();
            Assert.True(openedInBackground && main.IsHomeSelected);
            LayoutStudioPolishWindow(window, new Size(440, 640));
            Assert.True(home.Visibility == Visibility.Visible && home.ActualWidth > 0);
            var toolbar = (FrameworkElement)window.FindName("TopControlsBar");
            var bounds = home.TransformToAncestor(toolbar).TransformBounds(new Rect(home.RenderSize));
            Assert.True(bounds.Left >= 0 && bounds.Right <= toolbar.ActualWidth);
            SaveResponsiveWindowImage(window, "studio-compact-navigation");
        }
        finally
        {
            ThemeManager.ApplyTheme(AppTheme.Dark);
        }
    });

    private static Task StudioPictureInPicturePalettesAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new NavigationFixture();
        var tab = fixture.AddTab("palettefixture");
        var window = new DetachedVideoWindow([tab], tab);
        var root = (FrameworkElement)window.Content;
        var title = (Border)window.FindName("TitleBar");
        try
        {
            foreach (var theme in Enum.GetValues<AppTheme>())
            {
                ThemeManager.ApplyTheme(theme);
                root.Measure(new Size(520, 327));
                root.Arrange(new Rect(0, 0, 520, 327));
                root.UpdateLayout();
                root.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                WpfVisualTest.AssertSolidBrushColor(WpfVisualTest.PaletteColor(window, "StudioSurface1Color"), title.Background);
                WpfVisualTest.AssertSolidBrushColor(WpfVisualTest.PaletteColor(window, "StudioBorderColor"), title.BorderBrush);
                var indicator = FindVisualDescendants<System.Windows.Shapes.Ellipse>(title).Single();
                WpfVisualTest.AssertSolidBrushColor(WpfVisualTest.PaletteColor(window, "StudioAccentColor"), indicator.Fill);
                SaveResponsiveWindowImage(window, $"picture-in-picture-{theme}");
            }
        }
        finally
        {
            window.Close();
            ThemeManager.ApplyTheme(AppTheme.Dark);
        }
    });

    private static Task DownloadInputFeedbackAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new CardDownloadTestService();
        await using var main = CreateDownloadCardsMain(service, [], action => action());
        var window = new MainWindow { DataContext = main };
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, main);
        try
        {
            main.ShowDownloadsHomePageCommand.Execute(null);
            main.VodDownloadUrl = "https://www.twitch.tv/livefixture";
            await Assert.ThrowsAsync<ArgumentException>(() => main.DownloadVodUrlCommand.ExecuteAsync());
            Assert.True(main.HasDownloadUrlError && !main.DownloadUrlError.Contains("Parameter", StringComparison.Ordinal));
            Assert.Equal(0, service.Requests.Count);
            foreach (var size in new[] { new Size(1320, 820), new Size(440, 640) })
            {
                LayoutStudioPolishWindow(window, size);
                var view = FindVisualDescendants<VodDownloadsView>((FrameworkElement)window.Content).Single();
                var error = (TextBlock)view.FindName("DownloadUrlErrorText");
                Assert.True(error.Visibility == Visibility.Visible && error.ActualHeight > 0);
                Assert.Equal(main.DownloadUrlError, error.Text);
                SaveResponsiveWindowImage(window, $"download-input-error-{size.Width}");
            }
            main.VodDownloadUrl = "https://www.twitch.tv/videos/12345";
            Assert.Equal(false, main.HasDownloadUrlError);
            service.EnqueueGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = main.DownloadVodUrlCommand.ExecuteAsync();
            await TestWait.UntilAsync(() => service.Requests.Count == 1, TimeSpan.FromSeconds(2));
            main.VodDownloadUrl = "https://www.twitch.tv/videos/67890";
            service.EnqueueFailure = new IOException("The first request could not be queued.");
            service.EnqueueGate.SetResult();
            await Assert.ThrowsAsync<IOException>(() => pending);
            Assert.True(!main.HasDownloadUrlError && main.VodDownloadUrl.EndsWith("67890", StringComparison.Ordinal));
        }
        finally
        {
            service.EnqueueGate?.TrySetResult();
            window.Close();
        }
    });

    private static Task DownloadEnterAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new CardDownloadTestService { EnqueueGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var main = CreateDownloadCardsMain(service, [], action => action());
        var window = new MainWindow { DataContext = main };
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, main);
        try
        {
            main.ShowDownloadsHomePageCommand.Execute(null);
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            var view = FindVisualDescendants<VodDownloadsView>((FrameworkElement)window.Content).Single();
            var empty = (FrameworkElement)view.FindName("DownloadsEmptyState");
            Assert.Equal(Visibility.Visible, empty.Visibility);
            main.VodDownloadUrl = "https://www.twitch.tv/videos/12345";
            var input = (TextBox)view.FindName("VodDownloadUrlTextBox");
            var source = new SkipHotkeyPresentationSource { RootVisual = window };
            for (var count = 0; count < 2; count++)
            {
                input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                { RoutedEvent = Keyboard.KeyDownEvent });
            }
            Assert.Equal(1, service.Requests.Count);
            service.EnqueueGate.SetResult();
            await TestWait.UntilAsync(() => main.VodDownloads.Count == 1 && main.VodDownloadUrl == "", TimeSpan.FromSeconds(2));
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            Assert.Equal(Visibility.Collapsed, empty.Visibility);
            var downloadsButton = FindVisualDescendants<Button>((FrameworkElement)window.Content).Single(button =>
                ReferenceEquals(button.Command, main.ShowDownloadsHomePageCommand) && Equals(button.Style, window.FindResource("StudioRailDownloads")));
            Assert.Equal(Visibility.Visible, ((Border)downloadsButton.Template.FindName("RailBadge", downloadsButton)).Visibility);
            Assert.Equal("1", ((TextBlock)downloadsButton.Template.FindName("RailBadgeText", downloadsButton)).Text);
            SaveResponsiveWindowImage(window, "download-enter-queued");
        }
        finally
        {
            service.EnqueueGate.TrySetResult();
            window.Close();
        }
    });
}

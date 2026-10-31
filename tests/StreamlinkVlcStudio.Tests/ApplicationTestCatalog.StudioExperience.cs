using System.Windows.Threading;
using StreamlinkVlcStudio.App.Wpf.Themes;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> StudioExperienceTests { get; } =
    [
        ("studio experience: toolbar accents follow palette changes and stream card menus preserve background opening", StudioToolbarAndCardActionsAsync),
        ("studio experience: Following stream menus open channel VODs on Twitch and Kick", () => ChannelVodsFromMenuAsync(fromSearch: false)),
        ("studio experience: search result menus open channel VODs on Twitch and Kick", () => ChannelVodsFromMenuAsync(fromSearch: true)),
        ("studio experience: search result menus open live channels and keep browsing on Twitch and Kick", SearchResultOpeningFromMenuAsync),
        ("studio experience: non-live search result menus keep the channel VOD action", NonLiveSearchResultMenuAsync),
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

    private static Task ChannelVodsFromMenuAsync(bool fromSearch) => TestSta.RunOffscreenAsync(async () =>
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
        {
            const string channel = "vodmenu";
            var url = platform == PlatformKind.Twitch
                ? $"https://www.twitch.tv/{channel}"
                : $"https://kick.com/{channel}";
            var live = new FollowedLiveStream(platform, channel, "VOD Menu", "Live stream", "", 42,
                "", null, false, "en", url);
            var followed = new FakeFollowedStreamsService();
            followed.EnqueueResult(_ => Task.FromResult(new FollowedLiveStreamsResult(
                [live], [], [platform], [], [])));
            var search = new FakeStreamSearchService(new StreamSearchResult(
                StreamSearchResultStatus.Available,
                [new StreamSearchChannel(platform, channel, "VOD Menu", url, "", "", "",
                    StreamSearchChannelState.Live, StreamSearchSourceStatus.Available, "Live", true)],
                "1 live channel result."));
            var twitch = new FakeTwitchVodService(new TwitchVodSearchResult(
                TwitchVodSearchStatus.Available, null, [], "", ""));
            var kick = new FakeKickVodService(new KickVodSearchResult(
                KickVodSearchStatus.Available, [], "", ""));
            var settings = new AppSettings();
            settings.Chat.ConnectAutomatically = false;
            await using var main = TestViewModels.CreateMain(settings, new FakeSettingsService(settings),
                new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
                new MemoryLogger(), action => action(), followedStreamsService: followed,
                twitchVodService: twitch, kickVodService: kick, streamSearchService: search);
            var window = new MainWindow { DataContext = main };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, main);
            try
            {
                if (fromSearch)
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = -32000;
                    window.Top = -32000;
                    window.Width = 1320;
                    window.Height = 820;
                    window.Show();
                    main.NewStreamText = channel;
                    await main.AddAndPlayCommand.ExecuteAsync();
                    Assert.Equal(1, main.StreamSearchResults.Count);
                }
                else
                {
                    await main.RefreshFollowedChannelsCommand.ExecuteAsync();
                    Assert.Equal(1, main.LiveFollowedChannels.Count);
                }

                LayoutStudioPolishWindow(window, new Size(1320, 820));
                var command = fromSearch
                    ? main.StreamSearchResults.Single().OpenCommand
                    : main.LiveFollowedChannels.Single().OpenCommand;
                var visualRoot = window.Content;
                if (fromSearch)
                {
                    var popup = (System.Windows.Controls.Primitives.Popup)window.FindName("HomeStreamSearchPopup");
                    var popupContent = (FrameworkElement)popup.Child;
                    popupContent.Measure(new Size(560, 360));
                    popupContent.Arrange(new Rect(new Point(), new Size(560, 360)));
                    popupContent.UpdateLayout();
                    visualRoot = popupContent;
                }
                var card = FindVisualDescendants<Button>((DependencyObject)visualRoot)
                    .Single(button => ReferenceEquals(button.Command, command));
                var menu = card.ContextMenu!;
                Assert.NotNull(menu);
                menu.PlacementTarget = card;
                menu.DataContext = card.DataContext;
                menu.IsOpen = true;
                var items = menu.Items.OfType<MenuItem>().ToArray();
                Assert.Equal(3, items.Length);
                Assert.Equal("Open stream", items[0].Header);
                Assert.Equal("Open and keep browsing", items[1].Header);
                Assert.Equal("Go to VODs", items[2].Header);
                var openCommand = fromSearch
                    ? main.StreamSearchResults.Single().OpenCommand
                    : main.LiveFollowedChannels.Single().OpenCommand;
                var keepBrowsingCommand = fromSearch
                    ? main.StreamSearchResults.Single().OpenAndStayOnHomeCommand
                    : main.LiveFollowedChannels.Single().OpenAndStayOnHomeCommand;
                Assert.True(ReferenceEquals(openCommand, items[0].Command));
                Assert.True(ReferenceEquals(keepBrowsingCommand, items[1].Command));
                Assert.Equal(Visibility.Visible, items[0].Visibility);
                Assert.Equal(Visibility.Visible, items[1].Visibility);
                Assert.Equal("Ctrl+click", items[1].InputGestureText);
                var goToVods = menu.Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, "Go to VODs"));
                goToVods.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
                Assert.Equal(Visibility.Visible, goToVods.Visibility);
                if (fromSearch) Assert.True(ContextMenuService.GetShowOnDisabled(card));
                var vodCommand = fromSearch
                    ? main.StreamSearchResults.Single().GoToVodsCommand
                    : main.LiveFollowedChannels.Single().GoToVodsCommand;
                Assert.True(ReferenceEquals(vodCommand, goToVods.Command),
                    $"Menu command: {goToVods.Command?.GetType().Name ?? "null"}; card context: {card.DataContext?.GetType().Name ?? "null"}.");
                Assert.True(vodCommand.CanExecute(null));
                menu.IsOpen = false;
                await vodCommand.ExecuteAsync();

                Assert.True(main.IsHomeSelected && main.IsTwitchVodsHomePageSelected);
                Assert.Equal(platform, main.SelectedVodPlatform);
                Assert.Equal(channel, main.TwitchVodSearchText);
                Assert.Equal(0, main.Tabs.Count);
                Assert.Equal(platform == PlatformKind.Twitch ? 1 : 0, twitch.CallCount);
                Assert.Equal(platform == PlatformKind.Kick ? 1 : 0, kick.CallCount);
                if (platform == PlatformKind.Twitch) Assert.Equal(channel, twitch.Requests.Single().Streamer);
                else Assert.Equal(channel, kick.Requests.Single().Channel);
            }
            finally
            {
                window.Close();
            }
        }
    });

    private static Task SearchResultOpeningFromMenuAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
            foreach (var keepBrowsing in new[] { false, true })
            {
                const string channel = "searchmenu";
                var url = platform == PlatformKind.Twitch
                    ? $"https://www.twitch.tv/{channel}"
                    : $"https://kick.com/{channel}";
                var search = new FakeStreamSearchService(new StreamSearchResult(
                    StreamSearchResultStatus.Available,
                    [new StreamSearchChannel(platform, channel, "Search Menu", url, "", "", "",
                    StreamSearchChannelState.Live, StreamSearchSourceStatus.Available, "Live", true)],
                    "1 live channel result."));
                var settings = new AppSettings
                {
                    StreamlinkPath = "streamlink.exe",
                    VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
                };
                settings.Chat.ConnectAutomatically = false;
                await using var main = TestViewModels.CreateMain(settings, new FakeSettingsService(settings),
                    new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
                    new MemoryLogger(), action => action(), streamSearchService: search);
                var window = new MainWindow
                {
                    DataContext = main,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                    Width = 1320,
                    Height = 820
                };
                RemoveMainWindowAutomaticStartup(window);
                SetMainWindowViewModel(window, main);
                try
                {
                    window.Show();
                    main.NewStreamText = channel;
                    await main.AddAndPlayCommand.ExecuteAsync();
                    var result = main.StreamSearchResults.Single();
                    LayoutStudioPolishWindow(window, new Size(1320, 820));
                    var popup = (System.Windows.Controls.Primitives.Popup)window.FindName("HomeStreamSearchPopup");
                    var popupContent = (FrameworkElement)popup.Child;
                    popupContent.Measure(new Size(560, 360));
                    popupContent.Arrange(new Rect(new Point(), new Size(560, 360)));
                    popupContent.UpdateLayout();
                    var card = FindVisualDescendants<Button>(popupContent)
                        .Single(button => ReferenceEquals(button.Command, result.OpenCommand));
                    var menu = card.ContextMenu!;
                    Assert.NotNull(menu);
                    menu.PlacementTarget = card;
                    menu.DataContext = card.DataContext;
                    menu.IsOpen = true;
                    var item = menu.Items.OfType<MenuItem>()
                        .Single(entry => Equals(entry.Header, keepBrowsing ? "Open and keep browsing" : "Open stream"));
                    var expectedCommand = keepBrowsing ? result.OpenAndStayOnHomeCommand : result.OpenCommand;
                    Assert.Equal(Visibility.Visible, item.Visibility);
                    Assert.True(ReferenceEquals(expectedCommand, item.Command));
                    Assert.True(item.Command.CanExecute(null));
                    menu.IsOpen = false;

                    await ((AsyncRelayCommand)item.Command).ExecuteAsync();

                    Assert.Equal(1, main.Tabs.Count);
                    Assert.Equal(platform, main.Tabs[0].Target.Platform);
                    Assert.Equal(channel, main.Tabs[0].Target.Channel);
                    Assert.Equal(keepBrowsing, main.IsHomeSelected);
                }
                finally
                {
                    window.Close();
                }
            }
    });

    private static Task NonLiveSearchResultMenuAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        foreach (var state in new[] { StreamSearchChannelState.Offline, StreamSearchChannelState.Unavailable })
        {
            var settings = new AppSettings();
            settings.Chat.ConnectAutomatically = false;
            var search = new FakeStreamSearchService(new StreamSearchResult(
                StreamSearchResultStatus.Available,
                [new StreamSearchChannel(PlatformKind.Twitch, "searchmenu", "Search Menu",
                    "https://www.twitch.tv/searchmenu", "", "", "", state,
                    StreamSearchSourceStatus.Available, "Not live", false)],
                "1 channel result."));
            await using var main = TestViewModels.CreateMain(settings, new FakeSettingsService(settings),
                new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
                new MemoryLogger(), action => action(), streamSearchService: search);
            var window = new MainWindow
            {
                DataContext = main,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                Width = 1320,
                Height = 820
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, main);
            try
            {
                window.Show();
                main.NewStreamText = "searchmenu";
                await main.AddAndPlayCommand.ExecuteAsync();
                var result = main.StreamSearchResults.Single();
                LayoutStudioPolishWindow(window, new Size(1320, 820));
                var popup = (System.Windows.Controls.Primitives.Popup)window.FindName("HomeStreamSearchPopup");
                var popupContent = (FrameworkElement)popup.Child;
                popupContent.Measure(new Size(560, 360));
                popupContent.Arrange(new Rect(new Point(), new Size(560, 360)));
                popupContent.UpdateLayout();
                var card = FindVisualDescendants<Button>(popupContent)
                    .Single(button => ReferenceEquals(button.Command, result.OpenCommand));
                var menu = card.ContextMenu!;
                Assert.NotNull(menu);
                menu.PlacementTarget = card;
                menu.DataContext = card.DataContext;
                menu.IsOpen = true;
                var items = menu.Items.OfType<MenuItem>().ToArray();
                Assert.Equal(3, items.Length);
                Assert.Equal(Visibility.Collapsed, items[0].Visibility);
                Assert.Equal(Visibility.Collapsed, items[1].Visibility);
                Assert.Equal("Go to VODs", items[2].Header);
                Assert.True(items[2].Command.CanExecute(null));
                Assert.True(ContextMenuService.GetShowOnDisabled(card));
                menu.IsOpen = false;
            }
            finally
            {
                window.Close();
            }
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

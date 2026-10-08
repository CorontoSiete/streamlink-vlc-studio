using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Threading;
using StreamlinkVlcStudio.App.Wpf.Themes;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> VodChannelSearchUiTests { get; } =
    [
        ("past broadcast search: actual streamer rows render and select broadcasts in wide and compact layouts", VodChannelRowsAsync),
        ("past broadcast search: Enter in the real input starts one search and Clear cancels pending results", VodChannelInputAsync),
        ("past broadcast search: physical arrow keys Escape and Enter select the focused streamer", VodChannelKeyboardAsync)
    ];

    private static Task VodChannelRowsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var matches = VodChannelSearchTestCatalog.Matches(PlatformKind.Twitch, "iitztimmy", "timmyfan");
        matches = matches with
        {
            Channels = [matches.Channels[0], matches.Channels[1] with
                { DisplayName = "TimmyFan with a long display name 🎮 日本語", State = StreamSearchChannelState.Unavailable }]
        };
        var search = new FakeStreamSearchService(matches);
        await using var main = VodChannelSearchTestCatalog.CreateMain(search);
        var window = new MainWindow { DataContext = main };
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, main);
        try
        {
            main.ShowTwitchVodsHomePageCommand.Execute(null);
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            var input = (TextBox)window.FindName("VodStreamerSearchTextBox");
            input.Text = "timmy";
            input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            await main.SearchTwitchVodsCommand.ExecuteAsync();
            var panel = (FrameworkElement)window.FindName("VodStreamerSearchResultsPanel");
            var items = (ItemsControl)window.FindName("VodChannelSearchResultsList");
            foreach (var theme in Enum.GetValues<AppTheme>())
            {
                ThemeManager.ApplyTheme(theme);
                foreach (var size in new[] { new Size(1320, 820), new Size(700, 640), new Size(440, 640) })
                {
                    LayoutStudioPolishWindow(window, size);
                    Assert.Equal(Visibility.Visible, panel.Visibility);
                    Assert.Equal(2, items.Items.Count);
                    var buttons = FindVisualDescendants<Button>(items).ToArray();
                    Assert.Equal(2, buttons.Length);
                    Assert.True(buttons.All(button => button.IsEnabled), "Channel selection cannot depend on live playback availability.");
                    Assert.Equal("Browse iiTzTimmy's Twitch broadcasts", AutomationProperties.GetName(buttons[0]));
                    foreach (var button in buttons)
                    {
                        var bounds = button.TransformToAncestor(panel).TransformBounds(new Rect(button.RenderSize));
                        Assert.True(bounds.Left >= 0 && bounds.Right <= panel.ActualWidth + 1,
                            $"A result row is clipped horizontally at {size}.");
                        Assert.True(button.ActualHeight >= 56);
                    }
                    Assert.True(input.ActualWidth > 90, $"Streamer search is unusable at {size}.");
                    SaveResponsiveWindowImage(window, $"past-broadcast-search-{theme}-{size.Width}");
                }
            }
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            var first = FindVisualDescendants<Button>(items).First();
            ((IInvokeProvider)new ButtonAutomationPeer(first).GetPattern(PatternInterface.Invoke)).Invoke();
            await TestWait.UntilAsync(() => main.HasTwitchVods, TimeSpan.FromSeconds(2));
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            Assert.Equal("iitztimmy", input.Text);
            Assert.Equal(Visibility.Collapsed, panel.Visibility);
            Assert.Equal(1, ((ItemsControl)window.FindName("VodCardsItemsControl")).Items.Count);
            SaveResponsiveWindowImage(window, "past-broadcast-search-selected");
        }
        finally
        {
            ThemeManager.ApplyTheme(AppTheme.Dark);
            window.Close();
        }
    });

    private static Task VodChannelInputAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource<StreamSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        var search = new FakeStreamSearchService(VodChannelSearchTestCatalog.Matches(PlatformKind.Twitch))
        {
            ResponderAsync = (_, token) => { observed = token; return release.Task; }
        };
        await using var main = VodChannelSearchTestCatalog.CreateMain(search);
        var window = new MainWindow { DataContext = main };
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, main);
        try
        {
            main.ShowTwitchVodsHomePageCommand.Execute(null);
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            var input = (TextBox)window.FindName("VodStreamerSearchTextBox");
            input.Text = "timmy";
            input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            var source = new SkipHotkeyPresentationSource { RootVisual = window };
            for (var count = 0; count < 2; count++)
                input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                { RoutedEvent = Keyboard.KeyDownEvent });
            Assert.Equal(1, search.CallCount);
            Assert.True(main.IsVodChannelSearchRunning);
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            SaveResponsiveWindowImage(window, "past-broadcast-search-loading");
            var clear = (Button)window.FindName("ClearVodStreamerSearchButton");
            ((IInvokeProvider)new ButtonAutomationPeer(clear).GetPattern(PatternInterface.Invoke)).Invoke();
            await TestWait.UntilAsync(() => main.TwitchVodSearchText == "", TimeSpan.FromSeconds(2));
            Assert.True(observed.IsCancellationRequested);
            release.SetResult(VodChannelSearchTestCatalog.Matches(PlatformKind.Twitch, "iitztimmy"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(false, main.IsVodChannelSearchVisible);
            Assert.Equal(0, main.VodChannelSearchResults.Count);
        }
        finally
        {
            release.TrySetResult(VodChannelSearchTestCatalog.Matches(PlatformKind.Twitch));
            window.Close();
        }
    });

    private static Task VodChannelKeyboardAsync() => TestSta.RunAsync(async () =>
    {
        var searchService = new FakeStreamSearchService(VodChannelSearchTestCatalog.Matches(PlatformKind.Twitch, "iitztimmy", "timmyfan"));
        var vodService = new FakeTwitchVodService(new TwitchVodSearchResult(TwitchVodSearchStatus.Available,
            new TwitchVodBroadcaster("123", "timmyfan", "TimmyFan"),
            [new TwitchVodItem("200", "", "123", "timmyfan", "TimmyFan", "Broadcast", "",
                "https://www.twitch.tv/videos/200", "", null, null, TimeSpan.FromHours(1), 10, TwitchVodTypeFilter.Archive)],
            "", "Broadcasts loaded."));
        await using var main = VodChannelSearchTestCatalog.CreateMain(searchService, vodService);
        var window = new MainWindow { DataContext = main, Width = 1320, Height = 820, Topmost = true };
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, main);
        try
        {
            main.ShowTwitchVodsHomePageCommand.Execute(null);
            window.Show();
            main.TwitchVodSearchText = "timmy";
            await main.SearchTwitchVodsCommand.ExecuteAsync();
            PumpResponsiveLayout(window);
            SaveResponsiveWindowImage(window, "past-broadcast-search-physical");
            var input = (TextBox)window.FindName("VodStreamerSearchTextBox");
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            await NativeWindowTest.RequireForegroundAsync(handle, TimeSpan.FromSeconds(2), "Past broadcast search keyboard test");
            var center = input.PointToScreen(new Point(input.ActualWidth / 2, input.ActualHeight / 2));
            var restoreCursor = NativeWindowTest.TryGetCursorPosition(out var originalCursor);
            try
            {
                NativeWindowTest.SendLeftClick((int)Math.Round(center.X), (int)Math.Round(center.Y));
                await TestWait.UntilAsync(() => input.IsKeyboardFocused, TimeSpan.FromSeconds(2));
            }
            finally
            {
                if (restoreCursor) NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
            }
            var buttons = FindVisualDescendants<Button>((ItemsControl)window.FindName("VodChannelSearchResultsList")).ToArray();
            async Task Press(ushort key, IInputElement expectedFocus)
            {
                NativeWindowTest.SendVirtualKeySequence((key, false), (key, true));
                await TestWait.UntilAsync(() => ReferenceEquals(Keyboard.FocusedElement, expectedFocus), TimeSpan.FromSeconds(2));
                PumpResponsiveLayout(window);
            }
            await Press(0x28, buttons[0]);
            await Press(0x28, buttons[1]);
            await Press(0x26, buttons[0]);
            await Press(0x1B, input);
            Assert.Equal(false, main.IsVodChannelSearchVisible);
            Assert.Equal("timmy", main.TwitchVodSearchText);
            await Press(0x28, buttons[0]);
            await Press(0x28, buttons[1]);
            NativeWindowTest.SendVirtualKeySequence((0x0D, false), (0x0D, true));
            await TestWait.UntilAsync(() => main.HasTwitchVods, TimeSpan.FromSeconds(2));
            Assert.Equal("timmyfan", vodService.Requests.Single().Streamer);
            Assert.Equal("200", main.TwitchVods.Single().Id);
            Assert.Equal(1, searchService.CallCount);
        }
        finally { window.Close(); }
    });
}

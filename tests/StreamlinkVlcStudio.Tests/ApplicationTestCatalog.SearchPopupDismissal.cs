using System.Windows.Interop;
using System.Windows.Threading;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> SearchPopupDismissalTests { get; } =
    [
        ("search popup dismissal: switching windows closes the native popup and retains results", SearchPopupWindowSwitchAsync),
        ("search popup dismissal: a search finishing after switching windows cannot reopen the popup", SearchPopupCompletionAfterWindowSwitchAsync),
        ("search popup dismissal: switching windows cancels a pending automatic search", SearchPopupDebounceAfterWindowSwitchAsync),
        ("search popup dismissal: an automatic search queued before switching windows stays canceled", SearchPopupQueuedSearchAfterWindowSwitchAsync),
        ("search popup dismissal: minimizing the owner closes the native popup", SearchPopupOwnerMinimizedAsync),
        ("search popup dismissal: hiding the owner closes the native popup", SearchPopupOwnerHiddenAsync),
        ("search popup dismissal: right and left clicking result text keeps menus and playback usable", SearchPopupResultRightClickAsync),
        ("search popup input: plain and nested result text remains inside the popup", SearchPopupTextInputAsync),
        ("search popup input: emoji content and visual children remain inside the popup", SearchPopupEmojiInputAsync),
        ("search popup input: outside text dismisses results without clearing the query", SearchPopupOutsideTextInputAsync)
    ];

    private static Task SearchPopupResultRightClickAsync() =>
        WithSearchDismissalWindowAsync(async (window, main, popup) =>
        {
            Assert.True(NativeWindowTest.TryGetCursorPosition(out var previousCursor));
            var previousForeground = NativeWindowTest.GetForegroundWindow();
            try
            {
                main.NewStreamText = "searchfixture";
                await main.AddAndPlayCommand.ExecuteAsync();
                PumpResponsiveLayout(window);
                Assert.True(popup.IsOpen && main.IsStreamSearchPanelVisible);
                var popupRoot = (FrameworkElement)popup.Child;
                var button = FindVisualDescendants<Button>(popupRoot)
                    .Single(item => ReferenceEquals(item.Command, main.StreamSearchResults.Single().OpenCommand));
                var menu = button.ContextMenu!;
                Assert.NotNull(menu);
                var events = new List<string>();
                window.Deactivated += (_, _) => events.Add("owner deactivated");
                window.PreviewMouseDown += (_, _) => events.Add("owner mouse down");
                popup.PreviewMouseDown += (_, _) => events.Add("popup mouse down");
                popup.Closed += (_, _) => events.Add("search popup closed");
                button.ContextMenuOpening += (_, _) => events.Add("context menu opening");
                menu.Opened += (_, _) => events.Add("context menu opened");
                menu.Closed += (_, _) => events.Add("context menu closed");
                var title = FindVisualDescendants<EmojiTextBlock>(button).Single();
                var run = title.Inlines.OfType<Run>().First();
                var character = run.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                var next = run.ContentStart.GetPositionAtOffset(1)!.GetCharacterRect(LogicalDirection.Forward);
                var screenPoint = title.PointToScreen(new Point((character.Left + next.Left) / 2,
                    character.Top + character.Height / 2));
                var x = (int)Math.Round(screenPoint.X);
                var y = (int)Math.Round(screenPoint.Y);
                var popupHandle = ((HwndSource)PresentationSource.FromVisual(popupRoot)!).Handle;
                await TestWait.UntilAsync(() => NativeWindowTest.IsWindowVisible(popupHandle), TimeSpan.FromSeconds(2),
                    "The search popup HWND must become visible before physical input");
                await TestWait.UntilAsync(() => NativeWindowTest.IsRootWindowAtPoint(popupHandle, x, y),
                    TimeSpan.FromSeconds(2), "The search popup must finish its fade-in and accept pointer input");
                Assert.True(NativeWindowTest.IsRootWindowAtPoint(popupHandle, x, y),
                    $"The physical right click must land in the search result popup: {NativeWindowTest.DescribeWindowAtPoint(x, y)}; " +
                    $"popup handle={popupHandle}, bounds={NativeWindowTest.GetWindowBounds(popupHandle)}, " +
                    $"popup topmost={NativeWindowTest.IsTopmost(popupHandle)}, owner handle={new WindowInteropHelper(window).Handle}, " +
                    $"button size={button.ActualWidth:0.##}x{button.ActualHeight:0.##}");

                Assert.True(ReferenceEquals(run, popupRoot.InputHitTest(popupRoot.PointFromScreen(screenPoint))),
                    "The physical search click must hit the streamer's actual text Run.");
                object? originalSource = null;
                window.AddHandler(Mouse.PreviewMouseDownEvent,
                    new MouseButtonEventHandler((_, args) => originalSource = args.OriginalSource), true);
                NativeWindowTest.SendRightClick(x, y);
                await TestWait.UntilAsync(() => menu.IsOpen || !popup.IsOpen, TimeSpan.FromSeconds(2),
                    "A physical right click must either open the result menu or expose the premature popup dismissal");
                PumpResponsiveLayout(window);

                var observed = $"events=[{string.Join(", ", events)}], popup={popup.IsOpen}, menu={menu.IsOpen}, " +
                    $"panel={main.IsStreamSearchPanelVisible}, foreground={NativeWindowTest.GetForegroundWindow()}";
                Assert.True(menu.IsOpen, $"Search result context menu did not remain open: {observed}");
                Assert.True(popup.IsOpen && main.IsStreamSearchPanelVisible,
                    $"Search results disappeared on right click: {observed}");
                Assert.Equal(0, main.Tabs.Count);
                Assert.True(ReferenceEquals(run, originalSource));
                menu.IsOpen = false;

                var content = (FrameworkElement)window.FindName("HomeContentScrollViewer");
                var outside = content.PointToScreen(new Point(content.ActualWidth / 2, content.ActualHeight - 30));
                var outsideX = (int)Math.Round(outside.X);
                var outsideY = (int)Math.Round(outside.Y);
                Assert.True(NativeWindowTest.IsRootWindowAtPoint(new WindowInteropHelper(window).Handle,
                    outsideX, outsideY), "The outside click must land in the main window.");
                NativeWindowTest.SendLeftClick(outsideX, outsideY);
                await TestWait.UntilAsync(() => !popup.IsOpen, TimeSpan.FromSeconds(2),
                    "A click outside the search popup must still dismiss it");
                Assert.True(main.HasStreamSearchResults);

                main.ShowStreamSearchDropdown();
                PumpResponsiveLayout(window);
                await TestWait.UntilAsync(() => NativeWindowTest.IsRootWindowAtPoint(popupHandle, x, y),
                    TimeSpan.FromSeconds(2), "The reopened result must accept the physical left click");
                originalSource = null;
                NativeWindowTest.SendLeftClick(x, y);
                await TestWait.UntilAsync(() => main.Tabs.Count == 1, TimeSpan.FromSeconds(2),
                    "Clicking the streamer's text must open playback");
                Assert.True(ReferenceEquals(run, originalSource));
                Assert.Equal("searchfixture", main.SelectedTab!.Target.Channel);
                Assert.Equal(false, main.IsHomeSelected);
                Assert.Equal(false, main.IsStreamSearchPanelVisible);
                Assert.Equal(false, popup.IsOpen);
            }
            finally
            {
                NativeWindowTest.SetCursorPosition(previousCursor.X, previousCursor.Y);
                NativeWindowTest.ActivateWindow(previousForeground);
            }
        });

    private static Task SearchPopupWindowSwitchAsync() => WithSearchDismissalWindowAsync(async (window, main, popup) =>
    {
        main.NewStreamText = "searchfixture";
        await main.AddAndPlayCommand.ExecuteAsync();
        PumpResponsiveLayout(window);
        var results = main.StreamSearchResults.ToArray();
        Assert.True(results.Length > 0 && popup.IsOpen);
        var popupHandle = ((HwndSource)PresentationSource.FromVisual(popup.Child)!).Handle;
        Assert.True(NativeWindowTest.IsWindowVisible(popupHandle));

        var other = CreateSearchDismissalOtherWindow();
        try
        {
            await ActivateSearchDismissalOtherWindowAsync(other);
            Assert.Equal(false, window.IsActive);
            await AssertSearchPopupDismissedAsync(main, popup, popupHandle);
            Assert.Equal("searchfixture", main.NewStreamText);
            Assert.True(main.StreamSearchResults.SequenceEqual(results));

            await NativeWindowTest.RequireForegroundAsync(new WindowInteropHelper(window).Handle,
                TimeSpan.FromSeconds(2), "Returning to the search owner");
            main.ShowStreamSearchDropdown();
            PumpResponsiveLayout(window);
            Assert.True(popup.IsOpen && main.IsStreamSearchPanelVisible);
            Assert.True(main.StreamSearchResults.SequenceEqual(results));
            main.DismissStreamSearchDropdown();
        }
        finally
        {
            other.Close();
        }
    });

    private static Task SearchPopupCompletionAfterWindowSwitchAsync()
    {
        var completion = new TaskCompletionSource<StreamSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeStreamSearchService(SearchDismissalFixtureResult)
        {
            ResponderAsync = (_, cancellation) => completion.Task.WaitAsync(cancellation)
        };
        return WithSearchDismissalWindowAsync(async (window, main, popup) =>
        {
            main.NewStreamText = "searchfixture";
            var searchTask = main.AddAndPlayCommand.ExecuteAsync();
            PumpResponsiveLayout(window);
            Assert.True(main.IsStreamSearchRunning && popup.IsOpen);
            var popupHandle = ((HwndSource)PresentationSource.FromVisual(popup.Child)!).Handle;
            var other = CreateSearchDismissalOtherWindow();
            try
            {
                await ActivateSearchDismissalOtherWindowAsync(other);
                Assert.Equal(false, window.IsActive);
                completion.TrySetResult(SearchDismissalFixtureResult);
                await searchTask;
                PumpResponsiveLayout(window);
                Assert.True(!main.IsStreamSearchRunning && main.HasStreamSearchResults);
                await AssertSearchPopupDismissedAsync(main, popup, popupHandle);
            }
            finally
            {
                completion.TrySetResult(SearchDismissalFixtureResult);
                await searchTask;
                other.Close();
            }
        }, service);
    }

    private static Task SearchPopupDebounceAfterWindowSwitchAsync()
    {
        var service = new FakeStreamSearchService(SearchDismissalFixtureResult);
        return WithSearchDismissalWindowAsync(async (window, main, popup) =>
        {
            main.NewStreamText = "searchfixture";
            var other = CreateSearchDismissalOtherWindow();
            try
            {
                await ActivateSearchDismissalOtherWindowAsync(other);
                Assert.Equal(false, window.IsActive);
                await Task.Delay(400); // Let the production 250 ms debounce expire.
                PumpResponsiveLayout(window);
                Assert.Equal(0, service.CallCount);
                Assert.Equal(false, main.IsStreamSearchPanelVisible);
                Assert.Equal(false, popup.IsOpen);
                Assert.Equal("searchfixture", main.NewStreamText);
            }
            finally
            {
                other.Close();
            }
        }, service);
    }

    private static Task SearchPopupOwnerMinimizedAsync() => SearchPopupOwnerUnavailableAsync(window =>
        window.WindowState = WindowState.Minimized);

    private static Task SearchPopupOwnerHiddenAsync() => SearchPopupOwnerUnavailableAsync(window => window.Hide());

    private static Task SearchPopupOwnerUnavailableAsync(Action<MainWindow> makeUnavailable) =>
        WithSearchDismissalWindowAsync(async (window, main, popup) =>
        {
            main.NewStreamText = "searchfixture";
            await main.AddAndPlayCommand.ExecuteAsync();
            PumpResponsiveLayout(window);
            Assert.True(popup.IsOpen);
            var popupHandle = ((HwndSource)PresentationSource.FromVisual(popup.Child)!).Handle;
            makeUnavailable(window);
            PumpResponsiveLayout(window);
            Assert.True(window.WindowState == WindowState.Minimized || !window.IsVisible);
            await AssertSearchPopupDismissedAsync(main, popup, popupHandle);
        });

    private static Task SearchPopupQueuedSearchAfterWindowSwitchAsync()
    {
        var callbacks = new ConcurrentQueue<Action>();
        var service = new FakeStreamSearchService(SearchDismissalFixtureResult);
        return WithSearchDismissalWindowAsync(async (window, main, popup) =>
        {
            while (callbacks.TryDequeue(out var setup)) setup();
            main.NewStreamText = "searchfixture";
            await TestWait.UntilAsync(() => !callbacks.IsEmpty, TimeSpan.FromSeconds(2),
                "The automatic search must be queued to the UI before switching windows");
            Assert.Equal(0, service.CallCount);
            var other = CreateSearchDismissalOtherWindow();
            try
            {
                await ActivateSearchDismissalOtherWindowAsync(other);
                Assert.Equal(false, window.IsActive);
                while (callbacks.TryDequeue(out var callback)) callback();
                PumpResponsiveLayout(window);
                Assert.Equal(0, service.CallCount);
                Assert.Equal(false, main.IsStreamSearchPanelVisible);
                Assert.Equal(false, popup.IsOpen);
            }
            finally
            {
                other.Close();
            }
        }, service, callbacks.Enqueue);
    }

    private static readonly StreamSearchResult SearchDismissalFixtureResult = new(
        StreamSearchResultStatus.Available,
        [new StreamSearchChannel(PlatformKind.Twitch, "searchfixture", "Search fixture",
            "https://www.twitch.tv/searchfixture", "", "Fixture broadcast", "Fixture category",
            StreamSearchChannelState.Live, StreamSearchSourceStatus.Available, "Playable stream found.", true)],
        "1 channel result.");

    private static Task WithSearchDismissalWindowAsync(Func<MainWindow, MainViewModel, Popup, Task> test,
        FakeStreamSearchService? service = null, Action<Action>? dispatch = null) => TestSta.RunAsync(async () =>
        {
            var settings = new AppSettings { StreamlinkPath = "streamlink.exe" };
            settings.Chat.ConnectAutomatically = false;
            var dispatcher = Dispatcher.CurrentDispatcher;
            await using var main = TestViewModels.CreateMain(settings, new FakeSettingsService(settings),
                new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
                new MemoryLogger(), dispatch ?? (action => dispatcher.BeginInvoke(action)),
                streamSearchService: service ?? new FakeStreamSearchService(SearchDismissalFixtureResult));
            var window = new MainWindow
            {
                Width = 1100,
                Height = 760,
                Left = 100,
                Top = 100,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                DataContext = main
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, main);
            try
            {
                window.Show();
                await NativeWindowTest.RequireForegroundAsync(new WindowInteropHelper(window).Handle,
                    TimeSpan.FromSeconds(2), "Search dismissal requires an active owner");
                PumpResponsiveLayout(window);
                Assert.True(window.IsActive);
                await test(window, main, (Popup)window.FindName("HomeStreamSearchPopup"));
            }
            finally
            {
                main.DismissStreamSearchDropdown();
                window.Close();
            }
        });

    private static Window CreateSearchDismissalOtherWindow() => new()
    {
        Title = "Search dismissal other window",
        Width = 360,
        Height = 240,
        Left = 60,
        Top = 60,
        WindowStartupLocation = WindowStartupLocation.Manual,
        ShowInTaskbar = false,
        Content = new TextBox { Text = "Another window", Margin = new Thickness(20) }
    };

    private static async Task ActivateSearchDismissalOtherWindowAsync(Window window)
    {
        window.Show();
        await NativeWindowTest.RequireForegroundAsync(new WindowInteropHelper(window).Handle,
            TimeSpan.FromSeconds(2), "Switching to another window");
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.True(window.IsActive);
    }

    private static async Task AssertSearchPopupDismissedAsync(MainViewModel main, Popup popup, IntPtr handle)
    {
        Assert.True(!main.IsStreamSearchPanelVisible && !popup.IsOpen,
            $"After the owner loses activation, search state must be dismissed: " +
            $"panel visible={main.IsStreamSearchPanelVisible}, popup open={popup.IsOpen}, " +
            $"native visible={NativeWindowTest.IsWindowVisible(handle)}, topmost={NativeWindowTest.IsTopmost(handle)}.");
        await TestWait.UntilAsync(() => !NativeWindowTest.IsWindowVisible(handle), TimeSpan.FromSeconds(2),
            "The native popup must disappear after dismissal, including its closing animation");
        Assert.Equal(false, NativeWindowTest.IsWindowVisible(handle));
    }
}

internal static partial class ApplicationTestCatalog
{
    private static Task SearchPopupTextInputAsync() => WithSearchInputWindowAsync((window, main, popup, button) =>
    {
        var title = FindVisualDescendants<EmojiTextBlock>(button).Single();
        var run = title.Inlines.OfType<Run>().First();
        AssertSearchInputStaysOpen(window, main, popup, run);

        var nested = new Run(" nested streamer text");
        title.Inlines.Add(new Span(new Bold(nested)));
        AssertSearchInputStaysOpen(window, main, popup, nested);
        return Task.CompletedTask;
    });

    private static Task SearchPopupEmojiInputAsync() => WithSearchInputWindowAsync((window, main, popup, button) =>
    {
        var title = FindVisualDescendants<EmojiTextBlock>(button).Single();
        // Exercise the same content boundary even on machines whose emoji
        // font falls back to text rather than producing an inline image.
        var emoji = new InlineUIContainer(new Image { Width = 14, Height = 14 });
        title.Inlines.Add(emoji);
        AssertSearchInputStaysOpen(window, main, popup, emoji);
        AssertSearchInputStaysOpen(window, main, popup, emoji.Child);
        AssertSearchInputStaysOpen(window, main, popup, button);
        AssertSearchInputStaysOpen(window, main, popup, popup.Child);
        return Task.CompletedTask;
    });

    private static Task SearchPopupOutsideTextInputAsync() => WithSearchInputWindowAsync((window, main, popup, _) =>
    {
        var outside = FindVisualDescendants<TextBlock>(window).First(block => block.IsVisible);
        var run = new Run("Outside search");
        outside.Inlines.Clear();
        outside.Inlines.Add(new Span(new Bold(run)));
        var routed = false;
        window.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => routed = true), true);
        run.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        });
        Assert.True(routed, "The outside text input must traverse the actual window handler.");
        Assert.Equal(false, main.IsStreamSearchPanelVisible);
        Assert.Equal(false, popup.IsOpen);
        Assert.Equal("searchfixture", main.NewStreamText);
        Assert.True(main.HasStreamSearchResults);
        Assert.Equal(0, main.Tabs.Count);
        return Task.CompletedTask;
    });

    private static void AssertSearchInputStaysOpen(MainWindow window, MainViewModel main, Popup popup,
        DependencyObject source)
    {
        foreach (var mouseButton in new[] { MouseButton.Left, MouseButton.Right })
        {
            object? observed = null;
            var handledByWindow = true;
            MouseButtonEventHandler handler = (_, args) =>
            {
                observed = args.OriginalSource;
                handledByWindow = args.Handled;
                // This offscreen event has no native pointer position. Stop after
                // the production window handler; the physical test covers the
                // remaining route, context menu and playback command.
                args.Handled = true;
            };
            window.AddHandler(Mouse.PreviewMouseDownEvent, handler, true);
            try
            {
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, mouseButton)
                {
                    RoutedEvent = Mouse.PreviewMouseDownEvent
                };
                if (source is ContentElement content) content.RaiseEvent(args);
                else ((UIElement)source).RaiseEvent(args);
                Assert.True(ReferenceEquals(source, observed),
                    $"The actual window handler must receive {source.GetType().Name} as OriginalSource.");
                Assert.True(main.IsStreamSearchPanelVisible && popup.IsOpen,
                    $"A {mouseButton} click on {source.GetType().Name} inside search must preserve the popup.");
                Assert.Equal(false, handledByWindow);
                Assert.Equal(0, main.Tabs.Count);
            }
            finally
            {
                window.RemoveHandler(Mouse.PreviewMouseDownEvent, handler);
            }
        }
    }

    private static Task WithSearchInputWindowAsync(Func<MainWindow, MainViewModel, Popup, Button, Task> test) =>
        TestSta.RunOffscreenAsync(async () =>
        {
            var settings = new AppSettings();
            settings.Chat.ConnectAutomatically = false;
            await using var main = TestViewModels.CreateMain(settings, new FakeSettingsService(settings),
                new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
                new MemoryLogger(), action => action(),
                streamSearchService: new FakeStreamSearchService(SearchDismissalFixtureResult));
            var window = new MainWindow
            {
                DataContext = main,
                Width = 1100,
                Height = 760,
                Left = -32000,
                Top = -32000,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, main);
            try
            {
                window.Show();
                main.NewStreamText = "searchfixture";
                await main.AddAndPlayCommand.ExecuteAsync();
                PumpResponsiveLayout(window);
                var popup = (Popup)window.FindName("HomeStreamSearchPopup");
                var root = (FrameworkElement)popup.Child;
                root.Measure(new Size(560, 360));
                root.Arrange(new Rect(0, 0, 560, 360));
                root.UpdateLayout();
                Assert.True(main.IsStreamSearchPanelVisible && popup.IsOpen);
                var button = FindVisualDescendants<Button>(root)
                    .Single(item => ReferenceEquals(item.Command, main.StreamSearchResults.Single().OpenCommand));
                await test(window, main, popup, button);
            }
            finally
            {
                main.DismissStreamSearchDropdown();
                window.Close();
            }
        });
}

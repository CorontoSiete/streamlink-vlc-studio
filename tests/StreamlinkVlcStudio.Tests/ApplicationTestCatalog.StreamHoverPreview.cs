internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> StreamHoverPreviewUiTests =>
    [
        ("stream hover preview: settings toggle and live card template bind to the shared controller", HoverPreviewBindingsAsync),
        ("stream hover preview: physical hover click leave hide and disable control the player", HoverPreviewInputAsync)
    ];

    private static Task HoverPreviewBindingsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = new AppSettings();
        await using var viewModel = TestViewModels.CreateMain(settings, new FakeSettingsService(settings),
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(), action => action());
        var window = new MainWindow { DataContext = viewModel, Width = 1000, Height = 700 };
        RemoveMainWindowAutomaticStartup(window);
        var toggle = (CheckBox)window.FindName("StreamHoverPreviewsCheckBox");
        Assert.NotNull(toggle);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.DataBind);
        Assert.Equal(false, toggle.IsChecked == true);
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        Assert.True(settings.EnableStreamHoverPreviews);
        settings.EnableStreamHoverPreviews = false;
        Assert.Equal(false, toggle.IsChecked == true);

        var template = (DataTemplate)window.Resources["LiveStreamCardTemplate"];
        var button = (Button)template.LoadContent();
        var live = new LiveStreamCardViewModel(LiveStreamCardData.FromFollowedStream(
            CreateTestFollowedStream(PlatformKind.Twitch, "preview")), (_, _) => Task.CompletedTask);
        button.DataContext = live;
        var host = new Window { DataContext = viewModel, Content = button };
        host.Resources.MergedDictionaries.Add(window.Resources);
        button.Measure(new Size(316, 320));
        button.Arrange(new Rect(0, 0, 316, 320));
        button.UpdateLayout();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.DataBind);
        var preview = FindHoverVisual<StreamHoverPreview>(button)!;
        Assert.NotNull(preview);
        Assert.True(ReferenceEquals(viewModel.HoverPreviews, preview.Controller));
        Assert.Equal(false, preview.IsPreviewEnabled);
        settings.EnableStreamHoverPreviews = true;
        Assert.True(preview.IsPreviewEnabled);
        Assert.True(ReferenceEquals(live, preview.DataContext));
        Assert.True(ReferenceEquals(live.OpenCommand, button.Command));
        host.Content = null;
        host.Close();
        window.Close();
    });

    private static Task HoverPreviewInputAsync() => TestSta.RunAsync(async () =>
    {
        var settings = new AppSettings { EnableStreamHoverPreviews = true };
        var calls = 0;
        var active = 0;
        var clicks = 0;
        Action<LivePreviewFrame>? publish = null;
        await using var controller = new StreamHoverPreviewController(settings, new MemoryLogger(),
            async (_, _, present, token) =>
            {
                publish = present;
                Interlocked.Increment(ref calls);
                Interlocked.Increment(ref active);
                try
                {
                    present(new(1, 1, [0, 255, 0, 0]));
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally { Interlocked.Decrement(ref active); }
            });
        var item = new LiveStreamCardViewModel(LiveStreamCardData.FromFollowedStream(
            CreateTestFollowedStream(PlatformKind.Twitch, "preview")), (_, _) => { clicks++; return Task.CompletedTask; });
        var preview = new StreamHoverPreview { Controller = controller, IsPreviewEnabled = true, Width = 300, Height = 170 };
        var button = new Button { Content = preview, DataContext = item, Command = item.OpenCommand, Width = 316, Height = 190 };
        var panel = new Grid { Background = Brushes.Black };
        panel.Children.Add(button);
        var window = new Window { Content = panel, Width = 560, Height = 400, Topmost = true, Title = "Hover preview verification" };
        NativeWindowTest.TryGetCursorPosition(out var previousCursor);
        var keepInputBusy = false;
        void RequeueInput()
        {
            if (keepInputBusy)
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(RequeueInput));
        }
        async Task Until(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            while (!condition()) await Task.Delay(20, timeout.Token);
        }
        void Move(FrameworkElement element, double x, double y)
        {
            var point = element.PointToScreen(new Point(x, y));
            NativeWindowTest.SetCursorPosition((int)point.X, (int)point.Y);
        }
        try
        {
            window.Show();
            NativeWindowTest.ActivateWindow(new System.Windows.Interop.WindowInteropHelper(window).Handle);
            Move(panel, 5, 5);
            await Task.Delay(80);
            Move(button, 150, 80);
            // Incoming video must reach the screen even while input keeps lower priorities busy.
            keepInputBusy = true;
            RequeueInput();
            try { await Until(() => FindHoverVisual<Image>(preview)?.Source is not null); }
            finally { keepInputBusy = false; }
            Assert.Equal(1, active);
            Assert.Equal(0, clicks);

            var queuedRenders = 0;
            void CountRender(object? sender, System.Windows.Threading.DispatcherHookEventArgs e)
            {
                if (e.Operation.Priority == System.Windows.Threading.DispatcherPriority.Render)
                    Interlocked.Increment(ref queuedRenders);
            }
            window.Dispatcher.Hooks.OperationPosted += CountRender;
            try
            {
                // Hold the UI while a decoder floods it: retain the latest frame without a queue of renders.
                Task.Run(() =>
                {
                    for (var i = 0; i < 1000; i++) publish!(new(1, 1, [0, 0, 255, 0]));
                }).GetAwaiter().GetResult();
                Assert.True(queuedRenders <= 1);
            }
            finally { window.Dispatcher.Hooks.OperationPosted -= CountRender; }
            await Until(() => BitmapAssert.CountPixels(FindHoverVisual<Image>(preview)?.Source,
                (red, green, _) => red == 255 && green == 0) == 1);

            // A disabled preview clears immediately, even if the pointer remains on the card.
            settings.EnableStreamHoverPreviews = false;
            await Until(() => active == 0 && FindHoverVisual<Image>(preview)?.Source is null);
            settings.EnableStreamHoverPreviews = true;
            preview.IsPreviewEnabled = false;
            preview.IsPreviewEnabled = true;
            await Until(() => active == 1);

            var center = button.PointToScreen(new Point(150, 80));
            NativeWindowTest.SendLeftClick((int)center.X, (int)center.Y);
            await Until(() => clicks == 1 && active == 0);
            await Task.Delay(100);
            Assert.Equal(0, active);

            Move(panel, 5, 5);
            await Task.Delay(80);
            Move(button, 150, 80);
            await Until(() => active == 1);
            Move(panel, 5, 5);
            await Until(() => active == 0);

            Move(button, 150, 80);
            await Until(() => active == 1);
            panel.Visibility = Visibility.Collapsed;
            await Until(() => active == 0);
            panel.Visibility = Visibility.Visible;
            Move(panel, 5, 5);
            await Task.Delay(80);
            Move(button, 150, 80);
            await Until(() => active == 1);
            window.Content = null;
            await Until(() => active == 0);
            Assert.True(calls >= 5);
        }
        finally
        {
            window.Close();
            NativeWindowTest.SetCursorPosition(previousCursor.X, previousCursor.Y);
        }
    });

    private static T? FindHoverVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var result = FindHoverVisual<T>(VisualTreeHelper.GetChild(root, index));
            if (result is not null) return result;
        }
        return null;
    }
}

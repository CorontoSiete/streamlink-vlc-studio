using System.Windows.Threading;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> StudioPolishTests { get; } =
    [
        ("studio polish: changing settings categories reveals the controls in wide and compact layouts", SettingsCategoryStartsAtTopAsync),
        ("studio polish: settings autosave failures show an inline error that clears after recovery", SettingsSaveFeedbackVisibleAsync),
        ("studio polish: pending and canceled saves report their state and permit retry", DeferredSettingsSaveFeedbackAsync),
        ("studio polish: search closes when leaving Home and retains results for an explicit reopen", SearchClosesOnNavigationAsync),
        ("studio polish: Escape dismisses search without clearing the query or results", SearchEscapeDismissesAsync),
        ("studio polish: clearing search removes results and cancels a pending search", ClearSearchAsync),
        ("studio polish: card previews preserve their aspect ratio and every row remains reachable", StudioCardGeometryAsync)
    ];

    private static Task SettingsCategoryStartsAtTopAsync() => WithStudioPolishWindowAsync((window, main, _) =>
    {
        main.IsSettingsOpen = true;
        foreach (var size in new[] { new Size(1320, 820), new Size(700, 640) })
        {
            main.SelectedSettingsCategory = SettingsCategory.Hotkeys;
            LayoutStudioPolishWindow(window, size);
            var viewer = (ScrollViewer)window.FindName(size.Width > 760
                ? "SettingsContentScrollViewer" : "SettingsViewport");
            viewer.ScrollToEnd();
            LayoutStudioPolishWindow(window, size);
            Assert.True(viewer.VerticalOffset > 20, "The source page must be scrolled for this regression.");

            main.SelectedSettingsCategory = SettingsCategory.Accounts;
            LayoutStudioPolishWindow(window, size);
            Assert.True(viewer.VerticalOffset < 1,
                $"Accounts inherited a scroll offset of {viewer.VerticalOffset} at {size}.");
        }
        return Task.CompletedTask;
    });

    private static Task SettingsSaveFeedbackVisibleAsync() => WithStudioPolishWindowAsync(async (window, main, settingsService) =>
    {
        main.IsSettingsOpen = true;
        foreach (var size in new[] { new Size(1320, 820), new Size(700, 640) })
        {
            settingsService.SaveException = new IOException("The test settings file is locked.");
            main.Settings.LowLatency = !main.Settings.LowLatency;
            await TestWait.UntilAsync(() => main.HasSettingsSaveError, TimeSpan.FromSeconds(3));
            LayoutStudioPolishWindow(window, size);
            var alert = (FrameworkElement)window.FindName("SettingsSaveError");
            var error = FindVisualDescendants<TextBlock>(alert).SingleOrDefault(text =>
                text.Text.Contains("test settings file is locked", StringComparison.Ordinal));
            Assert.True(error is { ActualHeight: > 0, ActualWidth: > 0 },
                $"A failed save has no visible explanation at {size}.");
            var errorBounds = error!.TransformToAncestor(alert).TransformBounds(new Rect(error.RenderSize));
            Assert.True(errorBounds.Top >= 0 && errorBounds.Bottom <= alert.ActualHeight,
                "Save feedback must not be clipped by the alert.");
            SaveResponsiveWindowImage(window, $"studio-save-error-{size.Width}");

            settingsService.SaveException = null;
            main.Settings.LowLatency = !main.Settings.LowLatency;
            await TestWait.UntilAsync(() => !main.HasSettingsSaveError, TimeSpan.FromSeconds(3));
            LayoutStudioPolishWindow(window, size);
            Assert.Equal(Visibility.Collapsed, alert.Visibility);
            Assert.True(window.FindName("SettingsStickyFooter") is null);
            Assert.True(window.FindName("SettingsSaveButton") is null);
        }
    });

    private static async Task SearchClosesOnNavigationAsync()
    {
        await using var fixture = new NavigationFixture();
        var main = fixture.Main;
        main.NewStreamText = "fixture";
        await main.AddAndPlayCommand.ExecuteAsync();
        Assert.True(main.IsStreamSearchPanelVisible && main.StreamSearchResults.Count > 0);
        var results = main.StreamSearchResults.ToArray();

        main.IsSettingsOpen = true;
        Assert.Equal(false, main.IsStreamSearchPanelVisible);
        main.IsSettingsOpen = false;
        Assert.Equal(false, main.IsStreamSearchPanelVisible);
        main.ShowStreamSearchDropdown();
        Assert.True(main.IsStreamSearchPanelVisible);

        main.SelectedTab = fixture.AddTab("navigationfixture");
        Assert.Equal(false, main.IsStreamSearchPanelVisible);
        main.SelectHomeCommand.Execute(null);
        Assert.Equal(false, main.IsStreamSearchPanelVisible);
        main.ShowStreamSearchDropdown();
        Assert.True(main.IsStreamSearchPanelVisible);
        Assert.True(main.StreamSearchResults.SequenceEqual(results));
    }

    private static async Task DeferredSettingsSaveFeedbackAsync()
    {
        var settings = new AppSettings();
        settings.Chat.ConnectAutomatically = false;
        var service = new DeferredStudioSettingsService(settings);
        await using var main = TestViewModels.CreateMain(settings, service,
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
            new MemoryLogger(), action => action());
        var save = main.SaveSettingsCommand.ExecuteAsync();
        try
        {
            Assert.True(main.IsSavingSettings);
            Assert.True(main.SettingsSaveStatus.StartsWith("Saving", StringComparison.Ordinal));
            Assert.Equal(false, main.SaveSettingsCommand.CanExecute(null));
        }
        finally
        {
            service.Completion.TrySetCanceled();
            await Assert.ThrowsAsync<OperationCanceledException>(() => save);
        }
        Assert.Equal(false, main.IsSavingSettings);
        Assert.True(main.SettingsSaveStatus.Contains("canceled", StringComparison.Ordinal));
        Assert.True(main.SaveSettingsCommand.CanExecute(null));

        service.Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Completion.SetResult();
        await main.SaveSettingsCommand.ExecuteAsync();
        Assert.Equal(false, main.IsSavingSettings);
        Assert.Equal(false, main.HasSettingsSaveError);
        Assert.True(main.SettingsSaveStatus.StartsWith("Settings saved", StringComparison.Ordinal));
    }

    private sealed class DeferredStudioSettingsService(AppSettings settings) : ISettingsService
    {
        public string SettingsPath => "memory";
        public TaskCompletionSource Completion { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default) => Completion.Task;
    }

    private static Task SearchEscapeDismissesAsync() => WithStudioPolishWindowAsync(async (window, main, _) =>
    {
        main.NewStreamText = "fixture";
        await main.AddAndPlayCommand.ExecuteAsync();
        var count = main.StreamSearchResults.Count;
        Assert.True(main.IsStreamSearchPanelVisible && count > 0);
        var search = (TextBox)window.FindName("HomeStreamSearchTextBox");
        Assert.True(window.TryExecuteHotkey(new HotkeyGesture(Key.Escape, ModifierKeys.None), search),
            "Escape must be handled while search results are open.");
        Assert.Equal(false, main.IsStreamSearchPanelVisible);
        Assert.Equal("fixture", main.NewStreamText);
        Assert.Equal(count, main.StreamSearchResults.Count);
        Assert.Equal(false, window.TryExecuteHotkey(new HotkeyGesture(Key.Escape, ModifierKeys.None), search));
    });

    private static Task ClearSearchAsync() => WithStudioPolishWindowAsync(async (window, main, _) =>
    {
        main.NewStreamText = "fixture";
        await main.AddAndPlayCommand.ExecuteAsync();
        LayoutStudioPolishWindow(window, new Size(1320, 820));
        var clear = (Button)window.FindName("ClearHomeSearchButton");
        Assert.Equal(Visibility.Visible, clear.Visibility);
        clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("", main.NewStreamText);
        Assert.Equal(0, main.StreamSearchResults.Count);
        Assert.Equal(false, main.IsStreamSearchPanelVisible);

        main.NewStreamText = "anotherfixture";
        clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(350); // The default 250 ms debounce must not repopulate a cleared input.
        LayoutStudioPolishWindow(window, new Size(1320, 820));
        Assert.Equal(false, main.IsStreamSearchPanelVisible);
        Assert.Equal(0, main.StreamSearchResults.Count);
        Assert.Equal(Visibility.Collapsed, clear.Visibility);
    });

    private static Task StudioCardGeometryAsync() => WithStudioPolishWindowAsync((window, main, _) =>
    {
        AddStudioCardFixtures(main);
        foreach (var keepGap in new[] { true, false })
        {
            main.Settings.KeepHomeCardRightGap = keepGap;
            foreach (var size in new[] { new Size(1320, 820), new Size(700, 640), new Size(360, 640) })
            {
                LayoutStudioPolishWindow(window, size);
                var items = FindVisualDescendants<ItemsControl>((FrameworkElement)window.Content)
                    .Single(control => ReferenceEquals(control.ItemsSource, main.LiveFollowedChannels));
                var panel = FindVisualDescendants<HomeCardWrapPanel>(items).Single();
                var previews = FindVisualDescendants<AspectRatioDecorator>(panel).ToArray();
                Assert.Equal(6, previews.Length);
                foreach (var preview in previews)
                {
                    Assert.True(preview.ActualHeight > 0 &&
                        Math.Abs(preview.ActualWidth - preview.ActualHeight * 16 / 9) < 2,
                        $"Preview is {preview.RenderSize} at {size}, KeepRightGap={keepGap}.");
                }

                var last = panel.Children[5];
                var bounds = last.TransformToAncestor(panel).TransformBounds(new Rect(last.RenderSize));
                Assert.True(bounds.Height > 100 && bounds.Bottom <= panel.ActualHeight + 1,
                    $"The final card is clipped: {bounds}; panel {panel.RenderSize}.");
                var viewer = (ScrollViewer)window.FindName("HomeContentScrollViewer");
                viewer.ScrollToTop();
                LayoutStudioPolishWindow(window, size);
                SaveResponsiveWindowImage(window, $"studio-cards-{size.Width}-gap-{keepGap}");
                viewer.ScrollToEnd();
                LayoutStudioPolishWindow(window, size);
                bounds = last.TransformToAncestor(viewer).TransformBounds(new Rect(last.RenderSize));
                Assert.True(bounds.Bottom <= viewer.ActualHeight + 1 && bounds.Bottom > 0,
                    $"The last row is unreachable after scrolling: {bounds}; viewer {viewer.RenderSize}.");
            }
        }
        return Task.CompletedTask;
    });

    private static Task WithStudioPolishWindowAsync(Func<MainWindow, MainViewModel, FakeSettingsService, Task> test) =>
        TestSta.RunOffscreenAsync(async () =>
        {
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
            };
            settings.Chat.ConnectAutomatically = false;
            var service = new FakeSettingsService(settings);
            await using var main = TestViewModels.CreateMain(settings, service,
                new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
                new MemoryLogger(), action => action());
            var window = new MainWindow { DataContext = main };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, main);
            try
            {
                LayoutStudioPolishWindow(window, new Size(1320, 820));
                await test(window, main, service);
            }
            finally
            {
                window.Close();
            }
        });

    private static void LayoutStudioPolishWindow(MainWindow window, Size size)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(size);
        root.Arrange(new Rect(new Point(), size));
        root.UpdateLayout();
        root.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        root.UpdateLayout();
    }
}

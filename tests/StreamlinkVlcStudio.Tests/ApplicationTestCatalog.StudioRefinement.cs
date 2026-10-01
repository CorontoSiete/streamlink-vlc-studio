internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> StudioRefinementTests { get; } =
    [
        ("studio refinement: library pages omit redundant headings and subtitles in wide and compact layouts", LibraryPageHeadersRemovedAsync),
        ("studio refinement: library destinations do not inherit another page's scroll position", LibraryDestinationScrollAsync),
        ("studio refinement: returning to a library page restores its own position", LibraryReturnScrollAsync),
        ("studio refinement: replacing broadcasts resets only that page's saved position", LibraryReplacementScrollAsync),
        ("studio refinement: every visible row paints when live cards arrive", LiveCardRowsPaintAsync),
        ("studio refinement: library cards remain painted after settings and palette changes", LibraryCardsAcrossThemesAsync),
        ("studio refinement: physical arrow keys navigate search results and Enter opens the focused result", SearchResultsKeyboardAsync)
    ];

    private static Task LibraryPageHeadersRemovedAsync() => WithStudioPolishWindowAsync((window, main, _) =>
    {
        var viewer = (ScrollViewer)window.FindName("HomeContentScrollViewer");
        var headingStyle = (Style)window.FindResource("StudioLibraryTitle");
        var subtitleStyle = (Style)window.FindResource("StudioPageSubtitle");
        var removedBindings = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(MainViewModel.BrowseCategoriesTitle),
            nameof(MainViewModel.TwitchVodResultsTitle),
            nameof(MainViewModel.DownloadsStatus)
        };
        var destinations = new (string Name, ICommand Command, string VisibilityPath)[]
        {
            ("following", main.ShowFollowedHomePageCommand, nameof(MainViewModel.IsFollowedHomePageVisible)),
            ("discover", main.ShowBrowseHomePageCommand, nameof(MainViewModel.IsBrowseHomePageVisible)),
            ("broadcasts", main.ShowTwitchVodsHomePageCommand, nameof(MainViewModel.IsTwitchVodsHomePageVisible)),
            ("downloads", main.ShowDownloadsHomePageCommand, nameof(MainViewModel.IsDownloadsHomePageVisible)),
            ("recent", main.ShowRecentHomePageCommand, nameof(MainViewModel.IsRecentHomePageVisible))
        };
        foreach (var size in new[] { new Size(1320, 820), new Size(700, 640), new Size(440, 640) })
        {
            foreach (var destination in destinations)
            {
                destination.Command.Execute(null);
                LayoutStudioPolishWindow(window, size);
                var page = FindVisualDescendants<FrameworkElement>(viewer).Single(element =>
                    BindingOperations.GetBinding(element, UIElement.VisibilityProperty)?.Path?.Path == destination.VisibilityPath);
                Assert.Equal(Visibility.Visible, page.Visibility);
                foreach (var text in FindVisualDescendants<TextBlock>(page))
                {
                    Assert.True(!ReferenceEquals(text.Style, headingStyle) && !ReferenceEquals(text.Style, subtitleStyle),
                        $"The {destination.Name} page still contains the removed heading '{text.Text}' at {size}.");
                    Assert.True(destination.Name != "downloads" || text.Text != "Downloads" || text.FontSize != 26,
                        $"The downloads page still contains its removed heading at {size}.");
                    var bindingPath = BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path;
                    Assert.True(bindingPath is null || !removedBindings.Contains(bindingPath),
                        $"The {destination.Name} page still contains the removed subtitle binding '{bindingPath}' at {size}.");
                }
                if (destination.Name == "downloads")
                {
                    var buttons = FindVisualDescendants<Button>(page).ToArray();
                    Assert.True(buttons.Any(button => ReferenceEquals(button.Command, main.OpenDownloadFolderCommand)));
                    Assert.True(buttons.Any(button => ReferenceEquals(button.Command, main.DownloadVodUrlCommand)));
                    Assert.True(buttons.Any(button => ReferenceEquals(button.Command, main.ShowDownloadsSettingsCommand)));
                }
                SaveResponsiveWindowImage(window, $"library-without-headers-{destination.Name}-{size.Width}");
            }
        }
        return Task.CompletedTask;
    });

    private static Task LibraryDestinationScrollAsync() => WithStudioPolishWindowAsync((window, main, _) =>
    {
        AddRefinementLibraryFixtures(main);
        foreach (var size in new[] { new Size(1320, 820), new Size(700, 640) })
        {
            main.ShowFollowedHomePageCommand.Execute(null);
            LayoutStudioPolishWindow(window, size);
            var viewer = (ScrollViewer)window.FindName("HomeContentScrollViewer");
            viewer.ScrollToVerticalOffset(460);
            LayoutStudioPolishWindow(window, size);
            Assert.True(viewer.VerticalOffset > 400, "Following must be scrolled for the regression.");

            main.ShowTwitchVodsHomePageCommand.Execute(null);
            LayoutStudioPolishWindow(window, size);
            Assert.True(viewer.VerticalOffset < 1,
                $"Past broadcasts inherited Following's {viewer.VerticalOffset}px scroll offset at {size}.");
        }
        return Task.CompletedTask;
    });

    private static Task LibraryReturnScrollAsync() => WithStudioPolishWindowAsync((window, main, _) =>
    {
        AddRefinementLibraryFixtures(main);
        var size = new Size(1320, 820);
        LayoutStudioPolishWindow(window, size);
        var viewer = (ScrollViewer)window.FindName("HomeContentScrollViewer");
        viewer.ScrollToVerticalOffset(180);
        LayoutStudioPolishWindow(window, size);

        main.IsSettingsOpen = true;
        LayoutStudioPolishWindow(window, size);
        main.IsSettingsOpen = false;
        LayoutStudioPolishWindow(window, size);
        Assert.True(Math.Abs(viewer.VerticalOffset - 180) < 1,
            "Returning from Settings must retain the current library position.");

        main.ShowTwitchVodsHomePageCommand.Execute(null);
        LayoutStudioPolishWindow(window, size);
        viewer.ScrollToVerticalOffset(420);
        LayoutStudioPolishWindow(window, size);
        main.ShowFollowedHomePageCommand.Execute(null);
        LayoutStudioPolishWindow(window, size);
        Assert.True(Math.Abs(viewer.VerticalOffset - 180) < 1,
            $"Following lost its own position: expected 180px, got {viewer.VerticalOffset}px.");

        // Multiple navigation notifications before layout must not save an intermediate page.
        main.ShowTwitchVodsHomePageCommand.Execute(null);
        main.ShowFollowedHomePageCommand.Execute(null);
        LayoutStudioPolishWindow(window, size);
        Assert.True(Math.Abs(viewer.VerticalOffset - 180) < 1);
        main.ShowTwitchVodsHomePageCommand.Execute(null);
        LayoutStudioPolishWindow(window, size);
        Assert.True(Math.Abs(viewer.VerticalOffset - 420) < 1,
            "Broadcasts should retain their independent position through rapid navigation.");
        main.GoBackCommand.Execute(null);
        LayoutStudioPolishWindow(window, size);
        Assert.True(Math.Abs(viewer.VerticalOffset - 180) < 1,
            "Back navigation must restore the destination's own position.");
        return Task.CompletedTask;
    });

    private static Task LiveCardRowsPaintAsync() => WithStudioPolishWindowAsync((window, main, _) =>
    {
        AddStudioCardFixtures(main);
        LayoutStudioPolishWindow(window, new Size(1320, 820));
        var root = (FrameworkElement)window.Content;
        var items = FindVisualDescendants<ItemsControl>(root)
            .Single(control => ReferenceEquals(control.ItemsSource, main.LiveFollowedChannels));
        var previews = FindVisualDescendants<AspectRatioDecorator>(items).ToArray();
        Assert.Equal(6, previews.Length);
        var bitmap = WpfVisualTest.Render(root);
        foreach (var preview in previews)
        {
            var point = preview.TransformToAncestor(root).Transform(new Point(20, preview.ActualHeight / 2));
            Assert.True(point.Y < root.ActualHeight, "Both rows must be visible in this fixture.");
            var actual = WpfVisualTest.PixelColor(bitmap, (int)point.X, (int)point.Y);
            Assert.Equal(WpfVisualTest.PaletteColor(window, "StudioSurface2Color"), actual);
        }
        SaveResponsiveWindowImage(window, "refinement-card-rows");
        return Task.CompletedTask;
    });

    private static Task LibraryReplacementScrollAsync() => WithStudioPolishWindowAsync((window, main, _) =>
    {
        AddRefinementLibraryFixtures(main);
        var size = new Size(1320, 820);
        var viewer = (ScrollViewer)window.FindName("HomeContentScrollViewer");
        LayoutStudioPolishWindow(window, size);
        viewer.ScrollToVerticalOffset(180);
        LayoutStudioPolishWindow(window, size);
        main.ShowTwitchVodsHomePageCommand.Execute(null);
        LayoutStudioPolishWindow(window, size);
        viewer.ScrollToVerticalOffset(460);
        LayoutStudioPolishWindow(window, size);
        main.ShowFollowedHomePageCommand.Execute(null);
        LayoutStudioPolishWindow(window, size);

        var broadcasts = main.TwitchVods.ToArray();
        main.TwitchVods.Clear();
        foreach (var broadcast in broadcasts) main.TwitchVods.Add(broadcast);
        LayoutStudioPolishWindow(window, size);
        Assert.True(Math.Abs(viewer.VerticalOffset - 180) < 1,
            "Replacing a hidden list must not move the current page.");
        main.ShowTwitchVodsHomePageCommand.Execute(null);
        LayoutStudioPolishWindow(window, size);
        Assert.True(viewer.VerticalOffset < 1, "A replacement list must discard its old saved position.");
        return Task.CompletedTask;
    });

    private static Task SearchResultsKeyboardAsync() => WithResponsiveWindowAsync(withVideo: false, async (window, main) =>
    {
        main.NewStreamText = "keyboardfixture";
        await main.AddAndPlayCommand.ExecuteAsync();
        main.DismissStreamSearchDropdown();
        main.StreamSearchResults.Clear();
        var opened = new List<string>();
        for (var index = 0; index < 12; index++)
        {
            var channel = "keyboardfixture" + index;
            main.StreamSearchResults.Add(new StreamSearchResultViewModel(new StreamSearchChannel(
                PlatformKind.Twitch, channel, "Keyboard result " + index, "fixture://" + channel,
                "", "A live channel", "Just Chatting",
                index == 1 ? StreamSearchChannelState.Unavailable : StreamSearchChannelState.Live,
                StreamSearchSourceStatus.Available, "Fixture channel", index != 1),
                (result, _) => { opened.Add(result.Channel); return Task.CompletedTask; }));
        }
        main.ShowStreamSearchDropdown();
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var search = (TextBox)window.FindName("HomeStreamSearchTextBox");
        window.Topmost = true;
        await NativeWindowTest.RequireForegroundAsync(handle, TimeSpan.FromSeconds(2),
            "Search keyboard navigation requires the test window to own the click point");
        PumpResponsiveLayout(window);
        // Activate through the same physical click as a user. WPF's logical focus and
        // Window.Activate alone can leave another native window owning keyboard input.
        var center = search.PointToScreen(new Point(search.ActualWidth / 2, search.ActualHeight / 2));
        var clickX = (int)Math.Round(center.X);
        var clickY = (int)Math.Round(center.Y);
        Assert.True(NativeWindowTest.IsRootWindowAtPoint(handle, clickX, clickY),
            "The test window must own the search click point. " + NativeWindowTest.DescribeWindowAtPoint(clickX, clickY));
        var restoreCursor = NativeWindowTest.TryGetCursorPosition(out var originalCursor);
        try
        {
            NativeWindowTest.SendLeftClick(clickX, clickY);
            await TestWait.UntilAsync(() => search.IsKeyboardFocused, TimeSpan.FromSeconds(2));
            await NativeWindowTest.RequireForegroundAsync(handle, TimeSpan.FromSeconds(2), "Search keyboard navigation");
        }
        finally
        {
            if (restoreCursor) NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
        }
        Assert.True(search.IsKeyboardFocused, "The actual search input must have keyboard focus.");
        Assert.True(NativeWindowTest.GetForegroundWindow() == handle,
            $"Physical search input requires native foreground: test window {handle}, " +
            $"foreground {NativeWindowTest.GetForegroundWindow()}, active {window.IsActive}.");
        var popup = (Popup)window.FindName("HomeStreamSearchPopup");
        var buttons = FindVisualDescendants<Button>(popup.Child)
            .Where(button => button.DataContext is StreamSearchResultViewModel).ToArray();
        Assert.Equal(12, buttons.Length);
        Assert.Equal(false, buttons[1].IsEnabled);

        async Task Press(ushort key, IInputElement expectedFocus)
        {
            NativeWindowTest.SendVirtualKeySequence((key, false), (key, true));
            await TestWait.UntilAsync(() => ReferenceEquals(Keyboard.FocusedElement, expectedFocus), TimeSpan.FromSeconds(2));
            PumpResponsiveLayout(window);
        }

        await Press(0x28, buttons[0]); // Down enters the results.
        await Press(0x28, buttons[2]); // Disabled results must be skipped.
        await Press(0x26, buttons[0]);
        await Press(0x26, search);
        await Press(0x26, buttons[^1]); // Up from the input reaches the final result.
        var viewer = FindVisualDescendants<ScrollViewer>(popup.Child).Single();
        Assert.True(viewer.VerticalOffset > 0, "Keyboard navigation must scroll the focused result into view.");
        var bounds = buttons[^1].TransformToAncestor(viewer).TransformBounds(new Rect(buttons[^1].RenderSize));
        Assert.True(bounds.Top >= -1 && bounds.Bottom <= viewer.ActualHeight + 1);
        SaveResponsiveWindowImage(window, "refinement-search-keyboard");
        SaveRefinementElementImage((FrameworkElement)popup.Child, "refinement-search-popup");
        NativeWindowTest.SendVirtualKeySequence((0x0D, false), (0x0D, true));
        await TestWait.UntilAsync(() => opened.Count == 1, TimeSpan.FromSeconds(2));
        Assert.Equal("keyboardfixture11", opened.Single());

        NativeWindowTest.SendVirtualKeySequence((0x1B, false), (0x1B, true));
        await TestWait.UntilAsync(() => !main.IsStreamSearchPanelVisible, TimeSpan.FromSeconds(2));
        Assert.True(search.IsKeyboardFocused);
        Assert.Equal("keyboardfixture", main.NewStreamText);
        Assert.Equal(12, main.StreamSearchResults.Count);
        await Press(0x28, buttons[0]);
        Assert.True(main.IsStreamSearchPanelVisible, "Down should explicitly reopen retained results after Escape.");
        main.DismissStreamSearchDropdown();
    });

    private static Task LibraryCardsAcrossThemesAsync() => WithResponsiveWindowAsync(withVideo: false, (window, main) =>
    {
        ResizeResponsiveWindow(window, 1320, 820);
        main.IsSettingsOpen = true;
        foreach (var category in Enum.GetValues<SettingsCategory>())
        {
            main.SelectedSettingsCategory = category;
            PumpResponsiveLayout(window);
        }
        main.IsSettingsOpen = false;
        AddStudioCardFixtures(main);
        var root = (FrameworkElement)window.Content;
        try
        {
            foreach (var theme in Enum.GetValues<AppTheme>())
            {
                StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(theme);
                PumpResponsiveLayout(window);
                var items = FindVisualDescendants<ItemsControl>(root)
                    .Single(control => ReferenceEquals(control.ItemsSource, main.LiveFollowedChannels));
                var previews = FindVisualDescendants<AspectRatioDecorator>(items).ToArray();
                Assert.Equal(6, previews.Length);
                SaveResponsiveWindowImage(window, "refinement-live-" + theme);
                var bitmap = WpfVisualTest.Render(root);
                foreach (var preview in previews)
                {
                    var point = preview.TransformToAncestor(root).Transform(new Point(20, preview.ActualHeight / 2));
                    Assert.True(point.Y < root.ActualHeight, "Both rows must be visible in this fixture.");
                    var actual = WpfVisualTest.PixelColor(bitmap, (int)point.X, (int)point.Y);
                    var expected = WpfVisualTest.PaletteColor(window, "StudioSurface2Color");
                    Assert.True(actual == expected,
                        $"{theme} preview at {point} did not paint: expected {expected}, got {actual}.");
                }
            }
        }
        finally
        {
            StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Dark);
        }
        return Task.CompletedTask;
    });

    private static void SaveRefinementElementImage(FrameworkElement element, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SVS_RESPONSIVE_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        System.IO.Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(WpfVisualTest.Render(element)));
        using var output = System.IO.File.Create(System.IO.Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }

    private static void AddRefinementLibraryFixtures(MainViewModel main)
    {
        for (var index = 0; index < 30; index++)
        {
            var channel = "libraryfixture" + index;
            main.LiveFollowedChannels.Add(new LiveStreamCardViewModel(new LiveStreamCardData(
                LiveStreamCardSource.Followed, StreamInputParser.Parse(channel, PlatformKind.Twitch), PlatformKind.Twitch,
                channel, "Library channel " + index, "Live broadcast " + index,
                "Just Chatting", 1234, "", "", DateTimeOffset.UtcNow, false, "en"),
                (_, _) => Task.CompletedTask));
            main.TwitchVods.Add(new VodViewModel(new TwitchVodItem(
                (1000 + index).ToString(CultureInfo.InvariantCulture), "stream", "broadcaster", channel, channel,
                "Past broadcast " + index, "", "https://www.twitch.tv/videos/" + (1000 + index), "",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, TimeSpan.FromHours(2), 1234,
                TwitchVodTypeFilter.Archive), (_, _) => Task.CompletedTask));
        }
    }
}

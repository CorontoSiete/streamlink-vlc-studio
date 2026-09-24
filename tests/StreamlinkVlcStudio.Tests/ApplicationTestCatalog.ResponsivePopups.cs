internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> ResponsivePopupTests =>
    [
        ("responsive clipping update banner text and actions remain reachable in narrow and short windows", () =>
            WithResponsiveWindowAsync(withVideo: false, (window, viewModel) =>
            {
                var failures = new List<string>();
                var updateChanged = typeof(MainViewModel).GetMethod(
                    "OnAppUpdateStateChanged", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(updateChanged);
                foreach (var phase in new[] { AppUpdatePhase.Available, AppUpdatePhase.Ready, AppUpdatePhase.NotifyOnly })
                {
                    var message = $"Version 9.8.7 is {phase}. Review the release and choose when to update the application.";
                    updateChanged!.Invoke(viewModel, [null, new AppUpdateStateChangedEventArgs(new AppUpdateState(phase, message))]);
                    foreach (var size in ResponsivePopupWindowSizes)
                    {
                        var client = ResizeResponsiveWindow(window, size.Width, size.Height);
                        var actions = FindVisualDescendants<Button>(window)
                            .Where(button => button.IsVisible &&
                                (ReferenceEquals(button.Command, viewModel.UpdateAppCommand) ||
                                 ReferenceEquals(button.Command, viewModel.LaterUpdateCommand)))
                            .ToArray();
                        Assert.Equal(2, actions.Length);
                        foreach (var action in actions)
                        {
                            RecordResponsiveClippingFailure(failures, $"{phase} {size.Width}x{size.Height}", () =>
                                AssertResponsiveControlCompletelyReachable(window, action, client));
                        }

                        var updateText = FindVisualDescendants<TextBlock>(window)
                            .Where(text => text.IsVisible && (text.Text == message || text.Text == "Application update"))
                            .ToArray();
                        Assert.Equal(2, updateText.Length);
                        foreach (var text in updateText)
                        {
                            RecordResponsiveClippingFailure(failures, $"{phase} {size.Width}x{size.Height} update text", () =>
                            {
                                Assert.True(text.ActualWidth >= 70,
                                    $"Update text '{text.Text}' has only {text.ActualWidth:0.##} DIPs available.");
                                AssertResponsiveControlCompletelyReachable(window, text, client);
                            });
                        }
                        if (size.Width is 320 or 200)
                        {
                            ResetResponsivePopupScrollOrigin(window, window);
                            SaveResponsiveWindowImage(window, $"update-banner-{phase}-{size.Width}x{size.Height}");
                        }
                    }
                }
                Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
                return Task.CompletedTask;
            })),
        ("responsive clipping home search keeps channel text state badges and result actions reachable", () =>
            WithResponsiveWindowAsync(withVideo: false, (window, viewModel) =>
            {
                var failures = new List<string>();
                // Populate only the presentation state. Empty image URLs and a local
                // no-op open callback keep this layout regression independent of the network.
                foreach (var state in new[] { StreamSearchChannelState.Live, StreamSearchChannelState.Offline, StreamSearchChannelState.Unavailable })
                {
                    var channel = new StreamSearchChannel(
                        PlatformKind.Twitch,
                        $"responsive{state}",
                        $"Responsive {state} channel",
                        $"fixture://responsive/{state}",
                        "",
                        "Responsive fixture stream",
                        "Fixture category",
                        state,
                        StreamSearchSourceStatus.Available,
                        state == StreamSearchChannelState.Live ? "Playable stream found." : "Fixture stream is currently unavailable.",
                        state == StreamSearchChannelState.Live,
                        ViewerCount: state == StreamSearchChannelState.Live ? 1234567 : null);
                    viewModel.StreamSearchResults.Add(new StreamSearchResultViewModel(channel, (_, _) => Task.CompletedTask));
                }

                var openDropdown = typeof(MainViewModel).GetMethod(
                    "SetStreamSearchDropdownOpen", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(openDropdown);
                openDropdown!.Invoke(viewModel, [true]);
                PumpResponsiveLayout(window);
                var popup = (Popup)window.FindName("HomeStreamSearchPopup");
                Assert.True(popup.IsOpen);
                var popupRoot = (FrameworkElement)popup.Child;
                foreach (var size in ResponsivePopupWindowSizes)
                {
                    ResizeResponsiveWindow(window, size.Width, size.Height);
                    var popupBounds = GetResponsiveScreenBounds(popupRoot);
                    Assert.True(popupBounds.Width > 0 && popupBounds.Height > 0);
                    var buttons = FindVisualDescendants<Button>(popupRoot)
                        .Where(button => button.DataContext is StreamSearchResultViewModel)
                        .ToArray();
                    Assert.Equal(3, buttons.Length);
                    foreach (var button in buttons)
                    {
                        var result = (StreamSearchResultViewModel)button.DataContext;
                        var context = $"{size.Width}x{size.Height} {result.State}";
                        RecordResponsiveClippingFailure(failures, context + " result action", () =>
                            AssertResponsiveControlCompletelyReachable(window, button, popupBounds));
                        var resultText = FindVisualDescendants<TextBlock>(button)
                            .Where(text => text.IsVisible &&
                                (text.Text == result.DisplayName || text.Text == result.CategoryName ||
                                 text.Text == result.StatusText || text.Text == result.StateText ||
                                 (result.HasViewerCount && text.Text == result.ViewerCountText)))
                            .ToArray();
                        Assert.Equal(result.HasViewerCount ? 5 : 4, resultText.Length);
                        foreach (var text in resultText)
                        {
                            RecordResponsiveClippingFailure(failures, context + $" '{text.Text}'", () =>
                            {
                                if (text.Text == result.DisplayName || text.Text == result.CategoryName || text.Text == result.StatusText)
                                {
                                    Assert.True(text.ActualWidth >= 70,
                                        $"Search text '{text.Text}' has only {text.ActualWidth:0.##} DIPs available.");
                                }
                                AssertResponsiveControlCompletelyReachable(window, text, popupBounds);
                            });
                        }
                    }
                    if (size.Width is 320 or 200)
                    {
                        ResetResponsivePopupScrollOrigin(window, popupRoot);
                        SaveResponsivePopupImage(popupRoot, $"search-popup-{size.Width}x{size.Height}");
                    }
                }
                Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
                return Task.CompletedTask;
            }))
    ];

    private static IReadOnlyList<(int Width, int Height)> ResponsivePopupWindowSizes =>
    [
        (480, 320),
        (320, 240),
        (200, 160),
        (1000, 160),
        (1100, 760)
    ];

    private static void ResetResponsivePopupScrollOrigin(MainWindow window, FrameworkElement root)
    {
        foreach (var scrollViewer in FindVisualDescendants<ScrollViewer>(root))
        {
            scrollViewer.ScrollToTop();
            scrollViewer.ScrollToLeftEnd();
        }
        PumpResponsiveLayout(window);
    }

    private static void SaveResponsivePopupImage(FrameworkElement popupRoot, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SVS_RESPONSIVE_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        System.IO.Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(WpfVisualTest.Render(popupRoot)));
        using var output = System.IO.File.Create(System.IO.Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }
}

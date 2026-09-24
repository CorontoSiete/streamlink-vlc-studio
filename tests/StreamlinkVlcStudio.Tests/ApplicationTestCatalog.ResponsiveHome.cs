internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> ResponsiveHomeControlTests =>
    [
        ("responsive clipping VOD platform and filter buttons remain completely reachable", () =>
            WithResponsiveWindowAsync(withVideo: false, (window, viewModel) =>
            {
                var failures = new List<string>();
                // The shared fixture has no VOD/browse provider and never starts
                // application startup, so changing pages cannot send requests.
                viewModel.ShowTwitchVodsHomePageCommand.Execute(null);
                foreach (var size in new (int Width, int Height)[] { (320, 480), (480, 320), (1100, 760) })
                {
                    var client = ResizeResponsiveWindow(window, size.Width, size.Height);
                    foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
                    {
                        (platform == PlatformKind.Twitch
                            ? viewModel.SelectTwitchVodPlatformCommand
                            : viewModel.SelectKickVodPlatformCommand).Execute(null);
                        PumpResponsiveLayout(window);
                        Assert.True(viewModel.IsTwitchVodsHomePageVisible);
                        var commands = new List<ICommand>
                        {
                            viewModel.SelectTwitchVodPlatformCommand,
                            viewModel.SelectKickVodPlatformCommand
                        };
                        if (platform == PlatformKind.Twitch)
                        {
                            commands.AddRange([
                                viewModel.ShowPastBroadcastsVodFilterCommand,
                                viewModel.ShowHighlightsVodFilterCommand,
                                viewModel.ShowUploadsVodFilterCommand,
                                viewModel.ShowAllVodFilterCommand]);
                        }
                        foreach (var command in commands)
                        {
                            var button = FindVisualDescendants<Button>(window)
                                .Single(candidate => candidate.IsVisible && ReferenceEquals(candidate.Command, command));
                            RecordResponsiveClippingFailure(failures, $"{size.Width}x{size.Height} {platform} VODs", () =>
                            {
                                AssertResponsiveControlCompletelyReachable(window, button, client,
                                    (FrameworkElement)window.FindName("HomeViewport"));
                                AssertResponsiveActionLabelsFullyVisible(button, client);
                            });
                        }
                    }
                }
                Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
                return Task.CompletedTask;
            })),
        ("responsive clipping browse category and stream controls remain completely reachable", () =>
            WithResponsiveWindowAsync(withVideo: false, (window, viewModel) =>
            {
                var failures = new List<string>();
                viewModel.ShowBrowseHomePageCommand.Execute(null);
                var setStreamsPage = typeof(MainViewModel).GetMethod("SetBrowseStreamsPageSelected",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(setStreamsPage);
                foreach (var size in new (int Width, int Height)[] { (320, 480), (480, 320), (1100, 760) })
                {
                    var client = ResizeResponsiveWindow(window, size.Width, size.Height);
                    foreach (var streamsPage in new[] { false, true })
                    {
                        // Select the UI state directly; loading category/stream data
                        // is independent of this layout and is deliberately absent.
                        setStreamsPage!.Invoke(viewModel, [streamsPage]);
                        PumpResponsiveLayout(window);
                        Assert.Equal(streamsPage, viewModel.IsBrowseStreamsPageVisible);
                        Assert.Equal(!streamsPage, viewModel.IsBrowseCategoriesPageVisible);
                        foreach (var command in new ICommand[]
                        {
                            viewModel.SelectTwitchBrowsePlatformCommand,
                            viewModel.SelectKickBrowsePlatformCommand,
                            viewModel.RefreshBrowseCommand
                        })
                        {
                            var button = FindVisualDescendants<Button>(window)
                                .Single(candidate => candidate.IsVisible && ReferenceEquals(candidate.Command, command));
                            RecordResponsiveClippingFailure(failures,
                                $"{size.Width}x{size.Height} browse {(streamsPage ? "streams" : "categories")}", () =>
                            {
                                AssertResponsiveControlCompletelyReachable(window, button, client,
                                    (FrameworkElement)window.FindName("HomeViewport"));
                                AssertResponsiveActionLabelsFullyVisible(button, client);
                            });
                        }
                    }
                }
                Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
                return Task.CompletedTask;
            }))
    ];
}

using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Threading;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> BrowseScrollTests { get; } =
    [
        .. from platform in new[] { PlatformKind.Twitch, PlatformKind.Kick }
         from returnRoute in new[] { "Back", "Browse", "Categories", "Platform" }
         from categoryOffset in new[] { 0d, 360d }
         select ($"browse scroll restores {platform} categories at {categoryOffset} through {returnRoute}",
             (Func<Task>)(() => BrowseScrollReturnAsync(platform, returnRoute, categoryOffset))),
        .. from platform in new[] { PlatformKind.Twitch, PlatformKind.Kick }
           from scenario in new[] { "rapid navigation", "delayed streams", "canceled streams", "pagination", "new list", "resize" }
           select ($"browse scroll handles {platform} {scenario}",
               (Func<Task>)(() => BrowseScrollEdgeCaseAsync(platform, scenario)))
    ];

    private static Task BrowseScrollEdgeCaseAsync(PlatformKind platform, string scenario)
    {
        return TestSta.RunAsync(async () =>
        {
            var pendingStreams = new TaskCompletionSource<BrowseResult<BrowseLiveStream>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var delayed = scenario is "delayed streams" or "canceled streams";
            await using var fixture = new BrowseScrollFixture(platform, scenario == "pagination",
                delayed ? service => new BrowseScrollDelayedService(service, pendingStreams.Task) : null);
            var main = fixture.ViewModel;
            fixture.ScrollTo(scenario == "resize" ? fixture.ScrollViewer.ScrollableHeight : 360);
            var savedOffset = fixture.ScrollViewer.VerticalOffset;
            var category = main.BrowseCategories[0];
            var opening = category.SelectCommand.ExecuteAsync();
            try
            {
                if (scenario != "rapid navigation")
                {
                    fixture.Settle();
                    fixture.AssertOffset(0, "Stream page should start at the top even while loading");
                }

                if (delayed)
                {
                    Assert.True(main.IsBrowseStreamsLoading);
                    if (scenario == "delayed streams")
                    {
                        pendingStreams.SetResult(fixture.BrowseService.StreamResponder!(new BrowseStreamRequest(platform, category.Id)));
                        await opening;
                        fixture.Settle();
                        fixture.AssertOffset(0, "Async results must not inherit the category offset");
                    }
                }
                else
                {
                    await opening;
                }

                if (scenario != "rapid navigation")
                {
                    fixture.ScrollTo(fixture.ScrollViewer.ScrollableHeight);
                }

                if (scenario == "resize")
                {
                    fixture.Window.Width = 1600;
                    fixture.Window.Height = 900;
                    fixture.Settle();
                }

                if (scenario == "new list")
                {
                    (platform == PlatformKind.Twitch ? main.SelectKickBrowsePlatformCommand : main.SelectTwitchBrowsePlatformCommand)
                        .Execute(null);
                    fixture.Settle();
                    fixture.AssertOffset(0, "A different platform has a new category list");
                    fixture.ScrollTo(360);
                    main.BrowseCategorySearchText = "new query";
                    await main.RefreshBrowseCommand.ExecuteAsync();
                    fixture.Settle();
                    fixture.AssertOffset(0, "A new search must discard the previous list's offset");
                    return;
                }

                main.GoBackCommand.Execute(null);
                fixture.Settle();
                Assert.True(main.IsBrowseCategoriesPageVisible);
                fixture.AssertOffset(Math.Min(savedOffset, fixture.ScrollViewer.ScrollableHeight),
                    "Returning must restore the saved category position within the current extent");
                Assert.Equal(1, fixture.BrowseService.CategoryRequests.Count);

                if (scenario == "canceled streams")
                {
                    // A provider may finish after cancellation. It must not move the returned page.
                    pendingStreams.SetResult(fixture.BrowseService.StreamResponder!(new BrowseStreamRequest(platform, category.Id)));
                    await opening;
                    fixture.Settle();
                    fixture.AssertOffset(savedOffset, "Late stream results must not change the restored page");
                }

                if (scenario == "pagination")
                {
                    Assert.True(main.CanLoadMoreBrowseCategories);
                    fixture.ScrollTo(fixture.ScrollViewer.ScrollableHeight);
                    Assert.Equal(2, fixture.BrowseService.CategoryRequests.Count);
                    Assert.Equal(72, main.BrowseCategories.Count);
                    fixture.ScrollTo(fixture.ScrollViewer.ScrollableHeight - 200);
                    savedOffset = fixture.ScrollViewer.VerticalOffset;
                    await main.BrowseCategories[^1].SelectCommand.ExecuteAsync();
                    fixture.Settle();
                    fixture.ScrollTo(500);
                    main.GoBackCommand.Execute(null);
                    fixture.Settle();
                    fixture.AssertOffset(savedOffset, "Appended categories must retain their saved position");
                    Assert.Equal(2, fixture.BrowseService.CategoryRequests.Count);
                }

                if (scenario == "rapid navigation")
                {
                    // Leave again before the stream reset executes; it must not affect Recent.
                    await main.BrowseCategories[1].SelectCommand.ExecuteAsync();
                    main.ShowRecentHomePageCommand.Execute(null);
                    fixture.Settle();
                    main.ShowBrowseHomePageCommand.Execute(null);
                    fixture.Settle();
                    fixture.AssertOffset(savedOffset, "Intervening Home pages must not overwrite the category position");
                }
            }
            finally
            {
                pendingStreams.TrySetResult(BrowseResult<BrowseLiveStream>.Unavailable("Test complete"));
                await opening;
            }
        });
    }

    private static Task BrowseScrollReturnAsync(PlatformKind platform, string returnRoute, double categoryOffset)
    {
        return TestSta.RunAsync(async () =>
        {
            await using var fixture = new BrowseScrollFixture(platform);
            var main = fixture.ViewModel;
            fixture.ScrollTo(categoryOffset);
            var savedOffset = fixture.ScrollViewer.VerticalOffset;
            Assert.True(Math.Abs(savedOffset - categoryOffset) < 0.01, "Category scroll setup failed.");

            // Invoke the actual category button, including WPF's Click/Command ordering.
            var categoryButton = FindVisualDescendants<Button>(fixture.Window)
                .Single(button => ReferenceEquals(button.DataContext, main.BrowseCategories[0]));
            InvokeBrowseScrollButton(categoryButton);
            fixture.Settle();
            Assert.True(main.IsBrowseStreamsPageVisible);
            fixture.AssertOffset(0, "Entering a category should start at the top");
            fixture.ScrollTo(fixture.ScrollViewer.ScrollableHeight);
            Assert.True(fixture.ScrollViewer.VerticalOffset > 500, "Stream list must be scrolled for this regression.");

            double? firstCategoryFrameOffset = null;
            var returnTimer = Stopwatch.StartNew();
            void OnCategoryFrame(object? sender, EventArgs e)
            {
                if (main.IsBrowseCategoriesPageVisible && firstCategoryFrameOffset is null)
                {
                    firstCategoryFrameOffset = fixture.ScrollViewer.VerticalOffset;
                    Console.WriteLine($"{platform} {returnRoute}: first category frame after {returnTimer.Elapsed.TotalMilliseconds:F1} ms, " +
                        $"offset {firstCategoryFrameOffset}, expected {savedOffset}.");
                }
            }

            CompositionTarget.Rendering += OnCategoryFrame;
            try
            {
                switch (returnRoute)
                {
                    case "Back":
                        main.GoBackCommand.Execute(null);
                        break;
                    case "Browse":
                        fixture.InvokeCommandButton(main.ShowBrowseHomePageCommand);
                        break;
                    case "Categories":
                        main.ReturnToBrowseCategoriesCommand.Execute(null);
                        break;
                    case "Platform":
                        fixture.InvokeCommandButton(platform == PlatformKind.Twitch
                            ? main.SelectTwitchBrowsePlatformCommand
                            : main.SelectKickBrowsePlatformCommand);
                        break;
                }

                fixture.Settle();
                await TestWait.UntilAsync(() => firstCategoryFrameOffset.HasValue, TimeSpan.FromSeconds(2));
                Assert.True(Math.Abs(firstCategoryFrameOffset!.Value - savedOffset) < 0.01,
                    $"The first category frame should already be restored: expected {savedOffset}, got {firstCategoryFrameOffset}.");
            }
            finally
            {
                CompositionTarget.Rendering -= OnCategoryFrame;
            }

            Assert.True(main.IsBrowseCategoriesPageVisible);
            fixture.AssertOffset(savedOffset, $"Returning through {returnRoute} should restore the category position");
            Assert.Equal(1, fixture.BrowseService.CategoryRequests.Count);

            // Commands used by history must behave the same way without a category Click event.
            fixture.ScrollTo(180);
            await main.BrowseCategories[1].SelectCommand.ExecuteAsync();
            fixture.Settle();
            fixture.AssertOffset(0, "Opening a second category through its command should start at the top");
            fixture.ScrollTo(700);
            main.GoBackCommand.Execute(null);
            fixture.Settle();
            fixture.AssertOffset(180, "Repeated visits should save the latest category position");
        });
    }

    private static void InvokeBrowseScrollButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        var provider = (IInvokeProvider?)peer.GetPattern(PatternInterface.Invoke);
        Assert.NotNull(provider);
        provider!.Invoke();
    }

    private sealed class BrowseScrollFixture : IAsyncDisposable
    {
        public FakeBrowseService BrowseService { get; } = new()
        {
            CategoryResponder = request => new BrowseResult<BrowseCategory>(
                BrowseResultStatus.Available,
                Enumerable.Range(1, 36).Select(index => new BrowseCategory(
                    request.Platform, index.ToString(CultureInfo.InvariantCulture),
                    $"Category {index}", "", [])).ToArray(),
                "", "Categories loaded"),
            StreamResponder = request => new BrowseResult<BrowseLiveStream>(
                BrowseResultStatus.Available,
                Enumerable.Range(1, 36).Select(index => new BrowseLiveStream(
                    request.Platform, $"channel{index}", $"Channel {index}", $"Stream {index}",
                    "1", "Category", 1000 - index, "", null, false, "en",
                    request.Platform == PlatformKind.Twitch
                        ? $"https://www.twitch.tv/channel{index}"
                        : $"https://kick.com/channel{index}")).ToArray(),
                "", "Streams loaded")
        };

        public MainViewModel ViewModel { get; }
        public MainWindow Window { get; }
        public ScrollViewer ScrollViewer { get; }

        public BrowseScrollFixture(PlatformKind platform, bool hasMoreCategories = false,
            Func<FakeBrowseService, IBrowseService>? wrapService = null)
        {
            if (hasMoreCategories)
            {
                BrowseService.CategoryResponder = request => new BrowseResult<BrowseCategory>(
                    BrowseResultStatus.Available,
                    Enumerable.Range(string.IsNullOrEmpty(request.Cursor) ? 1 : 37, 36)
                        .Select(index => new BrowseCategory(request.Platform,
                            index.ToString(CultureInfo.InvariantCulture), $"Category {index}", "", [])).ToArray(),
                    string.IsNullOrEmpty(request.Cursor) ? "next" : "", "Categories loaded");
            }

            var settings = new AppSettings();
            ViewModel = TestViewModels.CreateMain(
                settings, new FakeSettingsService(settings), new FakeStreamlinkService(),
                new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(),
                action => action(), browseService: wrapService?.Invoke(BrowseService) ?? BrowseService);
            if (platform == PlatformKind.Kick)
            {
                ViewModel.SelectKickBrowsePlatformCommand.Execute(null);
            }

            ViewModel.ShowBrowseHomePageCommand.Execute(null);
            Window = new MainWindow { Width = 980, Height = 620, DataContext = ViewModel };
            RemoveMainWindowAutomaticStartup(Window);
            SetMainWindowViewModel(Window, ViewModel);
            ScrollViewer = (ScrollViewer)Window.FindName("HomeContentScrollViewer");
            Window.Show();
            Settle();
            Assert.True(ScrollViewer.ScrollableHeight > 500, "Category grid must be scrollable.");
        }

        public void Settle()
        {
            Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Window.UpdateLayout();
        }

        public void ScrollTo(double offset)
        {
            ScrollViewer.ScrollToVerticalOffset(offset);
            Settle();
        }

        public void AssertOffset(double expected, string message)
        {
            Assert.True(Math.Abs(ScrollViewer.VerticalOffset - expected) < 0.01,
                $"{message}: expected {expected}, got {ScrollViewer.VerticalOffset}.");
        }

        public void InvokeCommandButton(ICommand command)
        {
            var button = FindVisualDescendants<Button>(Window)
                .Single(button => button.IsVisible && ReferenceEquals(button.Command, command));
            InvokeBrowseScrollButton(button);
        }

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await ViewModel.DisposeAsync();
        }
    }

    private sealed class BrowseScrollDelayedService(FakeBrowseService service,
        Task<BrowseResult<BrowseLiveStream>> streams) : IBrowseService
    {
        public Task<BrowseResult<BrowseCategory>> GetCategoriesAsync(BrowseCategoryRequest request,
            AppSettings settings, CancellationToken cancellationToken = default) =>
            service.GetCategoriesAsync(request, settings, cancellationToken);

        public Task<BrowseResult<BrowseCategoryViewerCount>> GetCategoryViewerCountsAsync(
            BrowseCategoryViewerCountRequest request, AppSettings settings, CancellationToken cancellationToken = default) =>
            service.GetCategoryViewerCountsAsync(request, settings, cancellationToken);

        public Task<BrowseResult<BrowseLiveStream>> GetStreamsAsync(BrowseStreamRequest request,
            AppSettings settings, CancellationToken cancellationToken = default) => streams;
    }
}

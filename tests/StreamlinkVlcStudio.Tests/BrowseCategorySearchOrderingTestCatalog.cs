internal static class BrowseCategorySearchOrderingTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("browse category search ordering: known counts stay descending across pages and refreshes", () => TestSta.RunOffscreenAsync(KnownCountsAsync)),
        ("browse category search ordering: delayed Twitch counts reorder existing and additional results", () => TestSta.RunOffscreenAsync(DelayedCountsAsync))
    ];

    private static async Task KnownCountsAsync()
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
        {
            BrowseCategory[] firstPage =
            [
                new(platform, "low", "The Lower", "", [], 10),
                new(platform, "unknown", "The Absent Count", "", []),
                new(platform, "high", "The Higher", "", [], 100),
                new(platform, "zero", "The Zero", "", [], 0),
                new(platform, "tie", "The Zebra", "", [], 100)
            ];
            var service = new FakeBrowseService
            {
                CategoryResponder = request => request.Query != "the" ? Page([])
                    : request.Cursor == "more" ? Page(
                    [
                        new(platform, "low", "Duplicate", "", [], 1),
                        new(platform, "most", "The Most Viewed", "", [], 1000),
                        new(platform, "alpha", "The Alpha", "", [], 100)
                    ]) : Page(firstPage, "more"),
                CategoryViewerCountResponder = _ => Task.FromResult(
                    BrowseResult<BrowseCategoryViewerCount>.Unavailable("Count unavailable."))
            };
            await using var viewModel = CreateMain(service);
            if (platform == PlatformKind.Kick) viewModel.SelectKickBrowsePlatformCommand.Execute(null);
            viewModel.ShowBrowseHomePageCommand.Execute(null);
            viewModel.BrowseCategorySearchText = "  the  ";

            Assert.SequenceEqual(new[] { "high", "tie", "low", "zero", "unknown" }, Ids(viewModel));
            var firstCards = viewModel.BrowseCategories.ToArray();

            await viewModel.LoadMoreBrowseCategoriesCommand.ExecuteAsync();

            Assert.SequenceEqual(new[] { "most", "alpha", "high", "tie", "low", "zero", "unknown" }, Ids(viewModel));
            Assert.Equal("more", service.CategoryRequests[^1].Cursor);
            Assert.Equal(10, viewModel.BrowseCategories.Single(card => card.Id == "low").Category.ViewerCount);

            await viewModel.RefreshBrowseCommand.ExecuteAsync();

            Assert.SequenceEqual(firstCards, viewModel.BrowseCategories);
            Assert.SequenceEqual(new[] { "high", "tie", "low", "zero", "unknown" }, Ids(viewModel));
        }
    }

    private static async Task DelayedCountsAsync()
    {
        var counts = new[] { "lower", "higher", "most" }.ToDictionary(id => id,
            _ => new TaskCompletionSource<BrowseResult<BrowseCategoryViewerCount>>(TaskCreationOptions.RunContinuationsAsynchronously));
        var service = new FakeBrowseService
        {
            CategoryResponder = request => request.Query != "the" ? Page([])
                : request.Cursor == "more" ? Page(
                [
                    new(PlatformKind.Twitch, "lower", "Duplicate", "", []),
                    new(PlatformKind.Twitch, "most", "The Most Viewed", "", []),
                    new(PlatformKind.Twitch, "known", "The Known Count", "", [], 500)
                ]) : Page(
                [
                    new(PlatformKind.Twitch, "lower", "The Zebra", "", []),
                    new(PlatformKind.Twitch, "higher", "The Alpha", "", []),
                    new(PlatformKind.Twitch, "zero", "The Zero", "", [], 0)
                ], "more"),
            CategoryViewerCountResponderWithCancellation = (request, token) => counts[request.CategoryIds.Single()].Task.WaitAsync(token)
        };
        await using var viewModel = CreateMain(service);
        viewModel.ShowBrowseHomePageCommand.Execute(null);
        viewModel.BrowseCategorySearchText = "the";

        Assert.Equal(3, viewModel.BrowseCategories.Count);
        Assert.Equal(false, viewModel.IsBrowseCategoriesLoading);
        var higherCard = viewModel.BrowseCategories.Single(card => card.Id == "higher");

        counts["lower"].SetResult(Count("lower", 150));
        await WaitForOrderAsync(viewModel, "lower", "zero", "higher");

        counts["higher"].SetResult(Count("higher", 1500));
        await WaitForOrderAsync(viewModel, "higher", "lower", "zero");

        await viewModel.LoadMoreBrowseCategoriesCommand.ExecuteAsync();

        Assert.SequenceEqual(new[] { "higher", "known", "lower", "zero", "most" }, Ids(viewModel));
        Assert.Equal("more", service.CategoryRequests[^1].Cursor);
        counts["most"].SetResult(Count("most", 20000));
        await WaitForOrderAsync(viewModel, "most", "higher", "known", "lower", "zero");

        Assert.True(ReferenceEquals(higherCard, viewModel.BrowseCategories[1]));
        Assert.Equal(150, viewModel.BrowseCategories.Single(card => card.Id == "lower").Category.ViewerCount);
        Assert.Equal(3, service.CategoryViewerCountRequests.Count);
        Assert.SequenceEqual(new[] { "higher", "lower", "most" },
            service.CategoryViewerCountRequests.SelectMany(request => request.CategoryIds).Order(StringComparer.Ordinal));
    }

    private static MainViewModel CreateMain(FakeBrowseService service)
    {
        var settings = new AppSettings();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        return TestViewModels.CreateMain(settings, new FakeSettingsService(settings), new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(), action => dispatcher.Invoke(action),
            browseService: service, browseCategorySearchDebounceInterval: TimeSpan.Zero);
    }

    private static BrowseResult<BrowseCategory> Page(IReadOnlyList<BrowseCategory> categories, string cursor = "") =>
        new(BrowseResultStatus.Available, categories, cursor, "Loaded categories.");

    private static BrowseResult<BrowseCategoryViewerCount> Count(string id, int viewers) =>
        new(BrowseResultStatus.Available, [new(id, viewers)], "", "Loaded count.");

    private static IEnumerable<string> Ids(MainViewModel viewModel) => viewModel.BrowseCategories.Select(card => card.Id);

    private static Task WaitForOrderAsync(MainViewModel viewModel, params string[] ids) =>
        TestWait.UntilAsync(() => Ids(viewModel).SequenceEqual(ids), TimeSpan.FromSeconds(2));
}

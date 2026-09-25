internal static class BrowseStreamResourceTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("browse stream resources: Refresh joins an initial load and later refreshes again", ShareLoadAsync),
        ("browse stream resources: unchanged refreshes retain cards and commands without collection changes", PreserveCardsAsync),
        ("browse stream resources: refresh reconciles ordering identities and current metadata", UpdateCardsAsync),
        ("browse stream resources: unavailable refresh retains usable results and pagination", () => FailedRefreshAsync(false)),
        ("browse stream resources: thrown refresh retains usable results and pagination", () => FailedRefreshAsync(true)),
        ("browse stream resources: failed Load More can retry the same cursor", RetryPageAsync),
        ("browse stream resources: Refresh supersedes pagination and rejects a late page", RefreshDuringPageAsync),
        ("browse stream resources: successful empty refresh removes stale cards", EmptyRefreshAsync),
        ("browse stream resources: refresh resets completed cursor history", ResetCursorsAsync),
        ("browse stream resources: changing categories rejects late results and loading flags", ChangeCategoryAsync),
        ("browse stream resources: changing platforms cancels the old stream load", ChangePlatformAsync),
        ("browse stream resources: shutdown cancels loading and retained commands start no work", ShutdownAsync)
    ];

    private static Task ShareLoadAsync() => RunAsync(async (viewModel, service) =>
    {
        var select = viewModel.BrowseCategories[0].SelectCommand.ExecuteAsync();
        var refresh = viewModel.RefreshBrowseCommand.ExecuteAsync();
        Assert.Equal(1, service.Requests.Count);
        Assert.Equal(false, service.Requests[0].Token.IsCancellationRequested);
        Assert.Equal(false, refresh.IsCompleted);
        service.Requests[0].Complete([Stream("first")]);
        await Task.WhenAll(select, refresh);
        refresh = viewModel.RefreshBrowseCommand.ExecuteAsync();
        Assert.Equal(2, service.Requests.Count);
        service.Requests[1].Complete([Stream("second")]);
        await refresh;
        Assert.Equal("second", viewModel.BrowseStreams.Single().Channel);
    });

    private static Task PreserveCardsAsync() => RunAsync(async (viewModel, service) =>
    {
        await LoadInitialAsync(viewModel, service, [Stream("first"), Stream("second")]);
        var cards = viewModel.BrowseStreams.ToArray();
        var commands = cards.Select(card => card.OpenCommand).ToArray();
        var changes = 0;
        viewModel.BrowseStreams.CollectionChanged += (_, _) => changes++;
        for (var index = 0; index < 5; index++)
        {
            var oldVersion = cards[0].ThumbnailCacheVersion;
            var refresh = viewModel.RefreshBrowseCommand.ExecuteAsync();
            Assert.SequenceEqual(cards, viewModel.BrowseStreams);
            Assert.True(viewModel.IsBrowseStreamsLoading);
            service.Requests[^1].Complete([Stream("first"), Stream("second")]);
            await refresh;
            Assert.SequenceEqual(cards, viewModel.BrowseStreams);
            Assert.True(cards[0].ThumbnailCacheVersion > oldVersion);
            Assert.Equal(cards[0].ThumbnailCacheVersion, cards[0].ThumbnailImageRequest.CacheVersion);
        }
        Assert.SequenceEqual(commands, viewModel.BrowseStreams.Select(card => card.OpenCommand));
        Assert.Equal(0, changes);
    });

    private static Task UpdateCardsAsync() => RunAsync(async (viewModel, service) =>
    {
        await LoadInitialAsync(viewModel, service, [Stream("same"), Stream("removed"), Stream("same", PlatformKind.Kick)]);
        var twitch = viewModel.BrowseStreams[0];
        var kick = viewModel.BrowseStreams[2];
        var changedProperties = new List<string?>();
        var changes = new List<NotifyCollectionChangedAction>();
        twitch.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);
        viewModel.BrowseStreams.CollectionChanged += (_, e) => changes.Add(e.Action);
        var updated = Stream("SAME") with { Title = "New title", CategoryName = "New category", ViewerCount = 42 };
        var refresh = viewModel.RefreshBrowseCommand.ExecuteAsync();
        service.Requests[^1].Complete([Stream("same", PlatformKind.Kick), updated, Stream("new"), updated]);
        await refresh;
        Assert.Equal(3, viewModel.BrowseStreams.Count);
        Assert.True(ReferenceEquals(kick, viewModel.BrowseStreams[0]));
        Assert.True(ReferenceEquals(twitch, viewModel.BrowseStreams[1]));
        Assert.Equal("New title", twitch.Title);
        Assert.Equal("42", twitch.ViewerCountText);
        Assert.True(changedProperties.Contains(nameof(LiveStreamCardViewModel.Title)));
        Assert.True(changedProperties.Contains(nameof(LiveStreamCardViewModel.ViewerCountText)));
        Assert.Equal(false, changes.Contains(NotifyCollectionChangedAction.Reset));
        await twitch.OpenAndStayOnHomeCommand.ExecuteAsync();
        Assert.Equal("New category", viewModel.Tabs.Single().Target.CategoryName);
    });

    private static Task FailedRefreshAsync(bool throws) => RunAsync(async (viewModel, service) =>
    {
        await LoadInitialAsync(viewModel, service, [Stream("first")], "next");
        var card = viewModel.BrowseStreams.Single();
        var refresh = viewModel.RefreshBrowseCommand.ExecuteAsync();
        if (throws) service.Requests[^1].Fail();
        else service.Requests[^1].CompleteUnavailable();
        await refresh;
        Assert.True(ReferenceEquals(card, viewModel.BrowseStreams.Single()));
        Assert.True(viewModel.CanLoadMoreBrowseStreams);
        Assert.Equal(false, viewModel.IsBrowseStreamsLoading);
        Assert.True(viewModel.BrowseStatus.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        var more = viewModel.LoadMoreBrowseStreamsCommand.ExecuteAsync();
        Assert.Equal("next", service.Requests[^1].Request.Cursor);
        service.Requests[^1].Complete([Stream("second")]);
        await more;
        Assert.Equal(2, viewModel.BrowseStreams.Count);
    });

    private static Task RetryPageAsync() => RunAsync(async (viewModel, service) =>
    {
        await LoadInitialAsync(viewModel, service, [Stream("first")], "next");
        var more = viewModel.LoadMoreBrowseStreamsCommand.ExecuteAsync();
        service.Requests[^1].CompleteUnavailable();
        await more;
        Assert.True(viewModel.CanLoadMoreBrowseStreams);
        more = viewModel.LoadMoreBrowseStreamsCommand.ExecuteAsync();
        Assert.Equal("next", service.Requests[^1].Request.Cursor);
        Assert.Equal(3, service.Requests.Count);
        service.Requests[^1].Complete([Stream("first"), Stream("second")]);
        await more;
        Assert.Equal(2, viewModel.BrowseStreams.Count);
        Assert.Equal(false, viewModel.CanLoadMoreBrowseStreams);
    });

    private static Task RefreshDuringPageAsync() => RunAsync(async (viewModel, service) =>
    {
        await LoadInitialAsync(viewModel, service, [Stream("first")], "next");
        var more = viewModel.LoadMoreBrowseStreamsCommand.ExecuteAsync();
        var refresh = viewModel.RefreshBrowseCommand.ExecuteAsync();
        Assert.Equal(3, service.Requests.Count);
        Assert.True(service.Requests[1].Token.IsCancellationRequested);
        Assert.Equal("", service.Requests[2].Request.Cursor);
        service.Requests[2].Complete([Stream("fresh")]);
        await refresh;
        service.Requests[1].Complete([Stream("stale")], "stale-cursor");
        await more;
        Assert.Equal("fresh", viewModel.BrowseStreams.Single().Channel);
        Assert.Equal(false, viewModel.CanLoadMoreBrowseStreams);
    }, ignoreCancellation: true);

    private static Task EmptyRefreshAsync() => RunAsync(async (viewModel, service) =>
    {
        await LoadInitialAsync(viewModel, service, [Stream("first")], "next");
        var refresh = viewModel.RefreshBrowseCommand.ExecuteAsync();
        service.Requests[^1].Complete([]);
        await refresh;
        Assert.Equal(0, viewModel.BrowseStreams.Count);
        Assert.True(viewModel.IsBrowseStreamsEmptyVisible);
        Assert.Equal(false, viewModel.CanLoadMoreBrowseStreams);
    });

    private static Task ResetCursorsAsync() => RunAsync(async (viewModel, service) =>
    {
        await LoadInitialAsync(viewModel, service, [Stream("first")], "next");
        var more = viewModel.LoadMoreBrowseStreamsCommand.ExecuteAsync();
        service.Requests[^1].Complete([Stream("second")], "next");
        await more;
        Assert.Equal(false, viewModel.CanLoadMoreBrowseStreams);
        var refresh = viewModel.RefreshBrowseCommand.ExecuteAsync();
        service.Requests[^1].Complete([Stream("first")], "next");
        await refresh;
        Assert.Equal(1, viewModel.BrowseStreams.Count);
        Assert.True(viewModel.CanLoadMoreBrowseStreams);
    });

    private static Task ChangeCategoryAsync() => RunAsync(async (viewModel, service) =>
    {
        var first = viewModel.BrowseCategories[0].SelectCommand.ExecuteAsync();
        var second = viewModel.BrowseCategories[1].SelectCommand.ExecuteAsync();
        Assert.Equal(2, service.Requests.Count);
        Assert.True(service.Requests[0].Token.IsCancellationRequested);
        service.Requests[0].Complete([Stream("stale")]);
        await first;
        Assert.Equal(0, viewModel.BrowseStreams.Count);
        Assert.True(viewModel.IsBrowseStreamsLoading);
        service.Requests[1].Complete([Stream("current")]);
        await second;
        Assert.Equal("current", viewModel.BrowseStreams.Single().Channel);
        Assert.Equal("2", viewModel.SelectedBrowseCategory!.Id);
    }, ignoreCancellation: true);

    private static Task ChangePlatformAsync() => RunAsync(async (viewModel, service) =>
    {
        var first = viewModel.BrowseCategories[0].SelectCommand.ExecuteAsync();
        viewModel.SelectKickBrowsePlatformCommand.Execute(null);
        Assert.True(service.Requests[0].Token.IsCancellationRequested);
        var second = viewModel.BrowseCategories[0].SelectCommand.ExecuteAsync();
        service.Requests[1].Complete([Stream("current", PlatformKind.Kick)]);
        await second;
        service.Requests[0].Complete([Stream("stale")]);
        await first;
        Assert.Equal(PlatformKind.Kick, viewModel.BrowseStreams.Single().Platform);
    }, ignoreCancellation: true);

    private static Task ShutdownAsync() => RunAsync(async (viewModel, service) =>
    {
        var category = viewModel.BrowseCategories[0];
        var select = category.SelectCommand.ExecuteAsync();
        await viewModel.DisposeAsync();
        await select;
        Assert.True(service.Requests.Single().Token.IsCancellationRequested);
        await category.SelectCommand.ExecuteAsync();
        await viewModel.RefreshBrowseCommand.ExecuteAsync();
        await viewModel.LoadMoreBrowseStreamsCommand.ExecuteAsync();
        Assert.Equal(1, service.Requests.Count);
    });

    private static async Task LoadInitialAsync(MainViewModel viewModel, ControlledBrowseService service,
        IReadOnlyList<BrowseLiveStream> streams, string cursor = "")
    {
        var select = viewModel.BrowseCategories[0].SelectCommand.ExecuteAsync();
        service.Requests[^1].Complete(streams, cursor);
        await select;
    }

    private static BrowseLiveStream Stream(string channel, PlatformKind platform = PlatformKind.Twitch) => new(
        platform, channel, channel, "Title", "1", "Category", 10, "https://example.invalid/preview.jpg",
        null, false, "en", $"https://{(platform == PlatformKind.Twitch ? "twitch.tv" : "kick.com")}/{channel}");

    private static Task RunAsync(Func<MainViewModel, ControlledBrowseService, Task> test, bool ignoreCancellation = false) =>
        TestSta.RunOffscreenAsync(async () =>
        {
            var settings = new AppSettings { StreamlinkPath = "streamlink" };
            var service = new ControlledBrowseService(ignoreCancellation);
            await using var viewModel = TestViewModels.CreateMain(settings, new FakeSettingsService(settings),
                new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
                new MemoryLogger(), action => action(), browseService: service);
            try
            {
                viewModel.ShowBrowseHomePageCommand.Execute(null);
                await test(viewModel, service);
            }
            finally
            {
                foreach (var pending in service.Requests) pending.CompleteUnavailable();
            }
        });

    private sealed class PendingRequest(BrowseStreamRequest request, CancellationToken token)
    {
        private readonly TaskCompletionSource<BrowseResult<BrowseLiveStream>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal BrowseStreamRequest Request { get; } = request;
        internal CancellationToken Token { get; } = token;
        internal void Complete(IReadOnlyList<BrowseLiveStream> streams, string cursor = "") =>
            completion.TrySetResult(new(BrowseResultStatus.Available, streams, cursor, "Loaded streams"));
        internal void CompleteUnavailable() => completion.TrySetResult(BrowseResult<BrowseLiveStream>.Unavailable("Streams unavailable"));
        internal void Fail() => completion.TrySetException(new IOException("Streams unavailable"));
        internal Task<BrowseResult<BrowseLiveStream>> WaitAsync(bool ignoreCancellation) =>
            ignoreCancellation ? completion.Task : completion.Task.WaitAsync(Token);
    }

    private sealed class ControlledBrowseService(bool ignoreCancellation) : IBrowseService
    {
        internal List<PendingRequest> Requests { get; } = [];

        public Task<BrowseResult<BrowseCategory>> GetCategoriesAsync(BrowseCategoryRequest request, AppSettings settings,
            CancellationToken cancellationToken = default) => Task.FromResult(new BrowseResult<BrowseCategory>(
                BrowseResultStatus.Available,
                [new(request.Platform, "1", "First", "", [], 10), new(request.Platform, "2", "Second", "", [], 10)], "", "Loaded categories"));

        public Task<BrowseResult<BrowseCategoryViewerCount>> GetCategoryViewerCountsAsync(
            BrowseCategoryViewerCountRequest request, AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BrowseResult<BrowseCategoryViewerCount>(BrowseResultStatus.Available, [], "", ""));

        public Task<BrowseResult<BrowseLiveStream>> GetStreamsAsync(BrowseStreamRequest request, AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            var pending = new PendingRequest(request, cancellationToken);
            Requests.Add(pending);
            return pending.WaitAsync(ignoreCancellation);
        }
    }
}

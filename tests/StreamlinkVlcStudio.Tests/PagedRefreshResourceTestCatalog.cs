internal static class PagedRefreshResourceTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } = Build();

    private static IReadOnlyList<(string Name, Func<Task> Run)> Build()
    {
        var tests = new List<(string, Func<Task>)>();
        foreach (var surface in Enum.GetValues<Surface>())
        {
            tests.Add(($"paged refresh resources: {surface} retains cards and commands across unchanged refreshes", () => PreserveAsync(surface)));
            tests.Add(($"paged refresh resources: {surface} updates metadata ordering and commands in place", () => UpdateAsync(surface)));
            tests.Add(($"paged refresh resources: {surface} unavailable refresh retains results and pagination", () => FailureAsync(surface, false)));
            tests.Add(($"paged refresh resources: {surface} thrown refresh retains results and pagination", () => FailureAsync(surface, true)));
            tests.Add(($"paged refresh resources: {surface} failed page retries without consuming its cursor", () => RetryPageAsync(surface)));
            tests.Add(($"paged refresh resources: {surface} successful empty refresh clears results and cursor", () => EmptyAsync(surface)));
            tests.Add(($"paged refresh resources: {surface} refresh supersedes a late page and resets cursor history", () => SupersedeAsync(surface)));
            tests.Add(($"paged refresh resources: {surface} changing query clears results and rejects late refresh", () => ChangeQueryAsync(surface)));
        }
        tests.Add(("paged refresh resources: category navigation during refresh rejects late results", NavigateAsync));
        tests.Add(("paged refresh resources: category refresh reloads viewer counts on retained cards", ViewerCountsAsync));
        tests.Add(("paged refresh resources: overlapping category pages retain enriched counts without requerying", OverlapCountsAsync));
        tests.Add(("paged refresh resources: unchanged Kick category refresh preserves sorted cards without moves", KickOrderingAsync));
        return tests;
    }

    private static Task PreserveAsync(Surface surface) => RunAsync(surface, async scenario =>
    {
        await scenario.InitialAsync([new("1"), new("2")]);
        var original = scenario.Cards;
        var commands = original.Select(Command).ToArray();
        var collections = 0;
        var properties = 0;
        scenario.OnCollectionChanged(() => collections++);
        foreach (var card in original) ((ObservableObject)card).PropertyChanged += (_, _) => properties++;
        for (var index = 0; index < 5; index++)
        {
            var refresh = scenario.RefreshAsync();
            Assert.SequenceEqual(original, scenario.Cards);
            Assert.True(scenario.IsLoading);
            scenario.Service.Requests[^1].Complete([new("1"), new("2")]);
            await refresh;
            Assert.SequenceEqual(original, scenario.Cards);
        }
        Assert.SequenceEqual(commands, scenario.Cards.Select(Command));
        Assert.Equal(0, collections);
        Assert.Equal(0, properties);
    });

    private static Task UpdateAsync(Surface surface) => RunAsync(surface, async scenario =>
    {
        await scenario.InitialAsync([new("1"), new("2"), new("3")]);
        var first = scenario.Cards[0];
        var second = scenario.Cards[1];
        var command = Command(first);
        var changes = new List<string?>();
        ((ObservableObject)first).PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        var updated = new Row("1", "Updated", 42, "https://example.invalid/new.jpg");
        var refresh = scenario.RefreshAsync();
        scenario.Service.Requests[^1].Complete([new("2"), updated, new("4"), updated]);
        await refresh;
        Assert.SequenceEqual(new[] { "2", "1", "4" }, scenario.Ids);
        Assert.True(ReferenceEquals(second, scenario.Cards[0]));
        Assert.True(ReferenceEquals(first, scenario.Cards[1]));
        Assert.True(ReferenceEquals(command, Command(scenario.Cards[1])));
        if (first is VodViewModel vod)
        {
            Assert.Equal("Updated", vod.Title);
            Assert.Equal("42 views", vod.ViewCountText);
            Assert.Equal(updated.Thumbnail, vod.ThumbnailUrl);
            Assert.Equal("2:00", vod.DurationText);
            Assert.True(changes.Contains(nameof(VodViewModel.Title)));
            Assert.True(changes.Contains(nameof(VodViewModel.MetadataText)));
            Assert.True(changes.Contains(nameof(VodViewModel.ThumbnailUrl)));
            Assert.True(changes.Contains(nameof(VodViewModel.Target)));
            if (surface == Surface.TwitchVod) Assert.True(vod.IsSubscriberOnly);
            await vod.OpenAndStayOnHomeCommand.ExecuteAsync();
            Assert.Equal("Updated", scenario.ViewModel.Tabs.Single().Target.DisplayTitle);
            Assert.Equal(TimeSpan.FromMinutes(2), scenario.ViewModel.Tabs.Single().Target.MediaDuration);
        }
        else
        {
            var category = (BrowseCategoryViewModel)first;
            Assert.Equal("Updated", category.Name);
            Assert.Equal("42 viewers", category.ViewerCountText);
            Assert.Equal(updated.Thumbnail, category.ThumbnailUrl);
            Assert.True(changes.Contains(nameof(BrowseCategoryViewModel.Name)));
            Assert.True(changes.Contains(nameof(BrowseCategoryViewModel.MetadataText)));
            await command.ExecuteAsync();
            Assert.Equal("Updated", scenario.Service.StreamRequests.Single().CategoryName);
        }
    });

    private static Task FailureAsync(Surface surface, bool throws) => RunAsync(surface, async scenario =>
    {
        await scenario.InitialAsync([new("1")], "a");
        var original = scenario.Cards.Single();
        var refresh = scenario.RefreshAsync();
        if (throws) scenario.Service.Requests[^1].Fail();
        else scenario.Service.Requests[^1].Complete([new("invalid")], "wrong", available: false);
        await refresh;
        Assert.True(ReferenceEquals(original, scenario.Cards.Single()));
        Assert.Equal(false, scenario.IsLoading);
        Assert.True(scenario.CanLoadMore);
        Assert.True(scenario.Status.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        var more = scenario.MoreAsync();
        Assert.Equal("a", scenario.Service.Requests[^1].Cursor);
        scenario.Service.Requests[^1].Complete([new("2")]);
        await more;
        Assert.SequenceEqual(new[] { "1", "2" }, scenario.Ids);
    });

    private static Task RetryPageAsync(Surface surface) => RunAsync(surface, async scenario =>
    {
        await scenario.InitialAsync([new("1")], "a");
        var more = scenario.MoreAsync();
        scenario.Service.Requests[^1].Complete([], "a", available: false);
        await more;
        Assert.True(scenario.CanLoadMore);
        more = scenario.MoreAsync();
        Assert.Equal("a", scenario.Service.Requests[^1].Cursor);
        scenario.Service.Requests[^1].Complete([new("1"), new("2"), new("2")], "b");
        await more;
        Assert.SequenceEqual(new[] { "1", "2" }, scenario.Ids);
        more = scenario.MoreAsync();
        scenario.Service.Requests[^1].Complete([new("3")], "a");
        await more;
        Assert.Equal(false, scenario.CanLoadMore);
    });

    private static Task EmptyAsync(Surface surface) => RunAsync(surface, async scenario =>
    {
        await scenario.InitialAsync([new("1")], "a");
        var refresh = scenario.RefreshAsync();
        scenario.Service.Requests[^1].Complete([]);
        await refresh;
        Assert.Equal(0, scenario.Cards.Length);
        Assert.Equal(false, scenario.CanLoadMore);
        Assert.Equal(false, scenario.IsLoading);
    });

    private static Task SupersedeAsync(Surface surface) => RunAsync(surface, async scenario =>
    {
        await scenario.InitialAsync([new("1")], "a");
        var more = scenario.MoreAsync();
        scenario.Service.Requests[^1].Complete([new("2")], "b");
        await more;
        var original = scenario.Cards[0];
        more = scenario.MoreAsync();
        var oldPage = scenario.Service.Requests[^1];
        var refresh = scenario.RefreshAsync();
        Assert.True(oldPage.Token.IsCancellationRequested);
        Assert.Equal("", scenario.Service.Requests[^1].Cursor);
        scenario.Service.Requests[^1].Complete([new("1")], "a");
        await refresh;
        oldPage.Complete([new("stale")], "stale");
        await more;
        Assert.True(ReferenceEquals(original, scenario.Cards.Single()));
        Assert.True(scenario.CanLoadMore);
        more = scenario.MoreAsync();
        Assert.Equal("a", scenario.Service.Requests[^1].Cursor);
        scenario.Service.Requests[^1].Complete([new("2")]);
        await more;
        Assert.SequenceEqual(new[] { "1", "2" }, scenario.Ids);
    });

    private static Task ChangeQueryAsync(Surface surface) => RunAsync(surface, async scenario =>
    {
        await scenario.InitialAsync([new("1")], "a");
        var refresh = scenario.RefreshAsync();
        var old = scenario.Service.Requests[^1];
        scenario.SetQuery("changed");
        Assert.True(old.Token.IsCancellationRequested);
        Assert.Equal(0, scenario.Cards.Length);
        old.Complete([new("stale")], "stale");
        await refresh;
        Assert.Equal(0, scenario.Cards.Length);
        Assert.True(scenario.IsLoading);
        var current = scenario.RefreshAsync();
        scenario.Service.Requests[^1].Complete([new("2")]);
        await current;
        Assert.SequenceEqual(new[] { "2" }, scenario.Ids);
    });

    private static Task NavigateAsync() => RunAsync(Surface.Categories, async scenario =>
    {
        await scenario.InitialAsync([new("1")], "a");
        var category = scenario.ViewModel.BrowseCategories.Single();
        var refresh = scenario.RefreshAsync();
        var pending = scenario.Service.Requests[^1];
        Assert.True(ReferenceEquals(category, scenario.Cards.Single()));
        await category.SelectCommand.ExecuteAsync();
        Assert.True(pending.Token.IsCancellationRequested);
        pending.Complete([new("stale")], "wrong");
        await refresh;
        Assert.True(scenario.ViewModel.IsBrowseStreamsPageVisible);
        Assert.Equal("1", scenario.ViewModel.SelectedBrowseCategory!.Id);
        Assert.Equal(false, scenario.IsLoading);
        Assert.SequenceEqual(new[] { "1" }, scenario.Ids);
    });

    private static Task ViewerCountsAsync() => RunAsync(Surface.Categories, async scenario =>
    {
        await scenario.InitialAsync([new("1", Count: null)]);
        var category = scenario.ViewModel.BrowseCategories.Single();
        await TestWait.UntilAsync(() => category.Category.ViewerCount == 10, TimeSpan.FromSeconds(2));
        scenario.Service.ViewerCount = 20;
        var refresh = scenario.RefreshAsync();
        scenario.Service.Requests[^1].Complete([new("1", Count: null)]);
        await refresh;
        await TestWait.UntilAsync(() => scenario.ViewModel.BrowseCategories.Single().Category.ViewerCount == 20, TimeSpan.FromSeconds(2));
        Assert.True(ReferenceEquals(category, scenario.Cards.Single()));
        Assert.Equal(2, scenario.Service.ViewerCountRequests);
    });

    private static AsyncRelayCommand Command(object card) => card is VodViewModel vod
        ? vod.OpenCommand : ((BrowseCategoryViewModel)card).SelectCommand;

    private static Task OverlapCountsAsync() => RunAsync(Surface.Categories, async scenario =>
    {
        await scenario.InitialAsync([new("1", Count: null)], "a");
        await TestWait.UntilAsync(() => scenario.ViewModel.BrowseCategories[0].Category.ViewerCount == 10, TimeSpan.FromSeconds(2));
        var more = scenario.MoreAsync();
        scenario.Service.ViewerCount = 20;
        scenario.Service.Requests[^1].Complete([new("1", Count: null), new("2", Count: null)]);
        await more;
        await TestWait.UntilAsync(() => scenario.ViewModel.BrowseCategories[1].Category.ViewerCount == 20, TimeSpan.FromSeconds(2));
        Assert.Equal(10, scenario.ViewModel.BrowseCategories[0].Category.ViewerCount);
        Assert.Equal(2, scenario.Service.ViewerCountRequests);
    });

    private static Task KickOrderingAsync() => RunAsync(Surface.Categories, async scenario =>
    {
        Row[] rows = [new("2", "Second", 5), new("1", "First", 42), new("3", "", 5), new("2", "Duplicate", 100)];
        await scenario.InitialAsync(rows);
        Assert.SequenceEqual(new[] { "1", "2", "3" }, scenario.Ids);
        var cards = scenario.Cards;
        var changes = 0;
        scenario.OnCollectionChanged(() => changes++);
        var refresh = scenario.RefreshAsync();
        scenario.Service.Requests[^1].Complete(rows);
        await refresh;
        Assert.SequenceEqual(cards, scenario.Cards);
        Assert.Equal(0, changes);
    }, kickCategories: true);

    private static Task RunAsync(Surface surface, Func<Scenario, Task> test, bool kickCategories = false) => TestSta.RunOffscreenAsync(async () =>
    {
        var scenario = new Scenario(surface, kickCategories);
        await using var viewModel = scenario.ViewModel;
        try { await test(scenario); }
        finally
        {
            foreach (var pending in scenario.Service.Requests) pending.Complete([]);
        }
    });

    private enum Surface { TwitchVod, KickVod, Categories }
    private sealed record Row(string Id, string Title = "Original", int? Count = 10, string Thumbnail = "");
    private sealed record Page(IReadOnlyList<Row> Rows, string Cursor, bool Available);

    private sealed class Pending(string cursor, CancellationToken token)
    {
        private readonly TaskCompletionSource<Page> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Cursor { get; } = cursor;
        internal CancellationToken Token { get; } = token;
        // Deliberately ignore cancellation so generation checks must reject late responses.
        internal Task<Page> Task => completion.Task;
        internal void Complete(IReadOnlyList<Row> rows, string cursor = "", bool available = true) =>
            completion.TrySetResult(new(rows, cursor, available));
        internal void Fail() => completion.TrySetException(new HttpRequestException("Provider unavailable"));
    }

    private sealed class Scenario
    {
        private readonly Surface surface;
        internal ControlledService Service { get; } = new();
        internal MainViewModel ViewModel { get; }
        internal Scenario(Surface surface, bool kickCategories)
        {
            this.surface = surface;
            var settings = new AppSettings { StreamlinkPath = "streamlink" };
            ViewModel = TestViewModels.CreateMain(settings, new FakeSettingsService(settings), new FakeStreamlinkService(),
                new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(), action => action(),
                twitchVodService: Service, kickVodService: Service, browseService: Service,
                twitchVodSearchDebounceInterval: TimeSpan.Zero, browseCategorySearchDebounceInterval: TimeSpan.Zero);
            if (surface == Surface.Categories)
            {
                if (kickCategories) ViewModel.SelectKickBrowsePlatformCommand.Execute(null);
                ViewModel.ShowBrowseHomePageCommand.Execute(null);
            }
            else
            {
                ViewModel.ShowTwitchVodsHomePageCommand.Execute(null);
                if (surface == Surface.KickVod) ViewModel.SelectKickVodPlatformCommand.Execute(null);
                ViewModel.TwitchVodSearchText = "streamer";
            }
        }
        internal object[] Cards => surface == Surface.Categories
            ? ViewModel.BrowseCategories.Cast<object>().ToArray() : ViewModel.TwitchVods.Cast<object>().ToArray();
        internal IEnumerable<string> Ids => surface == Surface.Categories
            ? ViewModel.BrowseCategories.Select(card => card.Id) : ViewModel.TwitchVods.Select(card => card.Id);
        internal bool IsLoading => surface == Surface.Categories ? ViewModel.IsBrowseCategoriesLoading : ViewModel.IsTwitchVodSearchRunning;
        internal bool CanLoadMore => surface == Surface.Categories ? ViewModel.CanLoadMoreBrowseCategories : ViewModel.CanLoadMoreTwitchVods;
        internal string Status => surface == Surface.Categories ? ViewModel.BrowseStatus : ViewModel.TwitchVodStatus;
        internal Task RefreshAsync() => surface == Surface.Categories
            ? ViewModel.RefreshBrowseCommand.ExecuteAsync() : ViewModel.SearchTwitchVodsCommand.ExecuteAsync();
        internal Task MoreAsync() => surface == Surface.Categories
            ? ViewModel.LoadMoreBrowseCategoriesCommand.ExecuteAsync() : ViewModel.LoadMoreTwitchVodsCommand.ExecuteAsync();
        internal void SetQuery(string query)
        {
            if (surface == Surface.Categories) ViewModel.BrowseCategorySearchText = query;
            else ViewModel.TwitchVodSearchText = query;
        }
        internal void OnCollectionChanged(Action callback)
        {
            if (surface == Surface.Categories) ViewModel.BrowseCategories.CollectionChanged += (_, _) => callback();
            else ViewModel.TwitchVods.CollectionChanged += (_, _) => callback();
        }
        internal async Task InitialAsync(IReadOnlyList<Row> rows, string cursor = "")
        {
            var initial = RefreshAsync();
            Assert.Equal(1, Service.Requests.Count);
            Service.Requests[0].Complete(rows, cursor);
            await initial;
        }
    }

    private sealed class ControlledService : ITwitchVodService, IKickVodService, IBrowseService
    {
        internal List<Pending> Requests { get; } = [];
        internal List<BrowseStreamRequest> StreamRequests { get; } = [];
        internal int ViewerCount { get; set; } = 10;
        internal int ViewerCountRequests { get; private set; }
        private Task<Page> RequestAsync(string cursor, CancellationToken token)
        {
            var pending = new Pending(cursor, token);
            Requests.Add(pending);
            return pending.Task;
        }
        public async Task<TwitchVodSearchResult> SearchAsync(TwitchVodSearchRequest request, AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            var page = await RequestAsync(request.Cursor, cancellationToken);
            return new(page.Available ? TwitchVodSearchStatus.Available : TwitchVodSearchStatus.Unavailable, null,
                page.Rows.Select(row => new TwitchVodItem(row.Id, "stream", "broadcaster", request.Streamer, "Streamer",
                    row.Title, "", $"https://www.twitch.tv/videos/{row.Id}", row.Thumbnail, null, null,
                    TimeSpan.FromMinutes(row.Title == "Original" ? 1 : 2), row.Count, request.Type,
                    row.Title == "Original" ? TwitchVodAccessKind.Unknown : TwitchVodAccessKind.SubscriberOnly)).ToArray(),
                page.Cursor, page.Available ? "Loaded" : "Provider unavailable");
        }
        public async Task<KickVodSearchResult> SearchAsync(KickVodSearchRequest request, AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            var page = await RequestAsync(request.Cursor, cancellationToken);
            return new(page.Available ? KickVodSearchStatus.Available : KickVodSearchStatus.Unavailable,
                page.Rows.Select(row => new KickVodItem(row.Id, "stream", row.Id, request.Channel, "Streamer", row.Title,
                    $"https://kick.com/{request.Channel}/videos/{row.Id}", $"https://vod.kick.com/{row.Id}.m3u8",
                    row.Thumbnail, "Category", null, null, TimeSpan.FromMinutes(row.Title == "Original" ? 1 : 2), row.Count)).ToArray(),
                page.Cursor, page.Available ? "Loaded" : "Provider unavailable");
        }
        public async Task<BrowseResult<BrowseCategory>> GetCategoriesAsync(BrowseCategoryRequest request, AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            var page = await RequestAsync(request.Cursor, cancellationToken);
            return new(page.Available ? BrowseResultStatus.Available : BrowseResultStatus.Unavailable,
                page.Rows.Select(row => new BrowseCategory(request.Platform, row.Id, row.Title, row.Thumbnail, ["tag"], row.Count)).ToArray(),
                page.Cursor, page.Available ? "Loaded" : "Provider unavailable");
        }
        public Task<BrowseResult<BrowseCategoryViewerCount>> GetCategoryViewerCountsAsync(
            BrowseCategoryViewerCountRequest request, AppSettings settings, CancellationToken cancellationToken = default)
        {
            ViewerCountRequests++;
            return Task.FromResult(new BrowseResult<BrowseCategoryViewerCount>(BrowseResultStatus.Available,
                request.CategoryIds.Select(id => new BrowseCategoryViewerCount(id, ViewerCount)).ToArray(), "", "Loaded"));
        }
        public Task<BrowseResult<BrowseLiveStream>> GetStreamsAsync(BrowseStreamRequest request, AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            StreamRequests.Add(request);
            return Task.FromResult(new BrowseResult<BrowseLiveStream>(BrowseResultStatus.Available, [], "", "Loaded streams"));
        }
    }
}

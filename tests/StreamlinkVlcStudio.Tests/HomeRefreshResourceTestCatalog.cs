internal static class HomeRefreshResourceTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("home refresh resources: unchanged followed cards retain identity and refresh thumbnails", PreserveFollowedAsync),
        ("home refresh resources: followed changes update surviving cards and commands", UpdateFollowedAsync),
        ("home refresh resources: Twitch VOD Enter joins active search and permits later refresh", () => ShareVodAsync(PlatformKind.Twitch)),
        ("home refresh resources: Kick VOD Enter joins active search and permits later refresh", () => ShareVodAsync(PlatformKind.Kick)),
        ("home refresh resources: VOD query filter and platform changes reject stale results", ReplaceVodAsync),
        ("home refresh resources: VOD refresh supersedes pagination", RefreshVodPageAsync),
        ("home refresh resources: Browse refresh joins active categories and permits later refresh", ShareBrowseAsync),
        ("home refresh resources: Browse query and platform changes reject stale results", ReplaceBrowseAsync),
        ("home refresh resources: Browse refresh supersedes pagination", RefreshBrowsePageAsync),
        ("home refresh resources: shutdown cancels searches and prevents new requests", ShutdownAsync)
    ];

    private static Task PreserveFollowedAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new FakeFollowedStreamsService(Stream("one"), Stream("two"));
        await using var viewModel = Create(followed: service);
        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        var cards = viewModel.LiveFollowedChannels.ToArray();
        var commands = cards.Select(card => card.OpenCommand).ToArray();
        var changes = new List<NotifyCollectionChangedAction>();
        var properties = new List<string?>();
        viewModel.LiveFollowedChannels.CollectionChanged += (_, e) => changes.Add(e.Action);
        cards[0].PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        var version = cards[0].ThumbnailCacheVersion;
        for (var i = 0; i < 5; i++) await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.SequenceEqual(cards, viewModel.LiveFollowedChannels);
        Assert.SequenceEqual(commands, viewModel.LiveFollowedChannels.Select(card => card.OpenCommand));
        Assert.Equal(0, changes.Count);
        Assert.True(cards[0].ThumbnailCacheVersion > version);
        Assert.Equal(cards[0].ThumbnailCacheVersion, cards[0].ThumbnailImageRequest.CacheVersion);
        Assert.Equal(5, properties.Count(name => name == nameof(LiveStreamCardViewModel.ThumbnailImageRequest)));
        Assert.True(properties.All(name => name is nameof(LiveStreamCardViewModel.ThumbnailImageRequest)
            or nameof(LiveStreamCardViewModel.ThumbnailCacheVersion) or nameof(LiveStreamCardViewModel.MetadataText)));
    });

    private static Task UpdateFollowedAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new FakeFollowedStreamsService();
        var survivor = Stream("same");
        var kick = Stream("same", PlatformKind.Kick);
        service.EnqueueResult(survivor, Stream("removed"), kick);
        service.EnqueueResult(kick, survivor with
        {
            DisplayName = "Renamed",
            Title = "New title",
            CategoryName = "New category",
            ViewerCount = 42,
            ProfileImageUrl = "https://example.invalid/new.png",
            ThumbnailUrl = ""
        }, Stream("new"));
        await using var viewModel = Create(followed: service);
        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        var original = viewModel.LiveFollowedChannels[0];
        var originalKick = viewModel.LiveFollowedChannels[2];
        var command = original.OpenAndStayOnHomeCommand;
        var properties = new HashSet<string?>();
        var changes = new List<NotifyCollectionChangedAction>();
        original.PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        viewModel.LiveFollowedChannels.CollectionChanged += (_, e) => changes.Add(e.Action);
        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.True(ReferenceEquals(originalKick, viewModel.LiveFollowedChannels[0]));
        Assert.True(ReferenceEquals(original, viewModel.LiveFollowedChannels[1]));
        Assert.True(ReferenceEquals(command, original.OpenAndStayOnHomeCommand));
        Assert.SequenceEqual(new[] { "same", "same", "new" }, viewModel.LiveFollowedChannels.Select(card => card.Channel));
        Assert.Equal("Renamed", original.DisplayName);
        Assert.Equal("New title", original.Title);
        Assert.Equal("New category", original.Target.CategoryName);
        Assert.Equal("42", original.ViewerCountText);
        Assert.Equal(false, original.HasThumbnail);
        Assert.True(original.HasProfileImage);
        foreach (var name in new[] { "DisplayName", "Title", "CategoryName", "Target", "ViewerCountText",
                     "ProfileImageUrl", "HasProfileImage", "ThumbnailUrl", "HasThumbnail" })
            Assert.True(properties.Contains(name), $"Missing property update: {name}");
        Assert.Equal(false, changes.Contains(NotifyCollectionChangedAction.Reset));
        await command.ExecuteAsync();
        Assert.Equal("New category", viewModel.Tabs.Single().Target.CategoryName);
    });

    private static Task ShareVodAsync(PlatformKind platform) => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new ControlledVodService();
        await using var viewModel = Create(vod: service);
        if (platform == PlatformKind.Kick) viewModel.SelectKickVodPlatformCommand.Execute(null);
        viewModel.TwitchVodSearchText = "streamer";
        var first = service.Requests.Single();
        var enter = viewModel.SearchTwitchVodsCommand.ExecuteAsync();
        Assert.Equal(1, service.Requests.Count);
        Assert.Equal(false, first.Token.IsCancellationRequested);
        first.Complete("1", "next");
        await enter;
        Assert.Equal("1", viewModel.TwitchVods.Single().Id);
        var refresh = viewModel.SearchTwitchVodsCommand.ExecuteAsync();
        Assert.Equal(2, service.Requests.Count);
        service.Requests[1].Complete("2");
        await refresh;
        Assert.Equal("2", viewModel.TwitchVods.Single().Id);
    });

    private static Task ReplaceVodAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new ControlledVodService(ignoreCancellation: true);
        await using var viewModel = Create(vod: service);
        viewModel.TwitchVodSearchText = "old";
        viewModel.TwitchVodSearchText = "new";
        viewModel.ShowHighlightsVodFilterCommand.Execute(null);
        viewModel.SelectKickVodPlatformCommand.Execute(null);
        Assert.Equal(4, service.Requests.Count);
        Assert.True(service.Requests.Take(3).All(request => request.Token.IsCancellationRequested));
        var enter = viewModel.SearchTwitchVodsCommand.ExecuteAsync();
        Assert.Equal(4, service.Requests.Count);
        service.Requests[3].Complete("4");
        await enter;
        service.Requests[0].Complete("1");
        service.Requests[1].Complete("2");
        service.Requests[2].Complete("3");
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal("4", viewModel.TwitchVods.Single().Id);
        Assert.Equal(PlatformKind.Kick, viewModel.TwitchVods.Single().Platform);
    });

    private static Task RefreshVodPageAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new ControlledVodService(ignoreCancellation: true);
        await using var viewModel = Create(vod: service);
        viewModel.TwitchVodSearchText = "streamer";
        service.Requests[0].Complete("1", "next");
        await TestWait.UntilAsync(() => !viewModel.IsTwitchVodSearchRunning, TimeSpan.FromSeconds(2));
        var more = viewModel.LoadMoreTwitchVodsCommand.ExecuteAsync();
        Assert.Equal("next", service.Requests[1].Cursor);
        var refresh = viewModel.SearchTwitchVodsCommand.ExecuteAsync();
        Assert.Equal(3, service.Requests.Count);
        Assert.True(service.Requests[1].Token.IsCancellationRequested);
        Assert.Equal("", service.Requests[2].Cursor);
        service.Requests[2].Complete("3");
        await refresh;
        service.Requests[1].Complete("2", "stale");
        await more;
        Assert.Equal("3", viewModel.TwitchVods.Single().Id);
        Assert.Equal(false, viewModel.CanLoadMoreTwitchVods);
    });

    private static Task ShareBrowseAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new ControlledBrowseService();
        await using var viewModel = Create(browse: service);
        viewModel.ShowBrowseHomePageCommand.Execute(null);
        var refresh = viewModel.RefreshBrowseCommand.ExecuteAsync();
        Assert.Equal(1, service.Requests.Count);
        Assert.Equal(false, service.Requests[0].Token.IsCancellationRequested);
        service.Requests[0].Complete("1");
        await refresh;
        Assert.Equal("1", viewModel.BrowseCategories.Single().Id);
        refresh = viewModel.RefreshBrowseCommand.ExecuteAsync();
        Assert.Equal(2, service.Requests.Count);
        service.Requests[1].Complete("2");
        await refresh;
        Assert.Equal("2", viewModel.BrowseCategories.Single().Id);
    });

    private static Task ReplaceBrowseAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new ControlledBrowseService(ignoreCancellation: true);
        await using var viewModel = Create(browse: service);
        viewModel.BrowseCategorySearchText = "old";
        viewModel.BrowseCategorySearchText = "new";
        viewModel.SelectKickBrowsePlatformCommand.Execute(null);
        Assert.Equal(3, service.Requests.Count);
        Assert.True(service.Requests.Take(2).All(request => request.Token.IsCancellationRequested));
        var refresh = viewModel.RefreshBrowseCommand.ExecuteAsync();
        Assert.Equal(3, service.Requests.Count);
        service.Requests[2].Complete("3");
        await refresh;
        service.Requests[0].Complete("1");
        service.Requests[1].Complete("2");
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal("3", viewModel.BrowseCategories.Single().Id);
        Assert.Equal(PlatformKind.Kick, viewModel.BrowseCategories.Single().Platform);
    });

    private static Task RefreshBrowsePageAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new ControlledBrowseService(ignoreCancellation: true);
        await using var viewModel = Create(browse: service);
        viewModel.ShowBrowseHomePageCommand.Execute(null);
        service.Requests[0].Complete("1", "next");
        await TestWait.UntilAsync(() => !viewModel.IsBrowseCategoriesLoading, TimeSpan.FromSeconds(2));
        var more = viewModel.LoadMoreBrowseCategoriesCommand.ExecuteAsync();
        Assert.Equal("next", service.Requests[1].Cursor);
        var refresh = viewModel.RefreshBrowseCommand.ExecuteAsync();
        Assert.Equal(3, service.Requests.Count);
        Assert.True(service.Requests[1].Token.IsCancellationRequested);
        Assert.Equal("", service.Requests[2].Cursor);
        service.Requests[2].Complete("3");
        await refresh;
        service.Requests[1].Complete("2", "stale");
        await more;
        Assert.Equal("3", viewModel.BrowseCategories.Single().Id);
        Assert.Equal(false, viewModel.CanLoadMoreBrowseCategories);
    });

    private static Task ShutdownAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var vod = new ControlledVodService();
        var browse = new ControlledBrowseService();
        await using var viewModel = Create(vod: vod, browse: browse);
        viewModel.TwitchVodSearchText = "streamer";
        viewModel.ShowBrowseHomePageCommand.Execute(null);
        await viewModel.DisposeAsync();
        Assert.True(vod.Requests.Single().Token.IsCancellationRequested);
        Assert.True(browse.Requests.Single().Token.IsCancellationRequested);
        await viewModel.SearchTwitchVodsCommand.ExecuteAsync();
        await viewModel.RefreshBrowseCommand.ExecuteAsync();
        Assert.Equal(1, vod.Requests.Count);
        Assert.Equal(1, browse.Requests.Count);
    });

    private static MainViewModel Create(IFollowedStreamsService? followed = null,
        ControlledVodService? vod = null, IBrowseService? browse = null)
    {
        var settings = new AppSettings { StreamlinkPath = "streamlink" };
        return TestViewModels.CreateMain(settings, new FakeSettingsService(settings), new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(), action => action(),
            followedStreamsService: followed, twitchVodService: vod, kickVodService: vod, browseService: browse,
            twitchVodSearchDebounceInterval: TimeSpan.Zero, browseCategorySearchDebounceInterval: TimeSpan.Zero);
    }

    private static FollowedLiveStream Stream(string channel, PlatformKind platform = PlatformKind.Twitch) => new(
        platform, channel, channel, "Title", "Category", 10, "https://example.invalid/preview.jpg",
        null, false, "en", $"https://{(platform == PlatformKind.Twitch ? "twitch.tv" : "kick.com")}/{channel}");

    private sealed class PendingRequest(CancellationToken token, string cursor = "")
    {
        private readonly TaskCompletionSource<(string Id, string Cursor)> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token { get; } = token;
        internal string Cursor { get; } = cursor;
        internal void Complete(string id, string nextCursor = "") => completion.TrySetResult((id, nextCursor));
        internal Task<(string Id, string Cursor)> WaitAsync(bool ignoreCancellation) =>
            ignoreCancellation ? completion.Task : completion.Task.WaitAsync(Token);
    }

    private sealed class ControlledVodService(bool ignoreCancellation = false) : ITwitchVodService, IKickVodService
    {
        internal List<PendingRequest> Requests { get; } = [];

        public async Task<TwitchVodSearchResult> SearchAsync(TwitchVodSearchRequest request, AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            var pending = new PendingRequest(cancellationToken, request.Cursor);
            Requests.Add(pending);
            var page = await pending.WaitAsync(ignoreCancellation);
            return new(TwitchVodSearchStatus.Available, null,
                [new TwitchVodItem(page.Id, "stream", "broadcaster", request.Streamer, request.Streamer, "Video", "",
                    $"https://www.twitch.tv/videos/{page.Id}", "", null, null, TimeSpan.FromMinutes(1), 10, request.Type)],
                page.Cursor, "loaded");
        }

        public async Task<KickVodSearchResult> SearchAsync(KickVodSearchRequest request, AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            var pending = new PendingRequest(cancellationToken, request.Cursor);
            Requests.Add(pending);
            var page = await pending.WaitAsync(ignoreCancellation);
            return new(KickVodSearchStatus.Available,
                [new KickVodItem(page.Id, "stream", page.Id, request.Channel, request.Channel, "Video",
                    $"https://kick.com/streamer/videos/{page.Id}", $"https://vod.kick.com/{page.Id}.m3u8",
                    "", "", null, null, TimeSpan.FromMinutes(1), 10)], page.Cursor, "loaded");
        }
    }

    private sealed class ControlledBrowseService(bool ignoreCancellation = false) : IBrowseService
    {
        internal List<PendingRequest> Requests { get; } = [];

        public async Task<BrowseResult<BrowseCategory>> GetCategoriesAsync(BrowseCategoryRequest request, AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            var pending = new PendingRequest(cancellationToken, request.Cursor);
            Requests.Add(pending);
            var page = await pending.WaitAsync(ignoreCancellation);
            return new(BrowseResultStatus.Available,
                [new BrowseCategory(request.Platform, page.Id, request.Query, "", [], 10)], page.Cursor, "loaded");
        }

        public Task<BrowseResult<BrowseCategoryViewerCount>> GetCategoryViewerCountsAsync(
            BrowseCategoryViewerCountRequest request, AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BrowseResult<BrowseCategoryViewerCount>(BrowseResultStatus.Available, [], "", ""));

        public Task<BrowseResult<BrowseLiveStream>> GetStreamsAsync(BrowseStreamRequest request, AppSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BrowseResult<BrowseLiveStream>(BrowseResultStatus.Available, [], "", ""));
    }
}

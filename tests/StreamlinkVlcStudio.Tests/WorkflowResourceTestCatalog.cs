internal static class WorkflowResourceTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("workflow resources: Enter shares an active automatic search and still permits refresh", ShareSearchAsync),
        ("workflow resources: changed queries cancel shared search without accepting stale results", ReplaceSearchAsync),
        ("workflow resources: changing quality starts a new search instead of sharing old probes", SearchQualityAsync),
        ("workflow resources: viewer counts preserve search rows and commands while reordering", PreserveSearchRowsAsync),
        ("workflow resources: Recent reuses fresh live and offline results without rebuilding cards", ReuseRecentAsync),
        ("workflow resources: Recent checks new channels without refreshing fresh channels", NewRecentAsync),
        ("workflow resources: Recent retries unavailable metadata on the next visit", RetryRecentAsync),
        ("workflow resources: metadata freshness expires and deletion removes cached freshness", RecentFreshnessAsync),
        ("workflow resources: Recent checks at most four distinct channels concurrently", ConcurrentRecentAsync),
        ("workflow resources: shutdown cancels Recent requests before admitting queued channels", CancelRecentAsync)
    ];

    private static Task ShareSearchAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource<StreamSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        var search = new FakeStreamSearchService(SearchResult("streamer"))
        {
            ResponderAsync = (_, token) =>
            {
                observedToken = token;
                return release.Task.WaitAsync(token);
            }
        };
        await using var viewModel = Create(search: search);
        viewModel.NewStreamText = "streamer";
        Assert.Equal(1, search.CallCount);
        var originalToken = observedToken;
        var enter = viewModel.AddAndPlayCommand.ExecuteAsync();
        try
        {
            Assert.Equal(1, search.CallCount);
            Assert.Equal(false, originalToken.IsCancellationRequested);
            Assert.Equal(false, enter.IsCompleted);
        }
        finally { release.TrySetResult(SearchResult("streamer")); }
        await enter;
        Assert.Equal("streamer", viewModel.StreamSearchResults.Single().Channel);
        await viewModel.AddAndPlayCommand.ExecuteAsync();
        Assert.Equal(2, search.CallCount);
    });

    private static Task ReplaceSearchAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var oldRelease = new TaskCompletionSource<StreamSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newRelease = new TaskCompletionSource<StreamSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;
        var search = new FakeStreamSearchService(SearchResult("unused"))
        {
            ResponderAsync = (request, token) =>
            {
                if (request.Query == "old")
                {
                    oldToken = token;
                    // Deliberately ignore cancellation to exercise generation checks.
                    return oldRelease.Task;
                }
                return newRelease.Task.WaitAsync(token);
            }
        };
        await using var viewModel = Create(search: search);
        viewModel.NewStreamText = "old";
        var enter = viewModel.AddAndPlayCommand.ExecuteAsync();
        try
        {
            viewModel.NewStreamText = "new";
            Assert.True(oldToken.IsCancellationRequested);
            oldRelease.SetResult(SearchResult("old"));
            await enter;
            Assert.Equal(0, viewModel.StreamSearchResults.Count);
            var nextEnter = viewModel.AddAndPlayCommand.ExecuteAsync();
            Assert.Equal(2, search.CallCount);
            newRelease.SetResult(SearchResult("new"));
            await nextEnter;
            Assert.Equal("new", viewModel.StreamSearchResults.Single().Channel);
        }
        finally
        {
            oldRelease.TrySetResult(SearchResult("old"));
            newRelease.TrySetResult(SearchResult("new"));
        }
    });

    private static Task SearchQualityAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource<StreamSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new List<CancellationToken>();
        var search = new FakeStreamSearchService(SearchResult("streamer"))
        {
            ResponderAsync = (_, token) =>
            {
                tokens.Add(token);
                return release.Task.WaitAsync(token);
            }
        };
        await using var viewModel = Create(search: search);
        viewModel.NewStreamText = "streamer";
        viewModel.SelectedQuality = "480p";
        var enter = viewModel.AddAndPlayCommand.ExecuteAsync();
        try
        {
            Assert.Equal(2, search.CallCount);
            Assert.Equal("480p", search.Requests[1].Quality);
            Assert.True(tokens[0].IsCancellationRequested);
        }
        finally { release.TrySetResult(SearchResult("streamer")); }
        await enter;
        Assert.Equal(1, viewModel.StreamSearchResults.Count);
    });

    private static Task PreserveSearchRowsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var counts = new FakeViewerCountService
        {
            ResponderAsync = async (target, token) =>
            {
                await release.Task.WaitAsync(token);
                return new ViewerCountResult(ViewerCountState.Available, target.Channel == "second" ? 200 : 10, "updated");
            }
        };
        await using var viewModel = Create(search: new FakeStreamSearchService(SearchResult("first", "second")), counts: counts);
        viewModel.NewStreamText = "streamer";
        var original = viewModel.StreamSearchResults.ToArray();
        var actions = new List<NotifyCollectionChangedAction>();
        var properties = new List<string?>();
        viewModel.StreamSearchResults.CollectionChanged += (_, e) => actions.Add(e.Action);
        original[1].PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        release.SetResult();
        await TestWait.UntilAsync(() => viewModel.StreamSearchResults[0].ViewerCount == 200, TimeSpan.FromSeconds(2));
        Assert.True(ReferenceEquals(original[1], viewModel.StreamSearchResults[0]));
        Assert.True(ReferenceEquals(original[0].OpenCommand, viewModel.StreamSearchResults[1].OpenCommand));
        Assert.True(actions.All(action => action == NotifyCollectionChangedAction.Move));
        Assert.True(properties.Contains(nameof(StreamSearchResultViewModel.ViewerCount)));
        Assert.True(properties.Contains(nameof(StreamSearchResultViewModel.HasViewerCount)));
        Assert.True(properties.Contains(nameof(StreamSearchResultViewModel.ViewerCountText)));
        Assert.Equal("200 viewers", original[1].ViewerCountText);
    });

    private static Task ReuseRecentAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var metadata = new FakeStreamMetadataService(Metadata(), new(StreamMetadataState.Offline, "", "", "offline"));
        await using var viewModel = Create(settings: RecentSettings(2), metadata: metadata);
        viewModel.ShowRecentHomePageCommand.Execute(null);
        var cards = viewModel.RecentStreams.ToArray();
        var changes = 0;
        viewModel.RecentStreams.CollectionChanged += (_, _) => changes++;
        for (var i = 0; i < 5; i++)
        {
            viewModel.ShowFollowedHomePageCommand.Execute(null);
            viewModel.ShowRecentHomePageCommand.Execute(null);
        }
        Assert.Equal(2, metadata.CallCount);
        Assert.Equal(0, changes);
        Assert.SequenceEqual(cards, viewModel.RecentStreams);
        Assert.SequenceEqual(new[] { "Live", "Offline" }, cards.Select(card => card.LiveStatusText));
    });

    private static Task NewRecentAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = RecentSettings(1);
        var metadata = new FakeStreamMetadataService(Metadata());
        await using var viewModel = Create(settings: settings, metadata: metadata);
        viewModel.ShowRecentHomePageCommand.Execute(null);
        settings.RecentStreams.Add(Recent("newchannel"));
        viewModel.ShowRecentHomePageCommand.Execute(null);
        Assert.SequenceEqual(new[] { "channel0", "newchannel" }, metadata.Requests.Select(target => target.Channel));
        Assert.Equal(2, viewModel.RecentStreams.Count);
    });

    private static Task RetryRecentAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var metadata = new FakeStreamMetadataService(new(StreamMetadataState.Unavailable, "", "", "try again"), Metadata());
        await using var viewModel = Create(settings: RecentSettings(1), metadata: metadata);
        viewModel.ShowRecentHomePageCommand.Execute(null);
        Assert.Equal("Unknown", viewModel.RecentStreams.Single().LiveStatusText);
        viewModel.ShowRecentHomePageCommand.Execute(null);
        Assert.Equal(2, metadata.CallCount);
        Assert.Equal("Live", viewModel.RecentStreams.Single().LiveStatusText);
    });

    private static Task RecentFreshnessAsync()
    {
        var controller = new RecentStreamController();
        var now = DateTimeOffset.UtcNow;
        var interval = TimeSpan.FromMinutes(5);
        controller.RecordMetadataRefresh("Twitch:streamer", now, succeeded: true);
        Assert.True(controller.IsMetadataFresh("twitch:STREAMER", now + interval - TimeSpan.FromTicks(1), interval));
        Assert.Equal(false, controller.IsMetadataFresh("Twitch:streamer", now + interval, interval));
        Assert.Equal(false, controller.IsMetadataFresh("Twitch:streamer", now - TimeSpan.FromTicks(1), interval));
        Assert.Equal(false, controller.IsMetadataFresh("Twitch:streamer", now, TimeSpan.Zero));
        controller.RecordMetadataRefresh("Twitch:streamer", now, succeeded: false);
        Assert.Equal(false, controller.IsMetadataFresh("Twitch:streamer", now, interval));
        controller.RecordMetadataRefresh("Twitch:streamer", now, succeeded: true);
        controller.RemoveLiveStatus("Twitch:streamer");
        Assert.Equal(false, controller.IsMetadataFresh("Twitch:streamer", now, interval));
        return Task.CompletedTask;
    }

    private static Task ConcurrentRecentAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = RecentSettings(9);
        settings.RecentStreams.Add(Recent("CHANNEL0"));
        var metadata = new BlockingMetadataService();
        await using var viewModel = Create(settings: settings, metadata: metadata);
        try
        {
            viewModel.ShowRecentHomePageCommand.Execute(null);
            Assert.Equal(4, metadata.Requests.Count);
            viewModel.ShowRecentHomePageCommand.Execute(null);
            Assert.Equal(4, metadata.Requests.Count);
            metadata.Release.TrySetResult();
            await TestWait.UntilAsync(() => viewModel.RecentStreams.All(card => card.LiveStatusText == "Live"), TimeSpan.FromSeconds(2));
            Assert.Equal(9, metadata.Requests.Count);
            Assert.Equal(4, metadata.MaxConcurrentCalls);
        }
        finally { metadata.Release.TrySetResult(); }
    });

    private static Task CancelRecentAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var metadata = new BlockingMetadataService();
        await using var viewModel = Create(settings: RecentSettings(9), metadata: metadata);
        viewModel.ShowRecentHomePageCommand.Execute(null);
        Assert.Equal(4, metadata.Requests.Count);
        await viewModel.DisposeAsync();
        Assert.Equal(4, metadata.Requests.Count);
        Assert.Equal(0, metadata.ActiveCalls);
        Assert.True(metadata.Tokens.All(token => token.IsCancellationRequested));
    });

    private static MainViewModel Create(AppSettings? settings = null, IStreamSearchService? search = null,
        IStreamMetadataService? metadata = null, IViewerCountService? counts = null)
    {
        settings ??= new AppSettings();
        return TestViewModels.CreateMain(settings, new FakeSettingsService(settings), new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(), action => action(),
            streamSearchService: search, streamMetadataService: metadata, viewerCountService: counts,
            streamSearchDebounceInterval: TimeSpan.Zero, recentThumbnailRefreshInterval: TimeSpan.FromHours(1));
    }

    private static StreamSearchResult SearchResult(params string[] channels) => new(StreamSearchResultStatus.Available,
        channels.Select(channel => new StreamSearchChannel(PlatformKind.Twitch, channel, channel,
            $"https://www.twitch.tv/{channel}", "", "", "", StreamSearchChannelState.Live,
            StreamSearchSourceStatus.Available, "live", true)).ToArray(), "found");

    private static AppSettings RecentSettings(int count) => new()
    {
        RecentStreams = Enumerable.Range(0, count).Select(index => Recent($"channel{index}")).ToList()
    };

    private static RecentStreamSettings Recent(string channel) => new()
    {
        Platform = PlatformKind.Twitch,
        Channel = channel,
        Url = $"https://www.twitch.tv/{channel}"
    };

    private static StreamMetadataResult Metadata() => new(StreamMetadataState.Available, "", "", "live");

    private sealed class BlockingMetadataService : IStreamMetadataService
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<StreamTarget> Requests { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public int ActiveCalls { get; private set; }
        public int MaxConcurrentCalls { get; private set; }

        public async Task<StreamMetadataResult> GetLiveStreamMetadataAsync(StreamTarget target, AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(target);
            Tokens.Add(cancellationToken);
            MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, ++ActiveCalls);
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                return Metadata();
            }
            finally { ActiveCalls--; }
        }
    }
}

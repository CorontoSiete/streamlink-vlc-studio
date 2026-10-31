using System.Windows.Threading;

internal static class KickCategoryProgressTestCatalog
{
    private const string ListPath = "/public/v2/categories";
    private const string LivePath = "/public/v1/livestreams";
    private const string DetailPrefix = "/public/v1/categories/";

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Kick category progress: list cards arrive before discovery and counts", () => SourcesAsync(listFirst: true)),
        ("Kick category progress: discovery cards arrive before the list and counts", () => SourcesAsync(listFirst: false)),
        ("Kick category progress: search cards arrive before exact counts", SearchAsync),
        ("Kick category progress: cards remain selectable and finish in place", () => NavigationAsync(leaveHome: false)),
        ("Kick category progress: completion after leaving Browse preserves page status", () => NavigationAsync(leaveHome: true)),
        ("Kick category progress: refresh preserves enriched cards until completion", RefreshAsync),
        ("Kick category progress: authorization failures remove provisional cards", FailureAsync),
        ("Kick category progress: replaced searches and shutdown reject late updates", StaleAsync),
        ("Kick category progress: queued updates cannot replace final results", QueuedAsync),
        ("Kick category progress: measured first-card latency and full completion", MeasureAsync)
    ];

    private static async Task SourcesAsync(bool listFirst)
    {
        var secondSource = Signal();
        var releaseDetails = Signal();
        var preview = new TaskCompletionSource<IReadOnlyList<BrowseCategory>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = new ConcurrentQueue<IReadOnlyList<BrowseCategory>>();
        var detailRequests = new ConcurrentQueue<string>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path is ListPath or LivePath)
            {
                if ((path == ListPath) != listFirst) await secondSource.Task.WaitAsync(token);
                return Source(path);
            }
            Assert.True(path.StartsWith(DetailPrefix, StringComparison.Ordinal));
            detailRequests.Enqueue(path);
            await releaseDetails.Task.WaitAsync(token);
            return Detail(path);
        }));
        var load = Service(http).GetCategoriesAsync(new(PlatformKind.Kick), new AppSettings(),
            new CallbackProgress(items => { snapshots.Enqueue(items); preview.TrySetResult(items); }), cancellation.Token);
        try
        {
            var early = await preview.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.SequenceEqual(listFirst ? new[] { "1", "2" } : new[] { "2", "3" }, early.Select(category => category.Id));
            Assert.True(early.All(category => category.ViewerCount is null));
            Assert.True(!load.IsCompleted, "An early snapshot must not complete an unfinished page.");
            secondSource.TrySetResult();
            await TestWait.UntilAsync(() => snapshots.Any(items => items.Count == 3), TimeSpan.FromSeconds(2));
            Assert.True(!load.IsCompleted, "The page must still wait for exact totals.");
            releaseDetails.TrySetResult();
            var result = await load;
            Assert.True(result.IsAvailable);
            Assert.SequenceEqual(new[] { "3", "2", "1" }, result.Items.Select(category => category.Id));
            Assert.SequenceEqual(new int?[] { 300, 200, 100 }, result.Items.Select(category => category.ViewerCount));
            Assert.Equal("next-page", result.NextCursor);
            Assert.Equal(3, detailRequests.Count);
            Assert.Equal(3, detailRequests.Distinct().Count());
            Assert.Equal("Live 2", snapshots.Last().Single(category => category.Id == "2").Name);
        }
        finally
        {
            await cancellation.CancelAsync();
            await DrainAsync(load, cancellation.Token);
        }
    }

    private static async Task SearchAsync()
    {
        var release = Signal();
        IReadOnlyList<BrowseCategory>? preview = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/public/v1/categories") return Source(ListPath);
            await release.Task.WaitAsync(token);
            return Detail(request.RequestUri.AbsolutePath);
        }));
        var load = Service(http).GetCategoriesAsync(new(PlatformKind.Kick, "query"), new AppSettings(),
            new CallbackProgress(items => preview = items), cancellation.Token);
        try
        {
            Assert.Equal(2, preview!.Count);
            Assert.True(!load.IsCompleted);
            release.TrySetResult();
            var result = await load;
            Assert.SequenceEqual(new[] { "2", "1" }, result.Items.Select(category => category.Id));
            Assert.True(result.Items.All(category => category.ViewerCount is > 0));
        }
        finally
        {
            await cancellation.CancelAsync();
            await DrainAsync(load, cancellation.Token);
        }
    }

    private static Task NavigationAsync(bool leaveHome) => TestSta.RunOffscreenAsync(async () =>
    {
        var release = Signal();
        var requests = new ConcurrentQueue<string>();
        using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Enqueue(path);
            if (path == LivePath && request.RequestUri.Query.Contains("category_id", StringComparison.Ordinal))
                return Json("{\"data\":[]}");
            if (path is ListPath or LivePath) return Source(path);
            await release.Task.WaitAsync(token);
            return Detail(path);
        }));
        await using var main = CreateMain(Service(http));
        main.ShowBrowseHomePageCommand.Execute(null);
        main.SelectKickBrowsePlatformCommand.Execute(null);
        var load = main.RefreshBrowseCommand.ExecuteAsync();
        try
        {
            await TestWait.UntilAsync(() => main.BrowseCategories.Count == 3, TimeSpan.FromSeconds(2));
            var cards = main.BrowseCategories.ToDictionary(card => card.Id);
            Assert.True(main.IsBrowseCategoriesLoading);
            Assert.True(!main.HasBrowseCategorySearchCompleted);
            Assert.True(!main.CanLoadMoreBrowseCategories);
            Assert.True(cards.Values.All(card => card.HasThumbnail && card.Category.ViewerCount is null));
            await cards["2"].SelectCommand.ExecuteAsync();
            Assert.True(main.IsBrowseStreamsPageVisible);
            var streamStatus = main.BrowseStatus;
            if (leaveHome) main.ShowRecentHomePageCommand.Execute(null);
            var visibleStatus = main.StatusMessage;
            release.TrySetResult();
            await load;
            Assert.Equal(!leaveHome, main.IsBrowseStreamsPageVisible);
            Assert.Equal(streamStatus, main.BrowseStatus);
            Assert.Equal(visibleStatus, main.StatusMessage);
            Assert.True(!main.IsBrowseCategoriesLoading);
            Assert.True(main.HasBrowseCategorySearchCompleted);
            if (leaveHome) main.ShowBrowseHomePageCommand.Execute(null);
            else main.ReturnToBrowseCategoriesCommand.Execute(null);
            Assert.SequenceEqual(new[] { "3", "2", "1" }, main.BrowseCategories.Select(card => card.Id));
            Assert.True(main.CanLoadMoreBrowseCategories);
            foreach (var card in main.BrowseCategories)
            {
                Assert.True(ReferenceEquals(cards[card.Id], card));
                Assert.True(card.Category.ViewerCount is > 0);
            }
            Assert.Equal(1, requests.Count(path => path == ListPath));
            Assert.Equal(3, requests.Count(path => path.StartsWith(DetailPrefix, StringComparison.Ordinal)));
        }
        finally { release.TrySetResult(); await load; }
    });

    private static Task RefreshAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new ControlledBrowseService();
        await using var main = CreateMain(service);
        main.SelectKickBrowsePlatformCommand.Execute(null);
        var first = service.Pending.Last();
        first.Result.TrySetResult(Page([Category("one", 10)]));
        await main.RefreshBrowseCommand.ExecuteAsync();
        var original = main.BrowseCategories.Single();
        var refresh = main.RefreshBrowseCommand.ExecuteAsync();
        var pending = service.Pending.Last();
        pending.Progress.Report([Category("one"), Category("new")]);
        Assert.True(ReferenceEquals(original, main.BrowseCategories.Single()));
        Assert.Equal<int?>(10, original.Category.ViewerCount);
        pending.Result.TrySetResult(Page([Category("one", 20), Category("new", 30)]));
        await refresh;
        Assert.True(ReferenceEquals(original, main.BrowseCategories.Single(card => card.Id == "one")));
        Assert.Equal<int?>(20, original.Category.ViewerCount);
        Assert.SequenceEqual(new[] { "new", "one" }, main.BrowseCategories.Select(card => card.Id));
    });

    private static Task FailureAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        foreach (var status in new[] { BrowseResultStatus.Unauthorized, BrowseResultStatus.Unavailable })
        {
            var service = new ControlledBrowseService();
            await using var main = CreateMain(service);
            main.SelectKickBrowsePlatformCommand.Execute(null);
            var load = main.RefreshBrowseCommand.ExecuteAsync();
            var pending = service.Pending.Last();
            pending.Progress.Report([Category("early")]);
            Assert.Equal(1, main.BrowseCategories.Count);
            pending.Result.TrySetResult(new(status, [], "", "Lookup failed."));
            await load;
            Assert.Equal(0, main.BrowseCategories.Count);
            Assert.Equal("Lookup failed.", main.BrowseStatus);
            Assert.True(main.HasBrowseCategorySearchCompleted);
            Assert.True(!main.IsBrowseCategoriesLoading);
            Assert.True(!main.CanLoadMoreBrowseCategories);
        }
    });

    private static Task StaleAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new ControlledBrowseService();
        var main = CreateMain(service);
        try
        {
            main.SelectKickBrowsePlatformCommand.Execute(null);
            var oldLoad = main.RefreshBrowseCommand.ExecuteAsync();
            var old = service.Pending.Last();
            old.Progress.Report([Category("old")]);
            main.BrowseCategorySearchText = "new";
            var newLoad = main.RefreshBrowseCommand.ExecuteAsync();
            var current = service.Pending.Last();
            old.Progress.Report([Category("late")]);
            Assert.Equal(0, main.BrowseCategories.Count);
            current.Progress.Report([Category("new")]);
            current.Result.TrySetResult(Page([Category("new", 42)]));
            await Task.WhenAll(oldLoad, newLoad);
            old.Progress.Report([Category("later")]);
            Assert.Equal("new", main.BrowseCategories.Single().Id);
            Assert.Equal<int?>(42, main.BrowseCategories.Single().Category.ViewerCount);
            main.BrowseCategorySearchText = "shutdown";
            var shutdownLoad = main.RefreshBrowseCommand.ExecuteAsync();
            var shutdown = service.Pending.Last();
            shutdown.Progress.Report([Category("shutdown")]);
            await main.DisposeAsync();
            await shutdownLoad;
            Assert.True(shutdown.Token.IsCancellationRequested);
            shutdown.Progress.Report([Category("disposed")]);
            Assert.True(!main.BrowseCategories.Any(card => card.Id == "disposed"));
        }
        finally { await main.DisposeAsync(); }
    });

    private static Task QueuedAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new ControlledBrowseService();
        var queued = new Queue<Action>();
        var dispatcher = Dispatcher.CurrentDispatcher;
        await using var main = CreateMain(service, action =>
        {
            if (dispatcher.CheckAccess()) queued.Enqueue(action);
            else dispatcher.Invoke(() => queued.Enqueue(action));
        });
        main.SelectKickBrowsePlatformCommand.Execute(null);
        var load = main.RefreshBrowseCommand.ExecuteAsync();
        var pending = service.Pending.Last();
        pending.Progress.Report([Category("early")]);
        pending.Result.TrySetResult(Page([Category("final", 100)]));
        await load;
        while (queued.TryDequeue(out var action)) action();
        Assert.Equal("final", main.BrowseCategories.Single().Id);
        Assert.Equal<int?>(100, main.BrowseCategories.Single().Category.ViewerCount);
    });

    private static async Task MeasureAsync()
    {
        foreach (var listFirst in new[] { true, false })
        {
            var original = new List<double>();
            var early = new List<double>();
            var complete = new List<double>();
            for (var trial = 0; trial < 5; trial++)
            {
                using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    await Task.Delay(path is ListPath or LivePath ? (path == ListPath) == listFirst ? 40 : 160 : 120, token);
                    return path is ListPath or LivePath ? Source(path) : Detail(path);
                }));
                var service = Service(http);
                var clock = Stopwatch.StartNew();
                var before = await service.GetCategoriesAsync(new(PlatformKind.Kick), new AppSettings());
                var originalFirst = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                double? first = null;
                var after = await service.GetCategoriesAsync(new(PlatformKind.Kick), new AppSettings(),
                    new CallbackProgress(_ => first ??= clock.Elapsed.TotalMilliseconds));
                var finished = clock.Elapsed.TotalMilliseconds;
                Assert.SequenceEqual(before.Items, after.Items);
                Assert.Equal(before.NextCursor, after.NextCursor);
                Assert.True(first.HasValue);
                if (trial == 0) continue;
                original.Add(originalFirst);
                early.Add(first ?? throw new InvalidOperationException("No early category snapshot was reported."));
                complete.Add(finished);
            }
            Console.WriteLine($"KICK CATEGORY PROGRESS {(listFirst ? "list" : "discovery")}-first: " +
                $"40/160 ms sources; three exact 120 ms details; " +
                $"original first-card median={Median(original):F1} ms; early-card median={Median(early):F1} ms; " +
                $"full-completion median={Median(complete):F1} ms; " +
                $"samples original=[{Samples(original)}] early=[{Samples(early)}] full=[{Samples(complete)}]");
        }
    }

    private static MainViewModel CreateMain(IBrowseService service, Action<Action>? dispatch = null)
    {
        var settings = new AppSettings();
        settings.Chat.ConnectAutomatically = false;
        var dispatcher = Dispatcher.CurrentDispatcher;
        return TestViewModels.CreateMain(settings, new FakeSettingsService(settings), new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(),
            dispatch ?? (action => dispatcher.Invoke(action)), browseService: service,
            browseCategorySearchDebounceInterval: TimeSpan.FromHours(1));
    }

    private static BrowseService Service(HttpClient http) => new(new MemoryLogger(), http,
        new KickTokenProvider((_, _, _) => Task.FromResult<string?>("fixture-token")));
    private static BrowseCategory Category(string id, int? viewers = null) =>
        new(PlatformKind.Kick, id, id, $"https://images.example/{id}.webp", [], viewers);
    private static BrowseResult<BrowseCategory> Page(IReadOnlyList<BrowseCategory> items) =>
        new(BrowseResultStatus.Available, items, "next-page", "Loaded categories.");
    private static HttpResponseMessage Source(string path)
    {
        var categories = (path == ListPath ? new[] { 1, 2 } : new[] { 2, 3 }).Select(id => new
        {
            id,
            name = $"{(path == ListPath ? "List" : "Live")} {id}",
            thumbnail = $"https://images.example/{id}.webp"
        });
        return path == ListPath
            ? Json(JsonSerializer.Serialize(new { data = categories, pagination = new { next_cursor = "next-page" } }))
            : Json(JsonSerializer.Serialize(new { data = categories.Select(category => new { category }) }));
    }
    private static HttpResponseMessage Detail(string path)
    {
        var id = int.Parse(path[DetailPrefix.Length..], CultureInfo.InvariantCulture);
        return Json(JsonSerializer.Serialize(new { data = new { id, viewer_count = id * 100 } }));
    }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static double Median(IEnumerable<double> samples) => (samples.Order().ElementAt(1) + samples.Order().ElementAt(2)) / 2;
    private static string Samples(IEnumerable<double> samples) => string.Join(",", samples.Select(value => value.ToString("F1", CultureInfo.InvariantCulture)));
    private static async Task DrainAsync(Task task, CancellationToken token)
    {
        try { await task; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private sealed class CallbackProgress(Action<IReadOnlyList<BrowseCategory>> report) : IProgress<IReadOnlyList<BrowseCategory>>
    {
        public void Report(IReadOnlyList<BrowseCategory> value) => report(value);
    }

    private sealed class ControlledBrowseService : IBrowseService
    {
        internal sealed record Load(IProgress<IReadOnlyList<BrowseCategory>> Progress, CancellationToken Token)
        {
            internal TaskCompletionSource<BrowseResult<BrowseCategory>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        internal List<Load> Pending { get; } = [];
        public Task<BrowseResult<BrowseCategory>> GetCategoriesAsync(BrowseCategoryRequest request, AppSettings settings,
            CancellationToken cancellationToken = default) => Task.FromResult(Page([]));
        public Task<BrowseResult<BrowseCategory>> GetCategoriesAsync(BrowseCategoryRequest request, AppSettings settings,
            IProgress<IReadOnlyList<BrowseCategory>> categoriesProgress, CancellationToken cancellationToken = default)
        {
            if (request.Platform != PlatformKind.Kick) return Task.FromResult(Page([]));
            var load = new Load(categoriesProgress, cancellationToken);
            Pending.Add(load);
            return load.Result.Task.WaitAsync(cancellationToken);
        }
        public Task<BrowseResult<BrowseCategoryViewerCount>> GetCategoryViewerCountsAsync(BrowseCategoryViewerCountRequest request,
            AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(BrowseResult<BrowseCategoryViewerCount>.Unavailable("Unused."));
        public Task<BrowseResult<BrowseLiveStream>> GetStreamsAsync(BrowseStreamRequest request, AppSettings settings,
            CancellationToken cancellationToken = default) => Task.FromResult(new BrowseResult<BrowseLiveStream>(BrowseResultStatus.Available, [], "", "Loaded streams."));
    }
}

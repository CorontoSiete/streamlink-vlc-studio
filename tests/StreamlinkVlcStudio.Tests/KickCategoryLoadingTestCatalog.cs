internal static class KickCategoryLoadingTestCatalog
{
    private const string ListPath = "/public/v2/categories";
    private const string LivePath = "/public/v1/livestreams";
    private const string DetailPrefix = "/public/v1/categories/";

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Kick category loading: list details start before discovery finishes", () => OverlapAsync(listFirst: true)),
        ("Kick category loading: discovery details start before the list finishes", () => OverlapAsync(listFirst: false)),
        ("Kick category loading: partial details preserve merged discovery metadata", () => MergeAsync(invalidDetail: false)),
        ("Kick category loading: failed details preserve merged discovery metadata", () => MergeAsync(invalidDetail: true)),
        ("Kick category loading: cancellation drains sources and bounded details", CancellationAsync),
        ("Kick category loading: failed list cancels and drains speculative details", () => SourceFailureAsync(malformed: false, failList: true)),
        ("Kick category loading: malformed list cancels and drains speculative details", () => SourceFailureAsync(malformed: true, failList: true)),
        ("Kick category loading: malformed discovery cancels and drains speculative details", () => SourceFailureAsync(malformed: true, failList: false)),
        ("Kick category loading: discovery HTTP failure retains enriched list", DiscoveryFailureAsync),
        ("Kick category loading: detail authorization failures remain unauthorized", DetailAuthorizationAsync),
        ("Kick category loading: supplied totals skip redundant detail requests", SuppliedTotalsAsync),
        ("Kick category loading: measured list-first latency", () => MeasureAsync(listFirst: true)),
        ("Kick category loading: measured discovery-first latency", () => MeasureAsync(listFirst: false))
    ];

    internal static IReadOnlyList<(string Name, Func<Task> Run)> LiveProbeTests =>
        Environment.GetEnvironmentVariable("SVS_TEST_KICK_CATEGORY_LOADING_LIVE") == "1"
            ? [("Kick category loading: live request timing", LiveAsync)] : [];

    private static async Task OverlapAsync(bool listFirst)
    {
        var sourceRelease = Signal();
        var detailRelease = Signal();
        var fourStarted = Signal();
        var eightFinished = Signal();
        var started = 0;
        var finished = 0;
        var active = 0;
        var requests = new ConcurrentQueue<string>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Enqueue(path);
            if (path is ListPath or LivePath)
            {
                if ((path == ListPath) != listFirst)
                    await sourceRelease.Task.WaitAsync(token);
                return Source(path, Enumerable.Range(1, (path == ListPath) == listFirst ? 8 : 9));
            }
            Assert.True(path.StartsWith(DetailPrefix, StringComparison.Ordinal));
            var current = Interlocked.Increment(ref active);
            Assert.True(current <= 4, "Category detail concurrency exceeded four.");
            if (Interlocked.Increment(ref started) == 4) fourStarted.TrySetResult();
            try
            {
                await detailRelease.Task.WaitAsync(token);
                return Detail(path);
            }
            finally
            {
                Interlocked.Decrement(ref active);
                if (Interlocked.Increment(ref finished) == 8) eightFinished.TrySetResult();
            }
        }));
        var load = LoadAsync(http, cancellation.Token);
        try
        {
            await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(4, Volatile.Read(ref started));
            Assert.True(!load.IsCompleted);
            detailRelease.TrySetResult();
            await eightFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(!load.IsCompleted, "The second source must still be pending.");
            sourceRelease.TrySetResult();
            var result = await load;
            Assert.True(result.IsAvailable);
            Assert.Equal("next-page", result.NextCursor);
            Assert.SequenceEqual(Enumerable.Range(1, 9).Reverse().Select(id => id.ToString(CultureInfo.InvariantCulture)),
                result.Items.Select(category => category.Id));
            Assert.True(result.Items.All(category => category.Tags.SequenceEqual(new[] { "detail-tag" })));
            Assert.Equal(11, requests.Count);
            Assert.Equal(9, requests.Where(path => path.StartsWith(DetailPrefix, StringComparison.Ordinal)).Distinct().Count());
        }
        finally
        {
            await cancellation.CancelAsync();
            await DrainAsync(load, cancellation.Token);
        }
    }

    private static async Task MergeAsync(bool invalidDetail)
    {
        var detailStarted = Signal();
        var sourceRelease = Signal();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == ListPath) return Source(path, [1]);
            if (path == LivePath)
            {
                await sourceRelease.Task.WaitAsync(token);
                return Source(path, [1]);
            }
            detailStarted.TrySetResult();
            return Json(invalidDetail ? "{\"data\":null}" : "{\"data\":{\"viewer_count\":42}}");
        }));
        var load = LoadAsync(http, cancellation.Token);
        try
        {
            await detailStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            sourceRelease.TrySetResult();
            var result = await load;
            Assert.True(result.IsAvailable);
            var category = result.Items.Single();
            Assert.Equal("Live 1", category.Name);
            Assert.Equal("https://images.example/live-1.webp", category.ThumbnailUrl);
            Assert.SequenceEqual(new[] { "live-tag" }, category.Tags);
            Assert.Equal<int?>(invalidDetail ? null : 42, category.ViewerCount);
            Assert.Equal(invalidDetail, result.Message.Contains("Viewer counts unavailable", StringComparison.Ordinal));
        }
        finally
        {
            await cancellation.CancelAsync();
            await DrainAsync(load, cancellation.Token);
        }
    }

    private static async Task CancellationAsync()
    {
        var fourStarted = Signal();
        var started = 0;
        var detailFinished = 0;
        var sourceFinished = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == ListPath) return Source(path, Enumerable.Range(1, 8));
            var isDetail = path.StartsWith(DetailPrefix, StringComparison.Ordinal);
            if (isDetail && Interlocked.Increment(ref started) == 4) fourStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally
            {
                if (isDetail) Interlocked.Increment(ref detailFinished);
                else Interlocked.Increment(ref sourceFinished);
            }
            throw new InvalidOperationException("The pending request was not canceled.");
        }));
        var load = LoadAsync(http, cancellation.Token);
        try
        {
            await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await cancellation.CancelAsync();
            try
            {
                await load;
                throw new InvalidOperationException("Caller cancellation did not propagate.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Assert.Equal(1, Volatile.Read(ref sourceFinished));
            Assert.Equal(4, Volatile.Read(ref detailFinished));
            Assert.Equal(4, Volatile.Read(ref started));
        }
        finally
        {
            await cancellation.CancelAsync();
            await DrainAsync(load, cancellation.Token);
        }
    }

    private static async Task SourceFailureAsync(bool malformed, bool failList)
    {
        var fourStarted = Signal();
        var started = 0;
        var finished = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path is ListPath or LivePath)
            {
                if ((path == ListPath) != failList) return Source(path, Enumerable.Range(1, 8));
                await fourStarted.Task.WaitAsync(token);
                return malformed ? Json("{") : Json("{\"message\":\"Denied\"}", HttpStatusCode.Unauthorized);
            }
            if (Interlocked.Increment(ref started) == 4) fourStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { Interlocked.Increment(ref finished); }
            throw new InvalidOperationException("The pending detail was not canceled.");
        }));
        var load = LoadAsync(http, cancellation.Token);
        try
        {
            await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var result = await load.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(malformed ? BrowseResultStatus.Unavailable : BrowseResultStatus.Unauthorized, result.Status);
            Assert.Equal(0, result.Items.Count);
            Assert.Equal(4, Volatile.Read(ref finished));
            Assert.Equal(4, Volatile.Read(ref started));
        }
        finally
        {
            await cancellation.CancelAsync();
            await DrainAsync(load, cancellation.Token);
        }
    }

    private static async Task DiscoveryFailureAsync()
    {
        using var http = new HttpClient(new FakeHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            ListPath => Source(ListPath, [1, 2]),
            LivePath => Json("{}", HttpStatusCode.ServiceUnavailable),
            var path => Detail(path)
        }));
        var result = await LoadAsync(http);
        Assert.True(result.IsAvailable);
        Assert.SequenceEqual(new[] { "2", "1" }, result.Items.Select(category => category.Id));
        Assert.True(result.Items.All(category => category.ViewerCount is > 0));
    }

    private static async Task DetailAuthorizationAsync()
    {
        using var http = new HttpClient(new FakeHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            ListPath => Source(ListPath, [1]),
            LivePath => Source(LivePath, []),
            _ => Json("{\"message\":\"Denied\"}", HttpStatusCode.Forbidden)
        }));
        var result = await LoadAsync(http);
        Assert.Equal(BrowseResultStatus.Unauthorized, result.Status);
        Assert.Equal(0, result.Items.Count);
    }

    private static async Task MeasureAsync(bool listFirst)
    {
        var samples = new List<double>();
        for (var trial = 0; trial < 5; trial++)
        {
            var details = 0;
            using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path is ListPath or LivePath)
                {
                    await Task.Delay((path == ListPath) == listFirst ? 40 : 340, token);
                    return Source(path, Enumerable.Range(1, 8));
                }
                Interlocked.Increment(ref details);
                await Task.Delay(120, token);
                return Detail(path);
            }));
            var clock = Stopwatch.StartNew();
            var result = await LoadAsync(http);
            if (trial > 0) samples.Add(clock.Elapsed.TotalMilliseconds);
            Assert.True(result.IsAvailable);
            Assert.Equal(8, result.Items.Count);
            Assert.Equal(8, details);
            Assert.True(result.Items.All(category => category.ViewerCount is > 0));
            Assert.Equal("next-page", result.NextCursor);
        }
        Console.WriteLine($"KICK CATEGORY LOADING {(listFirst ? "list" : "discovery")}-first: " +
            $"40/340 ms sources, eight 120 ms details, warm samples={string.Join(", ", samples.Select(value => value.ToString("F1", CultureInfo.InvariantCulture)))} ms, " +
            $"median={(samples.Order().ElementAt(1) + samples.Order().ElementAt(2)) / 2:F1} ms");
    }

    private static async Task SuppliedTotalsAsync()
    {
        var requests = new ConcurrentQueue<string>();
        using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Enqueue(path);
            if (path == LivePath)
                return Json("{\"data\":[{\"category\":{\"id\":1,\"name\":\"Live One\",\"viewer_count\":123}}]}");
            Assert.Equal(ListPath, path);
            await Task.Delay(40, token);
            return Source(path, [1]);
        }));
        var result = await LoadAsync(http);
        Assert.True(result.IsAvailable);
        Assert.Equal<int?>(123, result.Items.Single().ViewerCount);
        Assert.Equal(2, requests.Count);
    }

    private static async Task LiveAsync()
    {
        // Load a private copy: a failed DPAPI read must never rewrite the user's settings.
        var original = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamStudio", "settings.json");
        var directory = Path.Combine(Path.GetTempPath(), "kick-category-loading-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var copy = Path.Combine(directory, "settings.json");
            File.Copy(original, copy);
            var settingsService = new JsonSettingsService(copy);
            var settings = await settingsService.LoadAsync();
            Assert.True(settingsService.LastLoadWarning is null, "Live timing unavailable: protected settings could not be loaded.");
            using var http = new HttpClient(new LiveTimingHandler()) { Timeout = TimeSpan.FromSeconds(15) };
            var service = new BrowseService(new MemoryLogger(), http);
            for (var trial = 0; trial < 4; trial++)
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var clock = Stopwatch.StartNew();
                var result = await service.GetCategoriesAsync(new(PlatformKind.Kick, "", PageSize: 10), settings, cancellation.Token);
                Console.WriteLine($"LIVE KICK CATEGORY LOADING trial={trial} elapsed={clock.Elapsed.TotalMilliseconds:F1} ms status={result.Status} categories={result.Items.Count} unknown={result.Items.Count(category => category.ViewerCount is null)}");
                Assert.True(result.IsAvailable, "Live timing unavailable: Kick categories were not available.");
                Assert.True(result.Items.Count > 0);
            }
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private sealed class LiveTimingHandler() : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            var response = await base.SendAsync(request, cancellationToken);
            Console.WriteLine($"LIVE KICK HEADERS path={request.RequestUri!.AbsolutePath} status={(int)response.StatusCode} elapsed={clock.Elapsed.TotalMilliseconds:F1} ms");
            return response;
        }
    }

    private static Task<BrowseResult<BrowseCategory>> LoadAsync(HttpClient http, CancellationToken token = default) =>
        new BrowseService(new MemoryLogger(), http, new KickTokenProvider((_, _, _) => Task.FromResult<string?>("fixture-token")))
            .GetCategoriesAsync(new(PlatformKind.Kick, "", PageSize: 10), new AppSettings(), token);

    private static HttpResponseMessage Source(string path, IEnumerable<int> ids)
    {
        var categories = ids.Select(id => new
        {
            id,
            name = $"{(path == ListPath ? "List" : "Live")} {id}",
            thumbnail = $"https://images.example/{(path == ListPath ? "list" : "live")}-{id}.webp",
            tags = new[] { path == ListPath ? "list-tag" : "live-tag" }
        }).ToArray();
        return path == ListPath
            ? Json(JsonSerializer.Serialize(new { data = categories, pagination = new { next_cursor = "next-page" } }))
            : Json(JsonSerializer.Serialize(new { data = categories.Select(category => new { category }) }));
    }

    private static HttpResponseMessage Detail(string path)
    {
        var id = int.Parse(path[DetailPrefix.Length..], CultureInfo.InvariantCulture);
        return Json(JsonSerializer.Serialize(new { data = new { id, name = $"Detail {id}", viewer_count = id * 100, tags = new[] { "detail-tag" } } }));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static async Task DrainAsync(Task task, CancellationToken token)
    {
        try { await task; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}

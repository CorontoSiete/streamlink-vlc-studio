internal static class LoadingConcurrencyTestCatalog
{
    private const string TwitchValidation = """
        {"client_id":"client","user_id":"1","login":"viewer","scopes":["user:read:follows"],"expires_in":3600}
        """;
    private const string TwitchLive = """
        {"data":[{"user_id":"42","user_login":"liveone","user_name":"Live One","title":"Live","viewer_count":42}],"pagination":{}}
        """;
    private const string TwitchFollows = """
        {"data":[{"broadcaster_login":"LIVEONE","broadcaster_name":"Live One"},{"broadcaster_login":"offline","broadcaster_name":"Offline"}],"pagination":{}}
        """;
    private const string KickCategories = """
        {"data":[{"id":1,"name":"Category One","viewer_count":100}],"pagination":{"next_cursor":"next"}}
        """;
    private const string KickLive = """
        {"data":[{"category":{"id":2,"name":"Category Two","viewer_count":200}}]}
        """;

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("loading concurrency: measured Twitch Home requests", () => MeasureAsync(kick: false)),
        ("loading concurrency: measured Kick Discover requests", () => MeasureAsync(kick: true)),
        ("loading concurrency: Twitch live and follow inventory overlap", () => OverlapAsync(kick: false)),
        ("loading concurrency: Kick categories and live discovery overlap", () => OverlapAsync(kick: true)),
        ("loading concurrency: Twitch live failures cancel and drain inventory", TwitchFailureAsync),
        ("loading concurrency: Kick list failures cancel and drain discovery", KickFailureAsync),
        ("loading concurrency: cancellation drains both Twitch requests", () => CancellationAsync(kick: false)),
        ("loading concurrency: cancellation drains both Kick requests", () => CancellationAsync(kick: true))
    ];

    private static async Task MeasureAsync(bool kick)
    {
        var samples = new List<double>();
        for (var trial = 0; trial < 4; trial++)
        {
            var requests = new ConcurrentQueue<string>();
            using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
            {
                requests.Enqueue(request.RequestUri!.AbsolutePath);
                if (IsPrimary(request.RequestUri, kick) || IsSecondary(request.RequestUri, kick))
                    await Task.Delay(150, token);
                return Respond(request.RequestUri, kick);
            }));
            var clock = Stopwatch.StartNew();
            await LoadAsync(http, kick);
            // Exclude the first sample from the reported median to separate JIT/setup.
            if (trial > 0) samples.Add(clock.Elapsed.TotalMilliseconds);
            Assert.Equal(1, requests.Count(path => path == PrimaryPath(kick)));
            Assert.Equal(1, requests.Count(path => path == SecondaryPath(kick)));
            Assert.Equal(kick ? 2 : 4, requests.Count);
        }
        Console.WriteLine($"LOADING CONCURRENCY {(kick ? "Kick Discover" : "Twitch Home")}: " +
            $"two 150 ms fixture responses, warm samples={string.Join(", ", samples.Select(value => value.ToString("F1", CultureInfo.InvariantCulture)))} ms, " +
            $"median={samples.Order().ElementAt(1):F1} ms");
    }

    private static async Task OverlapAsync(bool kick)
    {
        var primary = Signal();
        var secondary = Signal();
        var release = Signal();
        using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            var uri = request.RequestUri!;
            if (IsPrimary(uri, kick) || IsSecondary(uri, kick))
            {
                (IsPrimary(uri, kick) ? primary : secondary).TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return Respond(uri, kick);
        }));
        var load = LoadAsync(http, kick);
        try
        {
            await Task.WhenAll(primary.Task, secondary.Task).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(!load.IsCompleted, "Both requests must start before either response is released.");
        }
        finally
        {
            release.TrySetResult();
            await load;
        }
    }

    private static async Task TwitchFailureAsync()
    {
        foreach (var failure in new[] { "http", "malformed", "json", "cursor" })
        {
            var secondary = Signal();
            var drained = Signal();
            using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
            {
                var uri = request.RequestUri!;
                if (IsSecondary(uri, kick: false))
                {
                    secondary.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    finally { drained.TrySetResult(); }
                }
                if (IsPrimary(uri, kick: false))
                {
                    await secondary.Task.WaitAsync(token);
                    return failure switch
                    {
                        "http" => Json("{}", HttpStatusCode.ServiceUnavailable),
                        "malformed" => Json("""{"data":null}"""),
                        "json" => Json("{"),
                        _ => Json("""{"data":[],"pagination":{"cursor":"repeated"}}""")
                    };
                }
                return Respond(uri, kick: false);
            }));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var settings = TwitchSettings();
            var result = await new FollowedStreamsService(new MemoryLogger(), http)
                .GetLiveFollowedStreamsAsync(settings, cancellation.Token);
            Assert.True(drained.Task.IsCompletedSuccessfully, $"{failure}: unfinished inventory escaped the refresh.");
            Assert.Equal(0, result.OfflineChannels!.Count);
            Assert.True(!result.SucceededPlatforms!.Contains(PlatformKind.Twitch));
            Assert.True(result.Messages.Any(message => message.StartsWith("Twitch", StringComparison.Ordinal)));
        }
    }

    private static async Task KickFailureAsync()
    {
        foreach (var invalidJson in new[] { false, true })
        {
            var secondary = Signal();
            var drained = Signal();
            using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
            {
                if (IsSecondary(request.RequestUri!, kick: true))
                {
                    secondary.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    finally { drained.TrySetResult(); }
                }
                await secondary.Task.WaitAsync(token);
                return invalidJson ? Json("{") : Json("{}", HttpStatusCode.ServiceUnavailable);
            }));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var result = await Browse(http).GetCategoriesAsync(new BrowseCategoryRequest(PlatformKind.Kick, ""),
                new AppSettings(), cancellation.Token);
            Assert.True(!result.IsAvailable);
            Assert.True(drained.Task.IsCompletedSuccessfully, "Unfinished discovery escaped a failed browse request.");
        }
    }

    private static async Task CancellationAsync(bool kick)
    {
        var primary = Signal();
        var secondary = Signal();
        var finished = 0;
        using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            var uri = request.RequestUri!;
            if (IsPrimary(uri, kick) || IsSecondary(uri, kick))
            {
                (IsPrimary(uri, kick) ? primary : secondary).TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { Interlocked.Increment(ref finished); }
            }
            return Respond(uri, kick);
        }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var load = LoadAsync(http, kick, cancellation.Token);
        try
        {
            await Task.WhenAll(primary.Task, secondary.Task).WaitAsync(TimeSpan.FromSeconds(2));
            await cancellation.CancelAsync();
            try
            {
                await load;
                throw new InvalidOperationException("Caller cancellation did not propagate.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Assert.Equal(2, Volatile.Read(ref finished));
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await load; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    private static async Task LoadAsync(HttpClient http, bool kick, CancellationToken cancellationToken = default)
    {
        if (kick)
        {
            var result = await Browse(http).GetCategoriesAsync(new BrowseCategoryRequest(PlatformKind.Kick, ""),
                new AppSettings(), cancellationToken);
            Assert.True(result.IsAvailable);
            Assert.SequenceEqual(new[] { "2", "1" }, result.Items.Select(category => category.Id));
            Assert.Equal("next", result.NextCursor);
        }
        else
        {
            var result = await new FollowedStreamsService(new MemoryLogger(), http)
                .GetLiveFollowedStreamsAsync(TwitchSettings(), cancellationToken);
            Assert.Equal("liveone", result.Streams.Single().Channel);
            Assert.Equal("offline", result.OfflineChannels!.Single().Channel);
            Assert.Equal("https://images.example/offline.png", result.OfflineChannels!.Single().ProfileImageUrl);
            Assert.True(result.SucceededPlatforms!.Contains(PlatformKind.Twitch));
        }
    }

    private static BrowseService Browse(HttpClient http) => new(new MemoryLogger(), http,
        new KickTokenProvider((_, _, _) => Task.FromResult<string?>("fixture-token")));

    private static AppSettings TwitchSettings()
    {
        var settings = new AppSettings();
        settings.Chat.TwitchOAuthToken = "fixture-token";
        return settings;
    }

    private static HttpResponseMessage Respond(Uri uri, bool kick) => Json(uri.AbsolutePath switch
    {
        "/oauth2/validate" => TwitchValidation,
        "/helix/streams/followed" => TwitchLive,
        "/helix/channels/followed" => TwitchFollows,
        "/helix/users" => """{"data":[{"login":"offline","profile_image_url":"https://images.example/offline.png"}]}""",
        "/public/v2/categories" when kick => KickCategories,
        "/public/v1/livestreams" when kick => KickLive,
        _ => throw new InvalidOperationException($"Unexpected fixture request: {uri}")
    });

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string PrimaryPath(bool kick) => kick ? "/public/v2/categories" : "/helix/streams/followed";
    private static string SecondaryPath(bool kick) => kick ? "/public/v1/livestreams" : "/helix/channels/followed";
    private static bool IsPrimary(Uri uri, bool kick) => uri.AbsolutePath == PrimaryPath(kick);
    private static bool IsSecondary(Uri uri, bool kick) => uri.AbsolutePath == SecondaryPath(kick);
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

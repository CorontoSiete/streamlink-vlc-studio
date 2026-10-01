internal static class TwitchCategoryViewerCountTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Twitch category totals: use reported counts without stream pagination or account headers", ReportedTotalsAsync),
        ("Twitch category totals: bound batches and preserve category order", BatchesAsync),
        ("Twitch category totals: malformed and missing totals require complete stream pagination", InvalidTotalsAsync),
        ("Twitch category totals: incomplete batches do not mix reported and partial counts", IncompleteBatchAsync),
        ("Twitch category totals: failed fallback never publishes partial counts", FailedFallbackAsync),
        ("Twitch category totals: transport failures use complete Helix pagination", TransportFallbackAsync),
        ("Twitch category totals: timeout falls back without cancelling the caller", TimeoutFallbackAsync),
        ("Twitch category totals: caller cancellation stops lookup without starting Helix", CancellationAsync),
        ("Twitch category totals: missing credentials avoid network requests", MissingCredentialsAsync),
        ("Twitch category totals: Discover displays fast counts while another category needs pagination", DiscoverAsync)
    ];

    private static async Task ReportedTotalsAsync()
    {
        var aggregateRequests = 0;
        var streamRequests = 0;
        var settings = Settings("category-totals-reported-token");
        using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv") return ValidationResponse();
            if (request.RequestUri.Host == "api.twitch.tv")
            {
                streamRequests++;
                return JsonResponse("""
                    {"data":[{"id":"first","game_id":"509658","viewer_count":12000},
                             {"id":"second","game_id":"263490","viewer_count":20}],"pagination":{}}
                    """);
            }

            aggregateRequests++;
            Assert.Equal("gql.twitch.tv", request.RequestUri.Host);
            Assert.Equal("/gql", request.RequestUri.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.True(request.Headers.Authorization is null);
            Assert.SequenceEqual(["kimne78kx3ncx6brgo4mv6wki5h1ko"], request.Headers.GetValues("Client-Id"));
            Assert.True(request.Headers.Contains("X-Device-Id"));
            var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.DoesNotContain(settings.Chat.TwitchOAuthToken, payload);
            using var body = JsonDocument.Parse(payload);
            Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
            Assert.SequenceEqual(["509658", "263490"], body.RootElement.EnumerateArray()
                .Select(operation => operation.GetProperty("variables").GetProperty("id").GetString()!));
            foreach (var operation in body.RootElement.EnumerateArray())
            {
                Assert.Contains("game(id: $id)", operation.GetProperty("query").GetString()!);
                Assert.Contains("viewersCount", operation.GetProperty("query").GetString()!);
            }
            return JsonResponse("""
                [{"data":{"game":{"id":"509658","viewersCount":329518}}},
                 {"data":{"game":{"id":"263490","viewersCount":0}}}]
                """);
        }));
        var result = await new BrowseService(new MemoryLogger(), http).GetCategoryViewerCountsAsync(
            new BrowseCategoryViewerCountRequest(PlatformKind.Twitch, [" 509658 ", "263490", "509658", ""]), settings);

        Assert.Equal(BrowseResultStatus.Available, result.Status);
        Assert.SequenceEqual([new BrowseCategoryViewerCount("509658", 329518), new BrowseCategoryViewerCount("263490", 0)], result.Items);
        Assert.Equal(1, aggregateRequests);
        Assert.Equal(0, streamRequests);
    }

    private static async Task BatchesAsync()
    {
        var requests = 0;
        var ids = Enumerable.Range(1000, 53).Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray();
        using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv") return ValidationResponse();
            Assert.Equal("gql.twitch.tv", request.RequestUri.Host);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var batch = body.RootElement.EnumerateArray().ToArray();
            Assert.True(batch.Length is > 0 and <= 20);
            requests++;
            return JsonResponse(JsonSerializer.Serialize(batch.Select(operation =>
            {
                var id = operation.GetProperty("variables").GetProperty("id").GetString()!;
                return new { data = new { game = new { id, viewersCount = int.Parse(id, CultureInfo.InvariantCulture) } } };
            })));
        }));
        var result = await new BrowseService(new MemoryLogger(), http).GetCategoryViewerCountsAsync(
            new BrowseCategoryViewerCountRequest(PlatformKind.Twitch, ids), Settings("category-totals-batch-token"));

        Assert.Equal(BrowseResultStatus.Available, result.Status);
        Assert.SequenceEqual(ids, result.Items.Select(count => count.CategoryId));
        Assert.SequenceEqual(Enumerable.Range(1000, 53), result.Items.Select(count => count.ViewerCount));
        Assert.Equal(3, requests);
    }

    private static async Task InvalidTotalsAsync()
    {
        string[] invalidResponses =
        [
            "not json", "null", "{}", "[]", "[null]", "[{}]",
            """[{"data":{"game":null}}]""",
            """[{"data":{"game":{"id":"unexpected","viewersCount":900}}}]""",
            """[{"data":{"game":{"id":"1"}}}]""",
            """[{"data":{"game":{"id":"1","viewersCount":null}}}]""",
            """[{"data":{"game":{"id":"1","viewersCount":-1}}}]""",
            """[{"data":{"game":{"id":"1","viewersCount":"900"}}}]""",
            """[{"data":{"game":{"id":"1","viewersCount":9.5}}}]""",
            """[{"data":{"game":{"id":"1","viewersCount":2147483648}}}]""",
            """[{"data":{"game":{"id":"1","viewersCount":900}}},{"data":{"game":{"id":"2","viewersCount":800}}}]""",
            """[{"data":{"game":{"id":"1","viewersCount":900}},"errors":[{"message":"temporarily unavailable"}]}]"""
        ];
        for (var index = 0; index < invalidResponses.Length; index++)
        {
            var aggregateRequests = 0;
            var pages = 0;
            using var http = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                if (request.RequestUri!.Host == "id.twitch.tv") return ValidationResponse();
                if (request.RequestUri.Host == "gql.twitch.tv")
                {
                    aggregateRequests++;
                    return JsonResponse(invalidResponses[index]);
                }
                pages++;
                return StreamPage(request);
            }));
            var result = await new BrowseService(new MemoryLogger(), http).GetCategoryViewerCountsAsync(
                new BrowseCategoryViewerCountRequest(PlatformKind.Twitch, ["1"]), Settings($"category-totals-invalid-{index}"));

            Assert.Equal(BrowseResultStatus.Available, result.Status);
            Assert.SequenceEqual([new BrowseCategoryViewerCount("1", 125)], result.Items);
            Assert.Equal(1, aggregateRequests);
            Assert.Equal(2, pages);
        }
    }

    private static async Task IncompleteBatchAsync()
    {
        var pages = 0;
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv") return ValidationResponse();
            if (request.RequestUri.Host == "gql.twitch.tv") return JsonResponse("""
                [{"data":{"game":{"id":"1","viewersCount":900}}},{"data":{"game":null}}]
                """);
            pages++;
            return StreamPage(request, includeSecondCategory: true);
        }));
        var result = await new BrowseService(new MemoryLogger(), http).GetCategoryViewerCountsAsync(
            new BrowseCategoryViewerCountRequest(PlatformKind.Twitch, ["1", "2"]), Settings("category-totals-incomplete-token"));

        Assert.Equal(BrowseResultStatus.Available, result.Status);
        Assert.SequenceEqual([new BrowseCategoryViewerCount("1", 125), new BrowseCategoryViewerCount("2", 80)], result.Items);
        Assert.Equal(2, pages);
    }

    private static async Task FailedFallbackAsync()
    {
        var pages = 0;
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv") return ValidationResponse();
            if (request.RequestUri.Host == "gql.twitch.tv") return JsonResponse("""
                [{"data":{"game":{"id":"1","viewersCount":900}}},{"data":{"game":null}}]
                """);
            pages++;
            return pages == 1 ? StreamPage(request, includeSecondCategory: true)
                : JsonResponse("{}", HttpStatusCode.ServiceUnavailable);
        }));
        var result = await new BrowseService(new MemoryLogger(), http).GetCategoryViewerCountsAsync(
            new BrowseCategoryViewerCountRequest(PlatformKind.Twitch, ["1", "2"]), Settings("category-totals-failed-fallback-token"));

        Assert.Equal(BrowseResultStatus.Unavailable, result.Status);
        Assert.Equal(0, result.Items.Count);
        Assert.Equal(2, pages);
    }

    private static async Task TransportFallbackAsync()
    {
        foreach (var status in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, (HttpStatusCode)0 })
        {
            var pages = 0;
            using var http = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                if (request.RequestUri!.Host == "id.twitch.tv") return ValidationResponse();
                if (request.RequestUri.Host == "gql.twitch.tv") return status == 0
                    ? throw new HttpRequestException("connection failed") : JsonResponse("{}", status);
                pages++;
                return StreamPage(request);
            }));
            var result = await new BrowseService(new MemoryLogger(), http).GetCategoryViewerCountsAsync(
                new BrowseCategoryViewerCountRequest(PlatformKind.Twitch, ["1"]), Settings($"category-totals-transport-{(int)status}"));
            Assert.Equal(BrowseResultStatus.Available, result.Status);
            Assert.Equal(125, result.Items.Single().ViewerCount);
            Assert.Equal(2, pages);
        }
    }

    private static async Task TimeoutFallbackAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var aggregateCancelled = false;
        var pages = 0;
        using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv") return ValidationResponse();
            if (request.RequestUri.Host == "gql.twitch.tv")
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                finally { aggregateCancelled = cancellationToken.IsCancellationRequested; }
            }
            Assert.True(!cancellationToken.IsCancellationRequested);
            pages++;
            return StreamPage(request);
        }));
        var result = await new BrowseService(new MemoryLogger(), http).GetCategoryViewerCountsAsync(
            new BrowseCategoryViewerCountRequest(PlatformKind.Twitch, ["1"]), Settings("category-totals-timeout-token"), cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(aggregateCancelled);
        Assert.True(!cancellation.IsCancellationRequested);
        Assert.Equal(BrowseResultStatus.Available, result.Status);
        Assert.Equal(125, result.Items.Single().ViewerCount);
        Assert.Equal(2, pages);
    }

    private static async Task CancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pages = 0;
        using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv") return ValidationResponse();
            if (request.RequestUri.Host == "gql.twitch.tv")
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            pages++;
            return StreamPage(request);
        }));
        var task = new BrowseService(new MemoryLogger(), http).GetCategoryViewerCountsAsync(
            new BrowseCategoryViewerCountRequest(PlatformKind.Twitch, ["1"]), Settings("category-totals-cancel-token"), cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(2));
                throw new InvalidOperationException("The caller's cancellation was not propagated.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Assert.Equal(0, pages);
        }
        finally { cancellation.Cancel(); }
    }

    private static async Task MissingCredentialsAsync()
    {
        var requests = 0;
        using var http = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            requests++;
            return JsonResponse("{}");
        }));
        var result = await new BrowseService(new MemoryLogger(), http).GetCategoryViewerCountsAsync(
            new BrowseCategoryViewerCountRequest(PlatformKind.Twitch, ["1"]), new AppSettings());
        Assert.Equal(BrowseResultStatus.NotConfigured, result.Status);
        Assert.Equal(0, requests);
    }

    private static Task DiscoverAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var aggregates = 0;
        var pages = 0;
        var slowCategoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv") return ValidationResponse();
            if (request.RequestUri.AbsolutePath == "/helix/games/top") return JsonResponse(JsonSerializer.Serialize(new
            {
                data = Enumerable.Range(1, 10).Select(id => new { id = id.ToString(CultureInfo.InvariantCulture), name = $"Category {id}", box_art_url = "" }),
                pagination = new { }
            }));
            if (request.RequestUri.Host == "gql.twitch.tv")
            {
                Interlocked.Increment(ref aggregates);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var id = body.RootElement[0].GetProperty("variables").GetProperty("id").GetString()!;
                return id == "1" ? JsonResponse("{}", HttpStatusCode.ServiceUnavailable)
                    : JsonResponse(JsonSerializer.Serialize(new[] { new { data = new { game = new { id, viewersCount = int.Parse(id, CultureInfo.InvariantCulture) * 1000 } } } }));
            }
            Assert.Equal("/helix/streams", request.RequestUri.AbsolutePath);
            Interlocked.Increment(ref pages);
            slowCategoryStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("{}");
        }));
        var settings = Settings("category-totals-discover-token");
        await using var model = TestViewModels.CreateMain(settings, new FakeSettingsService(settings),
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
            new MemoryLogger(), action => action(), browseService: new BrowseService(new MemoryLogger(), http));
        model.ShowBrowseHomePageCommand.Execute(null);

        await slowCategoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await TestWait.UntilAsync(() => model.BrowseCategories.Count == 10 &&
            model.BrowseCategories.Skip(1).All(category => category.Category.ViewerCount is not null), TimeSpan.FromSeconds(2));
        Assert.True(!model.IsBrowseCategoriesLoading);
        Assert.True(model.BrowseCategories[0].Category.ViewerCount is null);
        Assert.SequenceEqual(Enumerable.Range(2, 9).Select(id => $"{id}K viewers"), model.BrowseCategories.Skip(1).Select(category => category.ViewerCountText));
        Assert.Equal(10, aggregates);
        Assert.Equal(1, pages);
    });

    private static AppSettings Settings(string token)
    {
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "category-client-id";
        settings.Chat.TwitchOAuthToken = token;
        return settings;
    }

    private static HttpResponseMessage ValidationResponse() => JsonResponse("""
        {"client_id":"category-client-id","login":"viewer","user_id":"1234","scopes":[],"expires_in":3600}
        """);

    private static HttpResponseMessage StreamPage(HttpRequestMessage request, bool includeSecondCategory = false)
    {
        Assert.Equal("api.twitch.tv", request.RequestUri!.Host);
        Assert.Equal("/helix/streams", request.RequestUri.AbsolutePath);
        Assert.Contains("first=100", request.RequestUri.Query);
        Assert.Contains("game_id=1", request.RequestUri.Query);
        return request.RequestUri.Query.Contains("after=next", StringComparison.Ordinal)
            ? JsonResponse("""{"data":[{"id":"first","game_id":"1","viewer_count":100},{"id":"later","game_id":"1","viewer_count":25}],"pagination":{}}""")
            : JsonResponse("{\"data\":[{\"id\":\"first\",\"game_id\":\"1\",\"viewer_count\":100}" +
                (includeSecondCategory ? ",{\"id\":\"second\",\"game_id\":\"2\",\"viewer_count\":80}" : "") +
                "],\"pagination\":{\"cursor\":\"next\"}}");
    }

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
}

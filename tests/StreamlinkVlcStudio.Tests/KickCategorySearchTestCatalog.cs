internal static class KickCategorySearchTestCatalog
{
    private const string PoolsName = "Pools, Hot Tubs & Bikinis";
    private const string PoolsThumbnail = "https://files.kick.com/images/subcategories/16/banner/conversion/918d2983-47b8-41af-b044-38980d39d887-banner.webp";

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Kick category search: hot tubs and other matches inside category names use the search API", SearchNamesAsync),
        ("Kick category search: short queries and punctuation are sent intact", QueryEncodingAsync),
        ("Kick category search: small result pages retain every match across API pages", PaginationAsync),
        ("Kick category search: duplicates and invalid rows cannot hide the next API page", InvalidRowsAsync),
        ("Kick category search: empty results differ from malformed or failed responses", FailuresAsync),
        ("Kick category search: unavailable details retain the discovered category", MissingDetailsAsync),
        ("Kick category search: invalid continuations request a refresh without guessing a page", InvalidCursorAsync),
        ("Kick category search: cancellation stops search and detail requests", CancellationAsync),
        ("Kick category search: discovery cards open the returned category and preserve the search", NavigationAsync)
    ];

    internal static IReadOnlyList<(string Name, Func<Task> Run)> LiveProbeTests =>
        Environment.GetEnvironmentVariable("SVS_TEST_KICK_CATEGORY_SEARCH_LIVE") == "1"
            ? [("Kick category search: live names metadata and category navigation", LiveSearchAsync)] : [];

    private static async Task SearchNamesAsync()
    {
        foreach (var (query, id, name) in new[]
        {
            ("hot tubs", 16, PoolsName), ("  HOT TUBS  ", 16, PoolsName),
            ("tubs", 16, PoolsName), ("Chatting", 15, "Just Chatting")
        })
        {
            var requests = new ConcurrentQueue<string>();
            using var http = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                Assert.Equal("api.kick.com", request.RequestUri!.Host);
                Assert.Equal("Bearer category-search-token", request.Headers.Authorization?.ToString());
                requests.Enqueue(request.RequestUri.AbsolutePath);
                if (request.RequestUri.AbsolutePath == "/public/v1/categories")
                {
                    Assert.Equal(query.Trim(), QueryValue(request.RequestUri, "q"));
                    Assert.Equal("1", QueryValue(request.RequestUri, "page"));
                    return SearchPage([Category(id, name)]);
                }

                Assert.Equal($"/public/v1/categories/{id}", request.RequestUri.AbsolutePath);
                return Detail(id, name, 1451);
            }));
            var result = await new BrowseService(new MemoryLogger(), http).GetCategoriesAsync(
                new BrowseCategoryRequest(PlatformKind.Kick, query), Settings());

            Assert.Equal(BrowseResultStatus.Available, result.Status);
            Assert.Equal("", result.NextCursor);
            var category = result.Items.Single();
            Assert.Equal(id.ToString(CultureInfo.InvariantCulture), category.Id);
            Assert.Equal(name, category.Name);
            Assert.Equal(1451, category.ViewerCount);
            Assert.SequenceEqual(new[] { "IRL" }, category.Tags);
            Assert.Equal(PoolsThumbnail, category.ThumbnailUrl);
            Assert.Equal(2, requests.Count);
        }
    }

    private static async Task QueryEncodingAsync()
    {
        foreach (var query in new[] { "ir", PoolsName, "Pokémon & + = ? /", "a" })
        {
            using var http = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                Assert.Equal("/public/v1/categories", request.RequestUri!.AbsolutePath);
                Assert.Equal(query, QueryValue(request.RequestUri, "q"));
                Assert.Equal("1", QueryValue(request.RequestUri, "page"));
                Assert.Equal(2, request.RequestUri.Query.TrimStart('?').Split('&').Length);
                return SearchPage([]);
            }));
            var result = await new BrowseService(new MemoryLogger(), http).GetCategoriesAsync(
                new BrowseCategoryRequest(PlatformKind.Kick, $"  {query}  "), Settings());
            Assert.Equal(BrowseResultStatus.Available, result.Status);
            Assert.Equal(0, result.Items.Count);
            Assert.Equal("", result.NextCursor);
        }
    }

    private static async Task PaginationAsync()
    {
        var searchPages = new ConcurrentQueue<int>();
        var detailIds = new ConcurrentQueue<int>();
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/public/v1/categories")
            {
                Assert.Equal("the", QueryValue(uri, "q"));
                var page = int.Parse(QueryValue(uri, "page"), CultureInfo.InvariantCulture);
                searchPages.Enqueue(page);
                Assert.True(page is 1 or 2, "Search must not prefetch later API pages.");
                return SearchPage(Enumerable.Range(page == 1 ? 1 : 101, page == 1 ? 100 : 3)
                    .Select(id => Category(id, $"The category {id}")));
            }

            var categoryId = int.Parse(uri.Segments[^1], CultureInfo.InvariantCulture);
            detailIds.Enqueue(categoryId);
            return Detail(categoryId, $"The category {categoryId}", categoryId);
        }));
        var service = new BrowseService(new MemoryLogger(), http);
        var settings = Settings();
        var ids = new List<int>();
        var cursor = "";
        var callCount = 0;
        do
        {
            var result = await service.GetCategoriesAsync(
                new BrowseCategoryRequest(PlatformKind.Kick, "the", cursor, 25), settings);
            Assert.Equal(BrowseResultStatus.Available, result.Status);
            Assert.True(result.Items.Count <= 25);
            ids.AddRange(result.Items.Select(category => int.Parse(category.Id, CultureInfo.InvariantCulture)));
            cursor = result.NextCursor;
            Assert.True(++callCount <= 5, "Category search pagination must terminate.");
            Assert.Equal(ids.Count, detailIds.Count);
        } while (cursor.Length > 0);

        Assert.SequenceEqual(Enumerable.Range(1, 103), ids.Order());
        Assert.SequenceEqual(Enumerable.Range(1, 103), detailIds.Order());
        Assert.SequenceEqual(new[] { 1, 1, 1, 1, 2 }, searchPages);

        var refreshed = await service.GetCategoriesAsync(
            new BrowseCategoryRequest(PlatformKind.Kick, "the", PageSize: 25), settings);
        Assert.SequenceEqual(Enumerable.Range(1, 25), refreshed.Items.Select(category => int.Parse(category.Id)).Order());
        Assert.Equal(1, searchPages.Last());
    }

    private static async Task InvalidRowsAsync()
    {
        var searchPages = new List<int>();
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/public/v1/categories")
            {
                var page = int.Parse(QueryValue(uri, "page"), CultureInfo.InvariantCulture);
                searchPages.Add(page);
                if (page == 2) return SearchPage([]);
                object?[] rows = [Category(16, PoolsName), Category(16, PoolsName), Category(15, "Just Chatting"), .. new object?[97]];
                return SearchPage(rows);
            }
            var id = int.Parse(uri.Segments[^1], CultureInfo.InvariantCulture);
            return Detail(id, id == 16 ? PoolsName : "Just Chatting", 0);
        }));
        var service = new BrowseService(new MemoryLogger(), http);
        var first = await service.GetCategoriesAsync(new(PlatformKind.Kick, "chat", PageSize: 1), Settings());
        var second = await service.GetCategoriesAsync(new(PlatformKind.Kick, "chat", first.NextCursor, 1), Settings());
        var last = await service.GetCategoriesAsync(new(PlatformKind.Kick, "chat", second.NextCursor, 1), Settings());
        Assert.Equal("16", first.Items.Single().Id);
        Assert.Equal("15", second.Items.Single().Id);
        Assert.Equal(BrowseResultStatus.Available, last.Status);
        Assert.Equal(0, last.Items.Count);
        Assert.Equal("", last.NextCursor);
        Assert.SequenceEqual(new[] { 1, 1, 2 }, searchPages);
    }

    private static async Task FailuresAsync()
    {
        foreach (var body in new[] { "{}", "{\"data\":null}", "{\"data\":{}}", "not json" })
        {
            using var http = new HttpClient(new FakeHttpMessageHandler(_ => Json(body)));
            var result = await new BrowseService(new MemoryLogger(), http).GetCategoriesAsync(new(PlatformKind.Kick, "hot tubs"), Settings());
            Assert.Equal(BrowseResultStatus.Unavailable, result.Status);
            Assert.Equal(0, result.Items.Count);
            Assert.Equal("", result.NextCursor);
        }
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError })
        {
            using var http = new HttpClient(new FakeHttpMessageHandler(_ => Json("{\"message\":\"Unavailable\"}", status)));
            var result = await new BrowseService(new MemoryLogger(), http).GetCategoriesAsync(new(PlatformKind.Kick, "hot tubs"), Settings());
            Assert.Equal(status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? BrowseResultStatus.Unauthorized : BrowseResultStatus.Unavailable, result.Status);
            Assert.Equal(0, result.Items.Count);
            Assert.Equal("", result.NextCursor);
        }
    }

    private static async Task MissingDetailsAsync()
    {
        foreach (var detail in new[] { "{\"data\":{}}", "{}" })
        {
            using var http = new HttpClient(new FakeHttpMessageHandler(request => request.RequestUri!.AbsolutePath == "/public/v1/categories"
                ? SearchPage([Category(16, PoolsName)]) : Json(detail)));
            var result = await new BrowseService(new MemoryLogger(), http).GetCategoriesAsync(new(PlatformKind.Kick, "hot tubs"), Settings());
            Assert.Equal(BrowseResultStatus.Available, result.Status);
            Assert.Equal("16", result.Items.Single().Id);
            Assert.Equal(PoolsName, result.Items.Single().Name);
            Assert.Equal(PoolsThumbnail, result.Items.Single().ThumbnailUrl);
            Assert.Equal<int?>(null, result.Items.Single().ViewerCount);
            Assert.Contains("Viewer counts unavailable", result.Message);
        }
    }

    private static async Task InvalidCursorAsync()
    {
        var calls = 0;
        using var http = new HttpClient(new FakeHttpMessageHandler(_ => { calls++; return SearchPage([]); }));
        var service = new BrowseService(new MemoryLogger(), http);
        foreach (var cursor in new[] { "old-api-cursor", "kick-category-search:0:0", "kick-category-search:1:-1", "kick-category-search:1:100", "kick-category-search:2147483647:0" })
        {
            var result = await service.GetCategoriesAsync(new(PlatformKind.Kick, "hot tubs", cursor), Settings());
            Assert.Equal(BrowseResultStatus.Unavailable, result.Status);
            Assert.Contains("Refresh", result.Message);
        }
        Assert.Equal(0, calls);
    }

    private static async Task CancellationAsync()
    {
        foreach (var cancelDetail in new[] { false, true })
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
            {
                if (cancelDetail && request.RequestUri!.AbsolutePath == "/public/v1/categories")
                    return SearchPage([Category(16, PoolsName)]);
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return SearchPage([]);
            }));
            var pending = new BrowseService(new MemoryLogger(), http).GetCategoriesAsync(new(PlatformKind.Kick, "hot tubs"), Settings(), cancellation.Token);
            await started.Task.WaitAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        }
    }

    private static Task NavigationAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        using var http = new HttpClient(new FakeHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/public/v1/categories" => SearchPage([Category(16, PoolsName)]),
            "/public/v1/categories/16" => Detail(16, PoolsName, 1451),
            "/public/v1/livestreams" => Streams(request),
            _ => throw new InvalidOperationException($"Unexpected browse request: {request.RequestUri.AbsolutePath}")
        }));
        var settings = Settings();
        settings.Chat.ConnectAutomatically = false;
        await using var main = TestViewModels.CreateMain(settings, new FakeSettingsService(settings), new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(), action => action(),
            browseService: new BrowseService(new MemoryLogger(), http), browseCategorySearchDebounceInterval: TimeSpan.FromHours(1));
        main.ShowBrowseHomePageCommand.Execute(null);
        main.BrowseCategorySearchText = "hot tubs";
        main.SelectKickBrowsePlatformCommand.Execute(null);
        await main.RefreshBrowseCommand.ExecuteAsync();
        var card = main.BrowseCategories.Single();
        Assert.Equal(PoolsName, card.Name);
        Assert.Equal("16", card.Category.Id);
        Assert.Equal("1.5K viewers | IRL", card.MetadataText);
        await card.SelectCommand.ExecuteAsync();
        Assert.True(main.IsBrowseStreamsPageVisible);
        Assert.Equal(PoolsName, main.SelectedBrowseCategoryName);
        Assert.Equal("16", main.SelectedBrowseCategory!.Id);
        Assert.Equal(PoolsName, main.BrowseStreams.Single().CategoryName);
        Assert.Equal("poolstreamer", main.BrowseStreams.Single().Channel);
        main.ReturnToBrowseCategoriesCommand.Execute(null);
        Assert.Equal("hot tubs", main.BrowseCategorySearchText);
        Assert.True(ReferenceEquals(card, main.BrowseCategories.Single()));
    });

    private static async Task LiveSearchAsync()
    {
        var settings = await new JsonSettingsService().LoadAsync();
        var service = new BrowseService(new MemoryLogger());
        foreach (var (query, expectedId) in new[] { ("hot tubs", "16"), ("HOT TUBS", "16"), ("tubs", "16"), (PoolsName, "16"), ("Chatting", "15") })
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var result = await service.GetCategoriesAsync(new(PlatformKind.Kick, query, PageSize: 10), settings, cancellation.Token);
            Console.WriteLine($"LIVE Kick category '{query}': {result.Status}; {string.Join(", ", result.Items.Select(category => $"{category.Id}: {category.Name}"))}");
            Assert.Equal(BrowseResultStatus.Available, result.Status);
            var category = result.Items.Single(item => item.Id == expectedId);
            Assert.True(category.ViewerCount is >= 0);
            Assert.True(category.Tags.Count > 0);
            Assert.True(Uri.TryCreate(category.ThumbnailUrl, UriKind.Absolute, out _));
            if (query == "hot tubs")
            {
                var streams = await service.GetStreamsAsync(new(PlatformKind.Kick, category.Id, category.Name), settings, cancellation.Token);
                Assert.Equal(BrowseResultStatus.Available, streams.Status);
                Assert.True(streams.Items.All(stream => stream.CategoryId == expectedId));
                Console.WriteLine($"LIVE Kick category navigation: {streams.Items.Count} streams for category {expectedId}.");
            }
        }
    }

    private static HttpResponseMessage Streams(HttpRequestMessage request)
    {
        Assert.Equal("16", QueryValue(request.RequestUri!, "category_id"));
        return Json(JsonSerializer.Serialize(new
        {
            data = new[] { new { slug = "poolstreamer", stream_title = "Pool stream", viewer_count = 50, category = new { id = 16, name = PoolsName } } },
            message = "OK"
        }));
    }

    private static AppSettings Settings()
    {
        var settings = new AppSettings();
        settings.Chat.KickOAuthToken = "category-search-token";
        return settings;
    }

    private static object Category(int id, string name) => new { id, name, thumbnail = PoolsThumbnail };
    private static HttpResponseMessage SearchPage(IEnumerable<object?> categories) => Json(JsonSerializer.Serialize(new { data = categories, message = "OK" }));
    private static HttpResponseMessage Detail(int id, string name, int viewers) => Json(JsonSerializer.Serialize(new
    {
        data = new { id, name, thumbnail = PoolsThumbnail, tags = new[] { "IRL" }, viewer_count = viewers },
        message = "OK"
    }));
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
    private static string QueryValue(Uri uri, string name) => uri.Query.TrimStart('?').Split('&')
        .Select(pair => pair.Split('=', 2)).Where(parts => parts.Length == 2 && parts[0] == name)
        .Select(parts => Uri.UnescapeDataString(parts[1])).Single();
}

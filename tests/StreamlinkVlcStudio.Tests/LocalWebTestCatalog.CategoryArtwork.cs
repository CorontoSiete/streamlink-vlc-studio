using Microsoft.AspNetCore.Http;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private const string ArtworkCdn = "https://static-cdn.jtvnw.net/ttv-boxart/";
    private static IReadOnlyList<(string Name, Func<Task> Run)> LiveCategoryArtworkTests =>
        Environment.GetEnvironmentVariable("SVS_TEST_TWITCH_DISCOVER_ARTWORK_LIVE") == "1"
            ? [("local web: category artwork live directory covers match Twitch availability", CategoryArtworkLiveAsync)] : [];

    private static async Task CategoryArtworkFormatsAsync()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var jpeg = await File.ReadAllBytesAsync(Path.Combine(fixture, "category-artwork", "league-of-legends-2026-10-22.jpg"));
        var png = await File.ReadAllBytesAsync(Path.Combine(fixture, "profile-images", "nicewigg-transparent-150.png"));
        var webp = await File.ReadAllBytesAsync(Path.Combine(fixture, "profile-images", "xqc-2026-10-22.webp"));
        var cases = new (byte[] Bytes, string? Header, string? Expected)[]
        {
            (jpeg, "image/png", "image/jpeg"), // Actual League, WoW, CS, Dota, VALORANT and IRL response format.
            (jpeg, "application/octet-stream", "image/jpeg"),
            (jpeg, null, "image/jpeg"),
            (png, "image/jpeg", "image/png"),
            (webp, "image/jpeg", "image/webp"),
            ("<html>CDN error</html>"u8.ToArray(), "image/jpeg", null),
            ([], "image/jpeg", null)
        };
        var requests = 0;
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requests++;
            var index = int.Parse(Path.GetFileName(request.RequestUri!.AbsolutePath).Split('-')[0], CultureInfo.InvariantCulture);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(cases[index].Bytes) };
            if (cases[index].Header is { } header) response.Content.Headers.ContentType = new(header);
            return response;
        }));
        var artwork = new LocalWebCategoryArtwork(http);
        for (var index = 0; index < cases.Length; index++)
        {
            // Serve twice to check that cached responses retain the format determined from bytes.
            for (var repeat = 0; repeat < 2; repeat++)
            {
                var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
                await artwork.ServeAsync(context, ArtworkCdn + index + "-285x380.jpg");
                Assert.Equal(cases[index].Expected is null ? 404 : 200, context.Response.StatusCode);
                Assert.Equal(cases[index].Expected, context.Response.ContentType);
                Assert.SequenceEqual(cases[index].Expected is null ? [] : cases[index].Bytes, ((MemoryStream)context.Response.Body).ToArray());
            }
        }
        Assert.Equal(cases.Length, requests);
    }

    private static async Task<DefaultHttpContext> ArtworkResponse(LocalWebWorkspace workspace, string path, string method = "GET")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        await workspace.ServeCategoryArtworkAsync(context, path.Split('/')[^1]);
        return context;
    }

    private static async Task VerifyCategoryArtworkAsync(LocalWebDependencies dependencies)
    {
        var time = DateTimeOffset.UtcNow;
        var recovered = false;
        var requests = new List<string>();
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xd9]; // Transport signature; decoded covers are checked by the browser regression.
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal(false, request.Headers.Contains("Cookie"));
            Assert.Equal(false, request.Headers.Contains("Authorization"));
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            if (path.Contains("498566", StringComparison.Ordinal) && !recovered)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("/ttv-static/404_boxart-285x380.jpg", UriKind.Relative);
                return redirect;
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) };
            response.Content.Headers.ContentType = new("image/jpeg");
            if (path.Contains("66082", StringComparison.Ordinal))
                response.RequestMessage = new(HttpMethod.Get, "https://static-cdn.jtvnw.net/ttv-static/404_boxart-285x380.jpg");
            if (path.Contains("unsafe", StringComparison.Ordinal))
            {
                response.StatusCode = HttpStatusCode.Found;
                response.Headers.Location = new Uri("https://127.0.0.1/private");
            }
            if (path.Contains("oversized", StringComparison.Ordinal))
            {
                response.Content = new ByteArrayContent(new byte[256 * 1024 + 1]);
                response.Content.Headers.ContentType = new("image/jpeg");
            }
            return response;
        }));
        var browse = new FakeBrowseService
        {
            CategoryResponder = request => new(BrowseResultStatus.Available,
                [new(request.Platform, "498566", "Slots", ArtworkCdn + "498566-285x380.jpg", []),
                 new(request.Platform, "66082", "Games + Demos", ArtworkCdn + "66082-285x380.jpg", []),
                 new(request.Platform, "509658", "Just Chatting", ArtworkCdn + "509658-285x380.jpg", [])], "next", "")
        };
        var artwork = new LocalWebCategoryArtwork(http, () => time);
        await using var workspace = new LocalWebWorkspace(dependencies with { Browse = browse, CategoryArtwork = artwork });
        var result = await Call(workspace, "categories", new { platform = "twitch" });
        var items = result.GetProperty("items").EnumerateArray().ToArray();
        var urls = items.Select(item => item.GetProperty("thumbnailUrl").GetString()!).ToArray();
        Assert.True(urls.All(url => url.StartsWith("/category-artwork/", StringComparison.Ordinal)));
        Assert.SequenceEqual(new[] { "498566", "66082", "509658" }, items.Select(item => item.GetProperty("id").GetString()!));
        Assert.Equal("next", result.GetProperty("nextCursor").GetString());
        Assert.Equal(0, requests.Count); // Listing categories must not wait for any image downloads.
        foreach (var url in urls.Take(2))
        {
            var missing = await ArtworkResponse(workspace, url);
            Assert.Equal(404, missing.Response.StatusCode);
            Assert.Equal(0L, missing.Response.Body.Length);
            Assert.Equal("no-store", missing.Response.Headers.CacheControl.ToString());
        }
        Assert.Equal(2, requests.Count); // Do not download Twitch's successful missing-art JPEG.
        var valid = await ArtworkResponse(workspace, urls[2]);
        Assert.Equal(200, valid.Response.StatusCode);
        Assert.Equal("image/jpeg", valid.Response.ContentType);
        Assert.SequenceEqual(jpeg, ((MemoryStream)valid.Response.Body).ToArray());
        var head = await ArtworkResponse(workspace, urls[2], "HEAD");
        Assert.Equal((long?)jpeg.Length, head.Response.ContentLength);
        Assert.Equal(0L, head.Response.Body.Length);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => ArtworkResponse(workspace, urls[2])));
        Assert.Equal(3, requests.Count);
        recovered = true;
        Assert.Equal(404, (await ArtworkResponse(workspace, urls[0])).Response.StatusCode);
        time = time.AddSeconds(31);
        var refreshed = await Call(workspace, "categories", new { platform = "twitch", query = "Slots" });
        Assert.Equal(urls[0], refreshed.GetProperty("items")[0].GetProperty("thumbnailUrl").GetString());
        Assert.Equal(200, (await ArtworkResponse(workspace, urls[0])).Response.StatusCode);
        Assert.Equal(4, requests.Count);
        Assert.Equal(404, (await ArtworkResponse(workspace, "/category-artwork/unregistered")).Response.StatusCode);

        foreach (var url in new[] { "https://127.0.0.1/a.jpg", "http://static-cdn.jtvnw.net/ttv-boxart/1.jpg",
            "https://static-cdn.jtvnw.net.evil.test/ttv-boxart/1.jpg", "https://user:secret@static-cdn.jtvnw.net/ttv-boxart/1.jpg",
            ArtworkCdn + "unsafe.jpg", ArtworkCdn + "oversized.jpg" })
        {
            var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
            await artwork.ServeAsync(context, url);
            Assert.Equal(404, context.Response.StatusCode);
            Assert.Equal(0L, context.Response.Body.Length);
        }
        Assert.Equal(6, requests.Count); // The foreign redirect and unsafe initial URLs were never fetched.
        var kick = await Call(workspace, "categories", new { platform = "kick" });
        Assert.Equal(ArtworkCdn + "498566-285x380.jpg", kick.GetProperty("items")[0].GetProperty("thumbnailUrl").GetString());
    }

    private static async Task VerifyCategoryArtworkLiveAsync(LocalWebDependencies dependencies)
    {
        var settings = await new JsonSettingsService().LoadAsync();
        using var capture = new CategoryArtworkCaptureHandler();
        using var browseHttp = new HttpClient(capture) { Timeout = TimeSpan.FromSeconds(15) };
        var browse = new BrowseService(new MemoryLogger(), browseHttp);
        await using var workspace = new LocalWebWorkspace(dependencies with { Browse = browse, GetSettingsAsync = _ => Task.FromResult(settings) });
        using var cdn = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var categories = new Dictionary<string, (BrowseCategory Category, string Route)>(StringComparer.Ordinal);
        foreach (var query in new[] { "", "Slots", "Games + Demos" })
        {
            var result = await Call(workspace, "categories", new { platform = "twitch", query });
            var items = result.GetProperty("items").EnumerateArray().ToArray();
            if (query.Length == 0) Assert.Equal(50, items.Length);
            else Assert.True(items.Any(item => item.GetProperty("name").GetString() == query));
            foreach (var item in items.Where(item => query.Length == 0 || item.GetProperty("name").GetString() == query))
            {
                var id = item.GetProperty("id").GetString()!;
                var route = item.GetProperty("thumbnailUrl").GetString()!;
                Assert.True(route.StartsWith("/category-artwork/", StringComparison.Ordinal));
                categories[id] = (capture.Categories[id], route);
            }
        }
        using var slots = new SemaphoreSlim(4, 4);
        var rows = await Task.WhenAll(categories.Values.Select(async entry =>
        {
            await slots.WaitAsync();
            try
            {
                using var upstream = await cdn.GetAsync(entry.Category.ThumbnailUrl);
                Assert.Equal(HttpStatusCode.OK, upstream.StatusCode);
                var directBytes = await upstream.Content.ReadAsByteArrayAsync();
                var final = upstream.RequestMessage!.RequestUri!;
                var missing = final.AbsolutePath.StartsWith("/ttv-static/404_boxart-", StringComparison.Ordinal);
                using var decoded = System.Drawing.Image.FromStream(new MemoryStream(directBytes));
                Assert.Equal(285, decoded.Width);
                Assert.Equal(380, decoded.Height);
                var response = await ArtworkResponse(workspace, entry.Route);
                var served = ((MemoryStream)response.Response.Body).ToArray();
                return new
                {
                    entry.Category.Name,
                    entry.Category.Id,
                    sourceUrl = entry.Category.ThumbnailUrl,
                    finalUrl = final.AbsoluteUri,
                    upstreamContentType = upstream.Content.Headers.ContentType?.MediaType,
                    expectedMissing = missing,
                    status = response.Response.StatusCode,
                    contentType = response.Response.ContentType,
                    bytes = served.Length,
                    exactBytes = served.SequenceEqual(directBytes),
                    decoded.Width,
                    decoded.Height
                };
            }
            finally { slots.Release(); }
        }));
        var directory = Environment.GetEnvironmentVariable("SVS_TEST_ARTIFACT_DIR");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "live-category-artwork.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        }
        foreach (var row in rows)
            Console.WriteLine($"LIVE {row.Name}: header={row.upstreamContentType}; missing={row.expectedMissing}; web status={row.status}; {row.bytes} bytes");
        var failures = rows.Where(row => row.expectedMissing ? row.status != 404 || row.bytes != 0 : row.status != 200 || !row.exactBytes).ToArray();
        Assert.Equal(0, failures.Length);
        Console.WriteLine($"LIVE directory: {rows.Length} categories checked; {rows.Count(row => !row.expectedMissing)} actual covers; {rows.Count(row => row.expectedMissing)} upstream missing covers.");
        await CheckLiveCategoryArtworkBrowserAsync(dependencies with { Browse = browse, GetSettingsAsync = _ => Task.FromResult(settings) },
            rows.ToDictionary(row => row.Id, row => row.expectedMissing, StringComparer.Ordinal), capture.Categories, cdn, directory);
    }

    private sealed class CategoryArtworkCaptureHandler() : DelegatingHandler(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        internal Dictionary<string, BrowseCategory> Categories { get; } = new(StringComparer.Ordinal);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (response.IsSuccessStatusCode && request.RequestUri is { Host: "api.twitch.tv", AbsolutePath: "/helix/games/top" or "/helix/search/categories" })
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                foreach (var category in BrowsePayloadMapper.ReadCategories(document.RootElement, PlatformKind.Twitch)) Categories[category.Id] = category;
            }
            return response;
        }
    }

    private static async Task CategoryArtworkBodyDeadlineAsync()
    {
        var streams = new System.Collections.Concurrent.ConcurrentBag<CodeReviewTestCatalog.StalledReadStream>();
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("valid.jpg", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([0xff, 0xd8, 0xff]) };
            var stream = new CodeReviewTestCatalog.StalledReadStream();
            streams.Add(stream);
            return new(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        }))
        { Timeout = TimeSpan.FromMilliseconds(150) };
        var artwork = new LocalWebCategoryArtwork(http);
        using var cancellation = new CancellationTokenSource();
        async Task Serve(int id)
        {
            var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
            await artwork.ServeAsync(context, ArtworkCdn + id + ".jpg");
            Assert.Equal(404, context.Response.StatusCode);
        }
        var pending = Task.WhenAll(Enumerable.Range(0, 8).Select(Serve));
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(8, streams.Count);
            Assert.True(streams.All(stream => stream.SawCancellation));
            Assert.Equal(false, cancellation.IsCancellationRequested);
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            await artwork.ServeAsync(context, ArtworkCdn + "valid.jpg").WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(200, context.Response.StatusCode);
            Assert.Equal(3L, context.Response.Body.Length);
        }
        finally
        {
            cancellation.Cancel();
            try { await pending; } catch (OperationCanceledException) { }
        }
    }

    private static async Task CategoryArtworkResourcesAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var metrics = new object();
        var requests = 0;
        var block = true;
        var time = DateTimeOffset.UtcNow;
        using var http = new HttpClient(new AsyncHttpMessageHandler(async (_, token) =>
        {
            Interlocked.Increment(ref requests);
            var current = Interlocked.Increment(ref active);
            lock (metrics) maximum = Math.Max(maximum, current);
            if (current == 4) started.TrySetResult();
            try
            {
                if (block) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xff, 0xd8, 0xff]) };
                response.Content.Headers.ContentType = new("image/jpeg");
                return response;
            }
            finally { Interlocked.Decrement(ref active); }
        }));
        var artwork = new LocalWebCategoryArtwork(http, () => time);
        async Task Serve(int id, CancellationToken token = default)
        {
            var context = new DefaultHttpContext { RequestAborted = token };
            context.Response.Body = new MemoryStream();
            await artwork.ServeAsync(context, ArtworkCdn + id + "-285x380.jpg");
            Assert.Equal(200, context.Response.StatusCode);
        }
        using var cancellation = new CancellationTokenSource();
        var pending = Enumerable.Range(0, 8).Select(id => Serve(id, cancellation.Token)).ToArray();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(4, active);
            Assert.Equal(4, requests);
        }
        finally { await cancellation.CancelAsync(); }
        foreach (var task in pending) await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, active);
        Assert.Equal(4, maximum);
        // Cancellation callbacks can release a slot before another queued callback runs.
        // Count retries from the fully drained baseline instead of assuming callback order.
        var cancelledRequests = requests;
        block = false;
        await Serve(0); // Cancelled work must be immediately retryable, not failure-cached.
        Assert.Equal(cancelledRequests + 1, requests);
        await Serve(0);
        Assert.Equal(cancelledRequests + 1, requests);
        time = time.AddMinutes(11);
        await Serve(0);
        Assert.Equal(cancelledRequests + 2, requests);
        // More distinct covers than the cache capacity evict the least recently used entry.
        for (var id = 100; id < 229; id++) { time = time.AddSeconds(1); await Serve(id); }
        Assert.Equal(cancelledRequests + 131, requests);
        await Serve(0);
        Assert.Equal(cancelledRequests + 132, requests);
    }
}

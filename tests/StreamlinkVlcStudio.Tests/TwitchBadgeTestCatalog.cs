internal static class TwitchBadgeTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Twitch badge recovery: every captured Alinity Helix version resolves exactly", HelixVersionsAsync),
        ("Twitch badge recovery: public catalogs preserve all versions and channel identity", PublicChannelsAsync),
        ("Twitch badge recovery: rejected Helix requests use an anonymous fallback", RejectedHelixAsync),
        ("Twitch badge recovery: malformed public responses back off and recover", InvalidPublicResponsesAsync),
        ("Twitch badge recovery: global public badges preserve bundled overrides", PublicGlobalAsync),
        .. Environment.GetEnvironmentVariable("SVS_TEST_LIVE_TWITCH_BADGES") == "true"
            ? [("Twitch badge recovery: opt-in live catalogs resolve three broadcasters", LiveChannelsAsync)] : Array.Empty<(string, Func<Task>)>()
    ];

    internal static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "twitch-badges", name));

    private static ChatMessage Message(string room = "38718052", string channel = "alinity") =>
        new(PlatformKind.Twitch, channel, "viewer", "message", DateTimeOffset.UnixEpoch, RoomId: room);

    private static void Prime(DockedChatBadgeCatalog catalog)
    {
        var coordinator = (CatalogLoadCoordinator)typeof(DockedChatBadgeCatalog)
            .GetField("loadCoordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        foreach (var scope in new[] { "badges:twitch:global", "badges:twitch:bundled" })
            coordinator.Ensure(scope, () => Task.FromResult(CatalogLoadResult.Successful()), preserveFromEviction: true);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private static async Task HelixVersionsAsync()
    {
        var requests = 0;
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://api.twitch.tv/helix/chat/badges?broadcaster_id=38718052", request.RequestUri!.AbsoluteUri);
            requests++;
            return Json(Fixture("alinity-helix-2026-10-22.json"));
        }));
        var catalog = new DockedChatBadgeCatalog(http);
        catalog.ConfigureTwitchCredentials("client", "token"); Prime(catalog);
        var message = Message(); catalog.EnsureForMessage(message);
        await TestWait.UntilAsync(() => catalog.TryGet(message, new("subscriber", "0"), out _), TimeSpan.FromSeconds(3));
        using var fixture = JsonDocument.Parse(Fixture("alinity-helix-2026-10-22.json"));
        var versions = fixture.RootElement.GetProperty("data")[0].GetProperty("versions");
        Assert.Equal(30, versions.GetArrayLength());
        foreach (var version in versions.EnumerateArray())
        {
            Assert.True(catalog.TryGet(message, new("subscriber", version.GetProperty("id").GetString()), out var image));
            Assert.Equal(version.GetProperty("image_url_2x").GetString(), image.ImageUrl);
            Assert.Equal(version.GetProperty("title").GetString(), image.Code);
        }
        Assert.Equal(1, requests);
    }

    private static async Task PublicChannelsAsync()
    {
        var fixtures = new Dictionary<string, string>();
        foreach (var name in new[] { "alinity", "pokimane", "shroud" })
        {
            var body = Fixture(name + "-public-2026-10-22.json");
            using var parsed = JsonDocument.Parse(body);
            fixtures[parsed.RootElement.GetProperty("data").GetProperty("user").GetProperty("id").GetString()!] = body;
        }
        var requests = 0;
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("https://gql.twitch.tv/gql", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method); Assert.True(!request.Headers.Contains("Authorization"));
            Assert.Equal(TwitchGraphQlTransport.PublicClientId, request.Headers.GetValues("Client-Id").Single());
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var room = body.RootElement.GetProperty("variables").GetProperty("id").GetString()!;
            requests++;
            return Json(fixtures[room]);
        }));
        var catalog = new DockedChatBadgeCatalog(http); Prime(catalog);
        foreach (var (room, body) in fixtures)
        {
            var message = Message(room); catalog.EnsureForMessage(message);
            await CheckPublicVersionsAsync(catalog, message, body);
        }
        Assert.Equal(3, requests);
        Assert.True(!catalog.TryGet(Message("999999"), new("subscriber", "0"), out _), "Another room must never inherit these subscriber images.");
    }

    private static async Task CheckPublicVersionsAsync(DockedChatBadgeCatalog catalog, ChatMessage message, string body)
    {
        using var parsed = JsonDocument.Parse(body);
        var versions = parsed.RootElement.GetProperty("data").GetProperty("user").GetProperty("broadcastBadges");
        var expected = versions.EnumerateArray().ToArray();
        // A global bits badge may resolve before the channel request completes.
        // Wait for every exact channel URL, rather than accepting that global fallback.
        await TestWait.UntilAsync(() => expected.All(version =>
            catalog.TryGet(message, new(version.GetProperty("setID").GetString()!, version.GetProperty("version").GetString()), out var image) &&
            image.ImageUrl == version.GetProperty("imageURL").GetString()), TimeSpan.FromSeconds(8));
        foreach (var version in versions.EnumerateArray())
        {
            Assert.True(catalog.TryGet(message, new(version.GetProperty("setID").GetString()!, version.GetProperty("version").GetString()), out var image));
            Assert.Equal(version.GetProperty("imageURL").GetString(), image.ImageUrl);
            Assert.Equal(version.GetProperty("title").GetString(), image.Code);
        }
    }

    private static async Task RejectedHelixAsync()
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.ServiceUnavailable })
        {
            var paths = new List<string>();
            using var http = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                paths.Add(request.RequestUri!.Host);
                if (request.RequestUri.Host == "api.twitch.tv")
                {
                    Assert.Equal("Bearer private-token", request.Headers.GetValues("Authorization").Single());
                    return Json("{}", status);
                }
                Assert.Equal("gql.twitch.tv", request.RequestUri.Host);
                Assert.True(!request.Headers.Contains("Authorization"));
                return Json(Fixture("alinity-public-2026-10-22.json"));
            }));
            var catalog = new DockedChatBadgeCatalog(http);
            catalog.ConfigureTwitchCredentials("client", "private-token"); Prime(catalog);
            catalog.EnsureForMessage(Message());
            await CheckPublicVersionsAsync(catalog, Message(), Fixture("alinity-public-2026-10-22.json"));
            Assert.SequenceEqual(new[] { "api.twitch.tv", "gql.twitch.tv" }, paths);
        }
    }

    private static async Task InvalidPublicResponsesAsync()
    {
        foreach (var body in new[]
        {
            "[]", "{}", "{\"data\":null}", "{\"data\":{\"user\":null}}",
            "{\"data\":{\"user\":{\"id\":\"38718052\",\"broadcastBadges\":null}}}",
            Fixture("alinity-public-2026-10-22.json").Replace("38718052", "999999", StringComparison.Ordinal),
            Fixture("alinity-public-2026-10-22.json").Replace("\"data\":", "\"errors\":[{\"message\":\"Unavailable\"}],\"data\":", StringComparison.Ordinal)
        })
        {
            var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch); var requests = 0; var recovered = false;
            using var http = new HttpClient(new FakeHttpMessageHandler(_ => { requests++; return Json(recovered ? Fixture("alinity-public-2026-10-22.json") : body); }));
            var catalog = new DockedChatBadgeCatalog(http, clock); Prime(catalog);
            catalog.EnsureForMessage(Message());
            Assert.True(!catalog.TryGet(Message(), new("subscriber", "0"), out _));
            recovered = true; catalog.EnsureForMessage(Message()); Assert.Equal(1, requests);
            clock.Advance(TimeSpan.FromSeconds(6)); catalog.EnsureForMessage(Message());
            await CheckPublicVersionsAsync(catalog, Message(), Fixture("alinity-public-2026-10-22.json"));
            Assert.Equal(2, requests);
        }
    }

    private static async Task PublicGlobalAsync()
    {
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.True(!request.Headers.Contains("Authorization"));
            return Json("""{"data":{"badges":[{"setID":"moderator","version":"1","title":"Moderator","imageURL":"https://static-cdn.jtvnw.net/moderator.png"},{"setID":"new-badge","version":"1","title":"New badge","imageURL":"https://static-cdn.jtvnw.net/new.png"}]}}""");
        }));
        var catalog = new DockedChatBadgeCatalog(http);
        typeof(DockedChatBadgeCatalog).GetMethod("AddTwitchBadge", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(catalog, [null, "moderator", "1", "Bundled Moderator", "https://static-cdn.jtvnw.net/bundled.png"]);
        var load = typeof(DockedChatBadgeCatalog).GetMethod("LoadTwitchBadgesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((await (Task<CatalogLoadResult>)load.Invoke(catalog, new object?[] { null })!).Succeeded);
        Assert.True(catalog.TryGet(Message(), new("moderator", "1"), out var moderator));
        Assert.Equal("https://static-cdn.jtvnw.net/bundled.png", moderator.ImageUrl);
        Assert.True(catalog.TryGet(Message(), new("new-badge", "1"), out _));
    }

    private static async Task LiveChannelsAsync()
    {
        var catalog = new DockedChatBadgeCatalog();
        foreach (var (login, room) in new[] { ("alinity", "38718052"), ("pokimane", "44445592"), ("shroud", "37402112") })
        {
            var message = Message(room, login); catalog.EnsureForMessage(message);
            await CheckPublicVersionsAsync(catalog, message, Fixture(login + "-public-2026-10-22.json"));
            Console.WriteLine($"Live Twitch badge catalog matched every captured version for {login} ({room}).");
        }
    }
}

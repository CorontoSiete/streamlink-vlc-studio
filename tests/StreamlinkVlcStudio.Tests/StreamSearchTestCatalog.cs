internal static class StreamSearchTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("stream search finds iiTzTimmy from a partial name without sign-in", PartialNameSearchAsync),
        ("stream search keeps relevant offline substring matches within the result limit", PartialMatchRelevanceAsync),
        ("stream search falls back to Helix for rejected malformed and empty website results", WebsiteFallbackAsync),
        ("stream search retains Kick discovery and exact probing when Twitch website search fails", IndependentPlatformFallbackAsync),
        ("stream search cancellation stops website discovery without starting Helix or probes", WebsiteCancellationAsync)
    ];

    private static async Task PartialNameSearchAsync()
    {
        foreach (var token in new[] { "", "configured-token-not-for-website" })
        {
            var settings = new AppSettings();
            settings.Chat.TwitchOAuthToken = token;
            var websiteRequests = 0;
            using var httpClient = new HttpClient(new AsyncHttpMessageHandler(async (request, cancellationToken) =>
            {
                if (request.RequestUri!.Host == "kick.com")
                {
                    Assert.Contains("searched_word=timmy", request.RequestUri.Query);
                    return JsonResponse("""{"channels":[]}""");
                }

                Assert.Equal("gql.twitch.tv", request.RequestUri.Host);
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.True(request.Headers.Authorization is null);
                Assert.SequenceEqual(["kimne78kx3ncx6brgo4mv6wki5h1ko"], request.Headers.GetValues("Client-Id"));
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal("timmy", body.RootElement.GetProperty("variables").GetProperty("query").GetString());
                Assert.Equal(10, body.RootElement.GetProperty("variables").GetProperty("first").GetInt32());
                websiteRequests++;
                return JsonResponse(WebsiteResponse(
                    new
                    {
                        login = "iitztimmy",
                        displayName = "iiTzTimmy",
                        profileImageURL = "https://static-cdn.jtvnw.net/timmy.png",
                        stream = new { id = "123", viewersCount = 4052, game = new { name = "VALORANT" } },
                        broadcastSettings = new { title = "Ranked matches" }
                    },
                    new { login = "timmy", displayName = "timmy", stream = (object?)null },
                    new { login = "IITZTIMMY", displayName = "iiTzTimmy", stream = (object?)null },
                    new { login = "../invalid", stream = (object?)null },
                    new { displayName = "Not a channel" },
                    null));
            }));
            var streamlink = new FakeStreamlinkService();
            var service = new StreamSearchService(new MemoryLogger(), streamlink, httpClient);

            var result = await service.SearchAsync(new StreamSearchRequest("  @Timmy  "), settings);

            Assert.Equal(1, websiteRequests);
            var timmy = result.Channels.First();
            Assert.Equal("iitztimmy", timmy.Channel);
            Assert.Equal("iiTzTimmy", timmy.DisplayName);
            Assert.Equal("https://www.twitch.tv/iitztimmy", timmy.Url);
            Assert.Equal("https://static-cdn.jtvnw.net/timmy.png", timmy.ThumbnailUrl);
            Assert.Equal(timmy.ThumbnailUrl, timmy.ProfileImageUrl);
            Assert.Equal("VALORANT", timmy.CategoryName);
            Assert.Equal("Ranked matches", timmy.Title);
            Assert.Equal(4052, timmy.ViewerCount);
            Assert.True(timmy.CanPlay);
            Assert.Equal(StreamSearchChannelState.Live, timmy.State);
            Assert.Equal(2, result.Channels.Count(channel => channel.Platform == PlatformKind.Twitch));
            var offline = result.Channels.Single(channel => channel.Platform == PlatformKind.Twitch && channel.Channel == "timmy");
            Assert.Equal(StreamSearchChannelState.Offline, offline.State);
            Assert.Equal(false, offline.CanPlay);
            Assert.Equal(0, streamlink.ProbeRequests.Count);
            Assert.Equal(false, result.Message.Contains("OAuth", StringComparison.Ordinal));
        }
    }

    private static async Task PartialMatchRelevanceAsync()
    {
        object[] items =
        [
            new { login = "iitztimmy", displayName = "iiTzTimmy", stream = (object?)null },
            .. Enumerable.Range(1, 9).Select(index => new
            {
                login = $"timmy{index}", displayName = $"Timmy{index}", stream = new { id = index.ToString(CultureInfo.InvariantCulture) }
            })
        ];
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request => JsonResponse(
            request.RequestUri!.Host == "gql.twitch.tv"
                ? WebsiteResponse(items)
                : """{"channels":[{"slug":"timmy","isLive":false}]}""")));
        var service = new StreamSearchService(new MemoryLogger(), new FakeStreamlinkService(), httpClient);

        var result = await service.SearchAsync(new StreamSearchRequest("timmy", PageSize: 10), new AppSettings());

        Assert.Equal(10, result.Channels.Count);
        Assert.True(result.Channels.Any(channel => channel.Channel == "iitztimmy" && channel.State == StreamSearchChannelState.Offline));
        Assert.True(result.Channels.Any(channel => channel.Platform == PlatformKind.Kick && channel.Channel == "timmy"));
        Assert.Equal(false, result.Channels.Any(channel => channel.Channel == "timmy9"));
    }

    private static async Task WebsiteFallbackAsync()
    {
        foreach (var websiteBody in new[]
        {
            """{"errors":[{"message":"Search temporarily unavailable"}]}""",
            "not json",
            """{"data":{"searchFor":null}}""",
            WebsiteResponse()
        })
        {
            var settings = new AppSettings();
            settings.Chat.TwitchOAuthToken = "search-fallback-" + Guid.NewGuid().ToString("N");
            settings.Chat.TwitchClientId = "search-client";
            var websiteRequests = 0;
            var helixRequests = 0;
            using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                switch (request.RequestUri!.Host)
                {
                    case "gql.twitch.tv":
                        websiteRequests++;
                        return JsonResponse(websiteBody);
                    case "id.twitch.tv":
                        return JsonResponse("""{"client_id":"search-client","login":"viewer","user_id":"123","expires_in":3600,"scopes":[]}""");
                    case "api.twitch.tv":
                        Assert.Equal("/helix/search/channels", request.RequestUri.AbsolutePath);
                        Assert.Equal(settings.Chat.TwitchOAuthToken, request.Headers.Authorization?.Parameter);
                        helixRequests++;
                        return JsonResponse("""{"data":[{"broadcaster_login":"timmy","display_name":"Timmy","is_live":false}]}""");
                    case "kick.com":
                        return JsonResponse("""{"channels":[]}""");
                    default:
                        throw new InvalidOperationException("Unexpected search endpoint.");
                }
            }));
            var service = new StreamSearchService(new MemoryLogger(), new FakeStreamlinkService(), httpClient);

            var result = await service.SearchAsync(new StreamSearchRequest("timmy"), settings);

            Assert.Equal(1, websiteRequests);
            Assert.Equal(1, helixRequests);
            Assert.Equal(StreamSearchChannelState.Offline, result.Channels.Single(channel => channel.Platform == PlatformKind.Twitch).State);
            Assert.Equal(false, result.Message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) &&
                result.Message.Contains("Twitch channel search", StringComparison.Ordinal));
        }
    }

    private static async Task IndependentPlatformFallbackAsync()
    {
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
            request.RequestUri!.Host == "gql.twitch.tv"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }
                : JsonResponse("""{"channels":[{"slug":"timmy","isLive":true}]}""")));
        var streamlink = new FakeStreamlinkService
        {
            ProbeStreamsOverride = (_, _) => Task.FromResult(new StreamlinkProbeResult(false, "No streams found."))
        };
        var service = new StreamSearchService(new MemoryLogger(), streamlink, httpClient);

        var result = await service.SearchAsync(new StreamSearchRequest("timmy"), new AppSettings { StreamlinkPath = "streamlink.exe" });

        Assert.Equal(2, result.Channels.Count);
        Assert.Equal(PlatformKind.Kick, result.Channels[0].Platform);
        Assert.True(result.Channels[0].CanPlay);
        Assert.Equal(PlatformKind.Twitch, streamlink.ProbeRequests.Single().Target.Platform);
        Assert.Equal("timmy", streamlink.ProbeRequests.Single().Target.Channel);
        Assert.Contains("Twitch channel search is unavailable", result.Message);
    }

    private static async Task WebsiteCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherTwitchRequests = 0;
        using var httpClient = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "gql.twitch.tv")
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            else if (request.RequestUri.Host != "kick.com")
            {
                otherTwitchRequests++;
            }

            return JsonResponse("""{"channels":[]}""");
        }));
        var streamlink = new FakeStreamlinkService();
        var service = new StreamSearchService(new MemoryLogger(), streamlink, httpClient);
        var settings = new AppSettings { StreamlinkPath = "streamlink.exe" };
        settings.Chat.TwitchOAuthToken = "search-cancellation-token";

        var search = service.SearchAsync(new StreamSearchRequest("timmy"), settings, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => search);
        Assert.Equal(0, otherTwitchRequests);
        Assert.Equal(0, streamlink.ProbeRequests.Count);
    }

    private static string WebsiteResponse(params object?[] items) => JsonSerializer.Serialize(new
    {
        data = new { searchFor = new { channels = new { edges = items.Select(item => new { item }) } } }
    });

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}

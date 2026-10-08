using System.Windows.Threading;

internal static class VodChannelSearchTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("past broadcast search: Twitch discovery accepts partial and display names and retains offline relevance", TwitchDiscoveryAsync),
        ("past broadcast search: Kick discovery is scoped and respects its minimum query length", KickDiscoveryAsync),
        ("past broadcast search: empty unavailable and mismatched-platform discovery never invents a channel", DiscoveryFailuresAsync),
        ("past broadcast search: selecting a Twitch match uses its canonical login for paging filters and refresh", SelectTwitchAsync),
        ("past broadcast search: Kick matches remain selectable without live playback", SelectKickAsync),
        ("past broadcast search: a single provider-confirmed exact login still loads broadcasts automatically", ExactLoginAsync),
        ("past broadcast search: an exact login cannot hide other partial-name matches", AmbiguousExactAsync),
        ("past broadcast search: channel URLs and known-channel navigation bypass discovery", ChannelUrlAsync),
        ("past broadcast search: typing and platform changes cancel discovery and reject late results", StaleDiscoveryAsync),
        ("past broadcast search: Enter joins discovery and a later search retries", ShareDiscoveryAsync),
        ("past broadcast search: debounce uses the latest query and Escape cancels a posted callback", DebounceAsync),
        ("past broadcast search: clearing the query invalidates old selection commands", ClearAsync),
        ("past broadcast search: provider errors show feedback and allow retry", RetryAsync),
        ("past broadcast search: shutdown cancels and drains channel discovery", ShutdownAsync)
    ];

    internal static IReadOnlyList<(string Name, Func<Task> Run)> LiveProbeTests =>
        Environment.GetEnvironmentVariable("SVS_TEST_VOD_CHANNEL_SEARCH_LIVE") == "1"
            ? [("past broadcast search: live Twitch and Kick partial-name discovery", LiveDiscoveryAsync)] : [];

    private static async Task TwitchDiscoveryAsync()
    {
        foreach (var query in new[] { "  @TiMmY  ", "Streamer Name" })
        {
            using var http = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
            {
                Assert.Equal("gql.twitch.tv", request.RequestUri!.Host);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal(query.Trim().TrimStart('@').ToLowerInvariant(),
                    body.RootElement.GetProperty("variables").GetProperty("query").GetString());
                return Json(Website(
                    new { login = "iitztimmy", displayName = "iiTzTimmy", stream = (object?)null },
                    new { login = "timmyfan", displayName = "TimmyFan", stream = new { id = "1" } },
                    new { login = "mysterytimmy", displayName = "MysteryTimmy" },
                    new { login = "IITZTIMMY", displayName = "iiTzTimmy", stream = (object?)null },
                    new { login = "../invalid" }));
            }));
            var streamlink = new FakeStreamlinkService();
            var service = new StreamSearchService(new MemoryLogger(), streamlink, http);
            var result = await service.SearchAsync(new StreamSearchRequest(query,
                Mode: StreamSearchMode.ChannelDiscovery, Platform: PlatformKind.Twitch),
                new AppSettings { CustomStreamlinkArguments = "\"unterminated" });
            Assert.Equal(StreamSearchResultStatus.Available, result.Status);
            Assert.SequenceEqual(["iitztimmy", "timmyfan", "mysterytimmy"], result.Channels.Select(channel => channel.Channel));
            Assert.True(result.Channels.All(channel => channel.Platform == PlatformKind.Twitch &&
                channel.SourceStatus == StreamSearchSourceStatus.Available));
            Assert.Equal(StreamSearchChannelState.Offline, result.Channels[0].State);
            Assert.Equal(StreamSearchChannelState.Unavailable, result.Channels[2].State);
            Assert.Equal(0, streamlink.ProbeRequests.Count);
        }
    }

    private static async Task KickDiscoveryAsync()
    {
        var requests = 0;
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requests++;
            Assert.Equal("kick.com", request.RequestUri!.Host);
            Assert.Contains("searched_word=train", request.RequestUri.Query);
            return Json("""{"channels":[{"slug":"trainwreckstv","user":{"username":"Trainwreckstv","profilePic":"https://example.test/avatar.png"},"isLive":false},{"slug":"trainfan","isLive":true},{"slug":"TRAINWRECKSTV"},{"slug":"../invalid"}]}""");
        }));
        var streamlink = new FakeStreamlinkService();
        var service = new StreamSearchService(new MemoryLogger(), streamlink, http);
        var shortQuery = await service.SearchAsync(new StreamSearchRequest("tr",
            Mode: StreamSearchMode.ChannelDiscovery, Platform: PlatformKind.Kick), new AppSettings());
        Assert.Equal(StreamSearchResultStatus.NotFound, shortQuery.Status);
        Assert.Contains("at least 3 characters", shortQuery.Message);
        Assert.Equal(0, requests);
        var result = await service.SearchAsync(
            new StreamSearchRequest("train", Mode: StreamSearchMode.ChannelDiscovery, Platform: PlatformKind.Kick), new AppSettings());
        Assert.SequenceEqual(["trainwreckstv", "trainfan"], result.Channels.Select(channel => channel.Channel));
        Assert.Equal("Trainwreckstv", result.Channels[0].DisplayName);
        Assert.Equal("https://example.test/avatar.png", result.Channels[0].ProfileImageUrl);
        Assert.Equal(false, result.Channels[0].CanPlay);
        Assert.Equal(0, streamlink.ProbeRequests.Count);
    }

    private static async Task DiscoveryFailuresAsync()
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
        {
            foreach (var unavailable in new[] { false, true })
            {
                var requests = 0;
                using var http = new HttpClient(new FakeHttpMessageHandler(_ =>
                {
                    requests++;
                    return unavailable ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }
                        : Json(platform == PlatformKind.Twitch ? Website() : """{"channels":[]}""");
                }));
                var streamlink = new FakeStreamlinkService();
                var service = new StreamSearchService(new MemoryLogger(), streamlink, http, (_, _, _) => Task.FromResult<string?>(null));
                var result = await service.SearchAsync(new StreamSearchRequest("nosuchstreamer",
                    Mode: StreamSearchMode.ChannelDiscovery, Platform: platform), new AppSettings { StreamlinkPath = "streamlink.exe" });
                Assert.Equal(unavailable ? StreamSearchResultStatus.Unavailable : StreamSearchResultStatus.NotFound, result.Status);
                Assert.Equal(0, result.Channels.Count);
                Assert.Contains(platform.ToString(), result.Message);
                Assert.Equal(1, requests);
                Assert.Equal(0, streamlink.ProbeRequests.Count);
                var mismatch = await service.SearchAsync(new StreamSearchRequest(platform == PlatformKind.Twitch
                        ? "https://kick.com/xqc" : "https://www.twitch.tv/iitztimmy",
                    Mode: StreamSearchMode.ChannelDiscovery, Platform: platform), new AppSettings());
                Assert.Equal(StreamSearchResultStatus.NotFound, mismatch.Status);
                Assert.Equal(1, requests);
            }
        }
    }

    private static Task SelectTwitchAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var search = new FakeStreamSearchService(Matches(PlatformKind.Twitch, "iitztimmy", "timmyfan"));
        var vod = new FakeTwitchVodService(TwitchPage("100", "next"), TwitchPage("101"), TwitchPage("102"), TwitchPage("103"));
        await using var main = CreateMain(search, vod, delay: TimeSpan.Zero);
        main.TwitchVodSearchText = "timmy";
        Assert.True(main.IsVodChannelSearchVisible);
        Assert.Equal(2, main.VodChannelSearchResults.Count);
        Assert.Equal(0, vod.CallCount);
        Assert.Equal(StreamSearchMode.ChannelDiscovery, search.Requests.Single().Mode);
        Assert.Equal(PlatformKind.Twitch, search.Requests.Single().Platform);
        await main.VodChannelSearchResults[0].SelectCommand.ExecuteAsync();
        Assert.Equal("iitztimmy", main.TwitchVodSearchText);
        Assert.Equal(false, main.IsVodChannelSearchVisible);
        Assert.Equal("100", main.TwitchVods.Single().Id);
        await main.LoadMoreTwitchVodsCommand.ExecuteAsync();
        Assert.Equal("next", vod.Requests[1].Cursor);
        main.ShowHighlightsVodFilterCommand.Execute(null);
        await TestWait.UntilAsync(() => main.TwitchVods.Count == 1 && main.TwitchVods[0].Id == "102", TimeSpan.FromSeconds(2));
        await main.SearchTwitchVodsCommand.ExecuteAsync();
        Assert.True(vod.Requests.All(request => request.Streamer == "iitztimmy"));
        Assert.Equal(TwitchVodTypeFilter.Highlight, vod.Requests[2].Type);
        Assert.Equal("103", main.TwitchVods.Single().Id);
        Assert.Equal(1, search.CallCount);
    });

    private static Task SelectKickAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var search = new FakeStreamSearchService(Matches(PlatformKind.Kick, "trainwreckstv"));
        var kick = new FakeKickVodService(KickPage("trainwreckstv"));
        await using var main = CreateMain(search, kick: kick, delay: TimeSpan.Zero);
        main.SelectKickVodPlatformCommand.Execute(null);
        main.TwitchVodSearchText = "train";
        Assert.Equal(0, kick.CallCount);
        Assert.True(main.VodChannelSearchResults.Single().SelectCommand.CanExecute(null));
        await main.VodChannelSearchResults.Single().SelectCommand.ExecuteAsync();
        Assert.Equal("trainwreckstv", kick.Requests.Single().Channel);
        Assert.Equal(PlatformKind.Kick, main.TwitchVods.Single().Platform);
        Assert.Equal(PlatformKind.Kick, search.Requests.Single().Platform);
    });

    private static Task ExactLoginAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var search = new FakeStreamSearchService(Matches(PlatformKind.Twitch, "iitztimmy"));
        var vod = new FakeTwitchVodService(TwitchPage());
        await using var main = CreateMain(search, vod, delay: TimeSpan.Zero);
        main.TwitchVodSearchText = "  @iiTzTimmy  ";
        await TestWait.UntilAsync(() => main.HasTwitchVods, TimeSpan.FromSeconds(2));
        Assert.Equal("iitztimmy", main.TwitchVodSearchText);
        Assert.Equal("iitztimmy", vod.Requests.Single().Streamer);
        Assert.Equal(1, search.CallCount);
    });

    private static Task ChannelUrlAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var search = new FakeStreamSearchService(Matches(PlatformKind.Twitch, "iitztimmy"));
        var vod = new FakeTwitchVodService(TwitchPage());
        await using var main = CreateMain(search, vod, delay: TimeSpan.Zero);
        main.TwitchVodSearchText = "https://www.twitch.tv/iitztimmy";
        Assert.Equal(1, vod.CallCount);
        Assert.Equal(0, search.CallCount);
        main.NewStreamText = "timmy";
        await main.AddAndPlayCommand.ExecuteAsync();
        await main.StreamSearchResults.Single().OpenCommand.ExecuteAsync();
        Assert.Equal("iitztimmy", main.TwitchVodSearchText);
        Assert.Equal(2, vod.CallCount);
        Assert.Equal(1, search.CallCount);
    });

    private static Task AmbiguousExactAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var search = new FakeStreamSearchService(Matches(PlatformKind.Twitch, "timmy", "iitztimmy", "timmyfan"));
        var vod = new FakeTwitchVodService(TwitchPage());
        await using var main = CreateMain(search, vod, delay: TimeSpan.Zero);
        main.TwitchVodSearchText = "timmy";
        Assert.True(main.IsVodChannelSearchVisible);
        Assert.Equal(3, main.VodChannelSearchResults.Count);
        Assert.Equal(0, vod.CallCount);
        await main.VodChannelSearchResults.Single(channel => channel.Channel == "iitztimmy").SelectCommand.ExecuteAsync();
        Assert.Equal("iitztimmy", vod.Requests.Single().Streamer);
        Assert.Equal("iitztimmy", main.TwitchVodSearchText);
    });

    private static Task StaleDiscoveryAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var pending = new List<(CancellationToken Token, TaskCompletionSource<StreamSearchResult> Completion)>();
        var search = new FakeStreamSearchService(Matches(PlatformKind.Twitch, "unused"))
        {
            ResponderAsync = (_, token) =>
            {
                var completion = new TaskCompletionSource<StreamSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Add((token, completion));
                return completion.Task;
            }
        };
        await using var main = CreateMain(search, delay: TimeSpan.Zero);
        try
        {
            main.TwitchVodSearchText = "old";
            main.TwitchVodSearchText = "new";
            main.SelectKickVodPlatformCommand.Execute(null);
            Assert.Equal(3, pending.Count);
            Assert.True(pending.Take(2).All(item => item.Token.IsCancellationRequested));
            pending[2].Completion.SetResult(Matches(PlatformKind.Kick, "newkick"));
            await TestWait.UntilAsync(() => main.VodChannelSearchResults.Count == 1, TimeSpan.FromSeconds(2));
            pending[0].Completion.SetResult(Matches(PlatformKind.Twitch, "oldmatch"));
            pending[1].Completion.SetResult(Matches(PlatformKind.Twitch, "newmatch"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("newkick", main.VodChannelSearchResults.Single().Channel);
            Assert.Equal(false, main.IsVodChannelSearchRunning);
        }
        finally
        {
            foreach (var item in pending) item.Completion.TrySetResult(Matches(PlatformKind.Twitch));
        }
    });

    private static Task ShareDiscoveryAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource<StreamSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var search = new FakeStreamSearchService(Matches(PlatformKind.Twitch)) { ResponderAsync = (_, _) => release.Task };
        await using var main = CreateMain(search, delay: TimeSpan.Zero);
        try
        {
            main.TwitchVodSearchText = "timmy";
            var enter = main.SearchTwitchVodsCommand.ExecuteAsync();
            Assert.Equal(1, search.CallCount);
            release.SetResult(Matches(PlatformKind.Twitch, "iitztimmy"));
            await enter;
            await main.SearchTwitchVodsCommand.ExecuteAsync();
            Assert.Equal(2, search.CallCount);
        }
        finally { release.TrySetResult(Matches(PlatformKind.Twitch)); }
    });

    private static Task DebounceAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var search = new FakeStreamSearchService(Matches(PlatformKind.Twitch, "iitztimmy"));
        var dispatcher = Dispatcher.CurrentDispatcher;
        await using (var main = CreateMain(search, delay: TimeSpan.FromMilliseconds(50), dispatch: action => dispatcher.BeginInvoke(action)))
        {
            main.TwitchVodSearchText = "t";
            main.TwitchVodSearchText = "tim";
            main.TwitchVodSearchText = "timmy";
            await TestWait.UntilAsync(() => main.HasVodChannelSearchResults, TimeSpan.FromSeconds(2));
            Assert.Equal("timmy", search.Requests.Single().Query);
        }

        var posted = new Queue<Action>();
        var canceled = new FakeStreamSearchService(Matches(PlatformKind.Twitch));
        await using var queued = CreateMain(canceled, delay: TimeSpan.Zero, dispatch: posted.Enqueue);
        queued.TwitchVodSearchText = "timmy";
        queued.DismissVodChannelSearchResults();
        while (posted.Count > 0) posted.Dequeue()();
        Assert.Equal(0, canceled.CallCount);
        queued.TwitchVodSearchText = "another";
        await queued.SearchTwitchVodsCommand.ExecuteAsync();
        while (posted.Count > 0) posted.Dequeue()();
        Assert.Equal(1, canceled.CallCount);
    });

    private static Task ClearAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var search = new FakeStreamSearchService(Matches(PlatformKind.Twitch, "iitztimmy"));
        var vod = new FakeTwitchVodService(TwitchPage());
        await using var main = CreateMain(search, vod, delay: TimeSpan.Zero);
        main.TwitchVodSearchText = "timmy";
        var row = main.VodChannelSearchResults.Single();
        main.TwitchVodSearchText = "";
        Assert.Equal(false, row.SelectCommand.CanExecute(null));
        await row.SelectCommand.ExecuteAsync();
        Assert.Equal(0, vod.CallCount);
        Assert.Equal(0, main.VodChannelSearchResults.Count);
        Assert.Equal(false, main.IsVodChannelSearchVisible);
    });

    private static Task RetryAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var attempt = 0;
        var search = new FakeStreamSearchService(Matches(PlatformKind.Twitch))
        {
            ResponderAsync = (_, _) => ++attempt == 1 ? Task.FromException<StreamSearchResult>(new HttpRequestException("offline"))
                : Task.FromResult(Matches(PlatformKind.Twitch, "iitztimmy"))
        };
        await using var main = CreateMain(search);
        main.TwitchVodSearchText = "timmy";
        await main.SearchTwitchVodsCommand.ExecuteAsync();
        Assert.True(main.IsVodChannelSearchVisible);
        Assert.Contains("Try again", main.VodChannelSearchStatus);
        Assert.Equal(false, main.IsVodChannelSearchRunning);
        await main.SearchTwitchVodsCommand.ExecuteAsync();
        Assert.Equal("iitztimmy", main.VodChannelSearchResults.Single().Channel);
    });

    private static Task ShutdownAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        CancellationToken observed = default;
        var search = new FakeStreamSearchService(Matches(PlatformKind.Twitch))
        {
            ResponderAsync = async (_, token) =>
            {
                observed = token;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Matches(PlatformKind.Twitch);
            }
        };
        var main = CreateMain(search, delay: TimeSpan.Zero);
        main.TwitchVodSearchText = "timmy";
        await main.DisposeAsync();
        Assert.True(observed.IsCancellationRequested);
        await main.SearchTwitchVodsCommand.ExecuteAsync();
        Assert.Equal(1, search.CallCount);
    });

    private static async Task LiveDiscoveryAsync()
    {
        var streamlink = new FakeStreamlinkService();
        var service = new StreamSearchService(new MemoryLogger(), streamlink);
        foreach (var (platform, query, expected) in new[]
            { (PlatformKind.Twitch, "timmy", "iitztimmy"), (PlatformKind.Kick, "train", "trainwreckstv") })
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var result = await service.SearchAsync(new StreamSearchRequest(query,
                Mode: StreamSearchMode.ChannelDiscovery, Platform: platform), new AppSettings(), cancellation.Token);
            Console.WriteLine($"LIVE {platform} '{query}': {result.Status}; {string.Join(", ", result.Channels.Select(channel => channel.Channel))}; {result.Message}");
            Assert.True(result.IsAvailable && result.Channels.Any(channel => channel.Channel == expected), result.Message);
            Assert.True(result.Channels.All(channel => channel.Platform == platform));
            await TestSta.RunOffscreenAsync(async () =>
            {
                var twitch = new FakeTwitchVodService(TwitchPage());
                var kick = new FakeKickVodService(KickPage(expected));
                await using var main = CreateMain(new FakeStreamSearchService(result), twitch, kick);
                if (platform == PlatformKind.Kick) main.SelectKickVodPlatformCommand.Execute(null);
                main.TwitchVodSearchText = query;
                await main.SearchTwitchVodsCommand.ExecuteAsync();
                Assert.True(main.IsVodChannelSearchVisible, "Real provider matches must remain available for selection.");
                Assert.Equal(0, twitch.CallCount + kick.CallCount);
                await main.VodChannelSearchResults.Single(channel => channel.Channel == expected).SelectCommand.ExecuteAsync();
                Assert.Equal(expected, platform == PlatformKind.Twitch
                    ? twitch.Requests.Single().Streamer : kick.Requests.Single().Channel);
            });
        }
        Assert.Equal(0, streamlink.ProbeRequests.Count);
    }

    internal static MainViewModel CreateMain(IStreamSearchService search, ITwitchVodService? twitch = null,
        IKickVodService? kick = null, TimeSpan? delay = null, Action<Action>? dispatch = null)
    {
        var settings = new AppSettings();
        settings.Chat.ConnectAutomatically = false;
        return TestViewModels.CreateMain(settings, new FakeSettingsService(settings), new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(), dispatch ?? (action => action()),
            twitchVodService: twitch ?? new FakeTwitchVodService(TwitchPage()),
            twitchVodSearchDebounceInterval: delay ?? TimeSpan.FromHours(1), streamSearchService: search,
            kickVodService: kick ?? new FakeKickVodService(KickPage()));
    }

    internal static StreamSearchResult Matches(PlatformKind platform, params string[] channels) => new(
        StreamSearchResultStatus.Available, channels.Select(channel => new StreamSearchChannel(platform,
            channel, channel == "iitztimmy" ? "iiTzTimmy" : channel == "xqc" ? "xQc" : channel,
            StreamInputParser.FromChannel(platform, channel).Url, "", "", "", StreamSearchChannelState.Offline,
            StreamSearchSourceStatus.Available, "Browse broadcasts", false)).ToArray(), "Choose a streamer to browse their broadcasts.");

    private static TwitchVodSearchResult TwitchPage(string id = "100", string cursor = "") => new(
        TwitchVodSearchStatus.Available, new TwitchVodBroadcaster("123", "iitztimmy", "iiTzTimmy"),
        [new TwitchVodItem(id, "", "123", "iitztimmy", "iiTzTimmy", "Broadcast", "",
            $"https://www.twitch.tv/videos/{id}", "", null, null, TimeSpan.FromHours(1), 10, TwitchVodTypeFilter.Archive)],
        cursor, "Broadcasts loaded.");

    private static KickVodSearchResult KickPage(string channel = "xqc") => new(KickVodSearchStatus.Available,
        [new KickVodItem("100", "", "uuid-100", channel, channel, "Broadcast", $"https://kick.com/{channel}/videos/uuid-100",
            "https://example.test/vod.m3u8", "", "", null, null, TimeSpan.FromHours(1), 10)], "", "Broadcasts loaded.");

    private static string Website(params object[] channels) => JsonSerializer.Serialize(new
    {
        data = new { searchFor = new { channels = new { edges = channels.Select(item => new { item }) } } }
    });
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

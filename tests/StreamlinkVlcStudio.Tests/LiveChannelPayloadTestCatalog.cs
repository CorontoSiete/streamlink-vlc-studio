internal static class LiveChannelPayloadTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("live payload: unidentified rows cannot replace the requested channel", ChannelIdentityAsync),
        ("live payload: malformed channel rows are unavailable rather than offline", MalformedRowsAsync),
        ("live payload: missing and invalid Twitch counts are unavailable", InvalidTwitchCountsAsync),
        ("live payload: negative viewer counts are unavailable on both platforms", NegativeCountsAsync),
        ("live payload: malformed Kick streams are distinct from offline streams", KickStreamShapeAsync),
        ("live payload: valid empty and unrelated results remain offline", OfflineResultsAsync),
        ("live payload: zero viewers preserve live metadata on both platforms", ZeroViewersAsync)
    ];

    private static async Task ChannelIdentityAsync()
    {
        foreach (var platform in Platforms)
        {
            var valid = ChannelJson(platform);
            foreach (var identity in new[] { "null", "17", "true", "\"\"" })
            {
                var unidentified = ChannelJson(platform, "999", identity);
                var result = await ReadAsync(platform, $$"""{"data":[{{unidentified}},{{valid}}]}""");
                Assert.Equal(ViewerCountState.Available, result.Count.State);
                Assert.Equal<int?>(42, result.Count.ViewerCount);
                Assert.Equal(StreamMetadataState.Available, result.Metadata.State);
                Assert.Equal("https://example.invalid/42.jpg", result.Metadata.ThumbnailUrl);
            }
        }
    }

    private static async Task MalformedRowsAsync()
    {
        foreach (var platform in Platforms)
        {
            foreach (var payload in new[] { "null", "[]", "{}", "{\"data\":{}}", "{\"data\":[null,42,{}]}" })
            {
                var result = await ReadAsync(platform, payload);
                Assert.Equal(ViewerCountState.Unavailable, result.Count.State);
                Assert.Equal(StreamMetadataState.Unavailable, result.Metadata.State);
            }
        }
    }

    private static async Task InvalidTwitchCountsAsync()
    {
        foreach (var count in new string?[] { null, "null", "\"unknown\"", "2147483648", "{}" })
        {
            var result = await ReadChannelAsync(PlatformKind.Twitch, ChannelJson(PlatformKind.Twitch, count));
            Assert.Equal(ViewerCountState.Unavailable, result.Count.State);
            Assert.Equal<int?>(null, result.Count.ViewerCount);
            Assert.Equal(StreamMetadataState.Available, result.Metadata.State);
        }
    }

    private static async Task NegativeCountsAsync()
    {
        foreach (var platform in Platforms)
        {
            var result = await ReadChannelAsync(platform, ChannelJson(platform, "-1"));
            Assert.Equal(ViewerCountState.Unavailable, result.Count.State);
            Assert.Equal<int?>(null, result.Count.ViewerCount);
            Assert.Equal(StreamMetadataState.Available, result.Metadata.State);
        }
    }

    private static async Task KickStreamShapeAsync()
    {
        foreach (var fields in new[] { "", ",\"stream\":17", ",\"stream\":[]", ",\"stream\":\"offline\"" })
        {
            var result = await ReadChannelAsync(PlatformKind.Kick, $$"""{"slug":"channel"{{fields}}}""");
            Assert.Equal(ViewerCountState.Unavailable, result.Count.State);
            Assert.Equal(StreamMetadataState.Unavailable, result.Metadata.State);
        }

        foreach (var stream in new[] { "null", "{\"is_live\":false,\"viewer_count\":10}" })
        {
            var result = await ReadChannelAsync(PlatformKind.Kick, $$"""{"slug":"channel","stream":{{stream}}}""");
            Assert.Equal(ViewerCountState.Offline, result.Count.State);
            Assert.Equal(StreamMetadataState.Offline, result.Metadata.State);
        }
    }

    private static async Task OfflineResultsAsync()
    {
        foreach (var platform in Platforms)
        {
            foreach (var data in new[] { "", ChannelJson(platform, identity: "\"other\"") })
            {
                var result = await ReadAsync(platform, $$"""{"data":[{{data}}]}""");
                Assert.Equal(ViewerCountState.Offline, result.Count.State);
                Assert.Equal(StreamMetadataState.Offline, result.Metadata.State);
            }
        }
    }

    private static async Task ZeroViewersAsync()
    {
        foreach (var platform in Platforms)
        {
            var result = await ReadChannelAsync(platform, ChannelJson(platform, "0", "\"CHANNEL\""));
            Assert.Equal(ViewerCountState.Available, result.Count.State);
            Assert.Equal<int?>(0, result.Count.ViewerCount);
            Assert.Equal("Category", result.Count.CategoryName);
            Assert.Equal("Title", result.Count.StreamTitle);
            Assert.Equal(StreamMetadataState.Available, result.Metadata.State);
            Assert.Equal("Category", result.Metadata.CategoryName);
            Assert.Equal("https://example.invalid/profile.jpg", result.Metadata.ProfileImageUrl);
        }
    }

    private static readonly PlatformKind[] Platforms = [PlatformKind.Twitch, PlatformKind.Kick];

    private static string ChannelJson(PlatformKind platform, string? count = "42", string identity = "\"channel\"")
    {
        var countProperty = count is null ? "" : $"\"viewer_count\":{count},";
        var thumbnail = count == "999" ? "999" : "42";
        return platform == PlatformKind.Twitch
            ? $$"""{"user_login":{{identity}},{{countProperty}}"thumbnail_url":"https://example.invalid/{{thumbnail}}.jpg","game_name":"Category","title":"Title","profile_image_url":"https://example.invalid/profile.jpg"}"""
            : $$"""{"slug":{{identity}},"stream":{"is_live":true,{{countProperty}}"thumbnail":"https://example.invalid/{{thumbnail}}.jpg","title":"Title"},"category":{"name":"Category"},"profile_picture":"https://example.invalid/profile.jpg"}""";
    }

    private static Task<(ViewerCountResult Count, StreamMetadataResult Metadata)> ReadChannelAsync(PlatformKind platform, string channel) =>
        ReadAsync(platform, $$"""{"data":[{{channel}}]}""");

    private static async Task<(ViewerCountResult Count, StreamMetadataResult Metadata)> ReadAsync(PlatformKind platform, string payload)
    {
        using var client = new HttpClient(new FakeHttpMessageHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.Host == "id.twitch.tv"
                ? """{"client_id":"client","login":"viewer","user_id":"1","expires_in":3600}"""
                : payload, Encoding.UTF8, "application/json")
        }));
        var logger = new MemoryLogger();
        var snapshots = new LiveChannelSnapshotProvider(client);
        var tokens = new KickTokenProvider((_, _, _) => Task.FromResult<string?>("token"));
        var settings = new AppSettings
        {
            Chat = new ChatSettings { TwitchOAuthToken = $"payload-{Guid.NewGuid():N}", TwitchClientId = "client" }
        };
        var target = StreamInputParser.FromChannel(platform, "channel");
        var count = await new ViewerCountService(logger, snapshots, tokens).GetViewerCountAsync(target, settings);
        var metadata = await new StreamMetadataService(logger, client, snapshots, tokens).GetLiveStreamMetadataAsync(target, settings);
        return (count, metadata);
    }
}

internal static partial class StreamHoverPreviewSourceTestCatalog
{
    private static IReadOnlyList<(string Name, Func<Task> Run)> AuthorizationTests =>
    [
        ("stream hover preview: anonymous Twitch TS avoids restarting device-bound prerolls", () => AnonymousTwitchAsync(false)),
        ("stream hover preview: anonymous Twitch MP4 avoids restarting device-bound prerolls", () => AnonymousTwitchAsync(true)),
        ("stream hover preview: authorization failures retain the exact Streamlink fallback", AuthorizationFailureAsync),
        ("stream hover preview: web GraphQL callers retain their explicit device and OAuth identity", WebGraphQlIdentityAsync)
    ];

    private static async Task AnonymousTwitchAsync(bool fragmentedMp4)
    {
        // Both reported channels return prerolls when the token has a newly invented
        // web device, but live content for Streamlink's anonymous token request.
        var media = fragmentedMp4 ? FragmentedMedia : Media;
        using var fixture = new Fixture
        {
            MasterText = fragmentedMp4 ? Master.Replace("IVS-NAME=\"360p\"", "IVS-NAME=\"360p30\"") : Master
        };
        bool? deviceBound = null;
        fixture.Override = (request, _) =>
        {
            if (request.RequestUri!.Host == "gql.twitch.tv")
            {
                deviceBound = request.Headers.Contains("X-Device-Id");
                Assert.True(request.Headers.Authorization is null);
                return Task.FromResult<HttpResponseMessage?>(null);
            }
            if (request.RequestUri.Host == "video.ttvnw.net")
            {
                Assert.NotNull(deviceBound);
                return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK)
                {
                    Content = new StringContent(deviceBound == true ? media.Replace("#EXTINF:2.0,", "#EXTINF:2.0,Amazon") : media)
                });
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        var transport = new Transport();
        Uri? decodedUri = null;
        var frames = 0;
        var player = new LibVlcLivePreview(transport, null, fixture.Resolver.OpenAsync,
            async (uri, _, present, token, _) =>
            {
                decodedUri = uri;
                if (uri == transport.PlaybackUri) return;
                using var client = new HttpClient();
                var playlist = await client.GetStringAsync(uri, token);
                Assert.Contains(fragmentedMp4 ? "first.mp4" : "first.ts", playlist);
                Assert.True(!playlist.Contains("Amazon", StringComparison.Ordinal));
                present(new(1, 1, [1, 0, 0, 0]));
            });
        await player.RunAsync(Request(), "fixture-vlc", _ => frames++, CancellationToken.None);
        Assert.Equal(0, transport.Starts);
        Assert.Equal(false, deviceBound);
        Assert.Equal(1, frames);
        Assert.Equal(3, fixture.Requests.Count);
        Assert.True(fixture.Requests[2].AbsolutePath.EndsWith("/360p/index.m3u8", StringComparison.Ordinal));
        using var check = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => check.GetStringAsync(decodedUri!));
    }

    private static async Task AuthorizationFailureAsync()
    {
        foreach (var (status, content) in new[]
        {
            (HttpStatusCode.OK, """{"errors":[{"message":"private-playback-token"}]}"""),
            (HttpStatusCode.Unauthorized, "private-playback-token"),
            (HttpStatusCode.OK, """{"data":{"streamPlaybackAccessToken":null}}"""),
            (HttpStatusCode.OK, "not-json-private-playback-token")
        })
        {
            using var fixture = new Fixture
            {
                Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.Host == "gql.twitch.tv"
                    ? new(status) { Content = new StringContent(content) } : null)
            };
            var transport = new Transport();
            var logger = new MemoryLogger();
            var request = Request();
            var player = new LibVlcLivePreview(transport, logger, fixture.Resolver.OpenAsync,
                (uri, _, _, _, _) =>
                {
                    Assert.Equal(transport.PlaybackUri, uri);
                    return Task.CompletedTask;
                });
            await player.RunAsync(request, "fixture-vlc", _ => { }, CancellationToken.None);
            Assert.Equal(1, fixture.Requests.Count);
            Assert.Equal(1, transport.Starts);
            Assert.True(ReferenceEquals(request, transport.Request));
            Assert.True(transport.Disposed);
            Assert.True(logger.Entries.All(entry => !entry.Message.Contains("private-playback-token", StringComparison.Ordinal)));
        }
    }

    private static async Task WebGraphQlIdentityAsync()
    {
        using var client = new HttpClient(new AsyncHttpMessageHandler((request, _) =>
        {
            Assert.Equal("existing-web-device", request.Headers.GetValues("X-Device-Id").Single());
            Assert.Equal("OAuth existing-oauth-token", request.Headers.GetValues("Authorization").Single());
            Assert.Equal(TwitchGraphQlTransport.PublicClientId, request.Headers.GetValues("Client-Id").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":{}}""")
            });
        }));
        using var document = await new TwitchGraphQlTransport(client).SendAsync("{}", TwitchGraphQlTransport.PublicClientId,
            "existing-web-device", CancellationToken.None, oauthToken: "existing-oauth-token");
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("data").ValueKind);
    }
}

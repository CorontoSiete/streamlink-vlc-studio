internal static class TwitchVodStartupTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All =>
    [
        ("VOD startup: validates only the requested quality after playback authorization", SelectedQualityAsync),
        ("VOD startup: quality ranking matches Streamlink and excludes audio", QualitySelectionAsync),
        ("VOD startup: legacy VIDEO groups preserve source and exact quality", LegacyMasterAsync),
        ("VOD startup: ambiguous master formats require the existing resolver", UnsupportedMastersAsync),
        ("VOD startup: custom arguments configs plugins and other targets keep Streamlink", ConfigurationAsync),
        ("VOD startup: denied token invalid JSON and provider errors fall back", TokenFailuresAsync),
        ("VOD startup: unavailable selected media falls back without lowering quality", MediaFailureAsync),
        ("VOD startup: unsafe variant and redirect locations are rejected before fetching", UnsafeLocationsAsync),
        ("VOD startup: private provider DNS is rejected before fetching", PrivateDnsAsync),
        ("VOD startup: oversized playlist falls back through bounded reads", OversizedAsync),
        ("VOD startup: cancellation stops resolution without starting fallback", CancellationAsync),
        ("VOD startup: direct resolution has a bounded fallback budget", BudgetAsync),
        ("VOD startup: repeated opens get fresh authorization and playlists", FreshAuthorizationAsync),
        ("VOD startup: playback consumes the validated playlist without a second CDN read", PlaylistHandoffAsync),
        ("VOD startup: growing playlists and cancelled opens retain fresh reads", GrowingAndCancelledHandoffAsync),
        ("VOD startup: playlist handoff expires and bounds retained memory", PlaylistHandoffLimitsAsync),
        ("VOD startup: fallback failures retain the subscriber-only fallback contract", FallbackFailureAsync)
    ];

    private const string Media = "#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXTINF:10,\n0-muted.ts\n#EXT-X-ENDLIST\n";
    private const string Token = """{"data":{"videoPlaybackAccessToken":{"signature":"sig+&","value":"{\"expires\":123}"}}}""";
    private const string Master = """
        #EXTM3U
        #EXT-X-STREAM-INF:BANDWIDTH=8000000,CODECS="avc1.64002A,mp4a.40.2",RESOLUTION=1920x1080,FRAME-RATE=60,IVS-NAME="1080p60"
        https://d123.cloudfront.net/vod/chunked/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=3000000,IVS-NAME="720p60"
        https://d123.cloudfront.net/vod/720p60/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=1000000,IVS-NAME="480p"
        https://d123.cloudfront.net/vod/480p30/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=100000,IVS-NAME="audio_only"
        https://d123.cloudfront.net/vod/audio_only/index.m3u8
        """;

    private static async Task SelectedQualityAsync()
    {
        using var fixture = new Fixture();
        var result = await fixture.Service.ResolveStreamUrlAsync(fixture.Request with { Quality = "720p60" });
        Assert.True(result.StreamUri.AbsolutePath.Contains("/720p60/"));
        Assert.Equal(3, fixture.Requests.Count);
        Assert.Equal(0, fixture.FallbackCount);
        Assert.Equal(HttpMethod.Post, fixture.Requests[0].Method);
        Assert.True(fixture.Requests[1].Uri.Query.Contains("nauthsig=sig%2B%26"));
        Assert.True(fixture.Requests[1].Uri.Query.Contains("supported_codecs=h264"));
        using var token = JsonDocument.Parse(fixture.TokenPayload!);
        Assert.Equal("12345", token.RootElement.GetProperty("variables").GetProperty("vodID").GetString());
        Assert.Equal(false, token.RootElement.GetProperty("variables").GetProperty("isLive").GetBoolean());
        Assert.True(fixture.Logger.Entries.All(entry => !entry.Message.Contains("sig+&") && !entry.Message.Contains("expires")));
    }

    private static Task QualitySelectionAsync()
    {
        foreach (var (quality, path) in new[] { ("best", "chunked"), ("worst", "480p30"), ("1080p60", "chunked"),
            ("720p60", "720p60"), ("480p", "480p30"), ("audio_only", "audio_only") })
            Assert.Equal($"/vod/{path}/index.m3u8", TwitchVodVariantPlaylist.Select(Master, new Uri("https://usher.ttvnw.net/master.m3u8"), quality).AbsolutePath);
        // "Audio Only" is the current Usher V2 name; Streamlink names it "audio".
        Assert.Equal("/vod/audio_only/index.m3u8", TwitchVodVariantPlaylist.Select(Master.Replace("\"audio_only\"", "\"Audio Only\""),
            new Uri("https://usher.ttvnw.net/master.m3u8"), "audio").AbsolutePath);
        return Task.CompletedTask;
    }

    private static Task LegacyMasterAsync()
    {
        const string master = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="chunked",NAME="Source"
            #EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID="720p60",NAME="720p60"
            #EXT-X-STREAM-INF:BANDWIDTH=1000000,VIDEO="chunked"
            source/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=9000000,VIDEO="720p60"
            720/index.m3u8
            """;
        var origin = new Uri("https://d123.cloudfront.net/archive/master.m3u8");
        Assert.Equal("/archive/source/index.m3u8", TwitchVodVariantPlaylist.Select(master, origin, "best").AbsolutePath);
        Assert.Equal("/archive/source/index.m3u8", TwitchVodVariantPlaylist.Select(master, origin, "source").AbsolutePath);
        Assert.Equal("/archive/720/index.m3u8", TwitchVodVariantPlaylist.Select(master, origin, "worst").AbsolutePath);
        return Task.CompletedTask;
    }

    private static async Task UnsupportedMastersAsync()
    {
        foreach (var master in new[] { "not HLS", "#EXTM3U", Master.Replace("IVS-NAME", "UNKNOWN-NAME"),
            Master.Replace("IVS-NAME=\"720p60\"", "IVS-NAME=\"1080p60\""),
            Master.Replace("IVS-NAME=\"720p60\"", "IVS-NAME=\"720p60"),
            Master.Replace("IVS-NAME=\"720p60\"", "IVS-NAME=\"720p60\",IVS-NAME=\"480p\""),
            Master + "\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",URI=\"external.m3u8\"", Master + "\n#EXT-X-STREAM-INF:IVS-NAME=\"160p\"" })
        {
            using var fixture = new Fixture { MasterText = master };
            await fixture.Service.ResolveStreamUrlAsync(fixture.Request);
            Assert.Equal(1, fixture.FallbackCount);
            Assert.Equal(2, fixture.Requests.Count);
        }
    }

    private static async Task ConfigurationAsync()
    {
        using var fixture = new Fixture();
        Assert.True(DirectVodResolutionPolicy.CanUse(fixture.Request, fixture.Directory));
        var configDirectory = Path.Combine(fixture.Directory, "streamlink");
        Directory.CreateDirectory(configDirectory);
        var config = Path.Combine(configDirectory, "config");
        await File.WriteAllTextAsync(config, "# installer default\nffmpeg-ffmpeg=C:\\Tools\\ffmpeg.exe\n");
        Assert.True(DirectVodResolutionPolicy.CanUse(fixture.Request, fixture.Directory));
        foreach (var setting in new[] { "twitch-api-header=Authorization=OAuth private", "http-proxy=http://proxy", "config=extra.conf", "unknown-option=value" })
        {
            await File.WriteAllTextAsync(config, setting);
            Assert.Equal(false, DirectVodResolutionPolicy.CanUse(fixture.Request, fixture.Directory));
        }
        File.Delete(config);
        await File.WriteAllTextAsync(Path.Combine(configDirectory, "config.twitch"), "twitch-access-token-param=playerType=site");
        Assert.Equal(false, DirectVodResolutionPolicy.CanUse(fixture.Request, fixture.Directory));
        File.Delete(Path.Combine(configDirectory, "config.twitch"));
        Directory.CreateDirectory(Path.Combine(configDirectory, "plugins"));
        Assert.Equal(false, DirectVodResolutionPolicy.CanUse(fixture.Request, fixture.Directory));
        Directory.Delete(Path.Combine(configDirectory, "plugins"));
        foreach (var request in new[] { fixture.Request with { CustomArguments = ["--http-header", "Authorization=secret"] },
            fixture.Request with { Target = fixture.Request.Target with { Kind = StreamTargetKind.Live } },
            fixture.Request with { Target = fixture.Request.Target with { Platform = PlatformKind.Kick, Kind = StreamTargetKind.KickVod } },
            fixture.Request with { Target = fixture.Request.Target with { Url = fixture.Request.Target.Url + "?t=10m" } },
            fixture.Request with { Target = fixture.Request.Target with { Url = "https://www.twitch.tv/videos/6789" } },
            fixture.Request with { Quality = "720p60,best" } })
        {
            Assert.Equal(false, DirectVodResolutionPolicy.CanUse(request, fixture.Directory));
            await fixture.Service.ResolveStreamUrlAsync(request);
        }
        Assert.Equal(0, fixture.Requests.Count);
        Assert.Equal(6, fixture.FallbackCount);
    }

    private static async Task TokenFailuresAsync()
    {
        foreach (var token in new[] { "{}", "null", "[]", "bad json", """{"data":{"videoPlaybackAccessToken":null}}""",
            """{"data":{"videoPlaybackAccessToken":{"signature":1,"value":"secret"}}}""",
            """{"errors":[{"message":"denied secret-token"}]}""" })
        {
            using var fixture = new Fixture { TokenText = token };
            await fixture.Service.ResolveStreamUrlAsync(fixture.Request);
            Assert.Equal(1, fixture.FallbackCount);
            Assert.Equal(1, fixture.Requests.Count);
            Assert.True(fixture.Logger.Entries.All(entry => !entry.Message.Contains("secret")));
        }
        using var denied = new Fixture { Override = (_, _) => Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.Forbidden)) };
        await denied.Service.ResolveStreamUrlAsync(denied.Request);
        Assert.Equal(1, denied.FallbackCount);
    }

    private static async Task MediaFailureAsync()
    {
        foreach (var text in new[] { "<html>unavailable</html>", "#EXTM3U\n#EXT-X-TARGETDURATION:10\n", Master,
            Media.Replace("#EXTM3U", "#EXTM3UX"), Media.Replace("0-muted.ts\n", ""), Media.Replace("#EXTINF:10,", "#EXTINF:NaN,"),
            Media.Replace("0-muted.ts", "https://evil.example/segment.ts"), Media.Replace("#EXTINF:10,\n", ""),
            Media.Replace("#EXTINF:10,", "#EXTINF:10,\n#EXTINF:10,"),
            Media + "#EXT-X-KEY:METHOD=AES-128,URI=\"https://127.0.0.1/key\"\n" })
        {
            using var fixture = new Fixture { MediaText = text };
            var request = fixture.Request with { Quality = "720p60" };
            await fixture.Service.ResolveStreamUrlAsync(request);
            Assert.Equal(request, fixture.FallbackRequest);
            Assert.Equal(3, fixture.Requests.Count);
        }
        using var unavailable = new Fixture
        {
            Override = (request, _) => Task.FromResult(
            request.RequestUri!.Host == "d123.cloudfront.net" ? new HttpResponseMessage(HttpStatusCode.NotFound) : null)
        };
        await unavailable.Service.ResolveStreamUrlAsync(unavailable.Request);
        Assert.Equal(1, unavailable.FallbackCount);
    }

    private static async Task UnsafeLocationsAsync()
    {
        foreach (var host in new[] { "http://d123.cloudfront.net", "https://evil.example", "https://127.0.0.1", "https://user:password@d123.cloudfront.net" })
        {
            using var fixture = new Fixture { MasterText = Master.Replace("https://d123.cloudfront.net", host) };
            await fixture.Service.ResolveStreamUrlAsync(fixture.Request);
            Assert.Equal(2, fixture.Requests.Count);
            Assert.Equal(1, fixture.FallbackCount);
        }
        using var redirect = new Fixture
        {
            Override = (request, _) => Task.FromResult(request.RequestUri!.Host == "usher.ttvnw.net"
            ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/master.m3u8") } } : null)
        };
        await redirect.Service.ResolveStreamUrlAsync(redirect.Request);
        Assert.Equal(2, redirect.Requests.Count);
        Assert.Equal(1, redirect.FallbackCount);
    }

    private static async Task PrivateDnsAsync()
    {
        using var fixture = new Fixture(privateDns: true);
        await fixture.Service.ResolveStreamUrlAsync(fixture.Request);
        Assert.Equal(1, fixture.Requests.Count);
        Assert.Equal(1, fixture.FallbackCount);
    }

    private static async Task OversizedAsync()
    {
        using var fixture = new Fixture
        {
            Override = (request, _) =>
        {
            if (request.RequestUri!.Host != "usher.ttvnw.net") return Task.FromResult<HttpResponseMessage?>(null);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Master) };
            response.Content.Headers.ContentLength = 100_000_000;
            return Task.FromResult<HttpResponseMessage?>(response);
        }
        };
        await fixture.Service.ResolveStreamUrlAsync(fixture.Request);
        Assert.Equal(1, fixture.FallbackCount);
        Assert.Equal(2, fixture.Requests.Count);
    }

    private static async Task CancellationAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture { Override = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return null; } };
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Service.ResolveStreamUrlAsync(fixture.Request, cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, fixture.FallbackCount);
        Assert.Equal(1, fixture.Requests.Count);
    }

    private static async Task BudgetAsync()
    {
        using var fixture = new Fixture { Override = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return null; } };
        await fixture.Service.ResolveStreamUrlAsync(fixture.Request).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(1, fixture.FallbackCount);
    }

    private static async Task FreshAuthorizationAsync()
    {
        using var fixture = new Fixture();
        await fixture.Service.ResolveStreamUrlAsync(fixture.Request);
        fixture.MasterText = Master.Replace("/vod/", "/new-vod/");
        var second = await fixture.Service.ResolveStreamUrlAsync(fixture.Request);
        Assert.True(second.StreamUri.AbsolutePath.StartsWith("/new-vod/"));
        Assert.Equal(6, fixture.Requests.Count);
        Assert.Equal(0, fixture.FallbackCount);
    }

    private static async Task FallbackFailureAsync()
    {
        using var fixture = new Fixture { TokenText = "{}", FailFallback = true };
        var caught = false;
        try { await fixture.Service.ResolveStreamUrlAsync(fixture.Request); }
        catch (InvalidOperationException error) { caught = error.Message == "Streamlink unavailable"; }
        Assert.True(caught);
        Assert.Equal(1, fixture.FallbackCount);
    }

    private static async Task PlaylistHandoffAsync()
    {
        using var fixture = new Fixture();
        await using var gateway = fixture.CreateGateway();
        var resolved = await fixture.Service.ResolveStreamUrlAsync(fixture.Request);
        Assert.Equal(3, fixture.Requests.Count);
        using (var prepared = await gateway.PrepareAsync(resolved.StreamUri, new Version(3, 0, 23), CancellationToken.None))
        {
            Assert.True(prepared.PlaybackUri.IsLoopback, "The handoff must still use muted-segment repair.");
            using var client = new HttpClient();
            var playlist = await client.GetStringAsync(prepared.PlaybackUri);
            Assert.True(playlist.Contains("#EXT-X-ENDLIST"));
            Assert.Equal(3, fixture.Requests.Count);
        }
        using var second = await gateway.PrepareAsync(resolved.StreamUri, new Version(3, 0, 23), CancellationToken.None);
        Assert.Equal(4, fixture.Requests.Count); // Consumed once, never a playback cache.
        await fixture.Service.ResolveStreamUrlAsync(fixture.Request);
        Assert.Equal(7, fixture.Requests.Count); // Reopening still authorizes and validates afresh.
        using var third = await gateway.PrepareAsync(resolved.StreamUri, new Version(3, 0, 23), CancellationToken.None);
        Assert.Equal(7, fixture.Requests.Count);
    }

    private static async Task GrowingAndCancelledHandoffAsync()
    {
        using var fixture = new Fixture { MediaText = Media.Replace("#EXT-X-ENDLIST", "") };
        await using var gateway = fixture.CreateGateway();
        var resolved = await fixture.Service.ResolveStreamUrlAsync(fixture.Request);
        using var prepared = await gateway.PrepareAsync(resolved.StreamUri, new Version(3, 0, 23), CancellationToken.None);
        Assert.Equal(4, fixture.Requests.Count);
        fixture.MediaText = Media;
        await fixture.Service.ResolveStreamUrlAsync(fixture.Request);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            using var cancelled = await gateway.PrepareAsync(resolved.StreamUri, new Version(3, 0, 23), cancellation.Token);
            throw new InvalidOperationException("Expected cancellation.");
        }
        catch (OperationCanceledException) { }
        Assert.NotNull(fixture.Handoff.Take(resolved.StreamUri));
    }

    private static Task PlaylistHandoffLimitsAsync()
    {
        var clock = new HandoffClock();
        var handoff = new TwitchVodPlaylistHandoff(clock);
        var uri = new Uri("https://d123.cloudfront.net/archive/index.m3u8");
        handoff.Offer(uri, Media);
        clock.Timestamp = 15000;
        Assert.True(handoff.Take(uri) is null);
        for (var i = 0; i < 9; i++)
        {
            clock.Timestamp++;
            handoff.Offer(new Uri(uri, i + ".m3u8"), Media);
        }
        Assert.True(handoff.Take(new Uri(uri, "0.m3u8")) is null);
        Assert.Equal(Media, handoff.Take(new Uri(uri, "8.m3u8")));
        var large = new string(' ', TwitchVodPlaylistHandoff.MaximumCharacters / 2) + "\n#EXT-X-ENDLIST";
        handoff.Offer(uri, large);
        handoff.Offer(new Uri(uri, "second.m3u8"), large);
        Assert.True(handoff.Take(uri) is null);
        Assert.Equal(large, handoff.Take(new Uri(uri, "second.m3u8")));
        handoff.Offer(uri, new string(' ', TwitchVodPlaylistHandoff.MaximumCharacters + 1) + Media);
        Assert.True(handoff.Take(uri) is null);
        handoff.Offer(uri, Media);
        handoff.Offer(uri, Media.Replace("#EXT-X-ENDLIST", ""));
        Assert.True(handoff.Take(uri) is null);
        handoff.Offer(uri, Media);
        Assert.True(handoff.Take(new Uri(uri, "../720p60/index.m3u8")) is null);
        var received = new ConcurrentBag<string>();
        Parallel.For(0, 16, _ => { if (handoff.Take(uri) is { } content) received.Add(content); });
        Assert.Equal(1, received.Count);
        return Task.CompletedTask;
    }

    private sealed class HandoffClock : TimeProvider
    {
        internal long Timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Timestamp;
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Directory = Path.Combine(Path.GetTempPath(), "svs-vod-startup-" + Guid.NewGuid().ToString("N"));
        internal readonly MemoryLogger Logger = new();
        internal readonly List<(HttpMethod Method, Uri Uri)> Requests = [];
        internal readonly StreamTransportRequest Request = new(StreamInputParser.Parse("https://www.twitch.tv/videos/12345", PlatformKind.Twitch),
            "best", Environment.ProcessPath!, false, []);
        internal readonly StreamlinkService Service;
        private readonly HttpClient client;
        internal readonly TwitchVodPlaylistHandoff Handoff = new();
        private readonly ReplayUrlSecurityValidator validator;
        internal TwitchMutedVodPlaybackGateway CreateGateway() => new(Logger, client, validator, playlistHandoff: Handoff);
        internal string TokenText = Token;
        internal string MasterText = Master;
        internal string MediaText = Media;
        internal string? TokenPayload;
        internal int FallbackCount;
        internal StreamTransportRequest? FallbackRequest;
        internal bool FailFallback;
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Override;

        internal Fixture(bool privateDns = false)
        {
            System.IO.Directory.CreateDirectory(Directory);
            client = new HttpClient(new AsyncHttpMessageHandler(async (request, cancellation) =>
            {
                Requests.Add((request.Method, request.RequestUri!));
                if (Override is not null && await Override(request, cancellation) is { } response) return response;
                if (request.RequestUri!.Host == "gql.twitch.tv")
                    TokenPayload = await request.Content!.ReadAsStringAsync(cancellation);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri!.Host switch
                    {
                        "gql.twitch.tv" => TokenText,
                        "usher.ttvnw.net" => MasterText,
                        "d123.cloudfront.net" => MediaText,
                        _ => throw new InvalidOperationException("Unexpected request host.")
                    })
                };
            }));
            validator = new ReplayUrlSecurityValidator((_, _) => Task.FromResult(new[] { IPAddress.Parse(privateDns ? "127.0.0.1" : "8.8.8.8") }));
            Service = new StreamlinkService(Logger, new TwitchVodUrlResolver(client, validator, Handoff),
                request => DirectVodResolutionPolicy.CanUse(request, Directory), (request, _) =>
                {
                    FallbackCount++;
                    FallbackRequest = request;
                    if (FailFallback) throw new InvalidOperationException("Streamlink unavailable");
                    return Task.FromResult(new StreamlinkResolvedUrl(new Uri("https://d123.cloudfront.net/fallback.m3u8"), "Streamlink fallback"));
                });
        }

        public void Dispose() { client.Dispose(); System.IO.Directory.Delete(Directory, recursive: true); }
    }
}

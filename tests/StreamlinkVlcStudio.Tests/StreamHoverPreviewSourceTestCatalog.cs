using StreamlinkVlcStudio.Infrastructure.Previews;

internal static partial class StreamHoverPreviewSourceTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All =>
    [
        ("stream hover preview: direct Twitch authorizes and requests only the selected quality", () => SelectedQualityAsync(PlatformKind.Twitch)),
        ("stream hover preview: direct Kick authorizes and requests only the selected quality", () => SelectedQualityAsync(PlatformKind.Kick)),
        ("stream hover preview: preview quality fallbacks preserve their order", QualityFallbackAsync),
        ("stream hover preview: custom arguments configs and plugins retain Streamlink", ConfigurationAsync),
        ("stream hover preview: unsafe redirects DNS and oversized responses never open native playback", InvalidSourcesAsync),
        ("stream hover preview: ad encrypted and unsupported playlists require Streamlink", UnsupportedPlaylistsAsync),
        ("stream hover preview: local playlist rejects unrelated requests and refreshes after handoff", PlaylistSessionAsync),
        ("stream hover preview: cancellation interrupts a pending playlist refresh and closes its listener", CancelRefreshAsync),
        ("stream hover preview: late direct resolution is disposed without starting fallback", LateCancellationAsync),
        ("stream hover preview: failed direct startup preserves the exact fallback request", DirectFailureAsync),
        ("stream hover preview: first-frame deadline stops direct playback before fallback", FirstFrameDeadlineAsync),
        ("stream hover preview: successful direct video outlives its startup deadline", SuccessfulDirectAsync),
        ("stream hover preview: a later ad stops direct video before Streamlink replacement", () => LaterAdAsync(false)),
        .. AuthorizationTests,
        .. FormatTests
    ];

    private const string Master = """
        #EXTM3U
        #EXT-X-STREAM-INF:BANDWIDTH=6000000,IVS-NAME="1080p60"
        1080p60/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=1400000,IVS-NAME="480p"
        480p/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=600000,IVS-NAME="360p"
        360p/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=160000,IVS-NAME="audio_only"
        audio/index.m3u8
        """;
    private const string Media = """
        #EXTM3U
        #EXT-X-VERSION:3
        #EXT-X-TARGETDURATION:2
        #EXT-X-MEDIA-SEQUENCE:100
        #EXTINF:2.0,
        first.ts
        #EXTINF:2.0,
        second.ts
        """;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static StreamTransportRequest Request(PlatformKind platform = PlatformKind.Twitch) =>
        new(StreamInputParser.Parse("preview_test", platform), LibVlcLivePreview.QualityPreference, Environment.ProcessPath!, true, [], true);

    private static async Task SelectedQualityAsync(PlatformKind platform)
    {
        using var fixture = new Fixture(platform);
        await using var source = await fixture.Resolver.OpenAsync(Request(platform), CancellationToken.None);
        Assert.NotNull(source);
        Assert.Equal(3, fixture.Requests.Count);
        Assert.True(fixture.Requests[2].AbsolutePath.EndsWith("/360p/index.m3u8"));
        using var client = new HttpClient();
        Assert.Contains("/360p/first.ts", await client.GetStringAsync(source!.PlaybackUri));
        Assert.Equal(3, fixture.Requests.Count); // The initial media playlist is handed off, not fetched twice.
        Assert.Equal(platform == PlatformKind.Twitch, source.PlaybackOptions!.LowLatency);
        Assert.Equal(platform == PlatformKind.Twitch ? 2000 : 6000, source.PlaybackOptions.LiveDelayMilliseconds);
        if (platform == PlatformKind.Twitch)
        {
            using var payload = JsonDocument.Parse(fixture.TokenPayload!);
            Assert.True(payload.RootElement.GetProperty("variables").GetProperty("isLive").GetBoolean());
            Assert.Equal("preview_test", payload.RootElement.GetProperty("variables").GetProperty("login").GetString());
            Assert.Contains("sig=signature%2B%26", fixture.Requests[1].Query);
            Assert.Contains("supported_codecs=h264", fixture.Requests[1].Query);
        }
        else Assert.Equal("/api/v2/channels/preview_test/livestream", fixture.Requests[0].AbsolutePath);
    }

    private static async Task QualityFallbackAsync()
    {
        foreach (var (master, quality) in new[]
        {
            (Master, "360p"),
            (Master.Replace("IVS-NAME=\"360p\"", "IVS-NAME=\"160p\""), "480p"),
            (Master.Replace("IVS-NAME=\"360p\"", "IVS-NAME=\"160p\"").Replace("IVS-NAME=\"480p\"", "IVS-NAME=\"240p\""), "1080p60")
        })
        {
            using var fixture = new Fixture { MasterText = master };
            await using var source = await fixture.Resolver.OpenAsync(Request(), CancellationToken.None);
            Assert.True(fixture.Requests[2].AbsolutePath.EndsWith($"/{quality}/index.m3u8"));
        }
        using var legacy = new Fixture(PlatformKind.Kick)
        {
            MasterText = "#EXTM3U\n#EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID=\"360p30\",NAME=\"360p\"\n" +
                "#EXT-X-STREAM-INF:BANDWIDTH=600000,VIDEO=\"360p30\"\n360p/index.m3u8"
        };
        await using var selected = await legacy.Resolver.OpenAsync(Request(PlatformKind.Kick), CancellationToken.None);
        Assert.True(legacy.Requests[2].AbsolutePath.EndsWith("/360p/index.m3u8"));
        using var conservative = new Fixture();
        await using var conservativeSource = await conservative.Resolver.OpenAsync(Request() with { LowLatency = false }, CancellationToken.None);
        Assert.Equal(false, conservativeSource!.PlaybackOptions!.LowLatency);
        Assert.Equal(4000, conservativeSource.PlaybackOptions.LiveDelayMilliseconds);
    }

    private static async Task ConfigurationAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hover-preview-config-" + Guid.NewGuid().ToString("N"));
        var configDirectory = Path.Combine(directory, "streamlink");
        Directory.CreateDirectory(configDirectory);
        try
        {
            foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
            {
                var request = Request(platform);
                Assert.True(LivePreviewPolicy.CanResolve(request, directory));
                var config = Path.Combine(configDirectory, "config." + platform.ToString().ToLowerInvariant());
                await File.WriteAllTextAsync(config, "# installer\nffmpeg-ffmpeg=C:\\ffmpeg.exe\n");
                Assert.True(LivePreviewPolicy.CanResolve(request, directory));
                foreach (var setting in new[] { "http-header=Authorization=secret", "http-proxy=http://proxy", "unknown-future-option=true" })
                {
                    await File.WriteAllTextAsync(config, setting);
                    Assert.Equal(false, LivePreviewPolicy.CanResolve(request, directory));
                }
                File.Delete(config);
                foreach (var changed in new[]
                {
                    request with { CustomArguments = ["--http-header", "Authorization=secret"] },
                    request with { Target = request.Target with { Kind = StreamTargetKind.TwitchVod } },
                    request with { Target = request.Target with { Url = request.Target.Url + "?custom=true" } },
                    request with { Target = request.Target with { Channel = "different_channel" } },
                    request with { StreamlinkPath = Path.Combine(directory, "missing.exe") }
                }) Assert.Equal(false, LivePreviewPolicy.CanResolve(changed, directory));
            }
            Directory.CreateDirectory(Path.Combine(configDirectory, "plugins"));
            Assert.Equal(false, LivePreviewPolicy.CanResolve(Request(), directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
        using var disabled = new Fixture(canResolve: _ => false);
        Assert.True(await disabled.Resolver.OpenAsync(Request(), CancellationToken.None) is null);
        Assert.Equal(0, disabled.Requests.Count);
    }

    private static async Task InvalidSourcesAsync()
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
        {
            using var redirect = new Fixture(platform)
            {
                Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.EndsWith("master.m3u8") ||
                    request.RequestUri.Host == "usher.ttvnw.net"
                    ? new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://127.0.0.1/private") } } : null)
            };
            await Assert.ThrowsAsync<InvalidDataException>(async () => await redirect.Resolver.OpenAsync(Request(platform), CancellationToken.None));
            Assert.Equal(2, redirect.Requests.Count);
        }
        using (var privateDns = new Fixture(privateDns: true))
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () => await privateDns.Resolver.OpenAsync(Request(), CancellationToken.None));
            Assert.Equal(1, privateDns.Requests.Count);
        }
        using var oversized = new Fixture
        {
            Override = (request, _) =>
            {
                if (request.RequestUri!.Host != "usher.ttvnw.net") return Task.FromResult<HttpResponseMessage?>(null);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Master) };
                response.Content.Headers.ContentLength = 100_000_000;
                return Task.FromResult<HttpResponseMessage?>(response);
            }
        };
        await Assert.ThrowsAsync<StreamlinkVlcStudio.Infrastructure.Http.PayloadTooLargeException>(
            async () => await oversized.Resolver.OpenAsync(Request(), CancellationToken.None));
    }

    private static async Task UnsupportedPlaylistsAsync()
    {
        foreach (var text in new[]
        {
            Media + "\n#EXT-X-DATERANGE:ID=\"stitched-ad-123\",CLASS=\"twitch-stitched-ad\"",
            Media.Replace("#EXTINF:2.0,", "#EXTINF:2.0,Amazon"),
            Media + "\n#EXT-X-KEY:METHOD=AES-128,URI=\"secret.key\"",
            Media + "\n#EXT-X-MAP:URI=\"init.mp4\"",
            Media.Replace("first.ts", "https://127.0.0.1/private.ts"),
            Media.Replace("first.ts", "https://cdn.ttvnw.net.attacker.example/first.ts"),
            Media.Replace("first.ts", "first.mp4"),
            Media + "\n#EXTINF:2.0,", Media + "\n#EXT-X-ENDLIST", "not a playlist"
        })
        {
            using var fixture = new Fixture { MediaText = text };
            await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Resolver.OpenAsync(Request(), CancellationToken.None));
        }
    }

    private static async Task PlaylistSessionAsync()
    {
        using var fixture = new Fixture();
        await using var source = (await fixture.Resolver.OpenAsync(Request(), CancellationToken.None))!;
        using var client = new HttpClient();
        using (var denied = await client.GetAsync(new Uri(source.PlaybackUri, "/wrong.m3u8")))
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using (var head = new HttpRequestMessage(HttpMethod.Head, source.PlaybackUri))
        using (var response = await client.SendAsync(head)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await client.GetStringAsync(source.PlaybackUri);
        Assert.Equal(3, fixture.Requests.Count);
        fixture.MediaText = Media.Replace("first.ts", "third.ts");
        Assert.Contains("third.ts", await client.GetStringAsync(source.PlaybackUri));
        Assert.Equal(4, fixture.Requests.Count);
        var address = source.PlaybackUri;
        await source.DisposeAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(address));
    }

    private static async Task CancelRefreshAsync()
    {
        var refreshing = Signal();
        var stopped = Signal();
        await using var source = new LivePreviewPlaylistSession(Media, async token =>
        {
            refreshing.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); return Media; }
            finally { stopped.SetResult(); }
        });
        using var client = new HttpClient();
        await client.GetStringAsync(source.PlaybackUri);
        var request = client.GetStringAsync(source.PlaybackUri);
        await refreshing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(stopped.Task.IsCompleted);
        await Assert.ThrowsAsync<HttpRequestException>(() => request);
    }

    private static async Task LateCancellationAsync()
    {
        var arriving = Signal();
        var release = Signal();
        using var cancellation = new CancellationTokenSource();
        var transport = new Transport();
        Uri? address = null;
        var player = new LibVlcLivePreview(transport, null, async (_, _) =>
        {
            arriving.SetResult();
            await release.Task;
            var source = new LivePreviewPlaylistSession(Media, _ => Task.FromResult(Media));
            address = source.PlaybackUri;
            return source;
        }, (_, _, _, _, _) => throw new InvalidOperationException("Canceled startup must not decode."));
        var run = player.RunAsync(Request(), "vlc", _ => { }, cancellation.Token);
        await arriving.Task;
        cancellation.Cancel();
        release.SetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, transport.Starts);
        using var client = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(address!));
    }

    private static async Task DirectFailureAsync()
    {
        var transport = new Transport();
        var logger = new MemoryLogger();
        var request = Request() with { CustomArguments = ["--http-header", "Authorization=secret"] };
        var player = new LibVlcLivePreview(transport, logger,
            (_, _) => throw new InvalidDataException("private signed URL secret"),
            (_, _, _, _, _) => Task.CompletedTask);
        await player.RunAsync(request, "vlc", _ => { }, CancellationToken.None);
        Assert.Equal(1, transport.Starts);
        Assert.True(ReferenceEquals(request, transport.Request));
        Assert.True(transport.Disposed);
        Assert.True(logger.Entries.All(entry => !entry.Message.Contains("secret")));
    }

    private static async Task FirstFrameDeadlineAsync()
    {
        var transport = new Transport();
        var stopped = false;
        var source = new LivePreviewPlaylistSession(Media, _ => Task.FromResult(Media));
        var player = new LibVlcLivePreview(transport, null, (_, _) => Task.FromResult<LivePreviewPlaylistSession?>(source),
            async (uri, _, _, token, _) =>
            {
                if (uri == transport.PlaybackUri) { Assert.True(stopped); return; }
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { stopped = true; }
            }, TimeSpan.FromMilliseconds(60));
        await player.RunAsync(Request(), "vlc", _ => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, transport.Starts);
        Assert.True(transport.Disposed);
        using var client = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(source.PlaybackUri));
    }

    private static async Task SuccessfulDirectAsync()
    {
        var transport = new Transport();
        var first = Signal();
        var source = new LivePreviewPlaylistSession(Media, _ => Task.FromResult(Media));
        using var cancellation = new CancellationTokenSource();
        var player = new LibVlcLivePreview(transport, null, (_, _) => Task.FromResult<LivePreviewPlaylistSession?>(source),
            async (_, _, present, token, _) =>
            {
                present(new(1, 1, [1, 0, 0, 0]));
                first.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }, TimeSpan.FromMilliseconds(60));
        var run = player.RunAsync(Request(), "vlc", _ => { }, cancellation.Token);
        try
        {
            await first.Task;
            await Task.Delay(200);
            Assert.Equal(false, run.IsCompleted);
            Assert.Equal(0, transport.Starts);
        }
        finally { cancellation.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => run); }
        using var client = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(source.PlaybackUri));
    }

    private static async Task LaterAdAsync(bool fragmentedMp4)
    {
        using var fixture = new Fixture { MediaText = fragmentedMp4 ? FragmentedMedia : Media };
        var source = (await fixture.Resolver.OpenAsync(Request(), CancellationToken.None))!;
        var directStarted = Signal();
        var fallbackStarted = Signal();
        var directStopped = false;
        var transport = new Transport();
        using var cancellation = new CancellationTokenSource();
        var player = new LibVlcLivePreview(transport, null, (_, _) => Task.FromResult<LivePreviewPlaylistSession?>(source),
            async (uri, _, present, token, _) =>
            {
                if (uri == transport.PlaybackUri)
                {
                    Assert.True(directStopped);
                    using var check = new HttpClient();
                    await Assert.ThrowsAsync<HttpRequestException>(() => check.GetStringAsync(source.PlaybackUri));
                    fallbackStarted.SetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
                else
                {
                    present(new(1, 1, [1, 0, 0, 0]));
                    directStarted.SetResult();
                    try { await Task.Delay(Timeout.Infinite, token); }
                    finally { directStopped = true; }
                }
            });
        var run = player.RunAsync(Request(), "vlc", _ => { }, cancellation.Token);
        try
        {
            await directStarted.Task;
            using var client = new HttpClient();
            await client.GetStringAsync(source.PlaybackUri);
            fixture.MediaText += "\n#EXT-X-DATERANGE:ID=\"stitched-ad-1\",CLASS=\"twitch-stitched-ad\"";
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(source.PlaybackUri));
            await fallbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, transport.Starts);
        }
        finally { cancellation.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => run); }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly List<Uri> Requests = [];
        internal readonly LivePreviewSourceResolver Resolver;
        private readonly HttpClient client;
        internal string MasterText = Master;
        internal string MediaText = Media;
        internal string? TokenPayload;
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Override;

        internal Fixture(PlatformKind platform = PlatformKind.Twitch, bool privateDns = false,
            Func<StreamTransportRequest, bool>? canResolve = null)
        {
            client = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
            {
                Requests.Add(request.RequestUri!);
                if (Override is not null && await Override(request, token) is { } response) return response;
                string content;
                if (request.RequestUri!.Host == "gql.twitch.tv")
                {
                    TokenPayload = await request.Content!.ReadAsStringAsync(token);
                    content = """{"data":{"streamPlaybackAccessToken":{"signature":"signature+&","value":"private-token"}}}""";
                }
                else if (request.RequestUri.Host == "kick.com")
                    content = """{"data":{"playback_url":"https://channel.us-west-2.playback.live-video.net/master.m3u8"}}""";
                else if (request.RequestUri.Host == "usher.ttvnw.net")
                    content = MasterText.Replace("\n360p/", "\nhttps://video.ttvnw.net/360p/")
                        .Replace("\n480p/", "\nhttps://video.ttvnw.net/480p/")
                        .Replace("\n1080p60/", "\nhttps://video.ttvnw.net/1080p60/")
                        .Replace("\naudio/", "\nhttps://video.ttvnw.net/audio/");
                else if (request.RequestUri.AbsolutePath == "/master.m3u8") content = MasterText;
                else content = MediaText;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
            }));
            var validator = new ReplayUrlSecurityValidator((_, _) => Task.FromResult(new[]
                { IPAddress.Parse(privateDns ? "127.0.0.1" : "8.8.8.8") }), LivePreviewPolicy.IsAllowedUri);
            Resolver = new LivePreviewSourceResolver(client, validator, canResolve ?? (_ => true));
        }

        public void Dispose() => client.Dispose();
    }

    private sealed class Transport : IStreamlinkService, IStreamTransportSession
    {
        internal int Starts;
        internal StreamTransportRequest? Request;
        internal bool Disposed;
        public Uri PlaybackUri => new("http://127.0.0.1:1/streamlink");
        public event EventHandler<string>? LogLineReceived { add { } remove { } }
        public Task<IStreamTransportSession> StartExternalHttpAsync(StreamTransportRequest request, CancellationToken cancellationToken = default)
        {
            Starts++;
            Request = request;
            return Task.FromResult<IStreamTransportSession>(this);
        }
        public Task<StreamlinkProbeResult> ProbeStreamsAsync(StreamTransportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StreamlinkResolvedUrl> ResolveStreamUrlAsync(StreamTransportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}

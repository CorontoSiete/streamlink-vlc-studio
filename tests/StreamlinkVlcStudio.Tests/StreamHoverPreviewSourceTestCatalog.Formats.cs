using StreamlinkVlcStudio.Infrastructure.Previews;

internal static partial class StreamHoverPreviewSourceTestCatalog
{
    private static IReadOnlyList<(string Name, Func<Task> Run)> FormatTests =>
    [
        ("stream hover preview: frame-rate quality names keep the selected preview small", FrameRateQualitiesAsync),
        ("stream hover preview: fragmented MP4 maps and segments are validated and refreshed", FragmentedMp4Async),
        ("stream hover preview: malformed unsafe ranged and missing MP4 maps retain fallback", InvalidFragmentedMp4Async),
        ("stream hover preview: initialization and container changes stop playlist reloads", ChangedInitializationAsync),
        ("stream hover preview: a later fragmented MP4 ad stops before Streamlink replacement", () => LaterAdAsync(true)),
        ("stream hover preview: native VLC decodes fragmented MP4 initialization and changing segments", NativeFragmentedMp4Async)
    ];

    // The format observed on summit1g: frame-rate names, a whole MP4 initialization
    // file, and two-second MP4 fragments despite a six-second target duration.
    private const string FragmentedMedia = """
        #EXTM3U
        #EXT-X-VERSION:7
        #EXT-X-TARGETDURATION:6
        #EXT-X-MEDIA-SEQUENCE:100
        #EXT-X-MAP:URI="init.mp4"
        #EXTINF:2.0,
        first.mp4
        #EXTINF:2.0,
        second.m4s
        """;

    private static async Task FrameRateQualitiesAsync()
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
            foreach (var (master, selected) in new[]
            {
            (Master.Replace("IVS-NAME=\"360p\"", "IVS-NAME=\"360p30\""), "360p"),
            (Master.Replace("IVS-NAME=\"360p\"", "IVS-NAME=\"360p60\""), "360p"),
            (Master.Replace("IVS-NAME=\"360p\"", "IVS-NAME=\"160p30\"")
                .Replace("IVS-NAME=\"480p\"", "IVS-NAME=\"480p30\""), "480p"),
            (Master.Replace("IVS-NAME=\"360p\"", "IVS-NAME=\"160p30\"")
                .Replace("IVS-NAME=\"480p\"", "IVS-NAME=\"480p60\""), "480p")
        })
            {
                using var fixture = new Fixture(platform) { MasterText = master };
                await using var source = await fixture.Resolver.OpenAsync(Request(platform), CancellationToken.None);
                Assert.NotNull(source);
                Assert.Equal(3, fixture.Requests.Count);
                Assert.True(fixture.Requests[2].AbsolutePath.EndsWith($"/{selected}/index.m3u8"));
            }
    }

    private static async Task FragmentedMp4Async()
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
        {
            using var fixture = new Fixture(platform) { MediaText = FragmentedMedia };
            await using var source = (await fixture.Resolver.OpenAsync(Request(platform), CancellationToken.None))!;
            var origin = fixture.Requests[2];
            using var client = new HttpClient();
            var playlist = await client.GetStringAsync(source.PlaybackUri);
            Assert.Contains($"#EXT-X-MAP:URI=\"{new Uri(origin, "init.mp4").AbsoluteUri}\"", playlist);
            Assert.Contains(new Uri(origin, "first.mp4").AbsoluteUri, playlist);
            Assert.Contains(new Uri(origin, "second.m4s").AbsoluteUri, playlist);
            Assert.Equal(3, fixture.Requests.Count);
            fixture.MediaText = FragmentedMedia.Replace("first.mp4", "third.mp4");
            Assert.Contains(new Uri(origin, "third.mp4").AbsoluteUri, await client.GetStringAsync(source.PlaybackUri));
            Assert.Equal(4, fixture.Requests.Count);
            Assert.Equal(false, source.FallbackToken.IsCancellationRequested);
        }
    }

    private static async Task InvalidFragmentedMp4Async()
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
            foreach (var content in new[]
            {
            FragmentedMedia.Replace("init.mp4", "https://127.0.0.1/private.mp4"),
            FragmentedMedia.Replace("init.mp4", "https://cdn.ttvnw.net.attacker.example/init.mp4"),
            FragmentedMedia.Replace("init.mp4", "http://video.ttvnw.net/init.mp4"),
            FragmentedMedia.Replace("init.mp4", "https://user:secret@video.ttvnw.net/init.mp4"),
            FragmentedMedia.Replace("init.mp4", "init.bin"),
            FragmentedMedia.Replace("URI=\"init.mp4\"", "URI=init.mp4"),
            FragmentedMedia.Replace("URI=\"init.mp4\"", "URI=\"init.mp4\",BYTERANGE=\"100@0\""),
            FragmentedMedia.Replace("URI=\"init.mp4\"", "URI=\"init.mp4\",URI=\"second.mp4\""),
            FragmentedMedia.Replace("#EXT-X-MAP:URI=\"init.mp4\"", "#EXT-X-MAP:"),
            FragmentedMedia.Replace("#EXT-X-MAP:URI=\"init.mp4\"", ""),
            FragmentedMedia.Replace("first.mp4", "first.ts"),
            FragmentedMedia.Replace("first.mp4", "https://127.0.0.1/private.mp4"),
            FragmentedMedia + "\n#EXT-X-MAP:URI=\"changed.mp4\"",
            FragmentedMedia + "\n#EXT-X-KEY:METHOD=AES-128,URI=\"secret.key\""
        })
            {
                using var fixture = new Fixture(platform) { MediaText = content };
                await Assert.ThrowsAsync<InvalidDataException>(async () =>
                    await fixture.Resolver.OpenAsync(Request(platform), CancellationToken.None));
            }
        using var ad = new Fixture
        {
            MediaText = FragmentedMedia + "\n#EXT-X-DATERANGE:ID=\"stitched-ad-1\",CLASS=\"twitch-stitched-ad\""
        };
        await Assert.ThrowsAsync<InvalidDataException>(async () => await ad.Resolver.OpenAsync(Request(), CancellationToken.None));
    }

    private static async Task ChangedInitializationAsync()
    {
        foreach (var (initial, changed) in new[]
        {
            (FragmentedMedia, FragmentedMedia.Replace("init.mp4", "changed.mp4")),
            (FragmentedMedia, Media),
            (Media, FragmentedMedia)
        })
        {
            using var fixture = new Fixture { MediaText = initial };
            await using var source = (await fixture.Resolver.OpenAsync(Request(), CancellationToken.None))!;
            using var client = new HttpClient();
            await client.GetStringAsync(source.PlaybackUri);
            fixture.MediaText = changed;
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(source.PlaybackUri));
            Assert.True(source.FallbackToken.IsCancellationRequested);
        }
    }

    private static async Task NativeFragmentedMp4Async()
    {
        var vlc = ExecutableResolver.FindVlcDirectory();
        if (vlc is null) throw new InteractiveDesktopTestSkippedException("VLC is not installed.");
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hover-preview-fmp4");
        using var server = LocalHlsHttpServer.StartPlaylist(directory);
        var origin = new Uri("https://video.ttvnw.net/fixture/index.m3u8");
        var validated = LivePreviewPlaylist.Rewrite(await File.ReadAllTextAsync(Path.Combine(directory, "index.m3u8")), origin, PlatformKind.Twitch);
        // Only fixture byte delivery is substituted. Production validation, the local
        // playlist handoff, native HLS decoder, callbacks and cancellation all run.
        var localPlaylist = validated.Replace(new Uri(origin, ".").AbsoluteUri, new Uri(server.MediaUri, ".").AbsoluteUri);
        var source = new LivePreviewPlaylistSession(localPlaylist, _ => Task.FromResult(localPlaylist),
            LivePreviewPlaylist.GetPlaybackOptions(validated, Request()));
        var transport = new Transport();
        var done = Signal();
        byte[]? first = null;
        var frames = 0;
        var firstFrameAt = -1L;
        var elapsed = Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var player = new LibVlcLivePreview(transport, null, (_, _) => Task.FromResult<LivePreviewPlaylistSession?>(source),
            LibVlcLivePreview.DecodeAsync);
        var run = Task.Run(() => player.RunAsync(Request(), vlc, frame =>
        {
            frames++;
            if (firstFrameAt < 0) firstFrameAt = elapsed.ElapsedMilliseconds;
            first ??= frame.Pixels;
            if (frames >= 30 && elapsed.ElapsedMilliseconds - firstFrameAt >= 2200 && !first.AsSpan().SequenceEqual(frame.Pixels))
                done.TrySetResult();
        }, cancellation.Token));
        try
        {
            var completed = await Task.WhenAny(run, done.Task);
            await completed;
            Assert.True(done.Task.IsCompletedSuccessfully);
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => run);
        }
        Assert.Equal(0, transport.Starts);
        Assert.True(server.RequestedPaths.Contains("/init.mp4"));
        Assert.True(server.RequestedPaths.Where(path => path.EndsWith(".m4s", StringComparison.Ordinal)).Distinct().Count() >= 2);
        var stoppedFrames = frames;
        await Task.Delay(100);
        Assert.Equal(stoppedFrames, frames);
        using var client = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(source.PlaybackUri));
        Console.WriteLine($"Native fragmented MP4 hover preview: first frame {firstFrameAt} ms, {frames} changing frames across segments; no fallback, decoder and listener closed.");
    }
}

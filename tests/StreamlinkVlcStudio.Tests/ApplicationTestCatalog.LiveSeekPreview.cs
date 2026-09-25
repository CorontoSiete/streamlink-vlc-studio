internal static partial class ApplicationTestCatalog
{
    private static IReadOnlyList<(string Name, Func<Task> Run)> LiveSeekPreviewTests =>
    [
        ("replay seek overlay live hover maps DVR segments and rejects ambiguous timelines", LiveSeekPlaylistAsync),
        ("replay seek overlay live hover caches frames and refreshes growing playlists", LiveSeekCacheAsync),
        ("replay seek overlay live hover retries failures and cancels obsolete decoding", LiveSeekCancellationAsync),
        ("replay seek overlay live hover reads observed fragmented MP4 timelines and initialization changes", LiveSeekFragmentedMp4Async),
        ("replay seek overlay live hover downloads and caches matching MP4 initialization sections", LiveSeekInitializationCacheAsync),
        ("replay seek overlay live hover uses current DVR without seeking playback", LiveSeekHoverAsync),
        .. string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_LIVE_PREVIEW_CHANNEL")) ?
            Array.Empty<(string, Func<Task>)>() :
            [("replay seek overlay live hover loads real current broadcast frames", LiveSeekCurrentBroadcastAsync),
             ("replay seek overlay live hover displays real frames during live playback", LiveSeekCurrentBroadcastUiAsync)],
        .. string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_PREVIEW_SEGMENT")) ?
            Array.Empty<(string, Func<Task>)>() :
            [("replay seek overlay live hover decodes an actual transport segment", LiveSeekDecoderAsync)]
    ];

    private static async Task LiveSeekCurrentBroadcastAsync()
    {
        var settings = await new JsonSettingsService().LoadAsync();
        var target = StreamInputParser.Parse(Environment.GetEnvironmentVariable("SVS_TEST_LIVE_PREVIEW_CHANNEL")!, PlatformKind.Twitch);
        var logger = new MemoryLogger();
        var streamlink = new StreamlinkService(logger);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var replay = await new ReplayResolver(logger, streamlink).ResolveCurrentReplayAsync(target, "best", settings, timeout.Token);
        Console.WriteLine($"Live provider: channel={target.Channel}, kind={replay.MediaKind}, available={replay.IsAvailable}, seconds={replay.Duration.TotalSeconds:0}, reason={replay.UnavailableReason}");
        Assert.True(replay.IsAvailable);
        var resolved = await streamlink.ResolveStreamUrlAsync(new(target with { Url = replay.ReplayUrl }, "best",
            settings.StreamlinkPath!, false, []), timeout.Token);
        Console.WriteLine($"Replay source: {resolved.StreamUri.Host}{resolved.StreamUri.AbsolutePath}");
        LibVlcNative.SetDllDirectory(settings.VlcDirectory);
        var source = new ReplaySeekPreviewSource(replay.ReplayId,
            replay.MediaKind == ReplayMediaKind.Archive ? replay.ReplayId : null,
            resolved.StreamUri, replay.Platform, replay.StreamStartedAtUtc, settings.VlcDirectory!);
        var liveClient = new LiveSeekPreviewClient();
        var playlist = await liveClient.GetPlaylistAsync(source.PlaylistUri!, source.Platform, source.StartedAt, timeout.Token);
        Console.WriteLine($"Parsed live preview segments: {playlist?.Segments.Count ?? 0}");
        var images = new ReplaySeekPreviewImages();
        foreach (var seconds in new[] { 60d, replay.Duration.TotalSeconds / 2, replay.Duration.TotalSeconds - 60 })
        {
            var watch = Stopwatch.StartNew();
            var image = await images.GetAsync(source, seconds, timeout.Token);
            Console.WriteLine($"Live preview at {seconds:0.0}: image={image is not null}, milliseconds={watch.ElapsedMilliseconds}");
            Assert.NotNull(image);
            SaveLivePreviewImage(image!, $"{target.Channel}-{seconds:0}");
        }
    }

    private static Task LiveSeekCurrentBroadcastUiAsync() => TestSta.RunAsync(async () =>
    {
        var settings = await new JsonSettingsService().LoadAsync();
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Docked;
        var target = StreamInputParser.Parse(Environment.GetEnvironmentVariable("SVS_TEST_LIVE_PREVIEW_CHANNEL")!, PlatformKind.Twitch);
        var logger = new MemoryLogger();
        var streamlink = new StreamlinkService(logger);
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        await using var tab = TestViewModels.CreateTab(target, "best", streamlink,
            new LibVlcPlaybackEngineFactory(logger, settings.Chat), new FakeChatClientFactory(), logger,
            action => dispatcher.BeginInvoke(action), initialVolume: 0,
            replayResolver: new ReplayResolver(logger, streamlink));
        using var fixture = new ReplayOverlayTestHost(tab);
        // Use production loading, network, and decoding; the normal UI fixture deliberately stubs images.
        fixture.Overlay.PreviewImageLoader = null;
        tab.SetVideoHandle(fixture.Target.Handle);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await tab.StartAsync(settings, cancellationToken: timeout.Token);
        await TestWait.UntilAsync(() => tab.Status == PlaybackStatus.Playing && tab.CanSeekReplay &&
            tab.ReplayPreviewSource?.PlaylistUri is not null, TimeSpan.FromSeconds(45));
        var engine = (IPlaybackEngine)typeof(StreamTabViewModel)
            .GetField("playbackEngine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;
        await TestWait.UntilAsync(() => engine.TryGetVideoSize(out var width, out _) && width > 0, TimeSpan.FromSeconds(15));
        Assert.Equal(false, tab.Target.IsExplicitVod);
        Assert.Equal(false, tab.IsReplayMode);
        Assert.NotNull(tab.ReplayPreviewSource);
        Console.WriteLine($"Live UI: channel={target.Channel}, replay={tab.ReplayPreviewSource!.ReplayId}, source={tab.ReplayPreviewSource.PlaylistUri!.Host}, status={tab.Status}");
        var hashes = new HashSet<string>();
        foreach (var fraction in new[] { 0.05, 0.5, 0.98 })
        {
            fixture.Overlay.ProcessPointerSample(new Point(100 + fraction, 100), true, Environment.TickCount64);
            fixture.FlushBindings();
            var hoverPoint = new Point(fixture.Slider.ActualWidth * fraction, 14);
            fixture.Overlay.UpdateSeekHover(hoverPoint);
            var preview = (Image)fixture.Overlay.FindName("SeekPreviewImage");
            var watch = Stopwatch.StartNew();
            while (preview.Source is null && watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                await Task.Delay(100);
                // Exercise repeated samples while the actual live timeline keeps advancing.
                fixture.Overlay.UpdateSeekHover(hoverPoint);
            }
            Assert.NotNull(preview.Source);
            fixture.FlushBindings();
            Assert.Equal(Visibility.Visible, ((Border)fixture.Overlay.FindName("SeekPreviewImageFrame")).Visibility);
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            Assert.Equal(false, tab.IsReplayMode);
            Assert.Equal(false, tab.IsBehindLive);
            var bitmap = (BitmapSource)preview.Source!;
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            hashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
            SaveLivePreviewImage(bitmap, $"{target.Channel}-ui-{fraction:0.00}");
            using var screenshot = CaptureReplayVlcSurface(fixture.Target);
            SaveReplayVlcArtifact(screenshot, VideoRendererMode.Automatic, $"live-{target.Channel}-{fraction:0.00}");
            Console.WriteLine($"Live UI hover {fraction:P0}: visible image {bitmap.PixelWidth}x{bitmap.PixelHeight}, load={watch.ElapsedMilliseconds} ms, live playback unchanged");
        }
        Assert.Equal(3, hashes.Count);
    });

    private static void SaveLivePreviewImage(BitmapSource bitmap, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SVS_TEST_ARTIFACT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, $"live-preview-{name}.png"));
        encoder.Save(output);
    }

    // Structural excerpt of the live NICKMERCS DVR playlist which reproduced the failure.
    // Wall-clock program timestamps differ from the replay's zero-based media timeline.
    private const string LiveFragmentedMp4Playlist = """
        #EXTM3U
        #EXT-X-VERSION:6
        #EXT-X-TARGETDURATION:14
        #EXT-X-PLAYLIST-TYPE:EVENT
        #EXT-X-MEDIA-SEQUENCE:0
        #EXT-X-TWITCH-ELAPSED-SECS:0.000
        #EXT-X-TWITCH-TOTAL-SECS:30.000
        #EXT-X-MAP:URI="init-0.mp4"
        #EXT-X-PROGRAM-DATE-TIME:2026-09-24T12:13:50.285Z
        #EXTINF:10.000,
        0.mp4
        #EXT-X-PROGRAM-DATE-TIME:2026-09-24T12:14:00.285Z
        #EXTINF:10.000,
        1.mp4
        #EXT-X-DISCONTINUITY
        #EXT-X-MAP:URI="init-1.mp4"
        #EXT-X-PROGRAM-DATE-TIME:2026-09-24T12:14:10.285Z
        #EXTINF:10.000,
        2.mp4
        """;

    private static Task LiveSeekFragmentedMp4Async()
    {
        var playlist = LiveSeekPlaylist.Parse(LiveFragmentedMp4Playlist, LivePreviewUri, PlatformKind.Twitch,
            DateTimeOffset.Parse("2026-09-24T12:13:48Z"))!;
        Assert.NotNull(playlist);
        Assert.Equal(3, playlist.Segments.Count);
        Assert.Equal(0d, playlist.GetSegment(0)!.Start);
        Assert.Equal(new Uri(LivePreviewUri, "0.mp4"), playlist.GetSegment(9.9)!.Uri);
        Assert.Equal(new Uri(LivePreviewUri, "init-0.mp4"), playlist.GetSegment(10)!.InitializationUri);
        Assert.Equal(new Uri(LivePreviewUri, "init-1.mp4"), playlist.GetSegment(20)!.InitializationUri);
        Assert.True(playlist.GetSegment(30) is null);
        foreach (var invalid in new[]
        {
            LiveFragmentedMp4Playlist.Replace("init-0.mp4", "file:///C:/private.mp4"),
            LiveFragmentedMp4Playlist.Replace("init-0.mp4", "https://untrusted.example/init.mp4"),
            LiveFragmentedMp4Playlist.Replace("URI=\"init-0.mp4\"", "URI=\"init-0.mp4\",BYTERANGE=\"400@0\""),
            LiveFragmentedMp4Playlist.Replace("#EXT-X-MAP:URI=\"init-0.mp4\"", "#EXT-X-MAP:URI=init-0.mp4")
        }) Assert.True(LiveSeekPlaylist.Parse(invalid, LivePreviewUri, PlatformKind.Twitch, null) is null);
        var m4s = LiveFragmentedMp4Playlist.Replace("0.mp4\n", "0.m4s\n");
        Assert.NotNull(LiveSeekPlaylist.Parse(m4s, LivePreviewUri, PlatformKind.Twitch, null)!.GetSegment(0));
        return Task.CompletedTask;
    }

    private static async Task LiveSeekInitializationCacheAsync()
    {
        var requests = new List<string>();
        var initializations = new List<byte>();
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = path.EndsWith(".m3u8") ? new StringContent(LiveFragmentedMp4Playlist) :
                    new ByteArrayContent([path.EndsWith("init-0.mp4") ? (byte)10 : path.EndsWith("init-1.mp4") ? (byte)20 : (byte)99])
            };
        }));
        var validator = new ReplayUrlSecurityValidator((_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }));
        var images = new LiveSeekPreviewImages(new(http, validator), (segment, initialization, _, _) =>
        {
            Assert.Equal((byte)99, segment[0]);
            Assert.NotNull(initialization);
            initializations.Add(initialization![0]);
            return Task.FromResult<byte[]?>(new byte[192 * 108 * 4]);
        }, () => 1);
        foreach (var seconds in new[] { 0d, 10, 20, 1, 11, 21 })
            Assert.NotNull(await images.GetAsync(LivePreviewSource(), seconds, CancellationToken.None));
        Assert.Equal("10,10,20", string.Join(',', initializations));
        Assert.Equal(6, requests.Count); // One playlist, two init sections, three media segments.
        Assert.Equal(1, requests.Count(path => path.EndsWith("init-0.mp4")));
        Assert.Equal(1, requests.Count(path => path.EndsWith("init-1.mp4")));
    }

    private static readonly Uri LivePreviewUri = new("https://d123.cloudfront.net/broadcast/chunked/index-dvr.m3u8");
    private const string LivePreviewPlaylist = """
        #EXTM3U
        #EXT-X-PLAYLIST-TYPE:EVENT
        #EXT-X-MEDIA-SEQUENCE:0
        #EXTINF:4.5,
        0.ts
        #EXTINF:7.5,
        1.ts
        """;

    private static ReplaySeekPreviewSource LivePreviewSource(string id = "live-dvr-123") =>
        new(id, null, LivePreviewUri, PlatformKind.Twitch, null, @"C:\VLC");

    private static Task LiveSeekPlaylistAsync()
    {
        var playlist = LiveSeekPlaylist.Parse(LivePreviewPlaylist, LivePreviewUri, PlatformKind.Twitch, null)!;
        Assert.Equal(new Uri(LivePreviewUri, "0.ts"), playlist.GetSegment(0)!.Uri);
        Assert.Equal(new Uri(LivePreviewUri, "0.ts"), playlist.GetSegment(4.49)!.Uri);
        Assert.Equal(new Uri(LivePreviewUri, "1.ts"), playlist.GetSegment(4.5)!.Uri);
        Assert.Equal(4.5, playlist.GetSegment(11.99)!.Start);
        foreach (var seconds in new[] { -1, 12, 100, double.NaN, double.PositiveInfinity })
            Assert.True(playlist.GetSegment(seconds) is null);

        var sliding = LivePreviewPlaylist.Replace("SEQUENCE:0", "SEQUENCE:500");
        Assert.True(LiveSeekPlaylist.Parse(sliding, LivePreviewUri, PlatformKind.Twitch, null) is null);
        var elapsed = sliding.Replace("#EXTINF:4.5,", "#EXT-X-TWITCH-ELAPSED-SECS:125.5\n#EXTINF:4.5,");
        playlist = LiveSeekPlaylist.Parse(elapsed, LivePreviewUri, PlatformKind.Twitch, null)!;
        Assert.True(playlist.GetSegment(125) is null);
        Assert.Equal(new Uri(LivePreviewUri, "0.ts"), playlist.GetSegment(125.5)!.Uri);
        Assert.Equal(new Uri(LivePreviewUri, "1.ts"), playlist.GetSegment(130)!.Uri);

        var dated = sliding.Replace("#EXTINF:4.5,", "#EXT-X-PROGRAM-DATE-TIME:2026-09-24T12:05:00Z\n#EXTINF:4.5,");
        playlist = LiveSeekPlaylist.Parse(dated, LivePreviewUri, PlatformKind.Twitch,
            DateTimeOffset.Parse("2026-09-24T12:00:00Z"))!;
        Assert.Equal(300d, playlist.GetSegment(300)!.Start);
        Assert.True(playlist.GetSegment(0) is null);

        foreach (var invalid in new[]
        {
            LivePreviewPlaylist.Replace("0.ts", "https://example.com/private.ts"),
            LivePreviewPlaylist.Replace("0.ts", "file:///C:/private.ts"),
            LivePreviewPlaylist.Replace("#EXTINF:4.5,", "#EXTINF:NaN,"),
            LivePreviewPlaylist.Replace("#EXTINF:4.5,", "#EXTINF:500,"),
            LivePreviewPlaylist.Replace("#EXTINF:4.5,", "#EXT-X-MAP:URI=\"https://127.0.0.1/init.mp4\"\n#EXTINF:4.5,"),
            LivePreviewPlaylist.Replace("#EXTINF:4.5,", "#EXT-X-BYTERANGE:100@0\n#EXTINF:4.5,"),
            LivePreviewPlaylist.Replace("#EXTINF:4.5,", "#EXT-X-KEY:METHOD=AES-128,URI=\"key\"\n#EXTINF:4.5,")
        }) Assert.True(LiveSeekPlaylist.Parse(invalid, LivePreviewUri, PlatformKind.Twitch, null) is null);
        var gap = LivePreviewPlaylist.Replace("#EXTINF:4.5,", "#EXT-X-GAP\n#EXTINF:4.5,");
        playlist = LiveSeekPlaylist.Parse(gap, LivePreviewUri, PlatformKind.Twitch, null)!;
        Assert.True(playlist.GetSegment(0) is null);
        Assert.NotNull(playlist.GetSegment(5));
        return Task.CompletedTask;
    }

    private static async Task LiveSeekCacheAsync()
    {
        var now = 1L;
        var manifestRequests = 0;
        var segmentRequests = 0;
        var decodes = 0;
        var manifest = LivePreviewPlaylist;
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            var isPlaylist = request.RequestUri!.AbsolutePath.EndsWith(".m3u8");
            if (isPlaylist) manifestRequests++; else segmentRequests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = isPlaylist ? new StringContent(manifest) : new ByteArrayContent([0x47])
            };
        }));
        var validator = new ReplayUrlSecurityValidator((_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }));
        var images = new LiveSeekPreviewImages(new(http, validator), (_, _, _, _) =>
        {
            decodes++;
            return Task.FromResult<byte[]?>(new byte[192 * 108 * 4]);
        }, () => now);
        var source = LivePreviewSource();
        var first = await images.GetAsync(source, 0, CancellationToken.None);
        Assert.True(first is { IsFrozen: true, PixelWidth: 192, PixelHeight: 108 });
        Assert.True(ReferenceEquals(first, await images.GetAsync(source, 4, CancellationToken.None)));
        Assert.Equal(1, decodes);
        Assert.Equal(1, manifestRequests);
        Assert.Equal(1, segmentRequests);
        Assert.NotNull(await images.GetAsync(source, 5, CancellationToken.None));
        Assert.True(await images.GetAsync(source, 15, CancellationToken.None) is null);
        Assert.Equal(2, decodes);
        manifest += "\n#EXTINF:10,\n2.ts\n";
        now += 5_001;
        Assert.NotNull(await images.GetAsync(source, 15, CancellationToken.None));
        Assert.Equal(2, manifestRequests);
        Assert.Equal(3, decodes);
        Assert.True(ReferenceEquals(first, await images.GetAsync(source, 1, CancellationToken.None)));
        Assert.Equal(3, segmentRequests);

        // A new broadcast cannot reuse frames just because it happens to have the same URL.
        Assert.NotNull(await images.GetAsync(LivePreviewSource("live-dvr-456"), 1, CancellationToken.None));
        Assert.Equal(4, decodes);
        Assert.Equal(3, manifestRequests);

        // Bound the per-overlay image cache, even when scrubbing a long stream.
        manifest = "#EXTM3U\n" + string.Join("\n", Enumerable.Range(0, 30).Select(i => $"#EXTINF:10,\n{i}.ts"));
        now += 5_001;
        for (var i = 0; i < 30; i++)
            Assert.NotNull(await images.GetAsync(source, i * 10, CancellationToken.None));
        var before = decodes;
        await images.GetAsync(source, 0, CancellationToken.None);
        Assert.Equal(before + 1, decodes);
    }

    private static async Task LiveSeekCancellationAsync()
    {
        var now = 1L;
        var decodes = 0;
        var decoding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new FakeHttpMessageHandler(request => new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith(".m3u8") ? LivePreviewPlaylist : "segment")
        }));
        var validator = new ReplayUrlSecurityValidator((_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }));
        var images = new LiveSeekPreviewImages(new(http, validator), async (_, _, _, token) =>
        {
            if (++decodes == 1)
            {
                decoding.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return decodes == 2 ? null : new byte[192 * 108 * 4];
        }, () => now);
        using var cancellation = new CancellationTokenSource();
        var pending = images.GetAsync(LivePreviewSource(), 1, cancellation.Token);
        await decoding.Task;
        cancellation.Cancel();
        var cancelled = false;
        try { await pending; } catch (OperationCanceledException) { cancelled = true; }
        Assert.True(cancelled);
        Assert.True(await images.GetAsync(LivePreviewSource(), 1, CancellationToken.None) is null);
        Assert.Equal(2, decodes); // Cancellation did not poison the cache or hold the gate.
        Assert.True(await images.GetAsync(LivePreviewSource(), 1, CancellationToken.None) is null);
        Assert.Equal(2, decodes); // A decoder failure backs off.
        now += 15_001;
        Assert.NotNull(await images.GetAsync(LivePreviewSource(), 1, CancellationToken.None));
        Assert.Equal(3, decodes);
        Assert.True(await images.GetAsync(LivePreviewSource() with { PlaylistUri = new("https://127.0.0.1/private.m3u8") },
            1, CancellationToken.None) is null);
    }

    private static Task LiveSeekHoverAsync() => TestSta.RunAsync(async () =>
    {
        var replay = new ReplaySessionInfo(PlatformKind.Twitch, "streamer", LivePreviewUri.AbsoluteUri,
            "live-dvr-123", null, TimeSpan.FromHours(1), true, "", MediaKind: ReplayMediaKind.CurrentLiveDvr);
        await using var session = await ReplayOverlayTestSession.CreateAsync(replay);
        using var fixture = new ReplayOverlayTestHost(session.Tab);
        var image = CreateSeekHoverTestImage(0x60);
        ReplaySeekPreviewSource? loadedSource = null;
        double loadedSeconds = 0;
        fixture.Overlay.PreviewImageLoader = (source, seconds, _) =>
        {
            loadedSource = source;
            loadedSeconds = seconds;
            return Task.FromResult<BitmapSource?>(image);
        };
        var seekCount = session.SeekCount;
        var position = session.Tab.ReplaySeekValue;
        fixture.Overlay.ProcessPointerSample(new Point(100, 100), true, Environment.TickCount64);
        fixture.FlushBindings();
        fixture.Overlay.UpdateSeekHover(new Point(fixture.Slider.ActualWidth / 2, 14));
        var previewImage = (Image)fixture.Overlay.FindName("SeekPreviewImage");
        await TestWait.UntilAsync(() => ReferenceEquals(previewImage.Source, image), TimeSpan.FromSeconds(2));
        Assert.NotNull(loadedSource);
        Assert.True(loadedSource!.VideoId is null);
        Assert.Equal(LivePreviewUri, loadedSource.PlaylistUri);
        Assert.Equal("live-dvr-123", loadedSource.ReplayId);
        Assert.Equal(1800d, loadedSeconds);
        Assert.Equal("30:00", ((TextBlock)fixture.Overlay.FindName("SeekPreviewTimestamp")).Text);
        Assert.Equal(seekCount, session.SeekCount);
        Assert.Equal(position, session.Tab.ReplaySeekValue);
        Assert.Equal(false, session.Tab.IsReplayMode);
    });

    private static async Task LiveSeekDecoderAsync()
    {
        var vlc = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
        LibVlcNative.SetDllDirectory(vlc);
        var bytes = await File.ReadAllBytesAsync(Environment.GetEnvironmentVariable("SVS_TEST_PREVIEW_SEGMENT")!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pixels = await LibVlcPreviewDecoder.DecodeAsync(bytes, null, vlc, timeout.Token);
        Assert.NotNull(pixels);
        Assert.Equal(192 * 108 * 4, pixels!.Length);
        // The steady blue fixture must produce real image pixels, not just an allocated buffer.
        Assert.True(Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4] > pixels[i * 4 + 2] + 40) > 10_000);
    }
}

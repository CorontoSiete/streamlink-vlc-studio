internal static partial class ApplicationTestCatalog
{
    private static IReadOnlyList<(string Name, Func<Task> Run)> KickSeekPreviewTests =>
    [
        ("replay seek overlay Kick VOD preview reuses resolved media through seek and restart", KickSeekPreviewSourceAsync),
        ("replay seek overlay Kick VOD preview loads and caches the hovered segment", () => KickSeekPreviewFramesAsync(false)),
        ("replay seek overlay Kick VOD preview displays a frame without seeking playback", KickSeekPreviewHoverAsync),
        .. string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ?
            Array.Empty<(string, Func<Task>)>() :
            [("replay seek overlay Kick VOD preview decodes real video frames", () => KickSeekPreviewFramesAsync(true))]
    ];

    private static readonly Uri KickPreviewMasterUri = new("https://vod.kick.com/streamer/master.m3u8");
    private static readonly Uri KickPreviewMediaUri = new("https://vod.kick.com/streamer/720p/index.m3u8?token=first");

    private static async Task KickSeekPreviewFramesAsync(bool native)
    {
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(KickPreviewMediaUri, ""))
        };
        var playbackFactory = new FakePlaybackEngineFactory();
        await using var tab = CreateKickPreviewTab(streamlink, playbackFactory);
        var settings = KickPreviewSettings();
        if (native)
        {
            settings.VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
            LibVlcNative.SetDllDirectory(settings.VlcDirectory);
        }
        await tab.StartAsync(settings);
        await StopReplayClockPollingAsync(tab);
        var source = tab.ReplayPreviewSource;
        Assert.NotNull(source);

        var requests = new List<Uri>();
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-event");
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!;
            requests.Add(uri);
            HttpContent content;
            if (uri == KickPreviewMediaUri)
                content = new StringContent(File.ReadAllText(Path.Combine(fixtureDirectory, "index.m3u8")) + "\n#EXT-X-ENDLIST\n");
            else if (uri == new Uri(KickPreviewMediaUri, "index0.ts") || uri == new Uri(KickPreviewMediaUri, "index4.ts"))
                content = new ByteArrayContent(File.ReadAllBytes(Path.Combine(fixtureDirectory, Path.GetFileName(uri.AbsolutePath))));
            else throw new InvalidOperationException($"Unexpected preview request: {uri}");
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = content };
        }));
        var validator = new ReplayUrlSecurityValidator((_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }));
        var decodes = 0;
        var liveImages = new LiveSeekPreviewImages(new(http, validator), async (segment, initialization, directory, token) =>
        {
            decodes++;
            Assert.True(initialization is null);
            Assert.Equal(settings.VlcDirectory, directory);
            return native ? await LibVlcPreviewDecoder.DecodeAsync(segment, initialization, directory, token) : new byte[192 * 108 * 4];
        }, () => 1);
        var images = new ReplaySeekPreviewImages(new(http, validator), () => 1, liveImages);
        var first = await images.GetAsync(source!, 5, CancellationToken.None);
        Assert.True(first is { IsFrozen: true, PixelWidth: 192, PixelHeight: 108 });
        Assert.True(ReferenceEquals(first, await images.GetAsync(source!, 6, CancellationToken.None)));
        var later = await images.GetAsync(source!, 45, CancellationToken.None);
        Assert.NotNull(later);
        Assert.Equal(2, decodes);
        Assert.SequenceEqual(new[] { KickPreviewMediaUri, new Uri(KickPreviewMediaUri, "index0.ts"),
            new Uri(KickPreviewMediaUri, "index4.ts") }, requests);
        if (native)
        {
            // The fixture is red at the beginning and blue after 40 seconds.
            var pixel = new byte[4];
            first!.CopyPixels(new Int32Rect(96, 54, 1, 1), pixel, 4, 0);
            Assert.True(pixel[2] > pixel[0] + 100);
            later!.CopyPixels(new Int32Rect(96, 54, 1, 1), pixel, 4, 0);
            Assert.True(pixel[0] > pixel[2] + 100);
        }
        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.Equal(1, playbackFactory.Engine!.PlayCount);
        Assert.Equal(0, playbackFactory.Engine.SeekCount);
    }

    private static Task KickSeekPreviewHoverAsync() => TestSta.RunAsync(async () =>
    {
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(KickPreviewMediaUri, ""))
        };
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine { Duration = TimeSpan.FromSeconds(60) });
        await using var tab = CreateKickPreviewTab(streamlink, playbackFactory);
        await tab.StartAsync(KickPreviewSettings());
        await StopReplayClockPollingAsync(tab);
        using var fixture = new ReplayOverlayTestHost(tab);
        var image = CreateSeekHoverTestImage(0x60);
        ReplaySeekPreviewSource? loadedSource = null;
        double loadedSeconds = 0;
        fixture.Overlay.PreviewImageLoader = (source, seconds, _) =>
        {
            loadedSource = source;
            loadedSeconds = seconds;
            return Task.FromResult<BitmapSource?>(image);
        };
        var position = tab.ReplaySeekValue;
        fixture.Overlay.ProcessPointerSample(new Point(100, 100), true, Environment.TickCount64);
        fixture.FlushBindings();
        fixture.Overlay.UpdateSeekHover(new Point(fixture.Slider.ActualWidth / 2, 14));
        var previewImage = (Image)fixture.Overlay.FindName("SeekPreviewImage");
        await TestWait.UntilAsync(() => ReferenceEquals(previewImage.Source, image), TimeSpan.FromSeconds(2));
        Assert.Equal(KickPreviewMediaUri, loadedSource!.PlaylistUri);
        Assert.Equal(30d, loadedSeconds);
        Assert.Equal("0:30", ((TextBlock)fixture.Overlay.FindName("SeekPreviewTimestamp")).Text);
        Assert.Equal(Visibility.Visible, ((Border)fixture.Overlay.FindName("SeekPreviewImageFrame")).Visibility);
        Assert.Equal(position, tab.ReplaySeekValue);
        Assert.Equal(0, playbackFactory.Engine!.SeekCount);
        Assert.Equal(1, playbackFactory.Engine.PlayCount);
        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
    });

    private static async Task KickSeekPreviewSourceAsync()
    {
        var resolvedUri = KickPreviewMediaUri;
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(resolvedUri, ""))
        };
        var playbackFactory = new FakePlaybackEngineFactory();
        await using var tab = CreateKickPreviewTab(streamlink, playbackFactory);
        var settings = KickPreviewSettings();
        Assert.True(tab.ReplayPreviewSource is null);
        await tab.StartAsync(settings);
        await StopReplayClockPollingAsync(tab);

        var source = tab.ReplayPreviewSource;
        Assert.NotNull(source);
        Assert.Equal(KickPreviewMediaUri, source!.PlaylistUri);
        Assert.Equal(PlatformKind.Kick, source.Platform);
        Assert.Equal("kick-video-123", source.ReplayId);
        Assert.True(source.VideoId is null);
        Assert.Equal(tab.Target.MediaStartedAtUtc, source.StartedAt);
        Assert.Equal(settings.VlcDirectory, source.VlcDirectory);
        Assert.Equal(KickPreviewMediaUri, playbackFactory.Engine!.LastPlayedUri);
        for (var i = 0; i < 10; i++) Assert.Equal(source, tab.ReplayPreviewSource);
        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.Equal(0, streamlink.StartCount);
        Assert.Equal(0, playbackFactory.Engine.SeekCount);

        await tab.SeekReplayAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(source, tab.ReplayPreviewSource);
        Assert.Equal(1, streamlink.ResolveStreamUrlCount);

        resolvedUri = new("https://vod.kick.com/streamer/720p/index.m3u8?token=refreshed");
        await tab.SeekReplayAsync(TimeSpan.FromSeconds(40), forceReload: true);
        Assert.Equal(resolvedUri, tab.ReplayPreviewSource!.PlaylistUri);
        Assert.Equal(2, streamlink.ResolveStreamUrlCount);

        resolvedUri = new("https://vod.kick.com/streamer/480p/index.m3u8?token=second");
        await tab.StartAsync(settings);
        Assert.Equal(resolvedUri, tab.ReplayPreviewSource!.PlaylistUri);
        Assert.Equal(3, streamlink.ResolveStreamUrlCount);
        await tab.StopAsync();
        Assert.True(tab.ReplayPreviewSource is null);
    }

    private static StreamTabViewModel CreateKickPreviewTab(FakeStreamlinkService streamlink,
        FakePlaybackEngineFactory playbackFactory)
    {
        var target = new StreamTarget(PlatformKind.Kick, "streamer", KickPreviewMasterUri.AbsoluteUri,
            StreamTargetKind.KickVod, "kick-video-123", MediaDuration: TimeSpan.FromSeconds(60),
            MediaStartedAtUtc: DateTimeOffset.Parse("2026-09-24T12:00:00Z"));
        var tab = TestViewModels.CreateTab(target, "best", streamlink, playbackFactory,
            new FakeChatClientFactory(), new MemoryLogger(), action => action(),
            vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
        tab.SetVideoHandle(new IntPtr(42));
        return tab;
    }

    private static AppSettings KickPreviewSettings()
    {
        var settings = new AppSettings { StreamlinkPath = "streamlink.exe", VlcDirectory = @"C:\VLC" };
        settings.Chat.ConnectAutomatically = false;
        return settings;
    }
}

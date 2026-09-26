internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> VodResumeNativeTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ? [] :
        [
            ("VOD resume: real VLC restores saved MP4 playback after reopening", () => NativeVodResumeAsync(false)),
            ("VOD resume: real VLC restores saved HLS playback after reopening", () => NativeVodResumeAsync(true))
        ];

    private static Task NativeVodResumeAsync(bool hls) => TestSta.RunOffscreenAsync(async () =>
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var settings = VodResumeTestCatalog.Settings();
        settings.VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
        settings.VideoRendererMode = VideoRendererMode.Gdi;
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures",
            hls ? "replay-position-event/index.m3u8" : "replay-position-colors.mp4");
        await using var server = hls ? new ReplayFixtureServer(Path.GetDirectoryName(fixture)!) : null;
        var source = server?.Uri ?? new Uri(fixture);
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(source, "Fixture"))
        };
        var target = VodResumeTestCatalog.Target() with { MediaDuration = TimeSpan.FromSeconds(60) };
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            var factory = new VodResumeNativeFactory(settings.Chat);
            await using (var tab = VodResumeTestCatalog.Tab(target, files.Create(), factory, streamlink: streamlink))
            {
                tab.SetVideoHandle(handle);
                tab.IsMuted = true;
                await tab.StartAsync(settings);
                Assert.Equal(PlaybackStatus.Playing, tab.Status);
                await TestWait.UntilAsync(() => factory.Engine!.TryGetPlaybackClock(out var clock) && clock.IsSeekable,
                    TimeSpan.FromSeconds(8));
                await tab.SeekReplayAsync(TimeSpan.FromSeconds(35.25));
                Assert.Equal(PlaybackStatus.Playing, tab.Status);
                PlaybackClock clock = null!;
                await TestWait.UntilAsync(() => factory.Engine!.TryGetPlaybackClock(out clock), TimeSpan.FromSeconds(2));
                Assert.True(clock.Position >= TimeSpan.FromSeconds(34) && clock.Position < TimeSpan.FromSeconds(40));
            }
            var bookmark = (await files.Create().GetAsync(target))!;
            Assert.NotNull(bookmark);
            Assert.True(bookmark.Position >= TimeSpan.FromSeconds(34) && bookmark.Position < TimeSpan.FromSeconds(40));
            var reopenedFactory = new VodResumeNativeFactory(settings.Chat);
            await using var reopened = VodResumeTestCatalog.Tab(target, files.Create(), reopenedFactory, streamlink: streamlink);
            reopened.SetVideoHandle(handle);
            reopened.IsMuted = true;
            await reopened.StartAsync(settings);
            Assert.Equal(PlaybackStatus.Playing, reopened.Status);
            PlaybackClock restored = null!;
            await TestWait.UntilAsync(() => reopenedFactory.Engine!.TryGetPlaybackClock(out restored), TimeSpan.FromSeconds(2));
            Console.WriteLine($"VOD resume native {(hls ? "HLS" : "MP4")}: saved={bookmark.Position}, reopened={restored.Position}");
            Assert.True(restored.Position >= bookmark.Position - TimeSpan.FromSeconds(1) &&
                restored.Position <= bookmark.Position + TimeSpan.FromSeconds(3));
            await TestWait.UntilAsync(() => reopenedFactory.Engine!.TryGetPlaybackHealth(out var health) &&
                health.DisplayedPictures > 0, TimeSpan.FromSeconds(5));
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });

    private sealed class VodResumeNativeFactory(ChatSettings chat,
        IPlaybackMediaSourceGateway? mediaSourceGateway = null, bool nativeOverlay = false) : IPlaybackEngineFactory
    {
        internal IPlaybackEngine? Engine { get; private set; }
        public async Task<IPlaybackEngine> CreateAsync(string vlcDirectory, bool enableNativeOverlay = true,
            string? nativeOverlayPositionStatePath = null, CancellationToken cancellationToken = default,
            VideoRendererMode rendererMode = VideoRendererMode.Automatic)
        {
            Engine = await new LibVlcPlaybackEngineFactory(new MemoryLogger(), chat, mediaSourceGateway).CreateAsync(vlcDirectory,
                enableNativeOverlay: nativeOverlay, cancellationToken: cancellationToken, rendererMode: rendererMode);
            return Engine;
        }
    }
}

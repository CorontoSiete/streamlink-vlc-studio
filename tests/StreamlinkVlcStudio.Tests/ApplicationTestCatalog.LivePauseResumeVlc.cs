internal static partial class ApplicationTestCatalog
{
    private static Task NativeLivePausePastPublishedDvrAsync() => NativeLivePauseWithDvrAsync(caughtUp: false);
    private static Task NativeLivePauseAfterDvrCatchesUpAsync() => NativeLivePauseWithDvrAsync(caughtUp: true);

    private static Task NativeLivePauseWithDvrAsync(bool caughtUp) => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-audio");
        var complete = File.ReadAllText(Path.Combine(directory, "index.m3u8"));
        var lastSegment = complete.LastIndexOf("#EXTINF:", StringComparison.Ordinal);
        var replayPlaylist = complete[..complete.LastIndexOf("#EXTINF:", lastSegment - 1, StringComparison.Ordinal)];
        await using var live = new ReplayFixtureServer(directory);
        await using var replayMedia = new ReplayFixtureServer(directory, caughtUp ? complete : replayPlaylist);
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            var replay = new ReplaySessionInfo(PlatformKind.Twitch, "streamer",
                "https://www.twitch.tv/videos/123", "123",
                DateTimeOffset.UtcNow - TimeSpan.FromSeconds(caughtUp ? 42 : 60),
                TimeSpan.FromSeconds(caughtUp ? 20 : 40), true, "");
            var streamlink = new FakeStreamlinkService
            {
                StartExternalHttpOverride = (_, _) =>
                    Task.FromResult<IStreamTransportSession>(new PauseClockTransport(live.Uri)),
                ResolveStreamUrlOverride = (_, _) =>
                    Task.FromResult(new StreamlinkResolvedUrl(replayMedia.Uri, "Local growing DVR"))
            };
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!
            };
            settings.Chat.ConnectAutomatically = false;
            var logger = new MemoryLogger();
            await using var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("streamer", PlatformKind.Twitch), "best", streamlink,
                new LibVlcPlaybackEngineFactory(logger, settings.Chat), new FakeChatClientFactory(),
                logger, action => action(), initialVolume: 0,
                replayResolver: new FakeReplayResolver(replay),
                vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
            tab.SetVideoHandle(handle);
            await tab.StartAsync(settings);
            await TestWait.UntilAsync(() => tab.CanSeekReplay, TimeSpan.FromSeconds(5));
            var engine = (LibVlcPlaybackEngine)typeof(StreamTabViewModel)
                .GetField("playbackEngine", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(tab)!;
            await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var health) &&
                health.DisplayedPictures > 0, TimeSpan.FromSeconds(6));
            Assert.True(engine.TryGetPlaybackHealth(out var before));

            await tab.PauseOrResumeAsync();
            var held = tab.ReplaySeekValue;
            await Task.Delay(TimeSpan.FromSeconds(7));
            var watch = Stopwatch.StartNew();
            await tab.PauseOrResumeAsync();
            await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var health) &&
                health.Generation > before.Generation && health.DisplayedPictures > 0,
                TimeSpan.FromSeconds(6), "Resume must display video from the selected live or replay input.");
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            Assert.Equal(caughtUp, tab.IsReplayMode);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(7));
            if (caughtUp)
            {
                Assert.True(engine.TryGetPlaybackClock(out var clock) &&
                    Math.Abs(clock.Position.TotalSeconds - held) < 5,
                    "The published DVR must resume near the held timestamp.");
                Console.WriteLine($"Live held {held:0.000} s and resumed replay video in {watch.ElapsedMilliseconds} ms.");
            }
            else
            {
                Assert.True(live.Requests.Count(request => request == "/index.m3u8") >= 2);
                Console.WriteLine($"Live resumed with video in {watch.ElapsedMilliseconds} ms while published DVR remained at 40 s.");
            }
        }
        finally
        {
            NativeWindowTest.DestroyWindow(handle);
        }
    });
}

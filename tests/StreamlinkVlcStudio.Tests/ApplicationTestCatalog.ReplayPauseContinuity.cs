internal static partial class ApplicationTestCatalog
{
    private static Task NativeReplayPauseGrowthAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-event");
        var complete = File.ReadAllText(Path.Combine(directory, "index.m3u8"));
        var lastSegment = complete.LastIndexOf("#EXTINF:", StringComparison.Ordinal);
        var initial = complete[..complete.LastIndexOf("#EXTINF:", lastSegment - 1, StringComparison.Ordinal)];
        await using var server = new ReplayFixtureServer(directory, initial);
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            using var engine = await new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings()).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: false);
            engine.SetVideoHandle(handle);
            // Leave room for the normal live buffer before both the initial and extended
            // ends. A ten-second append alone is retained as live buffer by VLC.
            await engine.PlayFromAsync(server.Uri, TimeSpan.FromSeconds(25), 0, PlaybackAudioState.Muted);
            Assert.True(engine.PreservesReplayPositionOnResume);
            Assert.True(engine.TryGetPlaybackClock(out var initialClock) && initialClock.Duration == TimeSpan.FromSeconds(40));
            await engine.PauseAsync();
            await Task.Delay(1000);
            server.UpdatePlaylist(complete);
            await engine.ResumeAsync();
            await TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var clock) && clock.Position.TotalSeconds > 42,
                TimeSpan.FromSeconds(22));
            Assert.True(engine.TryGetPlaybackClock(out var extended) && extended.Duration == TimeSpan.FromSeconds(60));
            Console.WriteLine($"Growing EVENT replay: initial end=40s, resumed position={extended.Position.TotalSeconds:0.000}s, new end={extended.Duration?.TotalSeconds:0}s.");
            Assert.True(server.Requests.Count(request => request == "/index.m3u8") >= 2);
            // A new live input must not inherit the prior replay's acknowledgement.
            await engine.PlayAsync(server.Uri, 0, PlaybackAudioState.Muted);
            Assert.Equal(false, engine.PreservesReplayPositionOnResume);
        }
        finally
        {
            NativeWindowTest.DestroyWindow(handle);
        }
    });

    private static Task NativeReplayPauseRetainsInputAsync(bool overlay = false) => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-event");
        await using var server = new ReplayFixtureServer(directory);
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            var target = StreamInputParser.Parse("streamer", PlatformKind.Twitch);
            var replay = new ReplaySessionInfo(PlatformKind.Twitch, "streamer", "https://www.twitch.tv/videos/123",
                "123", null, TimeSpan.FromSeconds(60), true, "");
            var streamlink = new FakeStreamlinkService
            {
                ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(server.Uri, "Local HLS event")),
                StartExternalHttpOverride = (_, _) => Task.FromResult<IStreamTransportSession>(new PauseClockTransport(server.Uri))
            };
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!
            };
            settings.Chat.Layout = overlay ? ChatLayout.Overlay : ChatLayout.Docked;
            settings.Chat.ConnectAutomatically = false;
            var logger = new MemoryLogger();
            await using var tab = TestViewModels.CreateTab(target, "best", streamlink,
                new LibVlcPlaybackEngineFactory(logger, settings.Chat), new FakeChatClientFactory(), logger,
                action => action(), initialVolume: 0, replayResolver: new FakeReplayResolver(replay),
                vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
            tab.SetVideoHandle(handle);
            await tab.StartAsync(settings);
            await TestWait.UntilAsync(() => tab.CanSeekReplay, TimeSpan.FromSeconds(5));
            await tab.SeekReplayAsync(TimeSpan.FromSeconds(35.25));
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var engine = (LibVlcPlaybackEngine)typeof(StreamTabViewModel).GetField("playbackEngine", flags)!.GetValue(tab)!;
            Assert.Equal(overlay, engine.UsesNativeOverlay);
            var generationField = typeof(LibVlcPlaybackEngine).GetField("playerGeneration", flags)!;
            for (var iteration = 0; iteration < 3; iteration++)
            {
                var hidden = iteration == 2;
                if (hidden) await tab.PauseForTabSwitchAsync();
                else await tab.PauseOrResumeAsync();
                await Task.Delay(iteration == 1 ? 7000 : 500);
                Assert.True(engine.TryGetPlaybackClock(out var held));
                var generation = (long)generationField.GetValue(engine)!;
                var watch = Stopwatch.StartNew();
                if (hidden) await tab.ResumeFromTabSwitchAsync();
                else await tab.PauseOrResumeAsync();
                Console.WriteLine($"HLS resume {iteration + 1}: command={watch.Elapsed.TotalMilliseconds:0}ms, generation {generation}->{generationField.GetValue(engine)}, held={held.Position.TotalSeconds:0.000}s.");
                Assert.Equal(generation, (long)generationField.GetValue(engine)!);
                Assert.Equal(PlaybackStatus.Playing, tab.Status);
                Assert.True(tab.IsBehindLive);
                await TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var clock) && clock.Position > held.Position,
                    TimeSpan.FromSeconds(2));
                Assert.True(engine.TryGetPlaybackClock(out var resumed));
                Assert.True((resumed.Position - held.Position).TotalSeconds is > 0 and < 2,
                    "Resume must advance the existing decoder from its held position, without jumping to the live edge.");
                InvokeReplayClockUpdate(tab);
                Assert.True(Math.Abs(tab.ReplaySeekValue - resumed.Position.TotalSeconds) < 1,
                    "The seekbar must follow the real decoder after an in-place resume.");
            }
        }
        finally
        {
            NativeWindowTest.DestroyWindow(handle);
        }
    });
}

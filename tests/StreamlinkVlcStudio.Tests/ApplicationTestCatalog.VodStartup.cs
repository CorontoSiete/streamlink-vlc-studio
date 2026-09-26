internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> VodStartupNativeTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_STARTUP_VOD_ID")) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ? [] :
        [
            ("VOD startup: provider resolution to presented output from beginning", () => MeasureVodStartupAsync(TimeSpan.Zero)),
            ("VOD startup: provider resolution to presented output at bookmark", () => MeasureVodStartupAsync(
                TimeSpan.TryParse(Environment.GetEnvironmentVariable("SVS_TEST_STARTUP_POSITION"),
                    System.Globalization.CultureInfo.InvariantCulture, out var position) ? position : TimeSpan.FromSeconds(601.217))),
            ("VOD startup: selected qualities match the installed Streamlink resolver", CompareVodQualitiesAsync)
        ];

    private static async Task CompareVodQualitiesAsync()
    {
        var target = StreamInputParser.Parse("https://www.twitch.tv/videos/" + Environment.GetEnvironmentVariable("SVS_TEST_STARTUP_VOD_ID"), PlatformKind.Twitch);
        var logger = new MemoryLogger();
        var direct = new StreamlinkService(logger);
        var original = new StreamlinkService(logger, null);
        foreach (var quality in new[] { "best", "720p60", "480p", "worst" })
        {
            var request = new StreamTransportRequest(target, quality,
                Environment.GetEnvironmentVariable("SVS_TEST_STREAMLINK_PATH") ?? @"C:\Program Files\Streamlink\bin\streamlink.exe", false, []);
            var expected = await original.ResolveStreamUrlAsync(request);
            var actual = await direct.ResolveStreamUrlAsync(request);
            Assert.Equal(expected.StreamUri, actual.StreamUri);
            Assert.True(actual.Message.Contains("directly"));
            Console.WriteLine($"Verified the same CDN playlist as Streamlink for {quality}.");
        }
    }

    private static Task MeasureVodStartupAsync(TimeSpan position) => TestSta.RunOffscreenAsync(async () =>
    {
        var target = StreamInputParser.Parse("https://www.twitch.tv/videos/" + Environment.GetEnvironmentVariable("SVS_TEST_STARTUP_VOD_ID"), PlatformKind.Twitch);
        var logger = new MemoryLogger();
        var service = Environment.GetEnvironmentVariable("SVS_TEST_STARTUP_STREAMLINK_ONLY") == "1"
            ? new StreamlinkService(logger, null) : new StreamlinkService(logger);
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            await using var gateway = new TwitchMutedVodPlaybackGateway(logger,
                Environment.GetEnvironmentVariable("SVS_TEST_STARTUP_FRESH_PLAYLIST") == "1" ? new TwitchVodPlaylistHandoff() : null);
            var timedGateway = new StartupTimedGateway(gateway);
            for (var trial = 1; trial <= 3; trial++)
            {
                var watch = Stopwatch.StartNew();
                var resolved = await service.ResolveStreamUrlAsync(new StreamTransportRequest(target, "best",
                    Environment.GetEnvironmentVariable("SVS_TEST_STREAMLINK_PATH") ?? @"C:\Program Files\Streamlink\bin\streamlink.exe", false, []));
                var resolveMs = watch.Elapsed.TotalMilliseconds;
                using var engine = await new LibVlcPlaybackEngineFactory(logger, new ChatSettings(), timedGateway).CreateAsync(
                    Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: true, rendererMode: VideoRendererMode.Gdi);
                engine.SetVideoHandle(handle);
                var createMs = watch.Elapsed.TotalMilliseconds - resolveMs;
                if (position > TimeSpan.Zero) await engine.PlayFromAsync(resolved.StreamUri, position, 0, PlaybackAudioState.Muted);
                else await engine.PlayAsync(resolved.StreamUri, 0, PlaybackAudioState.Muted);
                var confirmedMs = watch.Elapsed.TotalMilliseconds;
                engine.TryGetPlaybackHealth(out var beforeOutput);
                await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var health) && health.DisplayedPictures > beforeOutput.DisplayedPictures &&
                    health.PositionMilliseconds > position.TotalMilliseconds, TimeSpan.FromSeconds(15));
                var totalMs = watch.Elapsed.TotalMilliseconds;
                Assert.True(engine.TryGetPlaybackClock(out var clock) && clock.Duration > TimeSpan.Zero &&
                    clock.Position >= position && clock.Position < position + TimeSpan.FromSeconds(5));
                Console.WriteLine($"VOD startup trial={trial} position={position.TotalSeconds:0.###} resolve={resolveMs:0}ms create={createMs:0}ms prepare={timedGateway.Elapsed.TotalMilliseconds:0}ms confirmed={confirmedMs:0}ms first-output={totalMs:0}ms path={resolved.Message}");
                await ConfirmLongVodOutputAsync(engine, position, clock.Duration!.Value);
                Assert.True(engine.TryGetPlaybackHealth(out var sustainedStart));
                await Task.Delay(5000);
                Assert.True(engine.TryGetPlaybackHealth(out var sustainedEnd) &&
                    sustainedEnd.PositionMilliseconds >= sustainedStart.PositionMilliseconds + 3000 &&
                    sustainedEnd.DisplayedPictures > sustainedStart.DisplayedPictures &&
                    sustainedEnd.State == PlaybackEngineState.Playing, "Startup must lead to sustained playback.");
                Console.WriteLine($"Sustained 5s: clock advanced {sustainedEnd.PositionMilliseconds - sustainedStart.PositionMilliseconds}ms, displayed {sustainedEnd.DisplayedPictures - sustainedStart.DisplayedPictures} pictures.");
                var stopping = Stopwatch.StartNew();
                await engine.StopAsync();
                Assert.True(stopping.Elapsed < TimeSpan.FromSeconds(3), "Stopping must interrupt the input's HTTP reads.");
                Console.WriteLine($"Stop={stopping.Elapsed.TotalMilliseconds:0}ms");
            }
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });

    private sealed class StartupTimedGateway(IPlaybackMediaSourceGateway inner) : IPlaybackMediaSourceGateway
    {
        internal TimeSpan Elapsed { get; private set; }
        public async Task<PlaybackMediaSource> PrepareAsync(Uri mediaUri, Version? libVlcVersion, CancellationToken cancellationToken,
            bool preferFastReplay = false)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                return await inner.PrepareAsync(mediaUri, libVlcVersion, cancellationToken,
                    preferFastReplay && Environment.GetEnvironmentVariable("SVS_TEST_STARTUP_ADAPTIVE_ONLY") != "1");
            }
            finally { Elapsed = watch.Elapsed; }
        }
    }
}

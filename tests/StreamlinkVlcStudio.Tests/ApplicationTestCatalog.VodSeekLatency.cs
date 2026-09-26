internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> VodSeekLatencyTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ? [] :
        [
            ("VOD seek latency: delayed HLS opens without playing through preroll", () => NativeReplayFirstOutputAsync(true, delayedSegments: true)),
            ("VOD seek latency: initialized HLS seeks keep the input and confirm output", () => VodSeekLatencyAsync(false)),
            ("VOD seek latency: rebased HLS seeks keep the input and confirm output", () => VodSeekLatencyAsync(true)),
            .. string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_LONG_VOD_URI")) ? [] :
                new (string, Func<Task>)[] {
                    ("VOD seek latency: actual CDN media on a rebased timeline", () => VodSeekLatencyAsync(true, true)),
                    ("VOD seek latency: actual CDN media on the original timeline", () => VodSeekLatencyAsync(false, true))
                }
        ];

    private static Task VodSeekLatencyAsync(bool longVod, bool actual = false) => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-event");
        var template = File.ReadAllBytes(Path.Combine(directory, "index3.ts"));
        var playlist = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n#EXT-X-PLAYLIST-TYPE:VOD\n");
        for (var i = 0; i < 5060; i++) playlist.Append($"#EXTINF:10.000,\n{i}.ts\n");
        playlist.AppendLine("#EXT-X-ENDLIST");
        await using var server = new ReplayFixtureServer(directory, playlist.ToString(), key =>
            int.TryParse(Path.GetFileNameWithoutExtension(key), out var index) && index >= 0 && index < 5060
                ? ShiftTransportTimestamps(template, (index * 10L - 30) * 90000) : null);
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        var logger = new MemoryLogger();
        try
        {
            await using var gateway = new TwitchMutedVodPlaybackGateway(logger);
            using var engine = await new LibVlcPlaybackEngineFactory(logger, new ChatSettings(), gateway).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: actual,
                rendererMode: VideoRendererMode.Gdi);
            engine.SetVideoHandle(handle);
            var uri = actual ? new Uri(Environment.GetEnvironmentVariable("SVS_TEST_LONG_VOD_URI")!) : server.Uri;
            var start = longVod ? 49381.217 : actual ? 601.217 : 35.25;
            var duration = TimeSpan.FromSeconds(actual ? 50593.316 : 50600);
            var watch = Stopwatch.StartNew();
            await engine.PlayFromAsync(uri, TimeSpan.FromSeconds(start), 0, PlaybackAudioState.Muted);
            Console.WriteLine($"VOD open: {watch.Elapsed.TotalMilliseconds:0}ms at {start}s.");
            await ConfirmLongVodOutputAsync(engine, TimeSpan.FromSeconds(start), duration);
            Assert.True(engine.TryGetPlaybackHealth(out var initial));
            foreach (var target in new[] { start + 14, start + 124, start + 4 })
            {
                server.Requests.Clear();
                watch.Restart();
                await engine.SeekAsync(TimeSpan.FromSeconds(target));
                Assert.True(engine.TryGetPlaybackHealth(out var health));
                Console.WriteLine($"VOD seek: {watch.Elapsed.TotalMilliseconds:0}ms to {target}s; recreated={health.Generation != initial.Generation}; requests={string.Join(',', server.Requests.Take(10))}.");
                await ConfirmLongVodOutputAsync(engine, TimeSpan.FromSeconds(target), duration);
                Assert.Equal(initial.Generation, health.Generation);
                Assert.Equal(false, server.Requests.Contains("/index.m3u8"));
            }
            await engine.PauseAsync();
            await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var health) && health.State == PlaybackEngineState.Paused,
                TimeSpan.FromSeconds(2));
            await engine.SeekAsync(TimeSpan.FromSeconds(start + 24));
            Assert.True(engine.TryGetPlaybackHealth(out var paused));
            Assert.Equal(initial.Generation, paused.Generation);
            Assert.Equal(PlaybackEngineState.Paused, paused.State);
            await engine.ResumeAsync();
            await ConfirmLongVodOutputAsync(engine, TimeSpan.FromSeconds(start + 24), duration);
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });
}

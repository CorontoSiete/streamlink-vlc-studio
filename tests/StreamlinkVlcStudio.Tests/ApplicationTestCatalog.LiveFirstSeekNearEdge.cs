internal static partial class ApplicationTestCatalog
{
    private static Task PreparedReplayNearEdgeAsync(bool actual, bool prepared = true) => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-audio");
        await using var server = new ReplayFixtureServer(directory, segmentDelay: TimeSpan.FromMilliseconds(100));
        var uri = actual ? new Uri(Environment.GetEnvironmentVariable("SVS_TEST_LIVE_FIRST_SEEK_URI")!) : server.Uri;
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        var logger = new MemoryLogger();
        try
        {
            await using var gateway = new TwitchMutedVodPlaybackGateway(logger);
            using var engine = await new LibVlcPlaybackEngineFactory(logger, new ChatSettings(),
                actual ? gateway : new LiveReplayFixtureGateway()).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: false);
            engine.SetVideoHandle(handle);
            await engine.PlayAsync(new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-colors.mp4")),
                0, PlaybackAudioState.Muted);
            var native = IntPtr.Zero;
            if (prepared)
            {
                await engine.PrepareReplayAsync(uri);
                var input = GetPreparedReplayInput(engine);
                Assert.True(input is not null);
                native = GetPreparedReplayField<IntPtr>(input!, "Player");
                await Task.Delay(actual ? 12000 : 2000);
            }
            using var http = new HttpClient();
            var playlist = await http.GetStringAsync(uri);
            var seconds = playlist.Split('\n').Where(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                .Sum(line => double.Parse(line[8..].Split(',')[0], System.Globalization.CultureInfo.InvariantCulture));
            var target = TimeSpan.FromSeconds(seconds - 6.25);
            var watch = Stopwatch.StartNew();
            await engine.PlayFromAsync(uri, target, 0, PlaybackAudioState.Muted);
            Console.WriteLine($"Near-edge first skip: target={target.TotalSeconds:0.###}s confirmed={watch.ElapsedMilliseconds}ms.");
            if (prepared) Assert.Equal(native, (IntPtr)typeof(LibVlcPlaybackEngine).GetField("player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!);
            if (!actual) Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "Available media near the live edge must not wait for new segments or the seek timeout.");
            await ConfirmLongVodOutputAsync(engine, target, TimeSpan.FromSeconds(seconds));
        }
        finally
        {
            foreach (var entry in logger.Entries.Where(entry => entry.Source is "Replay" or "libVLC")) Console.WriteLine(entry.Message);
            NativeWindowTest.DestroyWindow(handle);
        }
    });

    private static Task LiveReplayContinuationAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-audio");
        var complete = File.ReadAllText(Path.Combine(directory, "index.m3u8"));
        var initial = complete[..complete.IndexOf("#EXTINF:", complete.IndexOf("index3.ts", StringComparison.Ordinal), StringComparison.Ordinal)];
        await using var server = new ReplayFixtureServer(directory, initial);
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            using var engine = await new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings(),
                new LiveReplayFixtureGateway()).CreateAsync(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: false);
            engine.SetVideoHandle(handle);
            await engine.PlayAsync(new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-colors.mp4")), 0, PlaybackAudioState.Muted);
            await engine.PrepareReplayAsync(server.Uri);
            await engine.PlayFromAsync(server.Uri, TimeSpan.FromSeconds(33.75), 0, PlaybackAudioState.Muted);
            await ConfirmLongVodOutputAsync(engine, TimeSpan.FromSeconds(33.75), TimeSpan.FromSeconds(40));
            Assert.True(engine.TryGetPlaybackHealth(out var original));
            // Exhaust the published media before appending more. A temporary live edge
            // must not become permanent EOF in either the demuxer or its source buffer.
            await Task.Delay(7000);
            Assert.True(engine.TryGetPlaybackHealth(out var waiting) && waiting.State == PlaybackEngineState.Playing);
            server.UpdatePlaylist(complete);
            await TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var clock) && clock.Position > TimeSpan.FromSeconds(41),
                TimeSpan.FromSeconds(22), "The same player must continue when new segments arrive after the live edge.");
            Assert.True(engine.TryGetPlaybackHealth(out var continued) && continued.Generation == original.Generation &&
                continued.DisplayedPictures > original.DisplayedPictures);
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });

    // Local fixture playlists have the same TS/EVENT format as the validated Twitch
    // sources. Gateway/policy tests separately cover production source selection.
    private sealed class LiveReplayFixtureGateway : IPlaybackMediaSourceGateway
    {
        public Task<PlaybackMediaSource> PrepareAsync(Uri mediaUri, Version? libVlcVersion,
            CancellationToken cancellationToken, bool preferFastReplay = false) =>
            Task.FromResult(new PlaybackMediaSource(mediaUri, null,
                useLiveReplayDemuxer: HlsReplayTimeline.IsPlaylist(mediaUri),
                liveReplaySegmentDuration: TimeSpan.FromSeconds(10)));
    }
}

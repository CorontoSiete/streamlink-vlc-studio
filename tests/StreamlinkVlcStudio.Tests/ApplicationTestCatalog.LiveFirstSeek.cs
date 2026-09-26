internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> LiveFirstSeekTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ? [] :
        [
            ("live first seek: growing HLS startup and subsequent seeks", () => LiveFirstSeekAsync(false)),
            ("live first seek: prepared input presents the requested pixels", PreparedReplayFirstPixelsAsync),
            ("live first seek: cancellation replacement and stop release preparation", PreparedReplayLifetimeAsync),
            ("live first seek: tab startup automatically prepares and adopts replay", PreparedReplayTabAsync),
            ("live first seek: paused preparation follows a growing broadcast", PreparedReplayGrowthAsync),
            ("live first seek: activation stays silent until ready then applies changed volume", () => PreparedReplayAudioAsync(false)),
            ("live first seek: activation respects a new mute while seeking", () => PreparedReplayAudioAsync(true)),
            ("live first seek: moving the video surface retains prepared replay", PreparedReplayRebindAsync),
            .. string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_LIVE_FIRST_SEEK_URI")) ? [] :
                new (string, Func<Task>)[] { ("live first seek: actual broadcast startup and subsequent seeks", () => LiveFirstSeekAsync(true)) }
        ];

    private static Task LiveFirstSeekAsync(bool actual) => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-audio");
        await using var server = new ReplayFixtureServer(directory, segmentDelay: TimeSpan.FromMilliseconds(200));
        var uri = actual ? new Uri(Environment.GetEnvironmentVariable("SVS_TEST_LIVE_FIRST_SEEK_URI")!) : server.Uri;
        var position = TimeSpan.FromSeconds(actual ? 601.217 : 35.25);
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        var logger = new MemoryLogger();
        try
        {
            await using var gateway = new TwitchMutedVodPlaybackGateway(logger);
            using var engine = await new LibVlcPlaybackEngineFactory(logger, new ChatSettings(), gateway).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: true,
                rendererMode: VideoRendererMode.Gdi);
            engine.SetVideoHandle(handle);
            for (var trial = 0; trial < 3; trial++)
            {
                // Exercise a live-to-replay replacement, not just an empty player's open.
                await engine.PlayAsync(new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-colors.mp4")), 0, PlaybackAudioState.Muted);
                await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var health) && health.DisplayedPictures > 0,
                    TimeSpan.FromSeconds(5));
                Assert.True(engine.TryGetPlaybackHealth(out var live));
                await engine.PrepareReplayAsync(uri);
                var prepared = GetPreparedReplayInput(engine);
                Assert.True(prepared is not null, "This fixture must prepare successfully, not fall back to a cold open.");
                var preparedPlayer = GetPreparedReplayField<IntPtr>(prepared!, "Player");
                var preparedMedia = GetPreparedReplayField<IntPtr>(prepared!, "Media");
                Assert.Equal(LibVlcNative.MediaPlayerState.Paused, LibVlcNative.libvlc_media_player_get_state(preparedPlayer));
                Assert.Equal(0, GetReplayNativeVolume(preparedPlayer));
                Assert.True(LibVlcNative.libvlc_media_get_stats(preparedMedia, out var preparedStats) != 0);
                Assert.Equal(0, preparedStats.DecodedVideo);
                Assert.Equal(0, preparedStats.DisplayedPictures);
                // Health reads intentionally fail rather than block on the audio worker's
                // native lock, and VLC publishes picture statistics periodically.
                await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var stillLive) &&
                    stillLive.Generation == live.Generation && stillLive.DisplayedPictures > live.DisplayedPictures,
                    TimeSpan.FromSeconds(2), "Preparing replay must leave the original live video advancing.");
                var watch = Stopwatch.StartNew();
                server.Requests.Clear();
                await engine.PlayFromAsync(uri, position, 0, PlaybackAudioState.Muted);
                var confirmed = watch.ElapsedMilliseconds;
                Assert.Equal(preparedPlayer, (IntPtr)typeof(LibVlcPlaybackEngine).GetField("player",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!);
                Assert.True(engine.TryGetPlaybackClock(out var opened));
                await ConfirmLongVodOutputAsync(engine, position, opened.Duration!.Value);
                Console.WriteLine($"Live first seek trial {trial + 1}: confirmed={confirmed}ms output={watch.ElapsedMilliseconds}ms duration={opened.Duration}.");
                Assert.True(engine.TryGetPlaybackHealth(out var initial));
                foreach (var offset in new[] { 10, -10 })
                {
                    watch.Restart();
                    await engine.SeekAsync(position + TimeSpan.FromSeconds(offset));
                    await ConfirmLongVodOutputAsync(engine, position + TimeSpan.FromSeconds(offset), opened.Duration!.Value);
                    Console.WriteLine($"Subsequent seek {offset}s: output={watch.ElapsedMilliseconds}ms.");
                    Assert.True(engine.TryGetPlaybackHealth(out var current) && current.Generation == initial.Generation);
                }
                await engine.PauseAsync();
                await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var health) && health.State == PlaybackEngineState.Paused,
                    TimeSpan.FromSeconds(2));
                Assert.True(await engine.TryResumeReplayAsync());
            }
        }
        finally
        {
            foreach (var entry in logger.Entries.Where(entry => entry.Source == "Replay")) Console.WriteLine(entry.Message);
            NativeWindowTest.DestroyWindow(handle);
        }
    });

    private static object? GetPreparedReplayInput(IPlaybackEngine engine)
    {
        var preparation = typeof(LibVlcPlaybackEngine).GetField("replayPreparation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine);
        if (preparation is null) return null;
        var task = (Task)preparation.GetType().GetProperty("Task")!.GetValue(preparation)!;
        return task.IsCompletedSuccessfully ? task.GetType().GetProperty("Result")!.GetValue(task) : null;
    }

    private static T GetPreparedReplayField<T>(object input, string field) =>
        (T)input.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(input)!;
}

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> ReplayPlaybackRateRecoveryTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ? [] :
        [
            ("replay playback speed remains changeable after ten seconds at 2x in a completed VOD",
                () => CompletedVodDelayedPlaybackRateAsync(muted: false)),
            ("replay playback speed remains changeable after ten seconds at 2x in a muted VOD",
                () => CompletedVodDelayedPlaybackRateAsync(muted: true)),
            ("replay playback speed remains changeable after ten seconds at 2x in a growing replay",
                GrowingReplayDelayedPlaybackRateAsync),
            .. string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_PLAYBACK_RATE_VOD_URI")) ? [] :
                new (string, Func<Task>)[] {
                    ("replay playback speed remains changeable on the reported xqc VOD",
                        ActualVodDelayedPlaybackRateAsync) }
        ];

    private static Task CompletedVodDelayedPlaybackRateAsync(bool muted) => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new FastVodFixture(muted, withAudio: true);
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            using var engine = await new LibVlcPlaybackEngineFactory(fixture.Logger, new ChatSettings(), fixture.Gateway)
                .CreateAsync(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!,
                    enableNativeOverlay: true, rendererMode: VideoRendererMode.Gdi);
            Assert.True(engine.UsesNativeOverlay);
            engine.SetVideoHandle(handle);
            await engine.PlayFromAsync(FastVodFixture.MediaUri, TimeSpan.FromSeconds(5.25), 0, PlaybackAudioState.Muted);
            var source = (PlaybackMediaSource)typeof(LibVlcPlaybackEngine).GetField("currentMediaSource",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
            Assert.True(source.UseAvformatDemuxer, "The completed VOD must use the production FFmpeg transport.");
            await ConfirmLongVodOutputAsync(engine, TimeSpan.FromSeconds(5.25), TimeSpan.FromSeconds(60));
            await VerifyDelayedPlaybackRateAsync(engine, fixture.Logger);
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });

    private static Task GrowingReplayDelayedPlaybackRateAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var server = new ReplayFixtureServer(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-audio"));
        var logger = new MemoryLogger();
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            using var engine = await new LibVlcPlaybackEngineFactory(logger, new ChatSettings(), new LiveReplayFixtureGateway())
                .CreateAsync(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!,
                    enableNativeOverlay: false, rendererMode: VideoRendererMode.Gdi);
            engine.SetVideoHandle(handle);
            await engine.PlayFromAsync(server.Uri, TimeSpan.FromSeconds(5.25), 0, PlaybackAudioState.Muted);
            await ConfirmLongVodOutputAsync(engine, TimeSpan.FromSeconds(5.25), TimeSpan.FromSeconds(60));
            await VerifyDelayedPlaybackRateAsync(engine, logger);
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });

    private static Task ActualVodDelayedPlaybackRateAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var uri = new Uri(Environment.GetEnvironmentVariable("SVS_TEST_PLAYBACK_RATE_VOD_URI")!);
        var position = TimeSpan.FromSeconds(double.Parse(
            Environment.GetEnvironmentVariable("SVS_TEST_PLAYBACK_RATE_VOD_POSITION") ?? "13792.994",
            CultureInfo.InvariantCulture));
        var logger = new MemoryLogger();
        await using var gateway = new TwitchMutedVodPlaybackGateway(logger);
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            using var engine = await new LibVlcPlaybackEngineFactory(logger, new ChatSettings(), gateway)
                .CreateAsync(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!,
                    enableNativeOverlay: true, rendererMode: VideoRendererMode.Gdi);
            engine.SetVideoHandle(handle);
            await engine.PlayFromAsync(uri, position, 0, PlaybackAudioState.Muted);
            await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var health) &&
                health.State == PlaybackEngineState.Playing && health.DisplayedPictures > 0 &&
                Math.Abs(health.PositionMilliseconds - position.TotalMilliseconds) < 3_000,
                TimeSpan.FromSeconds(10), "The actual VOD must present video at the reported position.");
            await VerifyDelayedPlaybackRateAsync(engine, logger);
            foreach (var rate in new[] { 0.5f, 0.75f, 1.25f, 1.5f, 1.75f, 2f, 1f })
            {
                Assert.True(engine.TryGetPlaybackHealth(out var before));
                Assert.True(await engine.TrySetPlaybackRateAsync(rate), $"The actual VOD rejected {rate:0.##}x.");
                await WaitForRateChangeVideoAsync(engine, before);
                await VerifyMeasuredPlaybackRateAsync(engine, rate);
            }
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });

    private static async Task VerifyDelayedPlaybackRateAsync(IPlaybackEngine engine, MemoryLogger logger)
    {
        try
        {
            Assert.True(engine.TryGetPlaybackHealth(out var initial));
            Assert.True(await engine.TrySetPlaybackRateAsync(2f));
            Assert.True(IsPlaybackRateRecoveryPending(engine), "The delayed request must exercise an actual HLS audio resynchronization.");
            await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var health) &&
                health.State == PlaybackEngineState.Playing &&
                health.PositionMilliseconds >= initial.PositionMilliseconds + 12_000 &&
                health.DisplayedPictures > initial.DisplayedPictures + 10,
                TimeSpan.FromSeconds(15), "The real decoder must play more than ten seconds at 2x before the next request.");
            Assert.True(engine.TryGetPlaybackHealth(out var before));
            Console.WriteLine($"Delayed speed request: initialMs={initial.PositionMilliseconds}, " +
                $"currentMs={before.PositionMilliseconds}, pictures={before.DisplayedPictures}.");
            var watch = Stopwatch.StartNew();
            var applied = await engine.TrySetPlaybackRateAsync(1f);
            Console.WriteLine($"Delayed 2x to 1x: applied={applied}, elapsedMs={watch.ElapsedMilliseconds}.");
            Assert.True(applied, "A recovered playback clock must remain eligible after more than ten seconds of playback.");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), "Recovered playback must not wait for the five-second timeout.");
            await WaitForRateChangeVideoAsync(engine, before);
            await VerifyMeasuredPlaybackRateAsync(engine, 1f);
            Assert.Equal(false, logger.Entries.Any(entry => entry.Message.Contains("previous HLS position to recover and timed out")));
        }
        finally
        {
            foreach (var entry in logger.Entries.Where(entry => entry.Level >= AppLogLevel.Warning))
                Console.WriteLine($"{entry.Source}: {entry.Message}");
        }
    }

    private static async Task WaitForRateChangeVideoAsync(IPlaybackEngine engine, PlaybackHealth before)
    {
        await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var health) &&
            health.State == PlaybackEngineState.Playing && health.Generation == before.Generation &&
            health.PositionMilliseconds >= before.PositionMilliseconds + 250 &&
            health.DisplayedPictures > before.DisplayedPictures,
            TimeSpan.FromSeconds(10), "Changing speed must keep the decoder presenting video at the held position.");
        await Task.Delay(500);
    }

    private static async Task VerifyMeasuredPlaybackRateAsync(IPlaybackEngine engine, float rate, TimeSpan? measurementDuration = null)
    {
        Assert.True(engine.TryGetPlaybackHealth(out var before));
        var watch = Stopwatch.StartNew();
        var samples = new List<(double WallMilliseconds, long PositionMilliseconds)>
        {
            (0, before.PositionMilliseconds)
        };
        // VLC publishes its clock in steps. Fit several seconds of real samples
        // so one update at either endpoint cannot hide a stuck neighboring rate.
        while (watch.Elapsed < (measurementDuration ?? TimeSpan.FromSeconds(5)))
        {
            await Task.Delay(100);
            if (engine.TryGetPlaybackHealth(out var sample))
            {
                Assert.Equal(before.Generation, sample.Generation);
                samples.Add((watch.Elapsed.TotalMilliseconds, sample.PositionMilliseconds));
            }
        }
        Assert.True(engine.TryGetPlaybackHealth(out var after));
        Assert.True(samples.Count >= 20, "Measuring speed requires enough native clock samples.");
        var meanWall = samples.Average(sample => sample.WallMilliseconds);
        var meanPosition = samples.Average(sample => (double)sample.PositionMilliseconds);
        var measuredRate = samples.Sum(sample => (sample.WallMilliseconds - meanWall) * (sample.PositionMilliseconds - meanPosition)) /
            samples.Sum(sample => Math.Pow(sample.WallMilliseconds - meanWall, 2));
        Console.WriteLine($"Measured playback: requested={rate:0.##}x, actual={measuredRate:0.###}x, " +
            $"positionMs={after.PositionMilliseconds}, pictures={after.DisplayedPictures}, " +
            $"decoded video={before.DecodedVideo}->{after.DecodedVideo}.");
        Assert.True(Math.Abs(measuredRate - rate) < 0.12,
            $"Requested {rate:0.##}x but the decoder clock advanced at {measuredRate:0.###}x.");
        Assert.Equal(before.Generation, after.Generation);
        Assert.True(after.DisplayedPictures > before.DisplayedPictures);
        Assert.True(after.DecodedVideo > before.DecodedVideo,
            "The native decoder must produce new video frames while its media clock advances.");
    }
}

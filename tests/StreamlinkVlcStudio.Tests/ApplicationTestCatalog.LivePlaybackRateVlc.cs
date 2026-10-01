
internal static partial class ApplicationTestCatalog
{
    private static Task LiveReplayNearEdgePlaybackRateAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-audio");
        await using var server = new ReplayFixtureServer(directory);
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            using var engine = await new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings(),
                new LiveReplayFixtureGateway()).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: false);
            engine.SetVideoHandle(handle);
            await engine.PlayAsync(new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-colors.mp4")),
                0, PlaybackAudioState.Muted);
            await engine.PrepareReplayAsync(server.Uri);
            await engine.PlayFromAsync(server.Uri, TimeSpan.FromSeconds(53.75), 0, PlaybackAudioState.Muted);
            await ConfirmLongVodOutputAsync(engine, TimeSpan.FromSeconds(53.75), TimeSpan.FromSeconds(60));
            Assert.True(await engine.TrySetPlaybackRateAsync(1.5f));
            var watch = Stopwatch.StartNew();
            Assert.True(await engine.TrySetPlaybackRateAsync(2f));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4),
                "A stalled published edge must not block the next speed selection.");
            Assert.True(await engine.TrySetPlaybackRateAsync(1f));
            await Task.Delay(750);
            Assert.True(engine.TryGetPlaybackHealth(out var health));
            Assert.True(health.PositionMilliseconds >= 50_000,
                $"Speed changes near the published edge moved replay to {health.PositionMilliseconds / 1000d:0.###}s.");
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });

    private static Task LiveReplayPlaybackRateAsync(bool actual) => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-audio");
        await using var server = new ReplayFixtureServer(directory);
        var uri = actual ? new Uri(Environment.GetEnvironmentVariable("SVS_TEST_LIVE_FIRST_SEEK_URI")!) : server.Uri;
        var duration = actual
            ? await HlsReplayTimeline.ReadPublishedDurationAsync(uri, PlatformKind.Twitch, CancellationToken.None)
            : TimeSpan.FromSeconds(60);
        Assert.True(duration is { } published && published > TimeSpan.FromMinutes(actual ? 5 : 0));
        var publishedDuration = duration ?? throw new InvalidOperationException("The replay duration is unavailable.");
        var position = actual ? publishedDuration - TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(35.25);
        var minimum = position - TimeSpan.FromSeconds(3);
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        var logger = new MemoryLogger();
        var frameMemory = IntPtr.Zero;
        LibVlcNative.PreviewLockCallback? lockFrame = null;
        LibVlcNative.PreviewUnlockCallback? unlockFrame = null;
        ReplayVideoDisplayCallback? displayFrame = null;
        var displayedColors = new ConcurrentQueue<(byte B, byte G, byte R)>();
        try
        {
            await using var gateway = new TwitchMutedVodPlaybackGateway(logger);
            using var engine = await new LibVlcPlaybackEngineFactory(logger, new ChatSettings(),
                actual ? gateway : new LiveReplayFixtureGateway()).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: false,
                rendererMode: VideoRendererMode.Gdi);
            engine.SetVideoHandle(handle);
            await engine.PlayAsync(new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-colors.mp4")),
                0, PlaybackAudioState.Muted);
            await engine.PrepareReplayAsync(uri);
            if (!actual)
            {
                var prepared = GetPreparedReplayInput(engine);
                Assert.True(prepared is not null);
                var native = GetPreparedReplayField<IntPtr>(prepared!, "Player");
                frameMemory = Marshal.AllocHGlobal(64 * 64 * 4 + 31);
                var pixels = new IntPtr((frameMemory.ToInt64() + 31) & ~31L);
                lockFrame = (_, planes) => { Marshal.WriteIntPtr(planes, pixels); return IntPtr.Zero; };
                unlockFrame = (_, _, _) => { };
                displayFrame = (_, _) => displayedColors.Enqueue((
                    Marshal.ReadByte(pixels), Marshal.ReadByte(pixels, 1), Marshal.ReadByte(pixels, 2)));
                LibVlcNative.libvlc_video_set_callbacks(native, lockFrame, unlockFrame,
                    Marshal.GetFunctionPointerForDelegate(displayFrame), IntPtr.Zero);
                LibVlcNative.libvlc_video_set_format(native, "RV32", 64, 64, 64 * 4);
            }
            await engine.PlayFromAsync(uri, position, 0, PlaybackAudioState.Muted);
            await ConfirmLongVodOutputAsync(engine, position, publishedDuration);
            Assert.True(engine.TryGetPlaybackHealth(out var initial));
            displayedColors.Clear();
            Console.WriteLine($"Rate replay source: actual={actual}, target={position.TotalSeconds:0.###}, duration={publishedDuration.TotalSeconds:0.###}, generation={initial.Generation}.");
            Assert.True(await engine.TrySetPlaybackRateAsync(1.5f));
            var resetWatch = Stopwatch.StartNew();
            var observedTransientReset = false;
            while (resetWatch.Elapsed < TimeSpan.FromMilliseconds(actual ? 750 : 2000))
            {
                if (engine.TryGetPlaybackHealth(out var health) &&
                    health.PositionMilliseconds < minimum.TotalMilliseconds)
                {
                    observedTransientReset = true;
                    break;
                }

                await Task.Delay(20);
            }
            Console.WriteLine($"Rate replay transient zero observed: {observedTransientReset}.");
            Assert.True(await engine.TrySetPlaybackRateAsync(2f));
            await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var health) &&
                health.PositionMilliseconds >= minimum.TotalMilliseconds &&
                health.DisplayedPictures > initial.DisplayedPictures,
                TimeSpan.FromSeconds(5), "A second rate change during VLC's zero clock must return to the held replay position.");

            var laterRates = actual ? new[] { 1.75f, 0.75f, 1.5f, 1f, 2f, 0.5f, 1f }
                : new[] { 1.75f, 0.75f, 1f };
            foreach (var rate in laterRates)
            {
                Assert.True(await engine.TrySetPlaybackRateAsync(rate), $"VLC rejected {rate:0.##}x.");
                await Task.Delay(75);
            }
            await Task.Delay(1500);
            await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var health) &&
                health.PositionMilliseconds >= minimum.TotalMilliseconds &&
                health.DisplayedPictures > initial.DisplayedPictures + 10,
                TimeSpan.FromSeconds(5), "Rapid rate changes must leave live replay advancing at the sought position.");
            Assert.True(engine.TryGetPlaybackHealth(out var final));
            Assert.Equal(initial.Generation, final.Generation);
            Assert.True(final.PositionMilliseconds >= minimum.TotalMilliseconds);
            if (!actual)
            {
                var colors = displayedColors.ToArray();
                Assert.True(colors.Count(frame => frame.G > 200 || frame.B > 200) >= 10,
                    "Rate changes must keep presenting replay content.");
                Assert.True(!colors.Any(frame => frame.R > 200 && frame.G < 30 && frame.B < 30),
                    "Rate changes must never present the red VOD beginning.");
            }
            Console.WriteLine($"Rate replay final: position={final.PositionMilliseconds / 1000d:0.###}s, displayed={final.DisplayedPictures}.");
        }
        finally
        {
            GC.KeepAlive(lockFrame);
            GC.KeepAlive(unlockFrame);
            GC.KeepAlive(displayFrame);
            if (frameMemory != IntPtr.Zero) Marshal.FreeHGlobal(frameMemory);
            foreach (var entry in logger.Entries.Where(entry => entry.Level >= AppLogLevel.Warning))
                Console.WriteLine($"{entry.Source}: {entry.Message}");
            NativeWindowTest.DestroyWindow(handle);
        }
    });
}

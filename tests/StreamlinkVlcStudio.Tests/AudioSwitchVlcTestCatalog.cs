internal static class AudioSwitchVlcTestCatalog
{
    // The software audio-memory output supplies real, volume-scaled PCM without
    // making sound. These opt-in tests require the installed native VLC decoder.
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY"))
            ? []
            :
            [
                ("native VLC automatic mute preserves silent PCM and avoids decoder restarts", PreserveDecoderAsync),
                ("native VLC starts background playback silently with its audio decoder ready", StartMutedAsync),
                ("native VLC rapid switching leaves only the selected player audible", RapidSwitchAsync),
                ("native VLC playback rate transition keeps PCM output continuous", PlaybackRatePcmAsync),
                .. AudioOutputTests
            ];

    private static IReadOnlyList<(string Name, Func<Task> Run)> AudioOutputTests =>
        string.Equals(Environment.GetEnvironmentVariable("SVS_TEST_AUDIO_OUTPUT"), "true", StringComparison.OrdinalIgnoreCase)
            ?
            [
                ("native VLC Windows audio output isolates mute and volume between players", NativeOutputIsolationAsync),
                ("native VLC HLS playback rate transition preserves Windows loopback audio", NativeHlsOutputRateChangeAsync),
                ("native VLC HTTP HLS playback rate transition preserves Windows loopback audio", NativeHttpHlsOutputRateChangeAsync),
                ("native VLC completed replay rate transition preserves Windows loopback audio", NativeCompletedReplayOutputRateChangeAsync)
            ]
            : [];

    private static async Task NativeOutputIsolationAsync()
    {
        // This extra opt-in opens the actual Windows output device. The fixture's
        // amplitude is below -54 dBFS so it does not play normal-volume test tones.
        using var first = await PcmFixture.CreateAsync(PlaybackAudioState.Audible, capturePcm: false, amplitude: 64);
        using var second = await PcmFixture.CreateAsync(PlaybackAudioState.Muted, capturePcm: false, amplitude: 64);
        await TestWait.UntilAsync(() => first.Track >= 0 && second.Track >= 0, TimeSpan.FromSeconds(5));
        await Task.Delay(500);
        AssertNativeOutputState(first, muted: false, volume: 80);
        AssertNativeOutputState(second, muted: true, volume: 0);

        first.Engine.SetAudioState(80, PlaybackAudioState.Muted);
        second.Engine.SetAudioState(80, PlaybackAudioState.Audible);
        await Task.Delay(500);
        AssertNativeOutputState(first, muted: true, volume: 0);
        AssertNativeOutputState(second, muted: false, volume: 80);

        // Test output restarts on the production backend: VLC's amem test output
        // does not reapply its software gain in Start(), whereas DirectSound does.
        first.Engine.SetAudioState(80, PlaybackAudioState.HardMuted);
        await TestWait.UntilAsync(() => first.Track < 0, TimeSpan.FromSeconds(2));
        await Task.Delay(300);
        AssertNativeOutputState(second, muted: false, volume: 80);
        first.Engine.SetAudioState(80, PlaybackAudioState.Muted);
        await TestWait.UntilAsync(() => first.Track >= 0, TimeSpan.FromSeconds(2));
        await Task.Delay(300);
        AssertNativeOutputState(first, muted: true, volume: 0);
        AssertNativeOutputState(second, muted: false, volume: 80);
    }

    private static void AssertNativeOutputState(PcmFixture fixture, bool muted, int volume)
    {
        Assert.Equal(muted ? 1 : 0, fixture.NativeMute);
        Assert.Equal(volume, fixture.NativeVolume);
    }

    private static readonly (float InitialRate, float TargetRate)[] PlaybackRateTransitions =
    [
        (1f, 0.5f),
        (1f, 0.75f),
        (1f, 1.25f),
        (1f, 1.5f),
        (1f, 1.75f),
        (1f, 2f),
        (0.5f, 1f),
        (1.5f, 1f),
        (0.5f, 1.5f),
        (1.5f, 0.5f)
    ];

    private const double MaximumRateChangeLowOutputRunMilliseconds = 100;

    private static async Task NativeHlsOutputRateChangeAsync()
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "playback-rate-tone");
        var playlistPath = Path.Combine(
            fixtureDirectory, "index.m3u8");
        Assert.True(File.Exists(playlistPath), $"Bundled HLS playback-rate fixture is missing: {playlistPath}");
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"svs-rate-hls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryPlaylistPath = Path.Combine(temporaryDirectory, "index.m3u8");
        var segmentUri = new Uri(Path.Combine(fixtureDirectory, "tone.ts")).AbsoluteUri;
        File.WriteAllLines(temporaryPlaylistPath, File.ReadAllLines(playlistPath).Select(line =>
            string.Equals(line, "tone.ts", StringComparison.Ordinal) ? segmentUri : line));

        try
        {
            await AssertRateTransitionsPreserveOutputAsync(
                "HLS VOD",
                () => PcmFixture.CreateAsync(
                    PlaybackAudioState.Audible, capturePcm: false, mediaPath: temporaryPlaylistPath));
        }
        finally
        {
            File.Delete(temporaryPlaylistPath);
            Directory.Delete(temporaryDirectory);
        }
    }

    private static async Task NativeHttpHlsOutputRateChangeAsync()
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "playback-rate-tone");
        Assert.True(File.Exists(Path.Combine(fixtureDirectory, "index.m3u8")),
            $"Bundled HLS playback-rate fixture is missing: {fixtureDirectory}");
        using var source = LocalHlsHttpServer.Start(fixtureDirectory);
        await AssertRateTransitionsPreserveOutputAsync(
            "HTTP HLS VOD",
            () => PcmFixture.CreateAsync(
                PlaybackAudioState.Audible, capturePcm: false, mediaUri: source.MediaUri));
    }

    private static async Task AssertRateTransitionsPreserveOutputAsync(
        string inputDescription,
        Func<Task<PcmFixture>> createFixture,
        double maximumRequestMilliseconds = 500,
        TimeSpan? observationDuration = null,
        IReadOnlyList<(float InitialRate, float TargetRate)>? rateTransitions = null)
    {
        using var capture = await WasapiLoopbackCapture.StartAsync();
        var outputFailures = new List<string>();
        foreach (var (initialRate, targetRate) in rateTransitions ?? PlaybackRateTransitions)
        {
            var fixtureStarted = Stopwatch.GetTimestamp();
            using var fixture = await createFixture();
            await TestWait.UntilAsync(
                () => fixture.PlaybackTimeMs > 0 &&
                    capture.GetPacketsSince(fixtureStarted).Any(packet => packet.ToneMagnitude > 0.0001),
                TimeSpan.FromSeconds(8));

            Assert.True(fixture.PlaybackLengthMs > 0,
                $"{inputDescription} must report a finite duration before audio resynchronization can be tested.");
            Assert.True(fixture.IsSeekable,
                $"{inputDescription} must be seekable before audio resynchronization can be tested.");

            if (initialRate != 1f)
            {
                Assert.True(await fixture.Engine.TrySetPlaybackRateAsync(initialRate),
                    $"VLC must accept {inputDescription} initial rate {initialRate:0.##}x.");
                await Task.Delay(500);
            }
            else
            {
                await Task.Delay(250);
            }

            var baselineStart = Stopwatch.GetTimestamp();
            var baseline = capture.GetPacketsBefore(baselineStart, TimeSpan.FromMilliseconds(250));
            Assert.True(baseline.Length >= 10,
                $"Expected Windows loopback PCM before {inputDescription} {initialRate:0.##}x->{targetRate:0.##}x; observed {baseline.Length} packets.");
            var baselineTone = baseline.Select(packet => packet.ToneMagnitude).Order().ElementAt(baseline.Length / 2);
            var baselineRms = baseline.Select(packet => packet.Rms).Order().ElementAt(baseline.Length / 2);
            Assert.True(baselineTone > 0.0001 && baselineRms > 0.0001,
                $"Expected a stable {inputDescription} tone before {initialRate:0.##}x->{targetRate:0.##}x; tone={baselineTone:0.000000}, RMS={baselineRms:0.000000}.");

            var changeStarted = Stopwatch.GetTimestamp();
            Assert.True(await fixture.Engine.TrySetPlaybackRateAsync(targetRate),
                $"VLC must accept {inputDescription} rate {initialRate:0.##}x->{targetRate:0.##}x.");
            var rateRequestMs = Stopwatch.GetElapsedTime(changeStarted).TotalMilliseconds;
            Assert.True(rateRequestMs <= maximumRequestMilliseconds,
                $"{inputDescription} rate request {initialRate:0.##}x->{targetRate:0.##}x took {rateRequestMs:0.0} ms; maximum is {maximumRequestMilliseconds:0} ms.");
            var outputObservation = observationDuration ?? TimeSpan.FromSeconds(1);
            await Task.Delay(outputObservation);
            capture.EnsureHealthy();

            var packets = capture.GetPacketsSince(changeStarted);
            var minimumPackets = (int)(outputObservation.TotalMilliseconds * 0.08);
            Assert.True(packets.Length >= minimumPackets,
                $"Expected at least {minimumPackets * 10} ms of Windows loopback samples for {inputDescription} {initialRate:0.##}x->{targetRate:0.##}x; observed {packets.Length} packets.");
            var longestLowRunMs = GetLongestLowOutputRunMilliseconds(packets, baselineRms);
            var firstAudio = packets.FirstOrDefault(packet => packet.Rms >= baselineRms * 0.5);
            var recoveryMs = firstAudio.Rms >= baselineRms * 0.5
                ? (firstAudio.Timestamp - changeStarted) * 1000d / Stopwatch.Frequency
                : double.PositiveInfinity;

            Console.WriteLine(
                $"Windows loopback {inputDescription} {initialRate:0.##}x->{targetRate:0.##}x: " +
                $"request={rateRequestMs:0.0} ms, first audio={recoveryMs:0.0} ms, " +
                $"longest low-output run={longestLowRunMs:0.0} ms, packets={packets.Length}, " +
                $"RMS before={baselineRms:0.000000}, after median={packets.Select(packet => packet.Rms).Order().ElementAt(packets.Length / 2):0.000000}, " +
                $"tone before={baselineTone:0.000000}, after median={packets.Select(packet => packet.ToneMagnitude).Order().ElementAt(packets.Length / 2):0.000000}, " +
                $"silent packets={packets.Count(packet => packet.IsSilent)}, mute={fixture.NativeMute}, volume={fixture.NativeVolume}.");
            if (Environment.GetEnvironmentVariable("SVS_TEST_VLC_LOG_DIRECTORY") is { Length: > 0 } captureDirectory)
            {
                var filename = FormattableString.Invariant($"windows-rate-{inputDescription.Replace(' ', '-')}-{initialRate:0.##}-{targetRate:0.##}.csv");
                File.WriteAllLines(Path.Combine(captureDirectory, filename),
                    new[] { "elapsed_ms,frames,sample_rate,rms,tone_magnitude,silent" }.Concat(packets.Select(packet =>
                        FormattableString.Invariant($"{(packet.Timestamp - changeStarted) * 1000d / Stopwatch.Frequency:0.000},{packet.Frames},{packet.SampleRate},{packet.Rms:R},{packet.ToneMagnitude:R},{packet.IsSilent}"))));
            }
            if (!double.IsFinite(recoveryMs) || recoveryMs > MaximumRateChangeLowOutputRunMilliseconds)
                outputFailures.Add($"{inputDescription} rate change {initialRate:0.##}x->{targetRate:0.##}x took {recoveryMs:0.0} ms to restore audible output; maximum is {MaximumRateChangeLowOutputRunMilliseconds:0} ms.");
            if (longestLowRunMs > MaximumRateChangeLowOutputRunMilliseconds)
                outputFailures.Add($"{inputDescription} rate change {initialRate:0.##}x->{targetRate:0.##}x left Windows output low for {longestLowRunMs:0.0} ms; maximum is {MaximumRateChangeLowOutputRunMilliseconds:0} ms.");
        }
        Assert.True(outputFailures.Count == 0, string.Join(Environment.NewLine, outputFailures));
    }

    private static Task NativeCompletedReplayOutputRateChangeAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "playback-rate-replay-tone");
        var mediaUri = new Uri("https://d2vi6trrdongqn.cloudfront.net/fixture/chunked/index.m3u8");
        using var upstream = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            var path = Path.Combine(fixtureDirectory, Path.GetFileName(request.RequestUri!.AbsolutePath));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = path.EndsWith(".m3u8", StringComparison.Ordinal)
                    ? new StringContent(File.ReadAllText(path))
                    : new ByteArrayContent(File.ReadAllBytes(path))
            };
        }));
        await using var gateway = new TwitchMutedVodPlaybackGateway(new MemoryLogger(), upstream, TestReplayUrlSecurity.PublicValidator);
        await AssertRateTransitionsPreserveOutputAsync(
            "completed FFmpeg HLS replay",
            () => PcmFixture.CreateAsync(PlaybackAudioState.Audible, capturePcm: false,
                mediaUri: mediaUri, completedReplayGateway: gateway),
            // A completed replay may wait for both the preceding recovery and its
            // own 1x preroll recovery. Each has the production five-second bound.
            maximumRequestMilliseconds: 10_000,
            observationDuration: TimeSpan.FromSeconds(3),
            rateTransitions: [.. PlaybackRateTransitions, (2f, 0.5f), (2f, 0.75f)]);
    });

    private static double GetLongestLowOutputRunMilliseconds(
        WasapiLoopbackCapture.AudioPacket[] packets, double baselineRms)
    {
        if (packets.Length == 0) return 0;
        Array.Sort(packets, static (left, right) => left.Timestamp.CompareTo(right.Timestamp));
        var longest = 0d;
        long? runStart = null;
        var runEnd = 0L;
        var previousPacketEnd = 0L;
        var gapTolerance = Stopwatch.Frequency / 40;
        foreach (var packet in packets)
        {
            var packetEnd = packet.Timestamp +
                (long)(packet.Frames * (double)Stopwatch.Frequency / packet.SampleRate);
            if (previousPacketEnd > 0 && packet.Timestamp - previousPacketEnd > gapTolerance)
            {
                longest = Math.Max(longest,
                    (packet.Timestamp - previousPacketEnd) * 1000d / Stopwatch.Frequency);
            }

            // A rate transition can briefly shift the tone's frequency while VLC
            // rebuilds scaletempo. RMS measures an audible dropout without treating
            // a pitch change as silence.
            var isLow = packet.Rms < baselineRms * 0.5;
            if (!isLow)
            {
                if (runStart is { } start)
                {
                    longest = Math.Max(longest, (runEnd - start) * 1000d / Stopwatch.Frequency);
                    runStart = null;
                }
                previousPacketEnd = packetEnd;
                continue;
            }

            if (runStart is null || packet.Timestamp - runEnd > gapTolerance)
            {
                if (runStart is { } start)
                    longest = Math.Max(longest, (runEnd - start) * 1000d / Stopwatch.Frequency);
                runStart = packet.Timestamp;
            }

            runEnd = packetEnd;
            previousPacketEnd = packetEnd;
        }

        if (runStart is { } lastStart)
            longest = Math.Max(longest, (runEnd - lastStart) * 1000d / Stopwatch.Frequency);
        return longest;
    }

    private static async Task PreserveDecoderAsync()
    {
        using var fixture = await PcmFixture.CreateAsync(PlaybackAudioState.Audible);
        await fixture.WaitForPcmAsync(nonzero: true);
        var track = fixture.Track;
        var setups = fixture.SetupCount;
        for (var index = 0; index < 4; index++)
        {
            fixture.Engine.SetAudioState(80, PlaybackAudioState.Muted);
            await fixture.AssertSettledPcmAsync(nonzero: false);
            Assert.Equal(track, fixture.Track);
            Assert.Equal(setups, fixture.SetupCount);

            var requestedAt = Stopwatch.GetTimestamp();
            fixture.Engine.SetAudioState(80, PlaybackAudioState.Audible);
            await fixture.WaitForPcmAsync(nonzero: true, since: requestedAt);
            Assert.Equal(track, fixture.Track);
            Assert.Equal(setups, fixture.SetupCount);
        }
    }

    private static async Task StartMutedAsync()
    {
        using var fixture = await PcmFixture.CreateAsync(PlaybackAudioState.Muted);
        await fixture.AssertSettledPcmAsync(nonzero: false);
        Assert.True(fixture.Track >= 0, "A background stream must start with its audio decoder warm.");

        var setups = fixture.SetupCount;
        fixture.Engine.SetAudioState(80, PlaybackAudioState.Audible);
        await fixture.WaitForPcmAsync(nonzero: true);
        Assert.Equal(setups, fixture.SetupCount);
    }

    private static async Task RapidSwitchAsync()
    {
        using var first = await PcmFixture.CreateAsync(PlaybackAudioState.Audible);
        using var second = await PcmFixture.CreateAsync(PlaybackAudioState.Muted);
        await first.WaitForPcmAsync(nonzero: true);
        await second.AssertSettledPcmAsync(nonzero: false);
        var firstSetups = first.SetupCount;
        var secondSetups = second.SetupCount;

        for (var index = 0; index < 20; index++)
        {
            var firstSelected = index % 2 == 0;
            first.Engine.SetAudioState(80, firstSelected ? PlaybackAudioState.Audible : PlaybackAudioState.Muted);
            second.Engine.SetAudioState(80, firstSelected ? PlaybackAudioState.Muted : PlaybackAudioState.Audible);
            await Task.Delay(5);
        }

        await Task.WhenAll(first.AssertSettledPcmAsync(nonzero: false), second.AssertSettledPcmAsync(nonzero: true));
        Assert.True(first.Track >= 0 && second.Track >= 0, "Rapid automatic muting must keep both decoders selected.");
        Assert.Equal(firstSetups, first.SetupCount);
        Assert.Equal(secondSetups, second.SetupCount);
    }

    private static async Task PlaybackRatePcmAsync()
    {
        using var fixture = await PcmFixture.CreateAsync(PlaybackAudioState.Audible);
        await fixture.WaitForPcmAsync(nonzero: true);
        await Task.Delay(300);

        var changeStarted = Stopwatch.GetTimestamp();
        Assert.True(await fixture.Engine.TrySetPlaybackRateAsync(1.5f), "VLC must accept the requested playback rate.");
        await Task.Delay(500);

        var blocks = fixture.GetBlocksSince(changeStarted);
        Assert.True(blocks.Length >= 10, $"Expected paced PCM after the rate change; observed {blocks.Length} callback blocks.");
        var baseline = fixture.GetBlocksBefore(changeStarted, TimeSpan.FromMilliseconds(200));
        Assert.True(baseline.Length >= 2, $"Expected a stable pre-change PCM baseline; observed {baseline.Length} callback blocks.");

        var baselineRms = baseline.Average(block => block.Rms);
        var minRms = blocks.Min(block => block.Rms);
        var zeroBlocks = blocks.Count(block => block.Peak == 0);
        Console.WriteLine(
            $"Playback-rate PCM: baseline blocks={baseline.Length}, before RMS={baselineRms:0.0}, after minimum RMS={minRms:0.0}, zero blocks={zeroBlocks}/{blocks.Length}, " +
            $"block sizes={string.Join(',', blocks.Select(block => block.SampleFrames).Distinct().Order())} frames.");

        Assert.True(!blocks.Any(block => block.Peak == 0), "The decoder must not emit a fully silent PCM block after the rate change.");
    }

    private sealed class PcmFixture : IDisposable
    {
        private static readonly Type EngineType = typeof(LibVlcPlaybackEngine);
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly string path;
        private readonly bool ownsMediaFile;
        private readonly IntPtr player;
        private readonly IntPtr videoWindow;
        private readonly ConcurrentQueue<PcmBlock> blocks = new();
        private readonly PlayCallback playCallback;
        private readonly SetupCallback setupCallback;
        private readonly CleanupCallback cleanupCallback;
        private int setupCount;

        private PcmFixture(LibVlcPlaybackEngine engine, string path, IntPtr player, bool capturePcm, bool ownsMediaFile, IntPtr videoWindow = default)
        {
            Engine = engine;
            this.path = path;
            this.ownsMediaFile = ownsMediaFile;
            this.player = player;
            this.videoWindow = videoWindow;
            playCallback = Capture;
            setupCallback = Setup;
            cleanupCallback = _ => { };
            if (capturePcm)
            {
                NativeSetCallbacks(player, playCallback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                NativeSetFormatCallbacks(player, setupCallback, cleanupCallback);
            }
        }

        internal LibVlcPlaybackEngine Engine { get; }
        internal int Track => NativeGetTrack(player);
        internal int NativeMute => NativeGetMute(player);
        internal int NativeVolume => NativeGetVolume(player);
        internal long PlaybackTimeMs => LibVlcNative.libvlc_media_player_get_time(player);
        internal long PlaybackLengthMs => LibVlcNative.libvlc_media_player_get_length(player);
        internal bool IsSeekable => LibVlcNative.libvlc_media_player_is_seekable(player) != 0;
        internal int SetupCount => Volatile.Read(ref setupCount);

        internal PcmBlock[] GetBlocksSince(long timestamp) => blocks.Where(block => block.Timestamp >= timestamp).ToArray();

        internal PcmBlock[] GetBlocksBefore(long timestamp, TimeSpan window)
        {
            var windowTicks = (long)(window.TotalSeconds * Stopwatch.Frequency);
            return blocks.Where(block => block.Timestamp >= timestamp - windowTicks && block.Timestamp < timestamp).ToArray();
        }

        internal static async Task<PcmFixture> CreateAsync(
            PlaybackAudioState initialState,
            bool capturePcm = true,
            int amplitude = 8000,
            string? mediaPath = null,
            Uri? mediaUri = null,
            IPlaybackMediaSourceGateway? completedReplayGateway = null)
        {
            var directory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
            Assert.True(File.Exists(Path.Combine(directory, "libvlc.dll")), "Configured libVLC is missing.");
            var path = mediaPath ?? Path.Combine(Path.GetTempPath(), $"svs-audio-{Guid.NewGuid():N}.wav");
            var ownsGeneratedMediaFile = mediaPath is null && mediaUri is null;
            var completedReplay = completedReplayGateway is not null;
            if (mediaPath is null && mediaUri is null) WriteTone(path, amplitude);
            var sourceUri = mediaUri ?? new Uri(path);
            var factory = new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings(), completedReplayGateway);
            var engine = (LibVlcPlaybackEngine)await factory.CreateAsync(directory,
                enableNativeOverlay: completedReplay, rendererMode: VideoRendererMode.Gdi);
            if (Environment.GetEnvironmentVariable("SVS_TEST_VLC_LOG_DIRECTORY") is { Length: > 0 } logDirectory)
            {
                Directory.CreateDirectory(logDirectory);
                MultistreamVlcDiagnostics.Attach(engine, logDirectory);
            }
            var videoWindow = IntPtr.Zero;
            try
            {
                if (completedReplay)
                {
                    Assert.Equal(false, capturePcm);
                    videoWindow = NativeWindowTest.CreateHiddenParentWindow();
                    engine.SetVideoHandle(videoWindow);
                    await engine.PlayFromAsync(sourceUri, TimeSpan.FromSeconds(5.25), 80, initialState);
                    Assert.True(engine.PreservesReplayPositionOnResume, "The completed replay must use the real FFmpeg preroll filter.");
                    Assert.True((bool)EngineType.GetField("usingAvformatReplay", PrivateInstance)!.GetValue(engine)!);
                    var replayPlayer = (IntPtr)EngineType.GetField("player", PrivateInstance)!.GetValue(engine)!;
                    return new PcmFixture(engine, path, replayPlayer, capturePcm, ownsGeneratedMediaFile, videoWindow);
                }

                // Install the native PCM observer between player creation and playback.
                // Normal PlayAsync has no public interception point there. Audio requests
                // and convergence still go through the real production engine below.
                var controller = (LibVlcAudioStateController)EngineType.GetField("audioStateController", PrivateInstance)!.GetValue(engine)!;
                controller.Update(80, initialState);
                EngineType.GetField("currentMediaSource", PrivateInstance)!.SetValue(engine,
                    PlaybackMediaSource.Direct(sourceUri));
                EngineType.GetField("currentMediaUri", PrivateInstance)!.SetValue(engine, sourceUri);
                EngineType.GetField("originalMediaUri", PrivateInstance)!.SetValue(engine, sourceUri);
                EngineType.GetMethod("CreatePlayerCore", PrivateInstance)!.Invoke(engine, [sourceUri, null]);
                var player = (IntPtr)EngineType.GetField("player", PrivateInstance)!.GetValue(engine)!;
                var fixture = new PcmFixture(engine, path, player, capturePcm, ownsGeneratedMediaFile);
                EngineType.GetMethod("ApplyAudioCore", PrivateInstance)!.Invoke(engine, null);
                Assert.Equal(0, NativePlay(player));
                engine.SetAudioState(80, initialState);
                return fixture;
            }
            catch
            {
                engine.Dispose();
                if (videoWindow != IntPtr.Zero) NativeWindowTest.DestroyWindow(videoWindow);
                if (ownsGeneratedMediaFile) File.Delete(path);
                throw;
            }
        }

        internal async Task WaitForPcmAsync(bool nonzero, long? since = null)
        {
            var start = since ?? Stopwatch.GetTimestamp();
            await TestWait.UntilAsync(() => blocks.Any(block => block.Timestamp >= start && block.Nonzero == nonzero),
                TimeSpan.FromSeconds(2));
        }

        internal async Task AssertSettledPcmAsync(bool nonzero)
        {
            // Allow already-decoded output blocks to pass, then require continuing
            // PCM for a complete observation window, including actual zero samples.
            await Task.Delay(350);
            var start = Stopwatch.GetTimestamp();
            await Task.Delay(300);
            var observed = blocks.Where(block => block.Timestamp >= start).ToArray();
            Assert.True(observed.Length >= 2, "Muted playback must continue producing PCM instead of deleting its decoder.");
            Assert.True(observed.All(block => block.Nonzero == nonzero),
                nonzero ? "Selected continuous tone must remain audible." : "Background PCM must be exactly silent.");
        }

        private int Setup(ref IntPtr opaque, IntPtr format, ref uint rate, ref uint channels)
        {
            Marshal.Copy(Encoding.ASCII.GetBytes("S16N"), 0, format, 4);
            rate = 48000;
            channels = 2;
            Interlocked.Increment(ref setupCount);
            return 0;
        }

        private void Capture(IntPtr opaque, IntPtr data, uint count, long pts)
        {
            var samples = new short[checked((int)count * 2)];
            Marshal.Copy(data, samples, 0, samples.Length);
            long sumSquares = 0;
            var peak = 0;
            foreach (var sample in samples)
            {
                var magnitude = Math.Abs((int)sample);
                peak = Math.Max(peak, magnitude);
                sumSquares += (long)sample * sample;
            }

            var rms = samples.Length == 0 ? 0 : Math.Sqrt(sumSquares / (double)samples.Length);
            blocks.Enqueue(new PcmBlock(Stopwatch.GetTimestamp(), checked((int)count), rms, peak));
        }

        private static void WriteTone(string path, int amplitude)
        {
            const int rate = 48000;
            const int seconds = 30;
            using var writer = new BinaryWriter(File.Create(path));
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + rate * seconds * 2);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(rate * seconds * 2);
            for (var sample = 0; sample < rate * seconds; sample++)
                writer.Write((short)(amplitude * Math.Sin(2 * Math.PI * 440 * sample / rate)));
        }

        public void Dispose()
        {
            Engine.Dispose();
            if (videoWindow != IntPtr.Zero) NativeWindowTest.DestroyWindow(videoWindow);
            GC.KeepAlive(playCallback);
            GC.KeepAlive(setupCallback);
            GC.KeepAlive(cleanupCallback);
            if (ownsMediaFile) File.Delete(path);
        }

        internal readonly record struct PcmBlock(long Timestamp, int SampleFrames, double Rms, int Peak)
        {
            internal bool Nonzero => Peak != 0;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PlayCallback(IntPtr opaque, IntPtr samples, uint count, long pts);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetupCallback(ref IntPtr opaque, IntPtr format, ref uint rate, ref uint channels);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CleanupCallback(IntPtr opaque);
    [DllImport("libvlc", EntryPoint = "libvlc_audio_set_callbacks", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeSetCallbacks(IntPtr player, PlayCallback play, IntPtr pause, IntPtr resume, IntPtr flush, IntPtr drain, IntPtr opaque);
    [DllImport("libvlc", EntryPoint = "libvlc_audio_set_format_callbacks", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeSetFormatCallbacks(IntPtr player, SetupCallback setup, CleanupCallback cleanup);
    [DllImport("libvlc", EntryPoint = "libvlc_media_player_play", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativePlay(IntPtr player);
    [DllImport("libvlc", EntryPoint = "libvlc_audio_get_track", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeGetTrack(IntPtr player);
    [DllImport("libvlc", EntryPoint = "libvlc_audio_get_mute", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeGetMute(IntPtr player);
    [DllImport("libvlc", EntryPoint = "libvlc_audio_get_volume", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeGetVolume(IntPtr player);
}

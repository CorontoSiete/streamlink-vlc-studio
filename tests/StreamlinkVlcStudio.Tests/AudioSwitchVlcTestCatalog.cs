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
                .. AudioOutputTests
            ];

    private static IReadOnlyList<(string Name, Func<Task> Run)> AudioOutputTests =>
        string.Equals(Environment.GetEnvironmentVariable("SVS_TEST_AUDIO_OUTPUT"), "true", StringComparison.OrdinalIgnoreCase)
            ? [("native VLC Windows audio output isolates mute and volume between players", NativeOutputIsolationAsync)]
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

    private sealed class PcmFixture : IDisposable
    {
        private static readonly Type EngineType = typeof(LibVlcPlaybackEngine);
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly string path;
        private readonly IntPtr player;
        private readonly ConcurrentQueue<PcmBlock> blocks = new();
        private readonly PlayCallback playCallback;
        private readonly SetupCallback setupCallback;
        private readonly CleanupCallback cleanupCallback;
        private int setupCount;

        private PcmFixture(LibVlcPlaybackEngine engine, string path, IntPtr player, bool capturePcm)
        {
            Engine = engine;
            this.path = path;
            this.player = player;
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
        internal int SetupCount => Volatile.Read(ref setupCount);

        internal static async Task<PcmFixture> CreateAsync(PlaybackAudioState initialState, bool capturePcm = true, int amplitude = 8000)
        {
            var directory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
            Assert.True(File.Exists(Path.Combine(directory, "libvlc.dll")), "Configured libVLC is missing.");
            var path = Path.Combine(Path.GetTempPath(), $"svs-audio-{Guid.NewGuid():N}.wav");
            WriteTone(path, amplitude);
            var factory = new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings());
            var engine = (LibVlcPlaybackEngine)await factory.CreateAsync(directory,
                enableNativeOverlay: false, rendererMode: VideoRendererMode.Gdi);
            try
            {
                // Install the native PCM observer between player creation and playback.
                // Normal PlayAsync has no public interception point there. Audio requests
                // and convergence still go through the real production engine below.
                var controller = (LibVlcAudioStateController)EngineType.GetField("audioStateController", PrivateInstance)!.GetValue(engine)!;
                controller.Update(80, initialState);
                EngineType.GetMethod("CreatePlayerCore", PrivateInstance)!.Invoke(engine, [new Uri(path), null]);
                var player = (IntPtr)EngineType.GetField("player", PrivateInstance)!.GetValue(engine)!;
                var fixture = new PcmFixture(engine, path, player, capturePcm);
                EngineType.GetMethod("ApplyAudioCore", PrivateInstance)!.Invoke(engine, null);
                Assert.Equal(0, NativePlay(player));
                engine.SetAudioState(80, initialState);
                return fixture;
            }
            catch
            {
                engine.Dispose();
                File.Delete(path);
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
            blocks.Enqueue(new PcmBlock(Stopwatch.GetTimestamp(), samples.Any(sample => sample != 0)));
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
            GC.KeepAlive(playCallback);
            GC.KeepAlive(setupCallback);
            GC.KeepAlive(cleanupCallback);
            File.Delete(path);
        }

        private readonly record struct PcmBlock(long Timestamp, bool Nonzero);
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

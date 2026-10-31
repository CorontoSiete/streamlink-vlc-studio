using System.Net.Http;

internal static partial class ApplicationTestCatalog
{
    private static Task NativePlaybackRateContinuityAsync(bool growing) => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "playback-rate-continuity");
        await using var server = new ReplayFixtureServer(directory);
        using var upstream = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            var path = Path.Combine(directory, Path.GetFileName(request.RequestUri!.AbsolutePath));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = path.EndsWith(".m3u8", StringComparison.Ordinal)
                    ? new StringContent(File.ReadAllText(path) + "\n#EXT-X-ENDLIST\n")
                    : new ByteArrayContent(File.ReadAllBytes(path))
            };
        }));
        await using var gateway = new TwitchMutedVodPlaybackGateway(new MemoryLogger(), upstream, TestReplayUrlSecurity.PublicValidator);
        var uri = growing ? server.Uri : FastVodFixture.MediaUri;
        using var source = growing
            ? await new LiveReplayFixtureGateway().PrepareAsync(uri, new Version(3, 0, 23), CancellationToken.None)
            : await gateway.PrepareAsync(uri, new Version(3, 0, 23), CancellationToken.None, preferFastReplay: true);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        const int frameBytes = 64 * 64 * 4;
        var memory = Marshal.AllocHGlobal(frameBytes * 64 + 31);
        var pixels = new IntPtr((memory.ToInt64() + 31) & ~31L);
        long decodedPicture = 0;
        long presentedPicture = 0;
        var colors = new ConcurrentDictionary<long, (byte B, byte G, byte R)>();
        var frames = new ConcurrentQueue<(long Timestamp, byte B, byte G, byte R)>();
        LibVlcNative.PreviewLockCallback lockFrame = (_, planes) =>
        {
            var picture = Interlocked.Increment(ref decodedPicture);
            Marshal.WriteIntPtr(planes, IntPtr.Add(pixels, (int)(picture % 64) * frameBytes));
            return new IntPtr(picture);
        };
        LibVlcNative.PreviewUnlockCallback unlockFrame = (_, picture, planes) =>
        {
            var frame = Marshal.ReadIntPtr(planes);
            colors[picture.ToInt64()] = (Marshal.ReadByte(frame), Marshal.ReadByte(frame, 1), Marshal.ReadByte(frame, 2));
        };
        ReplayVideoDisplayCallback displayFrame = (_, picture) =>
        {
            if (Interlocked.Exchange(ref presentedPicture, picture.ToInt64()) != picture.ToInt64() &&
                colors.TryRemove(picture.ToInt64(), out var color))
                frames.Enqueue((Stopwatch.GetTimestamp(), color.B, color.G, color.R));
        };
        try
        {
            using var engine = await new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings())
                .CreateAsync(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: false,
                    rendererMode: VideoRendererMode.Gdi);
            typeof(LibVlcPlaybackEngine).GetField("currentMediaSource", flags)!.SetValue(engine, source);
            typeof(LibVlcPlaybackEngine).GetField("originalMediaUri", flags)!.SetValue(engine, uri);
            var position = TimeSpan.FromSeconds(5.25);
            typeof(LibVlcPlaybackEngine).GetMethod("CreatePlayerCore", flags)!
                .Invoke(engine, [source.PlaybackUri, position]);
            // Memory callbacks need CPU pixels; exercise hardware output separately
            // through the existing production-engine playback-rate cases.
            var media = (IntPtr)typeof(LibVlcPlaybackEngine).GetField("media", flags)!.GetValue(engine)!;
            LibVlcNative.libvlc_media_add_option(media, ":avcodec-hw=none");
            var player = ReadNativePlaybackRatePlayer(engine);
            LibVlcNative.libvlc_video_set_callbacks(player, lockFrame, unlockFrame,
                Marshal.GetFunctionPointerForDelegate(displayFrame), IntPtr.Zero);
            LibVlcNative.libvlc_video_set_format(player, "RV32", 64, 64, 64 * 4);
            engine.SetAudioState(0, PlaybackAudioState.Muted);
            Assert.Equal(0, LibVlcNative.libvlc_media_player_play(player));
            var generation = (long)typeof(LibVlcPlaybackEngine).GetField("playerGeneration", flags)!.GetValue(engine)!;
            await (Task)typeof(LibVlcPlaybackEngine).GetMethod("SeekCoreAsync", flags)!
                .Invoke(engine, [position, generation, CancellationToken.None, true, false])!;
            typeof(LibVlcPlaybackEngine).GetMethod("ReleaseReplayOutputCore", flags)!.Invoke(engine, null);
            await TestWait.UntilAsync(() => frames.Count >= 15 && frames.TakeLast(15).All(
                    frame => frame.B > 150 || frame.G > 150 || frame.R > 150),
                TimeSpan.FromSeconds(5), "Opening must finish presenting gated preroll before measuring a speed change.");
            var previousRate = 1f;
            foreach (var rate in new[] { 2f, 0.5f, 0.75f, 1.25f, 1.5f, 1.75f, 1f })
            {
                var before = frames.Last();
                Assert.True(await engine.TrySetPlaybackRateAsync(rate));
                await Task.Delay(2200);
                var observed = frames.Where(frame => frame.Timestamp >= before.Timestamp).ToArray();
                var maximumGap = observed.Zip(observed.Skip(1), (left, right) =>
                    (right.Timestamp - left.Timestamp) * 1000d / Stopwatch.Frequency).DefaultIfEmpty(double.PositiveInfinity).Max();
                maximumGap = Math.Max(maximumGap, Stopwatch.GetElapsedTime(observed[^1].Timestamp).TotalMilliseconds);
                Console.WriteLine($"Continuous {(growing ? "growing" : "completed")} replay {previousRate:0.##}x->{rate:0.##}x: " +
                    $"new frames={observed.Length}, longest presentation gap={maximumGap:0.0} ms.");
                Assert.True(maximumGap < 250, $"Changing speed interrupted video presentation for {maximumGap:0.0} ms.");
                Assert.True(observed.Length >= 20, "Each speed must present newly decoded frames.");
                Assert.True(observed.All(frame => frame.B > 150 || frame.G > 150 || frame.R > 150),
                    "A speed change must never present a blank frame.");
                Assert.Equal(player, ReadNativePlaybackRatePlayer(engine));
                Assert.Equal(false, IsPlaybackRateRecoveryPending(engine));
                previousRate = rate;
            }
            var burstStart = frames.Last().Timestamp;
            foreach (var rate in new[] { 2f, 0.5f, 1.75f, 0.75f, 1.5f, 1.25f, 1f })
            {
                Assert.True(await engine.TrySetPlaybackRateAsync(rate));
                await Task.Delay(75);
            }
            await Task.Delay(2000);
            var burst = frames.Where(frame => frame.Timestamp >= burstStart).ToArray();
            var burstGap = burst.Zip(burst.Skip(1), (left, right) =>
                (right.Timestamp - left.Timestamp) * 1000d / Stopwatch.Frequency).Max();
            Console.WriteLine($"Rapid speed selections: longest presentation gap={burstGap:0.0} ms.");
            Assert.True(burstGap < 250, "Rapid selections must retain uninterrupted video.");
            Assert.Equal(false, IsPlaybackRateRecoveryPending(engine));
        }
        finally
        {
            GC.KeepAlive(lockFrame);
            GC.KeepAlive(unlockFrame);
            GC.KeepAlive(displayFrame);
            Marshal.FreeHGlobal(memory);
        }
    });
}

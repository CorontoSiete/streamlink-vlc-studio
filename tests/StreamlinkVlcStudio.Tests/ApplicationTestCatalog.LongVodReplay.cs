internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> LongVodReplayTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ? [] :
        [
            ("long VOD native resumes beyond thirteen hours with the full timeline and supports backward seeks", LongVodReplayAsync),
            ("long VOD finished: rebased HLS skip to exact end", LongVodFinishedAsync),
            ("long VOD finished: muted timestamps use the production repair and retain the full timeline", MutedLongVodFinishedAsync),
            .. string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_LONG_VOD_URI")) ? [] :
                new (string, Func<Task>)[] {
                    ("long VOD native reproduces the reported xqc resume using actual CDN media", ActualLongVodReplayAsync),
                    ("long VOD finished: reported xqc archive skip to exact end", () => NativeVodFinishedAsync(true, 50593.316,
                        sourceOverride: new Uri(Environment.GetEnvironmentVariable("SVS_TEST_LONG_VOD_URI")!), durationSeconds: 50593.316,
                        nativeOverlay: true)),
                    ("long VOD finished: reported xqc archive plays the last five seconds", () => NativeVodFinishedAsync(true, 50588.316,
                        sourceOverride: new Uri(Environment.GetEnvironmentVariable("SVS_TEST_LONG_VOD_URI")!), durationSeconds: 50593.316,
                        nativeOverlay: true)),
                    ("long VOD finished: reported xqc archive reopens at its final frame", () => NativeVodFinishedAsync(true, 50593.066,
                        fromFinished: true, sourceOverride: new Uri(Environment.GetEnvironmentVariable("SVS_TEST_LONG_VOD_URI")!),
                        durationSeconds: 50593.316, nativeOverlay: true))
                }
        ];

    private static async Task MutedLongVodFinishedAsync()
    {
        var template = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-event", "index3.ts"));
        var playlist = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n#EXT-X-PLAYLIST-TYPE:VOD\n");
        for (var i = 0; i < 5060; i++) playlist.Append($"#EXTINF:10.000,\n{i}-muted.ts\n");
        playlist.AppendLine("#EXT-X-ENDLIST");
        using var upstream = new System.Net.Http.HttpClient(new FakeHttpMessageHandler(request =>
        {
            var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            return new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = name.EndsWith(".m3u8", StringComparison.Ordinal)
                    ? new System.Net.Http.StringContent(playlist.ToString())
                    : new System.Net.Http.ByteArrayContent(AddMutedTimestamps(ShiftTransportTimestamps(template,
                        (int.Parse(name.Split('-')[0], CultureInfo.InvariantCulture) * 10L - 30) * 90000)))
            };
        }));
        var logger = new MemoryLogger();
        await using var gateway = new TwitchMutedVodPlaybackGateway(logger, upstream, TestReplayUrlSecurity.PublicValidator);
        await NativeVodFinishedAsync(true, 50600,
            sourceOverride: new Uri("https://d2vi6trrdongqn.cloudfront.net/regression/chunked/index-muted.m3u8"),
            durationSeconds: 50600, gatewayOverride: gateway, nativeOverlay: true);
        Assert.True(logger.Entries.Any(entry => entry.Source == "MutedVodRepair" && entry.Message.Contains("removing invalid timestamps")));
    }

    private static byte[] AddMutedTimestamps(byte[] data)
    {
        var videoPackets = 0;
        var changed = 0;
        for (var i = 0; i + 188 <= data.Length; i += 188)
        {
            var packet = data.AsSpan(i, 188);
            var adaptation = (packet[3] >> 4) & 3;
            var pes = 4 + ((adaptation & 2) != 0 ? 1 + packet[4] : 0);
            if ((packet[1] & 64) == 0 || (adaptation & 1) == 0 || pes + 14 > 188 ||
                packet[pes] != 0 || packet[pes + 1] != 0 || packet[pes + 2] != 1 ||
                packet[pes + 3] != 0xe0 || (packet[pes + 7] & 128) == 0 || ++videoPackets % 4 != 0) continue;
            var stamp = packet.Slice(pes + 9, 5);
            stamp[0] |= 14;
            stamp[1..].Fill(255);
            if ((adaptation & 2) != 0 && packet[4] >= 7 && (packet[5] & 16) != 0)
            {
                packet.Slice(6, 4).Fill(255);
                packet[10] |= 128;
            }
            changed++;
        }
        Assert.True(changed > 0, "The native fixture must contain the invalid muted timestamps.");
        return data;
    }

    private static async Task LongVodFinishedAsync()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-event");
        var template = File.ReadAllBytes(Path.Combine(directory, "index3.ts"));
        var playlist = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n#EXT-X-PLAYLIST-TYPE:VOD\n");
        for (var i = 0; i < 5060; i++) playlist.Append($"#EXTINF:10.000,\n{i}.ts\n");
        playlist.AppendLine("#EXT-X-ENDLIST");
        await using var server = new ReplayFixtureServer(directory, playlist.ToString(), key =>
            int.TryParse(Path.GetFileNameWithoutExtension(key), out var index) && index >= 0 && index < 5060
                ? ShiftTransportTimestamps(template, (index * 10L - 30) * 90000) : null);
        await NativeVodFinishedAsync(true, 50600, sourceOverride: server.Uri, durationSeconds: 50600);
    }

    private static Task LongVodReplayAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-event");
        var template = File.ReadAllBytes(Path.Combine(directory, "index3.ts"));
        var playlist = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n#EXT-X-PLAYLIST-TYPE:VOD\n");
        for (var i = 0; i < 10400; i++) playlist.Append($"#EXTINF:10.000,\n{i}.ts\n");
        playlist.AppendLine("#EXT-X-ENDLIST");
        await using var server = new ReplayFixtureServer(directory, playlist.ToString(), key =>
            int.TryParse(Path.GetFileNameWithoutExtension(key), out var index) && index >= 0 && index < 10400
                ? ShiftTransportTimestamps(template, (index * 10L - 30) * 90000) : null);
        await VerifyLongVodAsync(server.Uri, TimeSpan.FromSeconds(49381.217), TimeSpan.FromSeconds(104000), exerciseSeeks: true);
    });

    private static Task ActualLongVodReplayAsync() => TestSta.RunOffscreenAsync(() =>
        VerifyLongVodAsync(new Uri(Environment.GetEnvironmentVariable("SVS_TEST_LONG_VOD_URI")!),
            TimeSpan.FromSeconds(49381.217), TimeSpan.FromSeconds(50593.316), exerciseSeeks: false));

    private static async Task VerifyLongVodAsync(Uri uri, TimeSpan position, TimeSpan duration, bool exerciseSeeks,
        IPlaybackMediaSourceGateway? gatewayOverride = null)
    {
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        var logger = new MemoryLogger();
        try
        {
            await using var gateway = new TwitchMutedVodPlaybackGateway(logger);
            using var engine = await new LibVlcPlaybackEngineFactory(logger, new ChatSettings(), gatewayOverride ?? gateway).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: !exerciseSeeks,
                rendererMode: VideoRendererMode.Gdi);
            engine.SetVideoHandle(handle);
            var watch = Stopwatch.StartNew();
            await engine.PlayFromAsync(uri, position, 0, PlaybackAudioState.Muted);
            Console.WriteLine($"Long VOD opened in {watch.Elapsed.TotalSeconds:0.00}s.");
            await ConfirmLongVodOutputAsync(engine, position, duration);
            var native = (IntPtr)typeof(LibVlcPlaybackEngine).GetField("player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
            Assert.True(LibVlcNative.libvlc_media_player_get_time(native) < 30000,
                "VLC must receive a short segment-relative position, not the failing thirteen-hour jump.");
            var source = (PlaybackMediaSource)typeof(LibVlcPlaybackEngine).GetField("currentMediaSource", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
            if (gatewayOverride is not null) Assert.True(source.UseAvformatDemuxer);
            var temporary = source.PlaybackUri.LocalPath;
            Assert.True(File.Exists(temporary));
            if (exerciseSeeks)
            {
                await engine.PauseAsync();
                await engine.SeekAsync(TimeSpan.FromSeconds(49395.25));
                await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var state) && state.State == PlaybackEngineState.Paused, TimeSpan.FromSeconds(2));
                Assert.True(File.Exists(temporary), "An in-range paused seek must retain the current timeline lease.");
                await engine.ResumeAsync();
                await ConfirmLongVodOutputAsync(engine, TimeSpan.FromSeconds(49395.25), duration);
                await engine.SeekAsync(TimeSpan.FromSeconds(35.25));
                Assert.Equal(false, File.Exists(temporary));
                await ConfirmLongVodOutputAsync(engine, TimeSpan.FromSeconds(35.25), duration);
                await engine.SeekAsync(position);
                await ConfirmLongVodOutputAsync(engine, position, duration);
                var movedHandle = NativeWindowTest.CreateHiddenParentWindow();
                try
                {
                    var rebound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    engine.VideoOutputRebound += (_, _) => rebound.TrySetResult();
                    engine.SetVideoHandle(movedHandle);
                    await rebound.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var moved) &&
                        (moved.Position - position).Duration() < TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(5));
                    await engine.SeekAsync(TimeSpan.FromSeconds(100001.217));
                    await ConfirmLongVodOutputAsync(engine, TimeSpan.FromSeconds(100001.217), duration);
                    await engine.SeekAsync(TimeSpan.FromSeconds(35.25));
                    await ConfirmLongVodOutputAsync(engine, TimeSpan.FromSeconds(35.25), duration);
                    await engine.StopAsync();
                }
                finally { NativeWindowTest.DestroyWindow(movedHandle); }
            }
            await engine.StopAsync();
            Assert.Equal(false, File.Exists(temporary));
        }
        finally
        {
            foreach (var entry in logger.Entries.Where(entry => entry.Level >= AppLogLevel.Warning || entry.Message.Contains("Rebased")))
                Console.WriteLine(entry.Message);
            NativeWindowTest.DestroyWindow(handle);
        }
    }

    private static async Task ConfirmLongVodOutputAsync(IPlaybackEngine engine, TimeSpan position, TimeSpan duration)
    {
        Assert.True(engine.TryGetPlaybackClock(out var clock));
        Assert.True((clock.Position - position).Duration() < TimeSpan.FromSeconds(3), $"Expected {position}, got {clock.Position}.");
        Assert.True(clock.Duration.HasValue && (clock.Duration.Value - duration).Duration() < TimeSpan.FromMilliseconds(5), $"Expected duration {duration}, got {clock.Duration}.");
        Assert.True(engine.TryGetPlaybackHealth(out var first));
        await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var next) && next.DisplayedPictures > first.DisplayedPictures &&
            next.PositionMilliseconds > first.PositionMilliseconds, TimeSpan.FromSeconds(5));
        engine.TryGetPlaybackHealth(out var confirmed);
        Console.WriteLine($"Confirmed output: position={confirmed.PositionMilliseconds}ms, pictures={confirmed.DisplayedPictures}, duration={clock.Duration}.");
    }

    // Build a 28-hour TS timeline from the tiny original fixture. Rewriting both PCR and
    // PES PTS/DTS reproduces VLC's real rollover behavior without downloading a long video.
    private static byte[] ShiftTransportTimestamps(byte[] template, long offset)
    {
        var data = template.ToArray();
        const long mask = (1L << 33) - 1;
        for (var i = 0; i + 188 <= data.Length; i += 188)
        {
            var packet = data.AsSpan(i, 188);
            var adaptation = (packet[3] >> 4) & 3;
            if ((adaptation & 2) != 0 && packet[4] >= 7 && (packet[5] & 16) != 0)
            {
                var pcr = ((long)packet[6] << 25) | ((long)packet[7] << 17) | ((long)packet[8] << 9) | ((long)packet[9] << 1) | (uint)(packet[10] >> 7);
                pcr = (pcr + offset) & mask;
                packet[6] = (byte)(pcr >> 25); packet[7] = (byte)(pcr >> 17); packet[8] = (byte)(pcr >> 9);
                packet[9] = (byte)(pcr >> 1); packet[10] = (byte)((long)(uint)(packet[10] & 127) | ((pcr & 1) << 7));
            }
            var pes = 4 + ((adaptation & 2) != 0 ? 1 + packet[4] : 0);
            if ((packet[1] & 64) == 0 || (adaptation & 1) == 0 || pes + 19 > 188 ||
                packet[pes] != 0 || packet[pes + 1] != 0 || packet[pes + 2] != 1) continue;
            var count = (packet[pes + 7] & 192) == 192 ? 2 : (packet[pes + 7] & 128) != 0 ? 1 : 0;
            for (var n = 0; n < count; n++)
            {
                var stamp = packet.Slice(pes + 9 + n * 5, 5);
                var time = ((long)(stamp[0] & 14) << 29) | ((long)stamp[1] << 22) | ((long)(stamp[2] & 254) << 14) | ((long)stamp[3] << 7) | (uint)(stamp[4] >> 1);
                time = (time + offset) & mask;
                stamp[0] = (byte)((long)(uint)(stamp[0] & 241) | ((time >> 29) & 14)); stamp[1] = (byte)(time >> 22);
                stamp[2] = (byte)(((time >> 14) & 254) | 1); stamp[3] = (byte)(time >> 7); stamp[4] = (byte)((time << 1) | 1);
            }
        }
        return data;
    }
}

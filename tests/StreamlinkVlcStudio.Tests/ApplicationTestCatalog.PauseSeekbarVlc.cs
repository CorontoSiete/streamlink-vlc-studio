internal static partial class ApplicationTestCatalog
{
    private static IReadOnlyList<(string Name, Func<Task> Run)> NativePauseSeekbarTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY"))
            ? []
            :
            [
                ("pause continuity: native HLS resumes the same input after manual and hidden pauses", () => NativeReplayPauseRetainsInputAsync()),
                ("pause continuity: native overlay HLS retains its input after manual and hidden pauses", () => NativeReplayPauseRetainsInputAsync(overlay: true)),
                ("pause continuity: native HLS presents the held content without black frames after a long pause", () => NativeReplayFirstOutputAsync(true, pauseAfterOpening: true)),
                ("pause continuity: native HLS still follows playlist growth after resuming", NativeReplayPauseGrowthAsync),
                ("pause seekbar: native VLC VOD stays synchronized after a seven-second pause", () => NativePauseClockAsync(false)),
                ("pause seekbar: native VLC live resume reloads at the held timestamp", () => NativePauseClockAsync(true)),
                ("pause seekbar: native VLC behind-live resume survives delayed media loading", NativeDelayedReplayResumeAsync),
                ("pause seekbar: native VLC pending seek cancels without touching replacement media", NativeCancelPendingSeekAsync),
                ("pause seekbar: native VLC pending seek times out instead of claiming success", NativePendingSeekTimeoutAsync),
                ("pause seekbar: native VLC opening at a position cancels and does not affect the next media", () => NativePendingSeekAsync(cancel: true, startAtPosition: true)),
                ("pause seekbar: native VLC opening at a position times out without claiming success", () => NativePendingSeekAsync(cancel: false, startAtPosition: true)),
                ("pause seekbar: native VLC seeks at zero while paused and at the end", NativeSeekEdgesAsync),
                ("pause seekbar: native VLC first content frame from a file starts at the held position", () => NativeReplayFirstOutputAsync(false)),
                ("pause seekbar: native VLC first content frame from HLS starts at the held position", () => NativeReplayFirstOutputAsync(true)),
                ("pause seekbar: native VLC restore stays silent then applies the latest volume", () => NativeReplayAudioGateAsync(false)),
                ("pause seekbar: native VLC restore preserves a mute requested while loading", () => NativeReplayAudioGateAsync(true)),
                .. string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_RESUME_HLS_URI"))
                    ? Array.Empty<(string, Func<Task>)>()
                    : [("pause seekbar: native VLC HLS seek then pause resumes at the decoder position", () => NativeHlsReplayResumeAsync(false)),
                       ("pause seekbar: native VLC HLS overlay resume keeps the held position", () => NativeHlsReplayResumeAsync(true))]
            ];

    private static Task NativeHlsReplayResumeAsync(bool overlay) => TestSta.RunOffscreenAsync(async () =>
    {
        var uri = new Uri(Environment.GetEnvironmentVariable("SVS_TEST_RESUME_HLS_URI")!);
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            var target = StreamInputParser.Parse("streamer", PlatformKind.Twitch);
            var replay = new ReplaySessionInfo(PlatformKind.Twitch, "streamer", "https://www.twitch.tv/videos/123",
                "123", null, TimeSpan.FromHours(12), true, "");
            var streamlink = new FakeStreamlinkService
            {
                ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(uri, "HLS fixture")),
                StartExternalHttpOverride = (_, _) => Task.FromResult<IStreamTransportSession>(new PauseClockTransport(uri))
            };
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!
            };
            settings.Chat.Layout = overlay ? ChatLayout.Overlay : ChatLayout.Docked;
            settings.Chat.ConnectAutomatically = false;
            var logger = new MemoryLogger();
            await using var tab = TestViewModels.CreateTab(target, "best", streamlink,
                new LibVlcPlaybackEngineFactory(logger, settings.Chat), new FakeChatClientFactory(), logger,
                action => action(), initialVolume: 0, replayResolver: new FakeReplayResolver(replay),
                vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
            tab.SetVideoHandle(handle);
            await tab.StartAsync(settings);
            await TestWait.UntilAsync(() => tab.CanSeekReplay, TimeSpan.FromSeconds(5));
            var engine = (IPlaybackEngine)typeof(StreamTabViewModel)
                .GetField("playbackEngine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;
            var seekSeconds = overlay ? 12000 : 120;
            await tab.SeekReplayAsync(TimeSpan.FromSeconds(seekSeconds));
            foreach (var entry in logger.Entries.Where(entry => entry.Message.StartsWith("Replay restore timed out:", StringComparison.Ordinal)))
                Console.WriteLine(entry.Message);
            Assert.True(tab.Status == PlaybackStatus.Playing, tab.ErrorMessage);
            await Task.Delay(3000);
            Assert.True(engine.TryGetPlaybackClock(out var first));
            Console.WriteLine($"HLS initial seek: VLC={first.Position.TotalSeconds:0.000}s, seekbar={tab.ReplaySeekValue:0.000}s.");
            Assert.True(first.Position.TotalSeconds >= seekSeconds - 2 && first.Position.TotalSeconds < seekSeconds + 6,
                "The initial HLS seek must also reach its actual decoder position.");
            for (var iteration = 0; iteration < 3; iteration++)
            {
                var hidden = iteration == 2;
                if (hidden)
                    await tab.PauseForTabSwitchAsync();
                else
                    await tab.PauseOrResumeAsync();
                InvokeReplayClockUpdate(tab);
                var held = tab.ReplaySeekValue;
                await Task.Delay(iteration == 1 ? 7000 : 500);
                var resume = hidden ? tab.ResumeFromTabSwitchAsync() : tab.PauseOrResumeAsync();
                var samples = new List<double>();
                while (!resume.IsCompleted)
                {
                    if (engine.TryGetPlaybackClock(out var sample) && sample.Duration is not null)
                        samples.Add(sample.Position.TotalSeconds);
                    await Task.Delay(10);
                }
                await resume;
                Console.WriteLine($"HLS during resume {iteration + 1}: min={samples.DefaultIfEmpty(-1).Min():0.000}s, max={samples.DefaultIfEmpty(-1).Max():0.000}s, held={held:0.000}s.");
                // Opening HLS clocks include demux preroll before frames are presented. Keep
                // them as diagnostics; NativeReplayFirstOutputAsync checks presentation itself.
                await Task.Delay(3000);
                Assert.True(engine.TryGetPlaybackClock(out var resumed));
                Console.WriteLine($"HLS resume {iteration + 1}: held={held:0.000}s, actual VLC={resumed.Position.TotalSeconds:0.000}s, seekbar={tab.ReplaySeekValue:0.000}s, status={tab.Status}.");
                Assert.True(Math.Abs(resumed.Position.TotalSeconds - held) < 6,
                    "The actual HLS decoder must resume at the paused timestamp after seeking behind live.");
                Assert.Equal(PlaybackStatus.Playing, tab.Status);
                Assert.True(Math.Abs(tab.ReplaySeekValue - resumed.Position.TotalSeconds) < 2);
            }
        }
        finally
        {
            NativeWindowTest.DestroyWindow(handle);
        }
    });

    private static Task NativeDelayedReplayResumeAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"svs-resume-{Guid.NewGuid():N}.wav");
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            WritePauseClockMedia(path);
            await using var server = new DelayedPauseMediaServer(File.ReadAllBytes(path));
            var gateway = new DelayedPauseMediaGateway(server.Uri);
            var uri = new Uri(path);
            var target = StreamInputParser.Parse("streamer", PlatformKind.Twitch);
            var replay = new ReplaySessionInfo(PlatformKind.Twitch, "streamer", "https://www.twitch.tv/videos/123",
                "123", null, TimeSpan.FromSeconds(90), true, "");
            var streamlink = new FakeStreamlinkService
            {
                ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(uri, "Clock fixture")),
                StartExternalHttpOverride = (_, _) => Task.FromResult<IStreamTransportSession>(new PauseClockTransport(uri))
            };
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!
            };
            settings.Chat.Layout = ChatLayout.Docked;
            settings.Chat.ConnectAutomatically = false;
            var logger = new MemoryLogger();
            await using var tab = TestViewModels.CreateTab(target, "best", streamlink,
                new LibVlcPlaybackEngineFactory(logger, settings.Chat, gateway), new FakeChatClientFactory(), logger,
                action => action(), initialVolume: 0, replayResolver: new FakeReplayResolver(replay),
                vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
            tab.SetVideoHandle(handle);
            await tab.StartAsync(settings);
            await TestWait.UntilAsync(() => tab.CanSeekReplay, TimeSpan.FromSeconds(3));
            await tab.SeekReplayAsync(TimeSpan.FromSeconds(35));
            var engine = (IPlaybackEngine)typeof(StreamTabViewModel)
                .GetField("playbackEngine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;
            await TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var clock) &&
                clock.Position.TotalSeconds is >= 35 and < 40, TimeSpan.FromSeconds(5));
            await tab.PauseOrResumeAsync();
            InvokeReplayClockUpdate(tab);
            var held = tab.ReplaySeekValue;

            // The same replay opens over HTTP on resume. Keep its headers/bytes unavailable
            // until the restore request has been issued, reproducing a slow CDN response.
            gateway.UseHttp = true;
            var resume = tab.PauseOrResumeAsync();
            try
            {
                await server.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(250);
                Assert.Equal(false, resume.IsCompleted);
            }
            finally
            {
                server.Release.TrySetResult();
            }
            await resume.WaitAsync(TimeSpan.FromSeconds(10));
            await TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var clock) &&
                clock.IsSeekable && clock.Position > TimeSpan.Zero, TimeSpan.FromSeconds(5));
            await Task.Delay(1000);
            Assert.True(engine.TryGetPlaybackClock(out var resumed));
            Console.WriteLine($"Delayed resume: held={held:0.000}s, actual VLC={resumed.Position.TotalSeconds:0.000}s, status={tab.Status}.");
            Assert.True(Math.Abs(resumed.Position.TotalSeconds - held) < 4,
                "Resuming after seeking behind live must restore the actual decoder position, not restart at zero.");
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            InvokeReplayClockUpdate(tab);
            Assert.True(Math.Abs(tab.ReplaySeekValue - resumed.Position.TotalSeconds) < 1);
        }
        finally
        {
            NativeWindowTest.DestroyWindow(handle);
            File.Delete(path);
        }
    });

    private static Task NativeCancelPendingSeekAsync() => NativePendingSeekAsync(cancel: true);
    private static Task NativePendingSeekTimeoutAsync() => NativePendingSeekAsync(cancel: false);

    private static Task NativeSeekEdgesAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"svs-seek-edges-{Guid.NewGuid():N}.wav");
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            WritePauseClockMedia(path);
            using var engine = await new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings()).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: false);
            engine.SetVideoHandle(handle);
            await engine.PlayAsync(new Uri(path), 0, PlaybackAudioState.Muted);
            await engine.SeekAsync(TimeSpan.Zero);
            Assert.True(engine.TryGetPlaybackClock(out var start) && start.Position < TimeSpan.FromSeconds(2));
            await engine.PauseAsync();
            await engine.SeekAsync(TimeSpan.FromSeconds(35));
            await engine.ResumeAsync();
            await TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var middle) &&
                middle.Position.TotalSeconds is >= 35 and < 38, TimeSpan.FromSeconds(5));
            await engine.SeekAsync(TimeSpan.FromSeconds(90));
        }
        finally
        {
            NativeWindowTest.DestroyWindow(handle);
            File.Delete(path);
        }
    });

    private static Task NativePendingSeekAsync(bool cancel, bool startAtPosition = false) => TestSta.RunOffscreenAsync(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"svs-pending-seek-{Guid.NewGuid():N}.wav");
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            WritePauseClockMedia(path);
            await using var server = new DelayedPauseMediaServer(File.ReadAllBytes(path));
            using var engine = await new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings()).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: false);
            engine.SetVideoHandle(handle);
            using var cancellation = new CancellationTokenSource();
            Task seek;
            if (startAtPosition)
                seek = engine.PlayFromAsync(server.Uri, TimeSpan.FromSeconds(35), 0, PlaybackAudioState.Muted, cancellation.Token);
            else
            {
                await engine.PlayAsync(server.Uri, 0, PlaybackAudioState.Muted);
                seek = engine.SeekAsync(TimeSpan.FromSeconds(35), cancellation.Token);
            }
            await server.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                if (cancel)
                {
                    await Task.Delay(200);
                    Assert.Equal(false, seek.IsCompleted);
                    cancellation.Cancel();
                    await Assert.ThrowsAsync<OperationCanceledException>(() => seek.WaitAsync(TimeSpan.FromSeconds(2)));
                }
                else
                {
                    await Assert.ThrowsAsync<TimeoutException>(() => seek);
                }
            }
            finally
            {
                server.Release.TrySetResult();
                cancellation.Cancel();
            }

            await engine.PlayAsync(new Uri(path), 0, PlaybackAudioState.Muted);
            await TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var clock) &&
                clock.Position > TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
            Assert.True(engine.TryGetPlaybackClock(out var replacement));
            Assert.True(replacement.Position < TimeSpan.FromSeconds(5),
                "An abandoned seek must not move replacement media to the previous target.");
        }
        finally
        {
            NativeWindowTest.DestroyWindow(handle);
            File.Delete(path);
        }
    });

    private sealed class DelayedPauseMediaGateway(Uri httpUri) : IPlaybackMediaSourceGateway
    {
        public bool UseHttp { get; set; }
        public Task<PlaybackMediaSource> PrepareAsync(Uri mediaUri, Version? libVlcVersion, CancellationToken cancellationToken) =>
            Task.FromResult(PlaybackMediaSource.Direct(UseHttp ? httpUri : mediaUri));
    }

    private sealed class DelayedPauseMediaServer : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new();
        private readonly byte[] media;
        private readonly Task worker;
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Uri Uri { get; }

        public DelayedPauseMediaServer(byte[] media)
        {
            this.media = media;
            listener.Start();
            Uri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/clock.wav");
            worker = ServeAsync();
        }

        private async Task ServeAsync()
        {
            var clients = new List<Task>();
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    clients.Add(RespondAsync(client));
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            finally
            {
                await Task.WhenAll(clients);
            }
        }

        private async Task RespondAsync(System.Net.Sockets.TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var offset = 0;
                    var ranged = false;
                    while (await reader.ReadLineAsync(cancellation.Token) is { Length: > 0 } line)
                    {
                        if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                        {
                            offset = int.Parse(line[13..].Split('-')[0], CultureInfo.InvariantCulture);
                            ranged = true;
                        }
                    }
                    Requested.TrySetResult();
                    await Release.Task.WaitAsync(cancellation.Token);
                    var headers = ranged
                        ? $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {offset}-{media.Length - 1}/{media.Length}\r\n"
                        : "HTTP/1.1 200 OK\r\n";
                    headers += $"Content-Type: audio/wav\r\nAccept-Ranges: bytes\r\nContent-Length: {media.Length - offset}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellation.Token);
                    await stream.WriteAsync(media.AsMemory(offset), cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (IOException) { } // VLC can abandon a request when it seeks or closes.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await cancellation.CancelAsync();
            listener.Stop();
            await worker;
            cancellation.Dispose();
        }
    }

    private static Task NativePauseClockAsync(bool live) => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
        var path = Path.Combine(Path.GetTempPath(), $"svs-pause-clock-{Guid.NewGuid():N}.wav");
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            // A silent, seekable local fixture exercises the actual libVLC clock and pause API
            // without network timing, visible windows, or sound. Live metadata grows normally;
            // its transport/replay URLs both resolve to this same controlled media timeline.
            WritePauseClockMedia(path);
            var uri = new Uri(path);
            var duration = TimeSpan.FromSeconds(90);
            var target = new StreamTarget(PlatformKind.Twitch, "streamer", "https://www.twitch.tv/videos/123",
                live ? StreamTargetKind.Live : StreamTargetKind.TwitchVod, MediaId: "123", MediaDuration: duration);
            var replay = new ReplaySessionInfo(PlatformKind.Twitch, "streamer", target.Url, "123",
                DateTimeOffset.UtcNow - TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), true, "");
            var streamlink = new FakeStreamlinkService
            {
                ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(uri, "Local clock fixture")),
                StartExternalHttpOverride = (_, _) => Task.FromResult<IStreamTransportSession>(new PauseClockTransport(uri))
            };
            var settings = new AppSettings { StreamlinkPath = "streamlink.exe", VlcDirectory = directory };
            settings.Chat.Layout = ChatLayout.Docked;
            settings.Chat.ConnectAutomatically = false;
            var logger = new MemoryLogger();
            await using var tab = TestViewModels.CreateTab(target, "best", streamlink,
                new LibVlcPlaybackEngineFactory(logger, settings.Chat), new FakeChatClientFactory(), logger,
                action => action(), initialVolume: 0, replayResolver: new FakeReplayResolver(replay),
                vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
            tab.SetVideoHandle(handle);
            await tab.StartAsync(settings);
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            var engine = (IPlaybackEngine)typeof(StreamTabViewModel)
                .GetField("playbackEngine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;
            await TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var clock) &&
                clock.IsSeekable && clock.Position > TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
            await tab.PauseOrResumeAsync();
            InvokeReplayClockUpdate(tab);
            var held = tab.ReplaySeekValue;
            await Task.Delay(TimeSpan.FromSeconds(7));
            InvokeReplayClockUpdate(tab);
            AssertPauseClockPosition(tab, TimeSpan.FromSeconds(held));
            await tab.PauseOrResumeAsync();
            await TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var clock) &&
                clock.Position.TotalSeconds > held + 1, TimeSpan.FromSeconds(6));
            InvokeReplayClockUpdate(tab);
            Assert.True(engine.TryGetPlaybackClock(out var resumed));
            Console.WriteLine($"Native {(live ? "live" : "VOD")} pause clock: held={held:0.000}s, VLC={resumed.Position.TotalSeconds:0.000}s, seekbar={tab.ReplaySeekValue:0.000}s.");
            Assert.True(Math.Abs(tab.ReplaySeekValue - resumed.Position.TotalSeconds) < 1,
                "The seekbar must follow VLC's real media position after resume, excluding the seven paused seconds.");
            if (live)
                Assert.True(tab.IsBehindLive);
        }
        finally
        {
            NativeWindowTest.DestroyWindow(handle);
            File.Delete(path);
        }
    });

    private static void WritePauseClockMedia(string path)
    {
        const int sampleRate = 8000;
        const int dataBytes = sampleRate * 90 * 2;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
    }

    private sealed class PauseClockTransport(Uri uri) : IStreamTransportSession
    {
        public Uri PlaybackUri => uri;
        public event EventHandler<string>? LogLineReceived { add { } remove { } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

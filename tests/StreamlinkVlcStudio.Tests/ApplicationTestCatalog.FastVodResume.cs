internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> FastVodResumeTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ? [] :
        [
            ("VOD fast resume: first presented pixels belong to the bookmark", () => NativeReplayFirstOutputAsync(true, fastReplay: true)),
            ("VOD fast resume: long GOP preroll does not skip to the next keyframe", () => NativeReplayFirstOutputAsync(true, fastReplay: true, longGop: true)),
            ("VOD fast resume: buffered pause keeps the input and correct pixels", () => NativeReplayFirstOutputAsync(true, pauseAfterOpening: true, fastReplay: true)),
            ("VOD fast resume: ordinary and muted transport preserve seeking duration and completion", FastVodPlaybackAsync),
            ("VOD fast resume: missing precise seek filter falls back before releasing output", FastVodFallbackAsync),
            ("VOD fast resume: cancellation and stop interrupt a stalled native HTTP read", FastVodCancellationAsync),
            ("VOD fast resume: long timelines preserve seeking window moves and timestamp rollover", FastLongVodAsync)
        ];

    private sealed class FastVodFixture : IAsyncDisposable
    {
        internal static readonly Uri MediaUri = new("https://d2vi6trrdongqn.cloudfront.net/fixture/chunked/index.m3u8");
        internal readonly ConcurrentQueue<string> Requests = new();
        internal readonly MemoryLogger Logger = new();
        internal readonly TwitchMutedVodPlaybackGateway Gateway;
        private readonly System.Net.Http.HttpClient upstream;
        internal FastVodFixture(bool muted = false, bool stalled = false, bool longGop = false)
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", longGop ? "replay-position-long-gop" : "replay-position-event");
            var playlist = File.ReadAllText(Path.Combine(directory, "index.m3u8"));
            if (!playlist.Contains("#EXT-X-ENDLIST")) playlist += "\n#EXT-X-ENDLIST\n";
            if (muted) playlist = playlist.Replace("index3.ts", "index3-muted.ts");
            upstream = new System.Net.Http.HttpClient(new FakeHttpMessageHandler(request =>
            {
                var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
                Requests.Enqueue(name);
                return new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = name.EndsWith(".m3u8", StringComparison.Ordinal)
                        ? new System.Net.Http.StringContent(playlist)
                        : stalled ? new System.Net.Http.StreamContent(new FastVodResumeTestCatalog.WaitingStream())
                        : new System.Net.Http.ByteArrayContent(name.Contains("-muted", StringComparison.Ordinal)
                            ? AddMutedTimestamps(File.ReadAllBytes(Path.Combine(directory, name.Replace("-muted", ""))))
                            : File.ReadAllBytes(Path.Combine(directory, name)))
                };
            }));
            Gateway = new TwitchMutedVodPlaybackGateway(Logger, upstream, TestReplayUrlSecurity.PublicValidator);
        }
        public async ValueTask DisposeAsync() { await Gateway.DisposeAsync(); upstream.Dispose(); }
    }

    private static Task FastVodFallbackAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new FastVodFixture();
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            using var engine = await new LibVlcPlaybackEngineFactory(fixture.Logger, new ChatSettings(), new UnavailableReplayFilterGateway(fixture.Gateway)).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: false);
            engine.SetVideoHandle(handle);
            await engine.PlayFromAsync(FastVodFixture.MediaUri, TimeSpan.FromSeconds(35.25), 0, PlaybackAudioState.Muted);
            Assert.True(fixture.Logger.Entries.Any(entry => entry.Message.Contains("retrying with VLC")));
            await ConfirmLongVodOutputAsync(engine, TimeSpan.FromSeconds(35.25), TimeSpan.FromSeconds(60));
            Assert.True(engine.PreservesReplayPositionOnResume);
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });

    private sealed class UnavailableReplayFilterGateway(IPlaybackMediaSourceGateway inner) : IPlaybackMediaSourceGateway
    {
        public async Task<PlaybackMediaSource> PrepareAsync(Uri uri, Version? version, CancellationToken token, bool preferFastReplay = false)
        {
            // Keep the synthetic provider reachable through its injected HTTP client
            // when the engine selects the adaptive fallback as well.
            var source = await inner.PrepareAsync(uri, version, token, preferFastReplay: true);
            // A module that cannot attach must never remove the managed output gate.
            return new PlaybackMediaSource(source.PlaybackUri, source, useAvformatDemuxer: preferFastReplay);
        }
    }

    private static Task FastVodCancellationAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new FastVodFixture(stalled: true);
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        using var cancellation = new CancellationTokenSource();
        try
        {
            using var engine = await new LibVlcPlaybackEngineFactory(fixture.Logger, new ChatSettings(), fixture.Gateway).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: false);
            engine.SetVideoHandle(handle);
            var opening = engine.PlayFromAsync(FastVodFixture.MediaUri, TimeSpan.FromSeconds(35.25), 0, PlaybackAudioState.Muted, cancellation.Token);
            await TestWait.UntilAsync(() => fixture.Requests.Contains("index0.ts"), TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            try { await opening; throw new InvalidOperationException("Expected cancellation."); }
            catch (OperationCanceledException) { }
            Assert.Equal(false, engine.PreservesReplayPositionOnResume);
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });

    private static Task FastLongVodAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var template = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-event", "index3.ts"));
        var playlist = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n#EXT-X-PLAYLIST-TYPE:VOD\n");
        for (var i = 0; i < 10400; i++) playlist.Append($"#EXTINF:10.000,\n{i}.ts\n");
        playlist.AppendLine("#EXT-X-ENDLIST");
        using var upstream = new System.Net.Http.HttpClient(new FakeHttpMessageHandler(request =>
            new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri == FastVodFixture.MediaUri ? new System.Net.Http.StringContent(playlist.ToString()) :
                    new System.Net.Http.ByteArrayContent(ShiftTransportTimestamps(template,
                        (int.Parse(Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath), CultureInfo.InvariantCulture) * 10L - 30) * 90000))
            }));
        await using var gateway = new TwitchMutedVodPlaybackGateway(new MemoryLogger(), upstream, TestReplayUrlSecurity.PublicValidator);
        await VerifyLongVodAsync(FastVodFixture.MediaUri, TimeSpan.FromSeconds(49381.217), TimeSpan.FromSeconds(104000), true, gateway);
    });

    private static Task FastVodPlaybackAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        var movedHandle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            foreach (var muted in new[] { false, true })
            {
                await using var fixture = new FastVodFixture(muted);
                using var engine = await new LibVlcPlaybackEngineFactory(fixture.Logger, new ChatSettings(), fixture.Gateway).CreateAsync(
                    Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: true, rendererMode: VideoRendererMode.Gdi);
                Assert.True(engine.UsesNativeOverlay, "This check must exercise the production software decoder and overlay.");
                engine.SetVideoHandle(handle);
                await engine.PlayFromAsync(FastVodFixture.MediaUri, TimeSpan.FromSeconds(35.25), 0, PlaybackAudioState.Muted);
                Assert.True(engine.PreservesReplayPositionOnResume);
                Assert.True(fixture.Logger.Entries.Any(entry => entry.Source == "VOD resume" && entry.Message.Contains("Using VLC")));
                Assert.Equal(false, fixture.Logger.Entries.Any(entry => entry.Message.Contains("retrying with VLC")));
                await ConfirmLongVodOutputAsync(engine, TimeSpan.FromSeconds(35.25), TimeSpan.FromSeconds(60));
                Assert.True(engine.TryGetPlaybackHealth(out var initial));
                foreach (var position in new[] { 45.25, 25.25, 35.25 })
                {
                    await engine.SeekAsync(TimeSpan.FromSeconds(position));
                    await ConfirmLongVodOutputAsync(engine, TimeSpan.FromSeconds(position), TimeSpan.FromSeconds(60));
                    Assert.True(engine.TryGetPlaybackHealth(out var next) && next.Generation == initial.Generation);
                }
                var rebound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                engine.VideoOutputRebound += (_, _) => rebound.TrySetResult();
                engine.SetVideoHandle(movedHandle);
                await rebound.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await ConfirmLongVodOutputAsync(engine, TimeSpan.FromSeconds(35.25), TimeSpan.FromSeconds(60));
                var reboundSource = (PlaybackMediaSource)typeof(LibVlcPlaybackEngine).GetField("currentMediaSource",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                Assert.True(reboundSource.UseAvformatDemuxer, "Moving the output must reopen a corrected FFmpeg input with a fresh transport lease.");
                await engine.PauseAsync();
                await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var health) && health.State == PlaybackEngineState.Paused, TimeSpan.FromSeconds(2));
                await engine.SeekAsync(TimeSpan.FromSeconds(45.25));
                Assert.True(await engine.TryResumeReplayAsync());
                await ConfirmLongVodOutputAsync(engine, TimeSpan.FromSeconds(45.25), TimeSpan.FromSeconds(60));
                await engine.SeekAsync(TimeSpan.FromSeconds(59));
                await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var health) && health.State == PlaybackEngineState.Ended, TimeSpan.FromSeconds(5));
                await engine.StopAsync();
                Assert.Equal(false, engine.PreservesReplayPositionOnResume);
            }
        }
        finally { NativeWindowTest.DestroyWindow(movedHandle); NativeWindowTest.DestroyWindow(handle); }
    });
}

internal static partial class ApplicationTestCatalog
{
    private static Task PreparedReplayRebindAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-audio");
        await using var server = new ReplayFixtureServer(directory);
        var originalHandle = NativeWindowTest.CreateHiddenParentWindow();
        var movedHandle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            using var engine = await new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings(), new LiveReplayFixtureGateway()).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: true);
            engine.SetVideoHandle(originalHandle);
            await engine.PlayAsync(new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-colors.mp4")), 0, PlaybackAudioState.Muted);
            await engine.PrepareReplayAsync(server.Uri);
            var prepared = GetPreparedReplayInput(engine);
            Assert.True(prepared is not null);
            var native = GetPreparedReplayField<IntPtr>(prepared!, "Player");
            var rebound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.VideoOutputRebound += (_, _) => rebound.TrySetResult();
            engine.SetVideoHandle(movedHandle);
            await rebound.Task.WaitAsync(TimeSpan.FromSeconds(6));
            Assert.True(ReferenceEquals(prepared, GetPreparedReplayInput(engine)),
                "Moving live video to another HWND must retain the ready replay input.");
            await engine.PlayFromAsync(server.Uri, TimeSpan.FromSeconds(35.25), 0, PlaybackAudioState.Muted);
            Assert.Equal(native, (IntPtr)typeof(LibVlcPlaybackEngine).GetField("player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!);
            Assert.Equal(movedHandle, GetPreparedReplayWindow(native));
            await ConfirmLongVodOutputAsync(engine, TimeSpan.FromSeconds(35.25), TimeSpan.FromSeconds(60));
        }
        finally { NativeWindowTest.DestroyWindow(originalHandle); NativeWindowTest.DestroyWindow(movedHandle); }
    });

    [DllImport("libvlc", EntryPoint = "libvlc_media_player_get_hwnd", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetPreparedReplayWindow(IntPtr player);

    private static Task PreparedReplayAudioAsync(bool muted, bool startPaused = false) => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-audio");
        await using var server = new ReplayFixtureServer(directory, segmentDelay: TimeSpan.FromMilliseconds(500));
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            using var engine = await new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings(), new LiveReplayFixtureGateway()).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: false);
            engine.SetVideoHandle(handle);
            await engine.PlayAsync(new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-colors.mp4")), 0, PlaybackAudioState.Muted);
            await engine.PrepareReplayAsync(server.Uri);
            var input = GetPreparedReplayInput(engine);
            Assert.True(input is not null);
            var native = GetPreparedReplayField<IntPtr>(input!, "Player");
            var playerField = typeof(LibVlcPlaybackEngine).GetField("player", BindingFlags.Instance | BindingFlags.NonPublic)!;
            // The fixture contains silent PCM/AAC; audible state never emits test noise.
            var opening = engine.PlayFromAsync(server.Uri, TimeSpan.FromSeconds(35.25), 67, PlaybackAudioState.Audible, startPaused);
            await TestWait.UntilAsync(() => (IntPtr)playerField.GetValue(engine)! == native, TimeSpan.FromSeconds(3));
            Assert.Equal(0, GetReplayNativeVolume(native));
            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.AudioStateReapplied += (_, _) => applied.TrySetResult();
            engine.SetAudioState(43, muted ? PlaybackAudioState.Muted : PlaybackAudioState.Audible);
            // A successful soft mute needs no convergence pass/event.
            if (!muted) await applied.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(!opening.IsCompleted);
            Assert.Equal(0, GetReplayNativeVolume(native));
            if (startPaused) await AssertSilentUntilPausedAsync(engine, opening, native);
            await opening;
            Assert.Equal(muted ? 0 : 43, GetReplayNativeVolume(native));
            Assert.True(engine.TryGetPlaybackClock(out var clock) && clock.Position >= TimeSpan.FromSeconds(35.25));
            if (startPaused) await AssertNativeClockHeldAsync(engine);
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });

    private static Task PreparedReplayGrowthAsync(bool completed = false) => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-audio");
        var complete = File.ReadAllText(Path.Combine(directory, "index.m3u8"));
        var lastSegment = complete.LastIndexOf("#EXTINF:", StringComparison.Ordinal);
        var initial = complete[..complete.LastIndexOf("#EXTINF:", lastSegment - 1, StringComparison.Ordinal)];
        await using var server = new ReplayFixtureServer(directory, initial);
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            using var engine = await new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings(), new LiveReplayFixtureGateway()).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: false);
            engine.SetVideoHandle(handle);
            await engine.PlayAsync(new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-colors.mp4")), 0, PlaybackAudioState.Muted);
            await engine.PrepareReplayAsync(server.Uri);
            var input = GetPreparedReplayInput(engine);
            Assert.True(input is not null);
            var native = GetPreparedReplayField<IntPtr>(input!, "Player");
            Assert.Equal(40000L, LibVlcNative.libvlc_media_player_get_length(native));
            await Task.Delay(TimeSpan.FromSeconds(2));
            Console.WriteLine($"Paused preparation: length={LibVlcNative.libvlc_media_player_get_length(native)}ms, playlist requests={server.Requests.Count(request => request == "/index.m3u8")}.");
            Assert.Equal(LibVlcNative.MediaPlayerState.Paused, LibVlcNative.libvlc_media_player_get_state(native));
            Assert.True(LibVlcNative.libvlc_media_get_stats(GetPreparedReplayField<IntPtr>(input!, "Media"), out var stats) != 0);
            Assert.Equal(0, stats.DecodedVideo);
            server.UpdatePlaylist(completed ? complete + "#EXT-X-ENDLIST\n" : complete);
            var watch = Stopwatch.StartNew();
            await engine.PlayFromAsync(server.Uri, TimeSpan.FromSeconds(53.75), 0, PlaybackAudioState.Muted);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "A paused input must refresh a stale playlist before seeking into newly appended media.");
            Assert.Equal(native, (IntPtr)typeof(LibVlcPlaybackEngine).GetField("player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!);
            await ConfirmLongVodOutputAsync(engine, TimeSpan.FromSeconds(53.75), TimeSpan.FromSeconds(60));
            Console.WriteLine($"Prepared growing replay: first seek into appended media produced output in {watch.ElapsedMilliseconds}ms.");
            if (completed)
                await TestWait.UntilAsync(() => LibVlcNative.libvlc_media_player_get_state(native) == LibVlcNative.MediaPlayerState.Ended,
                    TimeSpan.FromSeconds(10), "Completing the growing playlist must still drain its last frames and end normally.");
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });

    private static Task PreparedReplayTabAsync(bool nearEdge = false) => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-audio");
        await using var server = new ReplayFixtureServer(directory);
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            var replay = new ReplaySessionInfo(PlatformKind.Twitch, "streamer", "https://www.twitch.tv/videos/123",
                "123", null, TimeSpan.FromSeconds(nearEdge ? 83.75 : 60), true, "");
            var streamlink = new FakeStreamlinkService
            {
                ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(server.Uri, "Local HLS")),
                StartExternalHttpOverride = (_, _) => Task.FromResult<IStreamTransportSession>(new PauseClockTransport(server.Uri))
            };
            var settings = new AppSettings { StreamlinkPath = "streamlink.exe", VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")! };
            settings.Chat.ConnectAutomatically = false;
            settings.Chat.Layout = ChatLayout.Overlay;
            var logger = new MemoryLogger();
            await using var tab = TestViewModels.CreateTab(StreamInputParser.Parse("streamer", PlatformKind.Twitch), "best", streamlink,
                new LibVlcPlaybackEngineFactory(logger, settings.Chat, new LiveReplayFixtureGateway()), new FakeChatClientFactory(), logger, action => action(),
                initialVolume: 0, replayResolver: new FakeReplayResolver(replay),
                vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
            tab.SetVideoHandle(handle);
            await tab.StartAsync(settings);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var engine = (IPlaybackEngine)typeof(StreamTabViewModel).GetField("playbackEngine", flags)!.GetValue(tab)!;
            await TestWait.UntilAsync(() => GetPreparedReplayInput(engine) is not null, TimeSpan.FromSeconds(10));
            var preparedPlayer = GetPreparedReplayField<IntPtr>(GetPreparedReplayInput(engine)!, "Player");
            Assert.True(!tab.IsReplayMode && tab.CanSeekReplay);
            var watch = Stopwatch.StartNew();
            if (nearEdge) await tab.SkipBackwardCommand.ExecuteAsync();
            else await tab.SeekReplayAsync(TimeSpan.FromSeconds(35.25));
            Console.WriteLine($"First skip button transition: {watch.ElapsedMilliseconds}ms.");
            if (nearEdge) Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            Assert.True(tab.IsReplayMode && tab.IsBehindLive);
            Assert.Equal(preparedPlayer, (IntPtr)typeof(LibVlcPlaybackEngine).GetField("player", flags)!.GetValue(engine)!);
            await ConfirmLongVodOutputAsync(engine, TimeSpan.FromSeconds(nearEdge ? 53.75 : 35.25), TimeSpan.FromSeconds(60));
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });

    private static Task PreparedReplayFirstPixelsAsync(bool nearEdge) => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-audio");
        await using var server = new ReplayFixtureServer(directory, segmentDelay: TimeSpan.FromMilliseconds(200));
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        var memory = Marshal.AllocHGlobal(64 * 64 * 4 + 31);
        var pixels = new IntPtr((memory.ToInt64() + 31) & ~31L);
        try
        {
            using var engine = await new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings(), new LiveReplayFixtureGateway()).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: false);
            engine.SetVideoHandle(handle);
            await engine.PlayAsync(new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-colors.mp4")), 0, PlaybackAudioState.Muted);
            await engine.PrepareReplayAsync(server.Uri);
            var input = GetPreparedReplayInput(engine);
            Assert.True(input is not null);
            var native = GetPreparedReplayField<IntPtr>(input!, "Player");
            var firstContent = new TaskCompletionSource<(byte B, byte G, byte R)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var frames = new ConcurrentQueue<(byte B, byte G, byte R)>();
            LibVlcNative.PreviewLockCallback lockFrame = (_, planes) => { Marshal.WriteIntPtr(planes, pixels); return IntPtr.Zero; };
            LibVlcNative.PreviewUnlockCallback unlockFrame = (_, _, _) => { };
            ReplayVideoDisplayCallback display = (_, _) =>
            {
                var color = (Marshal.ReadByte(pixels), Marshal.ReadByte(pixels, 1), Marshal.ReadByte(pixels, 2));
                frames.Enqueue(color);
                if (color.Item1 > 30 || color.Item2 > 30 || color.Item3 > 30) firstContent.TrySetResult(color);
            };
            LibVlcNative.libvlc_video_set_callbacks(native, lockFrame, unlockFrame, Marshal.GetFunctionPointerForDelegate(display), IntPtr.Zero);
            LibVlcNative.libvlc_video_set_format(native, "RV32", 64, 64, 64 * 4);
            try
            {
                var watch = Stopwatch.StartNew();
                await engine.PlayFromAsync(server.Uri, TimeSpan.FromSeconds(nearEdge ? 53.75 : 35.25), 0, PlaybackAudioState.Muted);
                var color = await firstContent.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Console.WriteLine($"Prepared first content: {watch.ElapsedMilliseconds}ms R={color.R} G={color.G} B={color.B}.");
                bool IsTarget((byte B, byte G, byte R) frame) => frame.R < 30 &&
                    (nearEdge ? frame.B > 200 && frame.G < 30 : frame.G > 200 && frame.B < 30);
                Assert.True(IsTarget(color), "The first visible content must be at the requested position, with no preroll flash.");
                if (nearEdge) Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
                await Task.Delay(750);
                Assert.True(frames.All(frame => (frame.R < 30 && frame.G < 30 && frame.B < 30) ||
                    IsTarget(frame)));
                Assert.Equal(native, (IntPtr)typeof(LibVlcPlaybackEngine).GetField("player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!);
            }
            finally
            {
                await engine.StopAsync();
                GC.KeepAlive(lockFrame); GC.KeepAlive(unlockFrame); GC.KeepAlive(display);
            }
        }
        finally { Marshal.FreeHGlobal(memory); NativeWindowTest.DestroyWindow(handle); }
    });

    private static Task PreparedReplayLifetimeAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-audio");
        await using var first = new ReplayFixtureServer(directory);
        await using var second = new ReplayFixtureServer(directory);
        await using var delayed = new ReplayFixtureServer(directory, segmentDelay: TimeSpan.FromSeconds(5));
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            using var engine = await new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings(), new LiveReplayFixtureGateway()).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: false);
            engine.SetVideoHandle(handle);
            await engine.PlayAsync(new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-colors.mp4")), 0, PlaybackAudioState.Muted);
            using (var cancellation = new CancellationTokenSource())
            {
                var preparing = engine.PrepareReplayAsync(delayed.Uri, cancellation.Token);
                await TestWait.UntilAsync(() => delayed.Requests.Any(request => request.EndsWith(".ts", StringComparison.Ordinal)), TimeSpan.FromSeconds(3));
                cancellation.Cancel();
                try { await preparing.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (OperationCanceledException) { }
            }
            await engine.PrepareReplayAsync(first.Uri);
            var old = GetPreparedReplayInput(engine);
            Assert.True(old is not null);
            var firstPlayer = GetPreparedReplayField<IntPtr>(old!, "Player");
            await engine.PrepareReplayAsync(first.Uri);
            Assert.Equal(firstPlayer, GetPreparedReplayField<IntPtr>(GetPreparedReplayInput(engine)!, "Player"));
            await engine.PrepareReplayAsync(second.Uri);
            await TestWait.UntilAsync(() => GetPreparedReplayField<IntPtr>(old!, "Player") == IntPtr.Zero, TimeSpan.FromSeconds(3));
            var replacement = GetPreparedReplayInput(engine);
            Assert.True(replacement is not null);
            await engine.StopAsync();
            await TestWait.UntilAsync(() => GetPreparedReplayField<IntPtr>(replacement!, "Player") == IntPtr.Zero, TimeSpan.FromSeconds(3));
            Assert.True(GetPreparedReplayInput(engine) is null);

            await engine.PlayAsync(new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-colors.mp4")), 0, PlaybackAudioState.Muted);
            using var readyCancellation = new CancellationTokenSource();
            await engine.PrepareReplayAsync(first.Uri, readyCancellation.Token);
            var ready = GetPreparedReplayInput(engine);
            Assert.True(ready is not null);
            readyCancellation.Cancel();
            await TestWait.UntilAsync(() => GetPreparedReplayField<IntPtr>(ready!, "Player") == IntPtr.Zero, TimeSpan.FromSeconds(3));
            Assert.True(engine.TryGetPlaybackHealth(out var live) && live.State == PlaybackEngineState.Playing);
            var pending = engine.PrepareReplayAsync(delayed.Uri);
            engine.Dispose();
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });
}

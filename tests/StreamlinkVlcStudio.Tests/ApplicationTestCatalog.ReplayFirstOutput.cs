internal static partial class ApplicationTestCatalog
{
    private static Task NativeReplayAudioGateAsync(bool muted) => TestSta.RunOffscreenAsync(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"svs-replay-audio-{Guid.NewGuid():N}.wav");
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            WritePauseClockMedia(path); // Silent PCM; never emits audible test sound.
            await using var server = new DelayedPauseMediaServer(File.ReadAllBytes(path));
            using var engine = await new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings()).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: false);
            engine.SetVideoHandle(handle);
            var opening = engine.PlayFromAsync(server.Uri, TimeSpan.FromSeconds(35), 67, PlaybackAudioState.Audible);
            try
            {
                await server.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var player = (IntPtr)typeof(LibVlcPlaybackEngine)
                    .GetField("player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                Assert.Equal(0, GetReplayNativeVolume(player));
                var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                engine.AudioStateReapplied += (_, _) => applied.TrySetResult();
                engine.SetAudioState(43, muted ? PlaybackAudioState.Muted : PlaybackAudioState.Audible);
                await applied.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(0, GetReplayNativeVolume(player));
                Assert.Equal(false, opening.IsCompleted);
                server.Release.TrySetResult();
                await opening.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(muted ? 0 : 43, GetReplayNativeVolume(player));
                Assert.True(engine.TryGetPlaybackClock(out var clock) && clock.Position >= TimeSpan.FromSeconds(35));
            }
            finally
            {
                server.Release.TrySetResult();
                try { await opening; } catch { /* Preserve the test assertion; teardown stops the player. */ }
            }
        }
        finally
        {
            NativeWindowTest.DestroyWindow(handle);
            File.Delete(path);
        }
    });

    [DllImport("libvlc", EntryPoint = "libvlc_audio_get_volume", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetReplayNativeVolume(IntPtr player);

    private static Task NativeReplayFirstOutputAsync(bool hls, bool pauseAfterOpening = false) => TestSta.RunOffscreenAsync(async () =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures",
            hls ? "replay-position-event/index.m3u8" : "replay-position-colors.mp4");
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        var memory = Marshal.AllocHGlobal(64 * 64 * 4 + 31);
        var pixels = new IntPtr((memory.ToInt64() + 31) & ~31L);
        await using var server = hls ? new ReplayFixtureServer(Path.GetDirectoryName(path)!) : null;
        try
        {
            using var engine = await new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings()).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: false);
            engine.SetVideoHandle(handle);
            var firstOutput = new TaskCompletionSource<(byte Blue, byte Green, byte Red)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var presentedFrames = new ConcurrentQueue<(byte Blue, byte Green, byte Red)>();
            var resumedContent = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            Stopwatch? resumeWatch = null;
            long preparedPicture = 0;
            long lastPresentedPicture = 0;
            long heldPicture = 0;
            LibVlcNative.PreviewLockCallback lockFrame = (_, planes) =>
            {
                Marshal.WriteIntPtr(planes, pixels);
                // Identify each prepared output buffer when VLC submits it for display.
                // This measures presentation refresh, independently of HLS's cached
                // get_time samples. Decoder advancement is checked in the tab tests.
                return new IntPtr(Interlocked.Increment(ref preparedPicture));
            };
            LibVlcNative.PreviewUnlockCallback unlockFrame = (_, _, _) => { };
            ReplayVideoDisplayCallback displayFrame = (_, picture) =>
            {
                Interlocked.Exchange(ref lastPresentedPicture, picture.ToInt64());
                var color = (Marshal.ReadByte(pixels), Marshal.ReadByte(pixels, 1), Marshal.ReadByte(pixels, 2));
                presentedFrames.Enqueue(color);
                if (color.Item1 > 30 || color.Item2 > 30 || color.Item3 > 30)
                    firstOutput.TrySetResult(color);
                if (Volatile.Read(ref resumeWatch) is { } watch &&
                    picture.ToInt64() != heldPicture)
                    resumedContent.TrySetResult(watch.Elapsed.TotalMilliseconds);
            };
            // Intercept presentation, not decoding: VLC may decode and discard preroll frames.
            // The same media-creation primitive is used by PlayFromAsync. Video callbacks must
            // be installed after creation and before playing; no visible window is needed.
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(LibVlcPlaybackEngine).GetMethod("CreatePlayerCore", flags)!
                .Invoke(engine, [server?.Uri ?? new Uri(path), TimeSpan.FromSeconds(35.25)]);
            var player = (IntPtr)typeof(LibVlcPlaybackEngine).GetField("player", flags)!.GetValue(engine)!;
            LibVlcNative.libvlc_video_set_callbacks(player, lockFrame, unlockFrame,
                Marshal.GetFunctionPointerForDelegate(displayFrame), IntPtr.Zero);
            LibVlcNative.libvlc_video_set_format(player, "RV32", 64, 64, 64 * 4);
            try
            {
                Assert.Equal(0, LibVlcNative.libvlc_media_player_play(player));
                var generation = (long)typeof(LibVlcPlaybackEngine).GetField("playerGeneration", flags)!.GetValue(engine)!;
                await (Task)typeof(LibVlcPlaybackEngine).GetMethod("SeekCoreAsync", flags)!
                    .Invoke(engine, [TimeSpan.FromSeconds(35.25), generation, CancellationToken.None, true])!;
                Assert.Equal(false, firstOutput.Task.IsCompleted);
                typeof(LibVlcPlaybackEngine).GetMethod("ReleaseReplayOutputCore", flags)!.Invoke(engine, null);
                var first = await firstOutput.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Console.WriteLine($"First content frame after buffering: R={first.Red}, G={first.Green}, B={first.Blue}; held position is green, beginning is red.");
                Assert.True(first.Green > 200 && first.Red < 30 && first.Blue < 30,
                    "The first content frame must come from the held position, without flashing the VOD beginning.");
                if (pauseAfterOpening)
                {
                    Assert.True(engine.PreservesReplayPositionOnResume, "The HLS pause filter must confirm attachment to this input.");
                    await engine.PauseAsync();
                    await TestWait.UntilAsync(() => LibVlcNative.libvlc_media_player_get_state(player) == LibVlcNative.MediaPlayerState.Paused,
                        TimeSpan.FromSeconds(2));
                    var heldMilliseconds = LibVlcNative.libvlc_media_player_get_time(player);
                    await Task.Delay(7000);
                    Assert.Equal(heldMilliseconds, LibVlcNative.libvlc_media_player_get_time(player));
                    heldPicture = Interlocked.Read(ref lastPresentedPicture);
                    presentedFrames.Clear();
                    Volatile.Write(ref resumeWatch, Stopwatch.StartNew());
                    await engine.ResumeAsync();
                    var latency = await resumedContent.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    Console.WriteLine($"HLS presentation refresh after seven-second pause: {latency:0}ms.");
                    Assert.True(latency < 1000, "Already buffered video must resume within two frames of the 2 fps fixture.");
                    await Task.Delay(500);
                    Assert.True(presentedFrames.Count > 0 && presentedFrames.All(frame =>
                            frame.Green > 200 && frame.Red < 30 && frame.Blue < 30),
                        "Every presented frame during resume must remain at the held content: no black, beginning, or live-edge frame.");
                    await engine.StopAsync();
                    Assert.Equal(false, engine.PreservesReplayPositionOnResume);
                    return;
                }
                await Task.Delay(1000);
                Assert.True(presentedFrames.All(frame =>
                        (frame.Green < 30 && frame.Red < 30 && frame.Blue < 30) ||
                        (frame.Green > 200 && frame.Red < 30 && frame.Blue < 30)),
                    "No frame during startup may show the beginning or jump to the live edge.");
            }
            finally
            {
                await engine.StopAsync();
                GC.KeepAlive(lockFrame);
                GC.KeepAlive(unlockFrame);
                GC.KeepAlive(displayFrame);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
            NativeWindowTest.DestroyWindow(handle);
        }
    });

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReplayVideoDisplayCallback(IntPtr opaque, IntPtr picture);

    private sealed class ReplayFixtureServer : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task worker;
        private readonly ConcurrentDictionary<string, byte[]> files;
        internal Uri Uri { get; }
        internal ConcurrentQueue<string> Requests { get; } = new();

        internal ReplayFixtureServer(string directory, string? playlist = null)
        {
            files = new(Directory.GetFiles(directory).ToDictionary(path => "/" + Path.GetFileName(path), File.ReadAllBytes));
            if (playlist is not null) UpdatePlaylist(playlist);
            listener.Start();
            Uri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/index.m3u8");
            worker = Task.Run(async () =>
            {
                try
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                        try
                        {
                            await using var stream = client.GetStream();
                            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                            var request = await reader.ReadLineAsync(cancellation.Token);
                            if (request is null) continue;
                            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellation.Token))) { }
                            var key = request.Split(' ')[1];
                            Requests.Enqueue(key);
                            var found = files.TryGetValue(key, out var data);
                            data ??= [];
                            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n");
                            await stream.WriteAsync(header, cancellation.Token);
                            await stream.WriteAsync(data, cancellation.Token);
                        }
                        catch (IOException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
                        {
                            // VLC cancels in-flight downloads when a seek replaces its input.
                            // Continue accepting requests from the new input.
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            });
        }

        internal void UpdatePlaylist(string playlist) => files["/index.m3u8"] = Encoding.UTF8.GetBytes(playlist);

        public async ValueTask DisposeAsync()
        {
            await cancellation.CancelAsync();
            listener.Stop();
            await worker;
            cancellation.Dispose();
        }
    }
}

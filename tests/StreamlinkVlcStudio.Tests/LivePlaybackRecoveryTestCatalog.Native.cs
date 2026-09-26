using System.Net.Sockets;

internal static partial class LivePlaybackRecoveryTestCatalog
{
    private static IReadOnlyList<(string Name, Func<Task> Run)> NativeTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ? [] :
        [("live recovery native VLC reconnects after truncated HTTP while server stays alive", NativeDisconnectAsync)];

    private static Task NativeDisconnectAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "fixture",
            VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY"),
            VideoRendererMode = VideoRendererMode.Gdi,
            KeepInactiveTabsRunning = true
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Docked;
        using var server = new TruncatedLiveServer();
        var service = new FakeStreamlinkService
        {
            StartExternalHttpOverride = (_, _) =>
            Task.FromResult<IStreamTransportSession>(new LocalSession(server.Uri))
        };
        var surface = new VideoSurface();
        var window = new Window
        {
            Content = surface,
            Width = 320,
            Height = 220,
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        await using var tab = TestViewModels.CreateTab(StreamInputParser.Parse("fixture", PlatformKind.Twitch), "best",
            service, new LibVlcPlaybackEngineFactory(logger, settings.Chat), new FakeChatClientFactory(), logger,
            action => window.Dispatcher.BeginInvoke(action));
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StreamTabViewModel.IsRecoveringLivePlayback))
                surface.Visibility = tab.IsRecoveringLivePlayback ? Visibility.Hidden : Visibility.Visible;
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            tab.SetVideoHandle(surface.Handle);
            tab.IsMuted = true;
            await tab.StartAsync(settings);
            var timer = (System.Threading.Timer)typeof(StreamTabViewModel)
                .GetField("liveHealthTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;
            timer.Change(Timeout.Infinite, Timeout.Infinite);
            var engine = (IPlaybackEngine)typeof(StreamTabViewModel).GetField("playbackEngine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;
            await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var h) && h.DisplayedPictures > 0,
                TimeSpan.FromSeconds(10));
            engine.TryGetPlaybackHealth(out var first);
            // Stop sending mid-response after actual pictures have reached the renderer.
            // The listener keeps accepting requests, exactly like external-HTTP continuous mode.
            server.Interrupt();
            await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var h) && h.State == PlaybackEngineState.Ended,
                TimeSpan.FromSeconds(20));
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            Assert.Equal(1, service.StartCount);
            Console.WriteLine("Reproduced original failure: VLC Ended, tab Playing, HTTP listener still alive.");
            timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
            await TestWait.UntilAsync(() => service.StartCount >= 2 && !tab.IsRecoveringLivePlayback &&
                engine.TryGetPlaybackHealth(out var h) && h.Generation != first.Generation && h.DisplayedPictures > 0,
                TimeSpan.FromSeconds(40));
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            Assert.True(server.Requests >= 2);
            Assert.True(tab.IsMuted);
            for (var n = 0; n < 3; n++)
            {
                Assert.True(engine.TryGetPlaybackHealth(out var recovered));
                Console.WriteLine($"Recovered output sample {n}: {recovered}");
                await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var advancing) &&
                    advancing.Generation == recovered.Generation && advancing.DisplayedPictures > recovered.DisplayedPictures,
                    TimeSpan.FromSeconds(4));
            }
        }
        finally
        {
            await tab.StopAsync();
            window.Close();
        }
    });

    private sealed class LocalSession(Uri uri) : IStreamTransportSession
    {
        public Uri PlaybackUri => uri;
        public event EventHandler<string>? LogLineReceived { add { } remove { } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TruncatedLiveServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new();
        private readonly TaskCompletionSource interrupt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly byte[] body;
        private readonly int firstSegmentLength;
        private int requests;
        internal Uri Uri { get; }
        internal int Requests => Volatile.Read(ref requests);
        internal TruncatedLiveServer()
        {
            var segments = Enumerable.Range(0, 6).Select(n => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "replay-position-event", $"index{n}.ts"))).ToArray();
            firstSegmentLength = segments[0].Length;
            body = segments.SelectMany(segment => segment).ToArray();
            listener.Start();
            Uri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
            _ = ServeAsync();
        }
        internal void Interrupt() => interrupt.TrySetResult();
        private async Task ServeAsync()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    while (await reader.ReadLineAsync(cancellation.Token) is { Length: > 0 }) { }
                    var number = Interlocked.Increment(ref requests);
                    var payload = number == 1 ? body.AsMemory(0, firstSegmentLength) : body.AsMemory();
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\nContent-Length: {payload.Length + 188}\r\nConnection: close\r\n\r\n"), cancellation.Token);
                    await stream.WriteAsync(payload, cancellation.Token);
                    if (number == 1) await interrupt.Task.WaitAsync(cancellation.Token);
                    else await Task.Delay(TimeSpan.FromSeconds(30), cancellation.Token);
                    // Deliberately close without the promised final packet.
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException) { }
        }
        public void Dispose() { cancellation.Cancel(); listener.Stop(); }
    }
}

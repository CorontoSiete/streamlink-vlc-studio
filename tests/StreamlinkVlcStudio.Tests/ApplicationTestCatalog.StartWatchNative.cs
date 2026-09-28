internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> StartWatchNativeTests =>
        Environment.GetEnvironmentVariable("SVS_TEST_START_WATCH_NATIVE") == "1"
            ? [("start watch native: real Streamlink and VLC start hide resume stop and restart", StartWatchNativeAsync)]
            : [];

    private static Task StartWatchNativeAsync() => TestSta.RunAsync(async () =>
    {
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = ExecutableResolver.FindStreamlink(),
            VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY") ?? ExecutableResolver.FindVlcDirectory(),
            VideoRendererMode = VideoRendererMode.Gdi,
            LowLatency = false
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Docked;
        Assert.True(File.Exists(settings.StreamlinkPath), "This opt-in test requires an installed Streamlink executable.");
        Assert.True(File.Exists(Path.Combine(settings.VlcDirectory!, "libvlc.dll")), "This opt-in test requires VLC.");
        var fixtureDirectory = Environment.GetEnvironmentVariable("SVS_TEST_START_WATCH_HLS_DIRECTORY");
        Assert.True(!string.IsNullOrWhiteSpace(fixtureDirectory) && Directory.Exists(fixtureDirectory),
            "Set SVS_TEST_START_WATCH_HLS_DIRECTORY to the generated two-second HLS fixture described in docs/start-watch-workflow-and-resources.md.");
        var segments = Directory.GetFiles(fixtureDirectory!, "segment*.ts").Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToArray();
        Assert.Equal(30, segments.Length);
        var broadcastClock = Stopwatch.StartNew();
        await using var server = new ReplayFixtureServer(fixtureDirectory!,
            playlist: BuildStartWatchLivePlaylist(3, segments.Length),
            resolve: key => key.StartsWith("/segment", StringComparison.Ordinal) && key.EndsWith(".ts", StringComparison.Ordinal) &&
                int.TryParse(key.AsSpan(8, key.Length - 11), out var number) && number >= 0 ? segments[number % segments.Length] : null);
        using var broadcastCancellation = new CancellationTokenSource();
        var broadcast = PublishLivePlaylistAsync();
        var target = StreamInputParser.FromChannel(PlatformKind.Twitch, "localstream") with { Url = "hls://" + server.Uri };
        var replay = new NativeStartWatchReplayResolver();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var viewModel = TestViewModels.CreateMain(settings, new FakeSettingsService(settings), new StreamlinkService(logger),
            new LibVlcPlaybackEngineFactory(logger, settings.Chat), new FakeChatClientFactory(), logger,
            action => dispatcher.BeginInvoke(action), replayResolver: replay);
        var window = new MainWindow
        {
            Title = "Stream Studio - playback verification",
            Width = 1100,
            Height = 760,
            Left = 100,
            Top = 100,
            WindowStartupLocation = WindowStartupLocation.Manual,
            DataContext = viewModel
        };
        // Exercise the production window and commands with in-memory settings.
        // The test never opens or saves the user's accounts or watch history.
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, viewModel);
        try
        {
            window.Show();
            SetMainWindowHandle(window);
            var elapsed = Stopwatch.StartNew();
            await viewModel.OpenStreamAsync(target);
            var tab = viewModel.SelectedTab!;
            tab.IsMuted = true;
            await TestWait.UntilAsync(() => tab.Status == PlaybackStatus.Playing && !tab.IsBusy, TimeSpan.FromSeconds(15));
            var engine = GetStartWatchPlaybackEngine(tab);
            await WaitForStartWatchFramesAsync(engine);
            Assert.True(engine.TryGetVideoSize(out var width, out var height));
            Assert.Equal(64, width);
            Assert.Equal(64, height);
            Assert.True(replay.RequestCount > 0);
            Console.WriteLine($"Local HLS first output: {elapsed.ElapsedMilliseconds} ms; {width}x{height}; optional replay still pending.");
            using var transport = ObserveStartWatchTransport(tab);
            var processId = transport.Id;
            elapsed.Restart();
            viewModel.SelectHomeCommand.Execute(null);
            await viewModel.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(PlaybackStatus.Paused, tab.Status);
            Assert.True(tab.IsLivePlaybackConnectionSuspended);
            Assert.Equal(false, engine.TryGetPlaybackHealth(out _));
            Assert.True(replay.LastToken.IsCancellationRequested);
            Console.WriteLine($"Home suspended the native player in {elapsed.ElapsedMilliseconds} ms; decoder/player released.");
            Assert.Equal(false, transport.HasExited);

            elapsed.Restart();
            viewModel.SelectedTab = tab;
            await viewModel.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(8));
            await WaitForStartWatchFramesAsync(engine);
            using (var resumedTransport = ObserveStartWatchTransport(tab)) Assert.Equal(processId, resumedTransport.Id);
            Assert.True(tab.IsMuted);
            Console.WriteLine($"Returning to the tab produced video in {elapsed.ElapsedMilliseconds} ms using the same transport.");

            elapsed.Restart();
            await viewModel.StopSelectedCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(8));
            await transport.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(PlaybackStatus.Stopped, tab.Status);
            Assert.Equal(false, engine.TryGetPlaybackHealth(out _));
            Console.WriteLine($"Stop released VLC and exited the Streamlink process in {elapsed.ElapsedMilliseconds} ms.");

            await viewModel.PlaySelectedCommand.ExecuteAsync();
            await TestWait.UntilAsync(() => tab.Status == PlaybackStatus.Playing && !tab.IsBusy, TimeSpan.FromSeconds(15));
            await WaitForStartWatchFramesAsync(GetStartWatchPlaybackEngine(tab));
            using var restartedTransport = ObserveStartWatchTransport(tab);
            Assert.True(restartedTransport.Id != processId);
            Console.WriteLine("Restart produced advancing native frames in the same tab.");

            // Optional observation time for the computer-use verification. Normal
            // automated runs complete immediately after the lifecycle assertions.
            if (int.TryParse(Environment.GetEnvironmentVariable("SVS_TEST_START_WATCH_UI_HOLD_SECONDS"), out var holdSeconds) && holdSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(holdSeconds, 180)));

            await viewModel.DisposeAsync();
            await restartedTransport.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await viewModel.DisposeAsync();
            window.Close();
            await broadcastCancellation.CancelAsync();
            await broadcast;
            foreach (var entry in logger.Entries.Where(entry => entry.Level == AppLogLevel.Error))
                Console.WriteLine(entry.Message);
        }

        async Task PublishLivePlaylistAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            try
            {
                while (await timer.WaitForNextTickAsync(broadcastCancellation.Token))
                    server.UpdatePlaylist(BuildStartWatchLivePlaylist(3 + (int)(broadcastClock.ElapsedMilliseconds / 2000), segments.Length));
            }
            catch (OperationCanceledException) when (broadcastCancellation.IsCancellationRequested) { }
        }
    });

    private static string BuildStartWatchLivePlaylist(int last, int segmentCount)
    {
        var first = Math.Max(0, last - 3);
        var playlist = new StringBuilder($"#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:{first}\n" +
            $"#EXT-X-DISCONTINUITY-SEQUENCE:{Math.Max(0, (first - 1) / segmentCount)}\n");
        for (var index = first; index <= last; index++)
        {
            if (index > 0 && index % segmentCount == 0) playlist.AppendLine("#EXT-X-DISCONTINUITY");
            playlist.AppendLine($"#EXTINF:2.000000,\nsegment{index:D6}.ts");
        }
        return playlist.ToString();
    }

    private static IPlaybackEngine GetStartWatchPlaybackEngine(StreamTabViewModel tab) =>
        (IPlaybackEngine)typeof(StreamTabViewModel).GetField("playbackEngine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;

    private static Process ObserveStartWatchTransport(StreamTabViewModel tab)
    {
        var session = typeof(StreamTabViewModel).GetField("streamSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;
        var process = (Process)session.GetType().GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        var observed = Process.GetProcessById(process.Id);
        _ = observed.SafeHandle; // Retain this exact process even if its ID is later reused.
        return observed;
    }

    private static async Task WaitForStartWatchFramesAsync(IPlaybackEngine engine)
    {
        await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var sample) && sample.DisplayedPictures > 0,
            TimeSpan.FromSeconds(8));
        Assert.True(engine.TryGetPlaybackHealth(out var before));
        await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var after) &&
            after.Generation == before.Generation && after.DisplayedPictures > before.DisplayedPictures,
            TimeSpan.FromSeconds(4));
    }

    private sealed class NativeStartWatchReplayResolver : IReplayResolver
    {
        internal int RequestCount { get; private set; }
        internal CancellationToken LastToken { get; private set; }

        public async Task<ReplaySessionInfo> ResolveCurrentReplayAsync(StreamTarget target, string quality, AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            LastToken = cancellationToken;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Replay discovery should remain pending until the tab cancels it.");
        }
    }
}

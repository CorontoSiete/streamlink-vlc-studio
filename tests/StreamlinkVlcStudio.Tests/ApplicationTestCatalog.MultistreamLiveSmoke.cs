using StreamlinkVlcStudio.Infrastructure.Logging;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> MultistreamLiveSmokeTests =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_MULTISTREAM_LIVE_URLS"))
            ? [("multistream live smoke: mixed providers open play chat and close twice", MultistreamLiveSmokeAsync)]
            : [];

    private static Task MultistreamLiveSmokeAsync() => TestSta.RunAsync(async () =>
    {
        var urls = Environment.GetEnvironmentVariable("SVS_TEST_MULTISTREAM_LIVE_URLS")!
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var targets = urls.Select(url => StreamInputParser.Parse(url, PlatformKind.Twitch)).ToArray();
        Assert.True(targets.Length is 4 or 8);
        Assert.Equal(2, targets.Select(target => target.Platform).Distinct().Count());
        var seconds = int.Parse(Environment.GetEnvironmentVariable("SVS_TEST_MULTISTREAM_LIVE_SECONDS") ?? "20", CultureInfo.InvariantCulture);
        var warmup = int.Parse(Environment.GetEnvironmentVariable("SVS_TEST_MULTISTREAM_LIVE_WARMUP") ?? "10", CultureInfo.InvariantCulture);
        var cycles = int.Parse(Environment.GetEnvironmentVariable("SVS_TEST_MULTISTREAM_LIVE_CYCLES") ?? "2", CultureInfo.InvariantCulture);
        Assert.True(seconds is >= 10 and <= 300 && warmup is >= 0 and <= 120 && cycles is >= 1 and <= 4);
        var output = Path.GetFullPath(Environment.GetEnvironmentVariable("SVS_TEST_MULTISTREAM_LIVE_OUTPUT")!);
        Directory.CreateDirectory(output);
        var results = new List<object>();
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            await using var logger = new FileAppLogger(Path.Combine(output, "cycle-" + cycle));
            var settings = new AppSettings
            {
                StreamlinkPath = ExecutableResolver.FindStreamlink(),
                VlcDirectory = ExecutableResolver.FindVlcDirectory(),
                MultiStreamEnabled = true,
                VideoRendererMode = VideoRendererMode.Gdi
            };
            settings.Chat.Layout = ChatLayout.Overlay;
            settings.Chat.ConnectAutomatically = true;
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var streamlink = new StreamlinkService(logger);
            await using var mediaGateway = new TwitchMutedVodPlaybackGateway(logger);
            var viewModel = TestViewModels.CreateMain(settings, new FakeSettingsService(settings), streamlink,
                new BenchmarkPlaybackFactory(new LibVlcPlaybackEngineFactory(logger, settings.Chat, mediaGateway), output),
                new ChatClientFactory(settings, logger), logger, action => dispatcher.BeginInvoke(action),
                viewerCountService: new ViewerCountService(logger),
                streamMetadataService: new StreamMetadataService(logger),
                replayResolver: new ReplayResolver(logger, streamlink),
                vodChatProvider: new VodChatProvider(logger));
            var window = new MainWindow
            {
                Title = "Stream Studio - mixed live validation",
                Width = 1360,
                Height = 900,
                Left = 20,
                Top = 20,
                WindowStartupLocation = WindowStartupLocation.Manual,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);
            using var processes = new MultistreamProcessMetrics();
            try
            {
                window.Show(); SetMainWindowHandle(window);
                foreach (var target in targets) await viewModel.OpenStreamAsync(target);
                await TestWait.UntilAsync(() => viewModel.Tabs.All(tab => tab.Status == PlaybackStatus.Playing && !tab.IsBusy),
                    TimeSpan.FromSeconds(65));
                var engines = viewModel.Tabs.Select(tab => (LibVlcPlaybackEngine)GetStartWatchPlaybackEngine(tab)).ToArray();
                foreach (var engine in engines) await WaitForStartWatchFramesAsync(engine);
                await TestWait.UntilAsync(() => viewModel.Tabs.All(tab => tab.NativeOverlay.IsNativeOverlayChatCurrent(settings)),
                    TimeSpan.FromSeconds(15));
                await Task.Delay(TimeSpan.FromSeconds(warmup));
                var dimensions = new List<object>();
                foreach (var engine in engines)
                {
                    int width = 0, height = 0;
                    await TestWait.UntilAsync(() => engine.TryGetVideoSize(out width, out height), TimeSpan.FromSeconds(5));
                    dimensions.Add(new { width, height });
                }
                var warmProcesses = processes.Sample();
                _ = processes.SampleGpu(warmProcesses.Select(process => process.Pid));
                var before = engines.Select(ReadBenchmarkVideo).ToArray();
                var beforeProcesses = processes.Sample();
                var beforeCpu = processes.TotalCpuSeconds;
                var beforeAllocated = GC.GetTotalAllocatedBytes(precise: true);
                var beforeCollections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                var samples = new List<object>();
                Console.WriteLine($"Live cycle {cycle}: measuring {targets.Length} streams for {seconds}s after {warmup}s warmup; pid {Environment.ProcessId}.");
                var measurementStartedUtc = DateTime.UtcNow;
                var clock = Stopwatch.StartNew();
                for (var sample = 1; sample <= seconds; sample++)
                {
                    var remaining = TimeSpan.FromSeconds(sample) - clock.Elapsed;
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
                    var tree = processes.Sample();
                    var gpu = processes.SampleGpu(tree.Select(process => process.Pid));
                    samples.Add(new
                    {
                        elapsed = clock.Elapsed.TotalSeconds,
                        processes = tree,
                        gpu,
                        gpuMemory = processes.SampleGpuMemory(tree.Select(process => process.Pid)),
                        video = engines.Select(engine =>
                        {
                            var stats = ReadBenchmarkVideo(engine);
                            return new { stats.DisplayedPictures, stats.LostPictures, stats.DecodedVideo, stats.DecodedAudio, stats.LostAudioBuffers };
                        }).ToArray(),
                        managedAllocated = GC.GetTotalAllocatedBytes(precise: false) - beforeAllocated
                    });
                }
                var afterProcesses = processes.Sample();
                var elapsed = clock.Elapsed.TotalSeconds;
                var cpu = processes.TotalCpuSeconds - beforeCpu;
                var after = engines.Select(ReadBenchmarkVideo).ToArray();
                var displayed = before.Zip(after, (begin, end) => end.DisplayedPictures - begin.DisplayedPictures).ToArray();
                results.Add(new
                {
                    cycle,
                    version = Environment.GetEnvironmentVariable("SVS_BENCHMARK_VERSION"),
                    utc = DateTime.UtcNow,
                    measurementStartedUtc,
                    warmupSeconds = warmup,
                    measuredSeconds = elapsed,
                    cpuSeconds = cpu,
                    cpuCores = cpu / elapsed,
                    managedAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - beforeAllocated,
                    collections = Enumerable.Range(0, 3).Select(index => GC.CollectionCount(index) - beforeCollections[index]).ToArray(),
                    channels = targets.Select(target => target.Url).ToArray(),
                    qualities = viewModel.Tabs.Select(tab => tab.Quality).ToArray(),
                    dimensions,
                    displayed,
                    lost = before.Zip(after, (begin, end) => end.LostPictures - begin.LostPictures).ToArray(),
                    audioDecoded = before.Zip(after, (begin, end) => end.DecodedAudio - begin.DecodedAudio).ToArray(),
                    audioLost = before.Zip(after, (begin, end) => end.LostAudioBuffers - begin.LostAudioBuffers).ToArray(),
                    chatMessages = viewModel.Tabs.Select(tab => tab.ChatMessages.Count).ToArray(),
                    audioSelected = viewModel.Tabs.Select(tab => !tab.IsAutoMuted).ToArray(),
                    dockedRows = FindVisualDescendants<DockedChatMessageTextBlock>(window).Count(),
                    visibleDockedRows = FindVisualDescendants<DockedChatMessageTextBlock>(window).Count(row => row.IsVisible),
                    beforeProcesses,
                    afterProcesses,
                    gpuError = processes.GpuError,
                    samples,
                    applicationSha256 = FileHash(typeof(MainViewModel).Assembly.Location),
                    infrastructureSha256 = FileHash(typeof(StreamlinkService).Assembly.Location),
                    testsSha256 = FileHash(typeof(ApplicationTestCatalog).Assembly.Location)
                });
                await File.WriteAllTextAsync(Path.Combine(output, "results.json"),
                    JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
                {
                    using var capture = new WindowCaptureTestSession(new System.Windows.Interop.WindowInteropHelper(window).Handle);
                    using var bitmap = await capture.NextFrameAsync();
                    bitmap.Save(Path.Combine(output, $"cycle-{cycle}.png"));
                }
                Assert.True(displayed.All(frames => frames >= seconds * 20), "One live input did not keep advancing.");
                Assert.True(processes.GpuError is null, processes.GpuError ?? "GPU counters unavailable.");
                Assert.True(viewModel.Tabs.All(tab => tab.NativeOverlay.IsNativeOverlayChatCurrent(settings)));
                Assert.Equal(1, viewModel.Tabs.Count(tab => !tab.IsAutoMuted));
                Console.WriteLine($"Live cycle {cycle}: {cpu / elapsed:F3} CPU cores; {string.Join(',', displayed)} displayed pictures; native chat controllers active on all {targets.Length} tabs.");
            }
            finally
            {
                _ = processes.Sample();
                await viewModel.DisposeAsync();
                window.Close();
                await processes.AssertChildrenExitedAsync();
                await File.WriteAllTextAsync(Path.Combine(output, $"cycle-{cycle}-cleanup.json"), "{\"allObservedChildrenExited\":true}\n");
            }
        }
    });
}

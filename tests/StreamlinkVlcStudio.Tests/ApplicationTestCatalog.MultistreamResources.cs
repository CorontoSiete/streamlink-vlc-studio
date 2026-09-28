using System.IO.MemoryMappedFiles;
using StreamlinkVlcStudio.Infrastructure.Logging;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> MultistreamResourceBenchmarks =>
        Environment.GetEnvironmentVariable("SVS_BENCHMARK_MULTISTREAM") == "1"
            ? [("multistream application benchmark: real WPF Streamlink VLC and native chat", BenchmarkMultistreamApplicationAsync)]
            : [];

    private static Task BenchmarkMultistreamApplicationAsync() => TestSta.RunAsync(async () =>
    {
        var count = int.Parse(Environment.GetEnvironmentVariable("SVS_BENCHMARK_STREAMS") ?? "4", CultureInfo.InvariantCulture);
        var mode = Environment.GetEnvironmentVariable("SVS_BENCHMARK_CHAT") ?? "quiet";
        var seconds = int.Parse(Environment.GetEnvironmentVariable("SVS_BENCHMARK_SECONDS") ?? "30", CultureInfo.InvariantCulture);
        var warmup = int.Parse(Environment.GetEnvironmentVariable("SVS_BENCHMARK_WARMUP") ?? "10", CultureInfo.InvariantCulture);
        Assert.True(count is 4 or 8 && seconds > 0 && warmup >= 0 && mode is "quiet" or "busy" or "animated");
        var fixture = Environment.GetEnvironmentVariable("SVS_TEST_START_WATCH_HLS_DIRECTORY")!;
        var renderer = Environment.GetEnvironmentVariable("SVS_BENCHMARK_NATIVE_RENDERER")!;
        var output = Path.GetFullPath(Environment.GetEnvironmentVariable("SVS_BENCHMARK_OUTPUT")!);
        var outputDirectory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(outputDirectory);
        Assert.True(File.Exists(renderer), "Compile render-resources.exe with scripts/test-chat-render-resources.ps1.");
        await using var logger = new FileAppLogger(Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(output) + "-logs"));
        var settings = new AppSettings
        {
            StreamlinkPath = ExecutableResolver.FindStreamlink(),
            VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY") ?? ExecutableResolver.FindVlcDirectory(),
            VideoRendererMode = VideoRendererMode.Gdi,
            MultiStreamEnabled = true,
            LowLatency = false
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Overlay;
        var segments = Directory.GetFiles(fixture, "segment*.ts").Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToArray();
        Assert.True(segments.Length >= 30);
        var broadcastClock = Stopwatch.StartNew();
        await using var server = new ReplayFixtureServer(fixture, BuildStartWatchLivePlaylist(3, segments.Length),
            resolve: key => key.StartsWith("/segment", StringComparison.Ordinal) && key.EndsWith(".ts", StringComparison.Ordinal) &&
                int.TryParse(key.AsSpan(8, key.Length - 11), out var number) && number >= 0 ? segments[number % segments.Length] : null,
            loadFiles: false);
        using var broadcastCancellation = new CancellationTokenSource();
        var broadcast = PublishAsync();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var factory = new BenchmarkPlaybackFactory(new LibVlcPlaybackEngineFactory(logger, settings.Chat), outputDirectory);
        var viewModel = TestViewModels.CreateMain(settings, new FakeSettingsService(settings), new StreamlinkService(logger),
            factory, new FakeChatClientFactory(), logger, action => dispatcher.BeginInvoke(action),
            replayResolver: new NativeStartWatchReplayResolver());
        var window = new MainWindow
        {
            Title = $"Stream Studio - {count} streams / {mode} resource measurement",
            Width = 1360,
            Height = 900,
            Left = 20,
            Top = 20,
            WindowStartupLocation = WindowStartupLocation.Manual,
            DataContext = viewModel
        };
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, viewModel);
        var chats = new List<BenchmarkNativeChat>();
        using var metrics = new MultistreamProcessMetrics();
        try
        {
            window.Show(); SetMainWindowHandle(window);
            for (var index = 0; index < count; index++)
            {
                var target = StreamInputParser.FromChannel(index % 2 == 0 ? PlatformKind.Twitch : PlatformKind.Kick, $"local{index}")
                    with
                { Url = "hls://" + server.Uri };
                await viewModel.OpenStreamAsync(target);
            }
            Assert.Equal(count, viewModel.Tabs.Count);
            await TestWait.UntilAsync(() => viewModel.Tabs.All(tab => tab.Status == PlaybackStatus.Playing && !tab.IsBusy && tab.IsVideoVisible),
                TimeSpan.FromSeconds(25));
            var engines = viewModel.Tabs.Select(tab => (LibVlcPlaybackEngine)GetStartWatchPlaybackEngine(tab)).ToArray();
            foreach (var engine in engines)
            {
                await WaitForStartWatchFramesAsync(engine);
                Assert.True(engine.UsesNativeOverlay && engine.HardwareOverlayComposition);
                Assert.Equal("dxva2", LibVlcRendererSelection.GetHardwareDecodingOption(engine.RendererMode, true, engine.HardwareOverlayComposition));
                // The nonblocking native query can fail while another callback
                // holds the player gate, even after pictures have appeared.
                int width = 0, height = 0;
                await TestWait.UntilAsync(() => engine.TryGetVideoSize(out width, out height), TimeSpan.FromSeconds(5));
                Assert.Equal(1920, width); Assert.Equal(1080, height);
                chats.Add(new BenchmarkNativeChat(renderer, mode, engine.NativeOverlayPipeName!));
            }
            using var currentProcess = Process.GetCurrentProcess();
            var loadedCore = currentProcess.Modules.Cast<ProcessModule>().SingleOrDefault(module =>
                Path.GetFileName(module.FileName).Equals("libvlccore.dll", StringComparison.OrdinalIgnoreCase));
            Assert.True(loadedCore is not null, "The active libvlccore module was not found in the benchmark process.");
            var loadedCorePath = loadedCore!.FileName;
            var loadedCoreSha256 = FileHash(loadedCorePath);
            await TestWait.UntilAsync(() => chats.All(chat => chat.Frames > 0), TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromSeconds(warmup));
            var warmProcesses = metrics.Sample();
            _ = metrics.SampleGpu(warmProcesses.Select(process => process.Pid));
            var beforeVideo = engines.Select(ReadBenchmarkVideo).ToArray();
            var beforeHls = server.BytesServed;
            var beforeRequests = server.Requests.Count;
            var beforePipe = chats.Sum(chat => chat.Bytes);
            var beforeFrames = chats.Select(chat => chat.Frames).ToArray();
            var beforeAllocated = GC.GetTotalAllocatedBytes(precise: true);
            var beforeCollections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            var samples = new List<object>();
            var beforeProcesses = metrics.Sample();
            var measurementStartedUtc = DateTime.UtcNow;
            var clock = Stopwatch.StartNew();
            for (var sample = 1; sample <= seconds; sample++)
            {
                var remaining = TimeSpan.FromSeconds(sample) - clock.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
                var processes = metrics.Sample();
                var gpu = metrics.SampleGpu(processes.Select(process => process.Pid));
                samples.Add(new
                {
                    elapsed = clock.Elapsed.TotalSeconds,
                    processes,
                    gpu,
                    gpuMemory = metrics.SampleGpuMemory(processes.Select(process => process.Pid)),
                    managedAllocated = GC.GetTotalAllocatedBytes(precise: false) - beforeAllocated,
                    hlsBytes = server.BytesServed - beforeHls,
                    pipeBytes = chats.Sum(chat => chat.Bytes) - beforePipe
                });
            }
            var afterProcesses = metrics.Sample();
            var elapsed = clock.Elapsed.TotalSeconds;
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - beforeAllocated;
            var afterVideo = engines.Select(ReadBenchmarkVideo).ToArray();
            var frames = beforeVideo.Zip(afterVideo, (begin, end) => new
            {
                decoded = end.DecodedVideo - begin.DecodedVideo,
                displayed = end.DisplayedPictures - begin.DisplayedPictures,
                lost = end.LostPictures - begin.LostPictures,
                audioDecoded = end.DecodedAudio - begin.DecodedAudio,
                audioLost = end.LostAudioBuffers - begin.LostAudioBuffers
            }).ToArray();
            var chatFrames = chats.Select((chat, index) => chat.Frames - beforeFrames[index]).ToArray();
            var cpu = afterProcesses.Sum(process => process.CpuSeconds) - beforeProcesses.Sum(process => process.CpuSeconds);
            var result = new
            {
                version = Environment.GetEnvironmentVariable("SVS_BENCHMARK_VERSION"),
                measurementStartedUtc,
                streams = count,
                chat = mode,
                warmupSeconds = warmup,
                measuredSeconds = elapsed,
                decoder = "dxva2",
                vlcCorePath = loadedCorePath,
                vlcCoreSha256 = loadedCoreSha256,
                width = 1920,
                height = 1080,
                fps = 60,
                logicalCpus = Environment.ProcessorCount,
                cpuSeconds = cpu,
                cpuCores = cpu / elapsed,
                managedAllocatedBytes = allocated,
                collections = Enumerable.Range(0, 3).Select(index => GC.CollectionCount(index) - beforeCollections[index]).ToArray(),
                hlsBytes = server.BytesServed - beforeHls,
                hlsRequests = server.Requests.Count - beforeRequests,
                pipeBytes = chats.Sum(chat => chat.Bytes) - beforePipe,
                chatFrames,
                frames,
                beforeProcesses,
                afterProcesses,
                gpuError = metrics.GpuError,
                samples,
                applicationSha256 = FileHash(typeof(MainViewModel).Assembly.Location),
                infrastructureSha256 = FileHash(typeof(StreamlinkService).Assembly.Location),
                rendererSha256 = FileHash(renderer)
            };
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{count} streams, {mode}: {cpu:F3} CPU seconds / {elapsed:F3} seconds; {allocated} managed bytes; " +
                $"frames {string.Join(',', frames.Select(frame => frame.displayed))}; lost {string.Join(',', frames.Select(frame => frame.lost))}.");
            // Save the desktop after measurement so capture allocations/GPU work do not affect the samples.
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
            {
                using var capture = new WindowCaptureTestSession(new System.Windows.Interop.WindowInteropHelper(window).Handle);
                using var bitmap = await capture.NextFrameAsync();
                bitmap.Save(Path.ChangeExtension(output, ".png"));
                var visibleChat = MeasureBenchmarkChat(window, bitmap);
                await File.WriteAllTextAsync(Path.ChangeExtension(output, ".visibility.json"),
                    JsonSerializer.Serialize(visibleChat, new JsonSerializerOptions { WriteIndented = true }));
                Assert.Equal(count, visibleChat.Length);
                Assert.True(visibleChat.All(pane => pane.GlyphPixels >= 5), "Chat disappeared from a video pane; inspect the capture and visibility counts.");
            }
            Assert.True(frames.All(frame => frame.displayed >= seconds * 55), "Playback cadence fell below 55 fps.");
            Assert.True(metrics.GpuError is null, metrics.GpuError ?? "GPU counters unavailable.");
            Assert.True(frames.All(frame => frame.lost == 0), "VLC reported lost pictures; inspect the saved raw result.");
            Assert.True(chatFrames.All(received => received >= seconds * 10), "Native chat heartbeat stopped.");
        }
        finally
        {
            foreach (var chat in chats) await chat.DisposeAsync();
            await viewModel.DisposeAsync();
            window.Close();
            await broadcastCancellation.CancelAsync();
            await broadcast;
            await metrics.AssertChildrenExitedAsync();
        }

        async Task PublishAsync()
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

    private static string FileHash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed record BenchmarkChatVisibility(int Left, int Top, int Right, int Bottom, int GlyphPixels);

    private static BenchmarkChatVisibility[] MeasureBenchmarkChat(MainWindow window, System.Drawing.Bitmap capture)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        return FindVisualDescendants<VideoSurface>(window)
            .Where(surface => surface.IsVisible && surface.ActualWidth > 0 && surface.ActualHeight > 0)
            .Select(surface =>
            {
                var origin = surface.TranslatePoint(new System.Windows.Point(), window);
                var scale = Math.Min(surface.ActualWidth / 1920, surface.ActualHeight / 1080);
                var videoX = origin.X + (surface.ActualWidth - 1920 * scale) / 2;
                var videoY = origin.Y + (surface.ActualHeight - 1080 * scale) / 2;
                // The fixture's red bar has no gray/white pixels in this source-space
                // rectangle. Contrast here therefore proves actual chat reached the
                // final composed window, including the small eight-stream tiles.
                var left = Math.Max(0, (int)((videoX + 40 * scale) * dpi.DpiScaleX));
                var top = Math.Max(0, (int)((videoY + 52 * scale) * dpi.DpiScaleY));
                var right = Math.Min(capture.Width, (int)((videoX + 300 * scale) * dpi.DpiScaleX));
                var bottom = Math.Min(capture.Height, (int)((videoY + 230 * scale) * dpi.DpiScaleY));
                var pixels = 0;
                for (var y = top; y < bottom; y++)
                    for (var x = left; x < right; x++)
                    {
                        var color = capture.GetPixel(x, y);
                        if (color.R > 150 && color.G > 40 && color.B > 40 && Math.Abs(color.G - color.B) < 25) pixels++;
                    }
                return new BenchmarkChatVisibility(left, top, right, bottom, pixels);
            }).ToArray();
    }

    private static LibVlcNative.MediaStatistics ReadBenchmarkVideo(LibVlcPlaybackEngine engine)
    {
        var media = (IntPtr)typeof(LibVlcPlaybackEngine).GetField("media", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
        Assert.True(LibVlcNative.libvlc_media_get_stats(media, out var stats) != 0);
        return stats;
    }

    private sealed class BenchmarkPlaybackFactory(LibVlcPlaybackEngineFactory factory, string outputDirectory) : IPlaybackEngineFactory
    {
        private readonly SemaphoreSlim creationGate = new(1, 1);

        // Apply the same untimed setup to both versions: the original VLC wrapper
        // can race localization/environment initialization when its cache is cold.
        public async Task<IPlaybackEngine> CreateAsync(string vlcDirectory, bool enableNativeOverlay = true,
            string? nativeOverlayPositionStatePath = null, CancellationToken cancellationToken = default,
            VideoRendererMode rendererMode = VideoRendererMode.Automatic)
        {
            await creationGate.WaitAsync(cancellationToken);
            try
            {
                var engine = await factory.CreateAsync(vlcDirectory, enableNativeOverlay,
                    Path.Combine(outputDirectory, $"position-{Guid.NewGuid():N}.txt"), cancellationToken, rendererMode);
                MultistreamVlcDiagnostics.Attach((LibVlcPlaybackEngine)engine, outputDirectory);
                return engine;
            }
            finally { creationGate.Release(); }
        }
    }

    private sealed class BenchmarkNativeChat : IAsyncDisposable
    {
        private readonly EventWaitHandle stop;
        private readonly MemoryMappedFile mapping;
        private readonly MemoryMappedViewAccessor view;
        private readonly Process process;
        private readonly Task<string> output, error;
        internal long Bytes => view.ReadInt64(0);
        internal long Frames => view.ReadInt64(8);

        internal BenchmarkNativeChat(string renderer, string mode, string pipe)
        {
            var key = $"Local\\svs-benchmark-{Guid.NewGuid():N}";
            stop = new EventWaitHandle(false, EventResetMode.ManualReset, key + "-stop");
            mapping = MemoryMappedFile.CreateNew(key, 16);
            view = mapping.CreateViewAccessor();
            var info = BoundedProcessRunner.CreateRedirectedStartInfo(renderer, ["--stream-pipe", mode, pipe, key + "-stop", key]);
            process = Process.Start(info)!;
            output = process.StandardOutput.ReadToEndAsync();
            error = process.StandardError.ReadToEndAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                stop.Set();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                Assert.Equal(0, process.ExitCode);
                var diagnostics = (await output) + (await error);
                if (!string.IsNullOrWhiteSpace(diagnostics)) Console.WriteLine(diagnostics);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose(); view.Dispose(); mapping.Dispose(); stop.Dispose();
            }
        }
    }
}

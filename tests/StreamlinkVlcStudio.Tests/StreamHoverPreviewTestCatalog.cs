internal static partial class StreamHoverPreviewTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All =>
    [
        ("stream hover preview: disabled and VOD targets start no work", DisabledAsync),
        ("stream hover preview: setting persists both values and upgrades default off", PersistenceAsync),
        ("stream hover preview: leaving during the hover delay starts no work", DebounceAsync),
        ("stream hover preview: request preserves platform and custom transport settings", RequestAsync),
        ("stream hover preview: replacing rapid hovers waits for cleanup and rejects stale frames", ReplacementAsync),
        ("stream hover preview: disabling cancels startup and clears buffered video", DisableAsync),
        ("stream hover preview: failures preserve fallback and allow another hover", FailureAsync),
        ("stream hover preview: shutdown awaits cleanup and refuses future work", ShutdownAsync),
        ("stream hover preview: only live cards are eligible", EligibilityAsync),
        ("stream hover preview: frame mailbox stays bounded", MailboxAsync),
        ("stream hover preview: missing VLC releases the opened transport", () => TransportCleanupAsync(false)),
        ("stream hover preview: canceled transport arrival is disposed before decoding", () => TransportCleanupAsync(true)),
        ("stream hover preview: native VLC presents changing video and releases transport", NativePlaybackAsync),
        .. CleanupTests,
        .. StreamHoverPreviewSourceTestCatalog.All,
        .. string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_HOVER_CHANNEL"))
            ? Array.Empty<(string, Func<Task>)>()
            : [("stream hover preview: real provider delivers changing live video", LiveProviderAsync)]
    ];

    private static AppSettings Settings() => new()
    {
        EnableStreamHoverPreviews = true,
        StreamlinkPath = "streamlink.exe",
        VlcDirectory = "vlc"
    };
    private static StreamTarget Target(string channel = "first", PlatformKind platform = PlatformKind.Twitch) =>
        StreamInputParser.Parse(channel, platform);
    private static LivePreviewFrame Frame(byte value = 1) => new(1, 1, [value, 0, 0, 0]);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task WaitAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    private static async Task DisabledAsync()
    {
        var settings = new AppSettings();
        var count = 0;
        await using var controller = new StreamHoverPreviewController(settings, new MemoryLogger(),
            (_, _, _, _) => { count++; return Task.CompletedTask; }, TimeSpan.Zero);
        Assert.True(controller.Begin(Target()) is null);
        settings.EnableStreamHoverPreviews = true;
        Assert.True(controller.Begin(Target() with { Kind = StreamTargetKind.TwitchVod }) is null);
        Assert.Equal(0, count);
    }

    private static async Task PersistenceAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "StreamStudioTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, "{}");
            var service = new JsonSettingsService(path);
            var settings = await service.LoadAsync();
            Assert.Equal(false, settings.EnableStreamHoverPreviews);
            var changes = 0;
            settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppSettings.EnableStreamHoverPreviews)) changes++; };
            foreach (var enabled in new[] { true, false })
            {
                settings.EnableStreamHoverPreviews = enabled;
                await service.SaveAsync(settings);
                Assert.Equal(enabled, (await new JsonSettingsService(path).LoadAsync()).EnableStreamHoverPreviews);
            }
            Assert.Equal(2, changes);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task DebounceAsync()
    {
        var calls = 0;
        var controller = new StreamHoverPreviewController(Settings(), new MemoryLogger(),
            (_, _, _, _) => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
        var session = controller.Begin(Target())!;
        session.Stop();
        await controller.DisposeAsync();
        Assert.Equal(0, calls);
        Assert.Equal(StreamHoverPreviewState.Stopped, session.State);
    }

    private static async Task RequestAsync()
    {
        var settings = Settings();
        settings.CustomStreamlinkArguments = "--http-header \"Referer=https://kick.com/\"";
        settings.LowLatency = false;
        StreamTransportRequest? captured = null;
        string? directory = null;
        await using var controller = new StreamHoverPreviewController(settings, new MemoryLogger(),
            async (request, vlc, present, token) =>
            {
                captured = request;
                directory = vlc;
                present(Frame());
                await Task.Delay(Timeout.Infinite, token);
            }, TimeSpan.Zero);
        var session = controller.Begin(Target("second", PlatformKind.Kick))!;
        await WaitAsync(() => session.State == StreamHoverPreviewState.Playing);
        Assert.Equal(PlatformKind.Kick, captured!.Target.Platform);
        Assert.Equal("360p,360p30,360p60,480p,480p30,480p60,best", captured.Quality);
        Assert.Equal("streamlink.exe", captured.StreamlinkPath);
        Assert.Equal(false, captured.LowLatency);
        Assert.Equal(true, captured.IsMultiStream);
        Assert.SequenceEqual(new[] { "--http-header", "Referer=https://kick.com/" }, captured.CustomArguments);
        Assert.Equal("vlc", directory);
    }

    private static async Task ReplacementAsync()
    {
        var release = Signal();
        var started = Signal();
        var requests = new ConcurrentQueue<string>();
        Action<LivePreviewFrame>? lateFrame = null;
        await using var controller = new StreamHoverPreviewController(Settings(), new MemoryLogger(),
            async (request, _, present, token) =>
            {
                requests.Enqueue(request.Target.Channel);
                if (request.Target.Channel == "first")
                {
                    lateFrame = present;
                    started.SetResult();
                    await release.Task; // A provider that ignores cancellation during startup/cleanup.
                }
                else
                {
                    present(Frame(3));
                    await Task.Delay(Timeout.Infinite, token);
                }
            }, TimeSpan.Zero);
        var first = controller.Begin(Target())!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = controller.Begin(Target("second"))!;
        var third = controller.Begin(Target("third"))!;
        try
        {
            await Task.Delay(60);
            Assert.SequenceEqual(new[] { "first" }, requests);
            lateFrame!(Frame(9));
            Assert.True(first.TakeFrame() is null);
            Assert.Equal(StreamHoverPreviewState.Stopped, second.State);
        }
        finally { release.TrySetResult(); }
        await WaitAsync(() => third.State == StreamHoverPreviewState.Playing);
        Assert.SequenceEqual(new[] { "first", "third" }, requests);
        Assert.Equal((byte)3, third.TakeFrame()!.Pixels[0]);
    }

    private static async Task DisableAsync()
    {
        var settings = Settings();
        var started = Signal();
        var canceled = Signal();
        Action<LivePreviewFrame>? publish = null;
        await using var controller = new StreamHoverPreviewController(settings, new MemoryLogger(),
            async (_, _, present, token) =>
            {
                publish = present;
                started.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { canceled.SetResult(); }
            }, TimeSpan.Zero);
        var session = controller.Begin(Target())!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        settings.EnableStreamHoverPreviews = false;
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        publish!(Frame());
        Assert.Equal(StreamHoverPreviewState.Stopped, session.State);
        Assert.True(session.TakeFrame() is null);
        Assert.True(controller.Begin(Target()) is null);
    }

    private static async Task FailureAsync()
    {
        var logger = new MemoryLogger();
        var attempts = 0;
        await using var controller = new StreamHoverPreviewController(Settings(), logger,
            (_, _, _, _) => { attempts++; throw new InvalidOperationException("secret-signed-url"); }, TimeSpan.Zero);
        var first = controller.Begin(Target())!;
        await WaitAsync(() => first.State == StreamHoverPreviewState.Unavailable);
        Assert.True(first.TakeFrame() is null);
        first.Stop();
        var second = controller.Begin(Target())!;
        await WaitAsync(() => second.State == StreamHoverPreviewState.Unavailable);
        await controller.DisposeAsync();
        Assert.Equal(2, attempts);
        Assert.True(logger.Entries.All(entry => !entry.Message.Contains("secret-signed-url")));
    }

    private static async Task ShutdownAsync()
    {
        var started = Signal();
        var release = Signal();
        var controller = new StreamHoverPreviewController(Settings(), new MemoryLogger(),
            async (_, _, _, _) => { started.SetResult(); await release.Task; }, TimeSpan.Zero);
        var session = controller.Begin(Target())!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = controller.DisposeAsync().AsTask();
        try
        {
            Assert.True(session.Token.IsCancellationRequested);
            Assert.Equal(false, closing.IsCompleted);
            Assert.True(controller.Begin(Target()) is null);
        }
        finally { release.TrySetResult(); await closing; }
        session.Stop(); // Cleanup may finish before a card's Unloaded notification.
    }

    private static Task EligibilityAsync()
    {
        var live = new LiveStreamCardViewModel(new(LiveStreamCardSource.Followed, Target(), PlatformKind.Twitch,
            "first", "First", "Title", "Category", 3, "", "", null, false, "en"), (_, _) => Task.CompletedTask);
        Assert.Equal(Target(), StreamHoverPreview.GetLiveTarget(live));
        var offline = new StreamSearchResultViewModel(Target(), new StreamlinkProbeResult(false, "Unavailable"), null, (_, _) => Task.CompletedTask);
        Assert.True(StreamHoverPreview.GetLiveTarget(offline) is null);
        var search = new StreamSearchResultViewModel(Target(), new StreamlinkProbeResult(true, "Live"), null, (_, _) => Task.CompletedTask);
        Assert.Equal(Target(), StreamHoverPreview.GetLiveTarget(search));
        var recent = new RecentStreamViewModel(new RecentStreamSettings { Channel = "first", Url = Target().Url },
            (_, _) => Task.CompletedTask, _ => Task.CompletedTask);
        Assert.True(StreamHoverPreview.GetLiveTarget(recent) is null);
        foreach (var state in new[] { RecentStreamLiveState.Live, RecentStreamLiveState.Offline, RecentStreamLiveState.Checking })
        {
            recent = new RecentStreamViewModel(new RecentStreamSettings { Channel = "first", Url = Target().Url },
                (_, _) => Task.CompletedTask, _ => Task.CompletedTask, new(state, DateTimeOffset.UtcNow, ""));
            Assert.Equal(state == RecentStreamLiveState.Live, StreamHoverPreview.GetLiveTarget(recent) is not null);
        }
        Assert.True(StreamHoverPreview.GetLiveTarget(null) is null);
        return Task.CompletedTask;
    }

    private static Task MailboxAsync()
    {
        var session = new StreamHoverPreviewSession(Target(), new MemoryLogger());
        for (var index = 0; index < 100; index++) session.Present(Frame((byte)index));
        Assert.Equal((byte)99, session.TakeFrame()!.Pixels[0]);
        Assert.True(session.TakeFrame() is null);
        session.Present(Frame());
        session.Stop();
        Assert.True(session.TakeFrame() is null);
        session.Complete();
        return Task.CompletedTask;
    }

    private static async Task NativePlaybackAsync()
    {
        var vlc = ExecutableResolver.FindVlcDirectory();
        if (vlc is null) throw new InteractiveDesktopTestSkippedException("VLC is not installed.");
        var uri = new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-audio", "index3.ts"));
        var service = new FixtureTransport(uri);
        var red = 0;
        var green = Signal();
        var count = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = Task.Run(() => new LibVlcLivePreview(service).RunAsync(
            new(Target(), LibVlcLivePreview.QualityPreference, "fixture", false, []), vlc, frame =>
            {
                Interlocked.Increment(ref count);
                Assert.Equal(320, frame.Width);
                Assert.Equal(180, frame.Height);
                var offset = (frame.Width * (frame.Height / 2) + frame.Width / 2) * 4;
                if (frame.Pixels[offset + 2] > 180 && frame.Pixels[offset + 1] < 80) Interlocked.Exchange(ref red, 1);
                if (frame.Pixels[offset + 1] > 180 && frame.Pixels[offset + 2] < 80 && Volatile.Read(ref red) == 1)
                    green.TrySetResult();
            }, cancellation.Token));
        try { await green.Task.WaitAsync(TimeSpan.FromSeconds(12)); }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => run);
        }
        Assert.True(service.Disposed);
        var finalCount = count;
        await Task.Delay(150);
        Assert.Equal(finalCount, count);
        Assert.True(count >= 3);
        Console.WriteLine($"Native hover preview: {count} frames, red-to-green transition, transport disposed.");
    }

    private static async Task TransportCleanupAsync(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var service = new FixtureTransport(new Uri("http://127.0.0.1:1/unused"), cancel ? cancellation.Cancel : null);
        var run = new LibVlcLivePreview(service).RunAsync(new(Target(), LibVlcLivePreview.QualityPreference, "fixture", false, []),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), _ => throw new InvalidOperationException("No frames expected"), cancellation.Token);
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => run);
        else await Assert.ThrowsAsync<FileNotFoundException>(() => run);
        Assert.True(service.Disposed);
    }

    private static async Task LiveProviderAsync()
    {
        var settings = await new JsonSettingsService().LoadAsync();
        var target = StreamInputParser.Parse(Environment.GetEnvironmentVariable("SVS_TEST_HOVER_CHANNEL")!, PlatformKind.Twitch);
        var logger = new MemoryLogger();
        var done = Signal();
        byte[]? first = null;
        var count = 0;
        var elapsed = Stopwatch.StartNew();
        long transportReadyAt = -1;
        long firstFrameAt = -1;
        long lastFrameAt = -1;
        long longestFrameGap = 0;
        long cleanupMilliseconds = 0;
        var observationMilliseconds = int.TryParse(Environment.GetEnvironmentVariable("SVS_TEST_HOVER_DURATION_SECONDS"),
            out var observationSeconds) ? Math.Clamp(observationSeconds, 12, 40) * 1000 : 12_000;
        logger.EntryWritten += (_, entry) =>
        {
            if (entry.Message.StartsWith("Streamlink HTTP transport ready", StringComparison.Ordinal) ||
                entry.Message.StartsWith("Direct preview source ready", StringComparison.Ordinal))
                Interlocked.CompareExchange(ref transportReadyAt, elapsed.ElapsedMilliseconds, -1);
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = Task.Run(() => new LibVlcLivePreview(new StreamlinkService(logger), logger).RunAsync(
            new(target, LibVlcLivePreview.QualityPreference, settings.StreamlinkPath ?? ExecutableResolver.FindStreamlink()!, settings.LowLatency,
                CommandLineTokenizer.Tokenize(settings.CustomStreamlinkArguments), true),
            settings.VlcDirectory ?? ExecutableResolver.FindVlcDirectory()!, frame =>
            {
                var now = elapsed.ElapsedMilliseconds;
                Interlocked.CompareExchange(ref firstFrameAt, now, -1);
                if (lastFrameAt >= 0) longestFrameGap = Math.Max(longestFrameGap, now - lastFrameAt);
                lastFrameAt = now;
                count++;
                first ??= frame.Pixels;
                // Observe several segment boundaries, not just the initially buffered video.
                if (count >= 30 && now - firstFrameAt >= observationMilliseconds && !first.AsSpan().SequenceEqual(frame.Pixels)) done.TrySetResult();
            }, cancellation.Token));
        try
        {
            var completed = await Task.WhenAny(run, done.Task);
            await completed;
        }
        finally
        {
            var cleanup = Stopwatch.StartNew();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => run);
            cleanupMilliseconds = cleanup.ElapsedMilliseconds;
        }
        Console.WriteLine($"Live {target.Platform} hover preview: transport ready {transportReadyAt} ms, first frame {firstFrameAt} ms, " +
            $"{count} changing frames over {lastFrameAt - firstFrameAt} ms, longest frame gap {longestFrameGap} ms; " +
            $"canceled and cleaned up in {cleanupMilliseconds} ms.");
    }

    private sealed class FixtureTransport(Uri uri, Action? arriving = null) : IStreamlinkService, IStreamTransportSession
    {
        public bool Disposed { get; private set; }
        public Uri PlaybackUri => uri;
        public event EventHandler<string>? LogLineReceived { add { } remove { } }
        public Task<IStreamTransportSession> StartExternalHttpAsync(StreamTransportRequest request, CancellationToken cancellationToken = default)
        {
            arriving?.Invoke();
            return Task.FromResult<IStreamTransportSession>(this);
        }
        public Task<StreamlinkProbeResult> ProbeStreamsAsync(StreamTransportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StreamlinkResolvedUrl> ResolveStreamUrlAsync(StreamTransportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}

using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Io;

internal static class CodeReviewTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("review: atomic writes preserve the destination on cancellation and failure", AtomicWritesPreserveDestinationAsync),
        ("review: settings accept a UTF-8 BOM without discarding saved preferences", SettingsAcceptUtf8BomAsync),
        ("review: duplicate settings properties recover without crashing", DuplicateSettingsRecoverAsync),
        ("review: HTTP text removes UTF-8 BOM with and without a charset", HttpTextRemovesUtf8BomAsync),
        ("review: HTTP response body reads retain the request timeout", HttpBodyReadTimesOutAsync),
        ("review: HTTP response reads honor both caller cancellation tokens", HttpBodyReadCancellationAsync),
        ("review: optional JSON reads share bounds decoding and timeout handling", OptionalJsonReadsAsync),
        ("review: update checks leave the checking state after timeout or cancellation", UpdateCheckCancellationAsync),
        ("review: playback releases its parking surface when engine disposal fails", PlaybackCleanupAfterDisposeFailureAsync),
        ("review: playback cleanup drains when its error reporting fails", PlaybackCleanupReportingFailureAsync),
        ("review: direct VOD resolution honors the custom HTTP environment", DirectVodHttpEnvironmentAsync),
        ("review: chat links keep balanced delimiters while trimming long suffixes", ChatLinkDelimitersAsync),
        ("review: replay seek commits retain their requested offset during clock refresh", ReplaySeekCommitOffsetAsync),
        ("review: portable command line parsing matches Windows for whitespace and quotes", PortableTokenizerMatchesWindows)
    ];

    private static async Task AtomicWritesPreserveDestinationAsync()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "state.json");
            await File.WriteAllTextAsync(path, "original");
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAsync<OperationCanceledException>(() => AtomicFile.WriteAsync(
                path,
                async (stream, token) =>
                {
                    await stream.WriteAsync("replacement"u8.ToArray(), token);
                    cancellation.Cancel();
                },
                cancellation.Token));
            Assert.Equal("original", await File.ReadAllTextAsync(path));
            Assert.Equal(1, Directory.GetFiles(root).Length);

            await Assert.ThrowsAsync<IOException>(() => AtomicFile.WriteAsync(
                path,
                (_, _) => throw new IOException("write failed"),
                CancellationToken.None));
            Assert.Equal("original", await File.ReadAllTextAsync(path));
            Assert.Equal(1, Directory.GetFiles(root).Length);

            var invoked = false;
            await Assert.ThrowsAsync<OperationCanceledException>(() => AtomicFile.WriteAsync(
                path,
                (_, _) => { invoked = true; return Task.CompletedTask; },
                cancellation.Token));
            Assert.Equal(false, invoked);

            await AtomicFile.WriteAsync(path, (stream, token) =>
                stream.WriteAsync("updated"u8.ToArray(), token).AsTask(), CancellationToken.None);
            Assert.Equal("updated", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SettingsAcceptUtf8BomAsync()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(path, "{\"DefaultQuality\":\"720p\"}", new UTF8Encoding(true));
            var service = new JsonSettingsService(path);
            Assert.Equal("720p", (await service.LoadAsync()).DefaultQuality);
            Assert.True(File.Exists(path));
            Assert.Equal(1, Directory.GetFiles(root).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task DuplicateSettingsRecoverAsync()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "settings.json");
            string[] payloads =
            [
                "{\"Chat\":{},\"Chat\":{}}",
                "{\"Chat\":{},\"chat\":{}}",
                "{\"Chat\":{\"TwitchOAuthToken\":\"first\",\"twitchOAuthToken\":\"second\"}}",
                "{\"DefaultQuality\":\"720p\",\"defaultQuality\":\"best\"}"
            ];
            foreach (var payload in payloads)
            {
                await File.WriteAllTextAsync(path, payload);
                var settings = await new JsonSettingsService(path).LoadAsync();
                Assert.NotNull(settings.Chat);
                Assert.Equal(false, File.Exists(path));
            }

            Assert.Equal(payloads.Length, Directory.GetFiles(root, "*.invalid-*").Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task HttpTextRemovesUtf8BomAsync()
    {
        foreach (var charset in new string?[] { null, "utf-8", "\"UTF-8\"" })
        {
            using var content = new ByteArrayContent([.. Encoding.UTF8.Preamble, .. "{\"ok\":true}"u8]);
            content.Headers.ContentType = new("application/json") { CharSet = charset };
            var text = await BoundedHttpContentReader.ReadJsonAsync(content);
            using var document = JsonDocument.Parse(text);
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        }
    }

    private static async Task HttpBodyReadTimesOutAsync()
    {
        foreach (var copyBody in new[] { false, true })
        {
            using var stream = new StalledReadStream();
            using var client = new HttpClient(new FakeHttpMessageHandler(_ => new(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            }))
            { Timeout = TimeSpan.FromMilliseconds(300) };
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
            using var response = await BoundedHttpResponseSender.SendAsync(client, request);
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                (copyBody ? response.Content.CopyToAsync(Stream.Null) :
                    BoundedByteReader.ReadOrThrowAsync(response.Content, 1024)).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(stream.SawCancellation);
        }
    }

    private static async Task HttpBodyReadCancellationAsync()
    {
        foreach (var cancelSend in new[] { true, false })
        {
            using var stream = new StalledReadStream();
            using var client = new HttpClient(new FakeHttpMessageHandler(_ => new(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            }))
            { Timeout = Timeout.InfiniteTimeSpan };
            using var sendCancellation = new CancellationTokenSource();
            using var readCancellation = new CancellationTokenSource();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
            using var response = await BoundedHttpResponseSender.SendAsync(client, request, sendCancellation.Token);
            var read = BoundedByteReader.ReadOrThrowAsync(response.Content, 1024, readCancellation.Token);
            await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            (cancelSend ? sendCancellation : readCancellation).Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.True(stream.SawCancellation);
        }
    }

    private static async Task OptionalJsonReadsAsync()
    {
        foreach (var payload in new[] { "\uFEFF{\"ok\":true}", "{broken", new string('a', 100) })
        {
            using var client = new HttpClient(new FakeHttpMessageHandler(_ => new(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            }));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
            using var document = await OptionalHttpJsonReader.SendAsync(client, request, 32);
            Assert.Equal(payload.StartsWith('\uFEFF'), document is not null);
        }

        using var stalledClient = new HttpClient(new FakeHttpMessageHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledReadStream())
        }))
        { Timeout = TimeSpan.FromMilliseconds(150) };
        using var stalledRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
        Assert.Equal<JsonDocument?>(null, await OptionalHttpJsonReader.SendAsync(stalledClient, stalledRequest, 32)
            .WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private static async Task UpdateCheckCancellationAsync()
    {
        foreach (var callerCancels in new[] { false, true })
        {
            var root = CreateTemporaryDirectory();
            try
            {
                using var stream = new StalledReadStream();
                using var client = new HttpClient(new FakeHttpMessageHandler(request => new(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StreamContent(stream)
                }))
                { Timeout = callerCancels ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(150) };
                using var service = new StagedAppUpdateService(new MemoryLogger(), client, root, Path.Combine(root, "updates"));
                using var cancellation = new CancellationTokenSource();
                var check = service.CheckAsync(UpdateCheckReason.Manual, cancellation.Token);
                if (callerCancels)
                {
                    await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
                    cancellation.Cancel();
                }

                await Assert.ThrowsAsync<OperationCanceledException>(() => check.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.Equal(callerCancels ? AppUpdatePhase.Idle : AppUpdatePhase.Failed, service.State.Phase);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task PlaybackCleanupAfterDisposeFailureAsync()
    {
        var engine = new FakePlaybackEngine { DisposeAction = () => throw new IOException("dispose failed") };
        var surface = new DisposalProbe();
        await new PlaybackResourceCoordinator(new MemoryLogger(), () => "test").StopAsync(
            engine, null, surface, CancellationToken.None);
        Assert.True(surface.Disposed);
    }

    private static async Task PlaybackCleanupReportingFailureAsync()
    {
        foreach (var failingDisplayName in new[] { false, true })
        {
            var logger = new MemoryLogger();
            logger.EntryWritten += (_, _) => throw new InvalidOperationException("logger failed");
            var cleanup = new PlaybackCleanupController(logger,
                () => failingDisplayName ? throw new InvalidOperationException("display name failed") : "test");
            var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cleanup.Observe(operation.Task);
            cleanup.Observe(operation.Task);
            var idle = cleanup.IdleTask;
            Assert.Equal(false, idle.IsCompleted);

            operation.SetException(new IOException("cleanup failed"));
            await idle.WaitAsync(TimeSpan.FromSeconds(2));

            var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cleanup.Observe(next.Task);
            Assert.Equal(false, cleanup.IdleTask.IsCompleted);
            next.SetResult();
            await cleanup.IdleTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static Task DirectVodHttpEnvironmentAsync()
    {
        var root = CreateTemporaryDirectory();
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        var request = new StreamTransportRequest(
            StreamInputParser.Parse("https://www.twitch.tv/videos/12345", PlatformKind.Twitch),
            "best", Environment.ProcessPath!, false, []);
        try
        {
            Environment.SetEnvironmentVariable("APPDATA", root);
            foreach (var variable in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY",
                         "REQUESTS_CA_BUNDLE", "CURL_CA_BUNDLE", "NETRC" })
            {
                var previous = Environment.GetEnvironmentVariable(variable);
                try
                {
                    Environment.SetEnvironmentVariable(variable, "custom-streamlink-setting");
                    Assert.Equal(false, DirectVodResolutionPolicy.CanUse(request));
                }
                finally
                {
                    Environment.SetEnvironmentVariable(variable, previous);
                }
            }
            return Task.CompletedTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", appData);
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task ChatLinkDelimitersAsync()
    {
        const string address = "https://example.com/a(b)[c]{d}";
        var suffix = string.Concat(Enumerable.Repeat(")]}", 2048));
        var links = ChatLinkParser.FindLinks($"See {address}{suffix}.!").ToArray();
        Assert.Equal(1, links.Length);
        Assert.Equal(4, links[0].Start);
        Assert.Equal(address.Length, links[0].Length);
        Assert.Equal(address, links[0].Uri.OriginalString);
        return Task.CompletedTask;
    }

    private static async Task ReplaySeekCommitOffsetAsync()
    {
        var playbackFactory = new FakePlaybackEngineFactory();
        var duration = TimeSpan.FromHours(1);
        var replay = new ReplaySessionInfo(PlatformKind.Twitch, "streamer",
            "https://www.twitch.tv/videos/123", "123", null, duration, true, "");
        await using var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch), "best",
            new FakeStreamlinkService(), playbackFactory, new FakeChatClientFactory(),
            new MemoryLogger(), action => action(), replayResolver: new FakeReplayResolver(replay));
        var settings = new AppSettings { StreamlinkPath = "streamlink.exe", VlcDirectory = @"C:\VLC" };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(settings);
        await TestWait.UntilAsync(() => tab.CanSeekReplay, TimeSpan.FromSeconds(2));

        var offset = TimeSpan.FromMinutes(25);
        tab.BeginReplaySeekPreview(offset.TotalSeconds);
        Assert.True(tab.IsReplaySeekPreviewActive);
        var clockRefreshed = false;
        tab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(tab.IsReplaySeekPreviewActive) && !tab.IsReplaySeekPreviewActive)
            {
                // Simulate a queued clock refresh when the preview stops protecting
                // the slider. It must not replace the user's committed seek offset.
                tab.ReplaySeekSliderValue = duration.TotalSeconds;
                clockRefreshed = true;
            }
        };

        await tab.CommitReplaySeekPreviewAsync(offset.TotalSeconds);
        Assert.True(clockRefreshed);
        Assert.True(tab.IsBehindLive);
        Assert.Equal(offset, playbackFactory.Engine!.Position);
    }

    private static Task PortableTokenizerMatchesWindows()
    {
        var portable = typeof(CommandLineTokenizer).GetMethod("TokenizePortable",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var random = new Random(731);
        const string alphabet = "a \t\n\r\u00a0\"\\";
        for (var sample = 0; sample < 5000; sample++)
        {
            var input = "a" + new string(Enumerable.Range(0, random.Next(1, 32))
                .Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var actual = (IReadOnlyList<string>)portable.Invoke(null, [input])!;
            Assert.SequenceEqual(CommandLineTokenizer.Tokenize(input), actual);
        }

        return Task.CompletedTask;
    }

    private static string CreateTemporaryDirectory() =>
        Directory.CreateTempSubdirectory("StreamStudio-review-").FullName;

    internal sealed class StalledReadStream : Stream
    {
        internal bool SawCancellation { get; private set; }
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }
        }
    }

    private sealed class DisposalProbe : IDisposable
    {
        internal bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}

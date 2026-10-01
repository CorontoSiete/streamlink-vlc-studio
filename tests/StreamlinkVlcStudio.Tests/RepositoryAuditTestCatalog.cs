using StreamlinkVlcStudio.Infrastructure.Http;

internal static class RepositoryAuditTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("repository audit: throttled Twitch downloads outlive the HTTP request timeout", () => ThrottledDownloadAsync(PlatformKind.Twitch)),
        ("repository audit: throttled Kick downloads outlive the HTTP request timeout", () => ThrottledDownloadAsync(PlatformKind.Kick)),
        ("repository audit: throttled muted Twitch downloads outlive the HTTP request timeout", () => ThrottledDownloadAsync(PlatformKind.Twitch, muted: true)),
        ("repository audit: slow consumers and progressing transfers keep their network read budget", HttpReadBudgetAsync),
        ("repository audit: stalled HTTP reads still time out in streaming and copy modes", HttpReadTimeoutsAsync),
        ("repository audit: read timeouts preserve both cancellation tokens and infinite timeouts", HttpReadCancellationAsync),
        ("repository audit: HTTP response copies cancel blocked writes with either caller token", HttpCopyCancellationAsync),
        ("repository audit: canceled media reads refund unused bandwidth reservations", BandwidthRefundAsync),
        ("repository audit: download observers cannot block other observers", DownloadObserversAsync),
        ("repository audit: bookmark observers cannot interrupt history updates", BookmarkObserversAsync),
        ("repository audit: update observers cannot interrupt update checks", UpdateObserversAsync),
        ("repository audit: shared event dispatch avoids per-notification allocations", EventDispatchAllocationsAsync),
        ("repository audit: VOD quality normalization preserves download deduplication", DownloadQualityAsync)
    ];

    private static async Task ThrottledDownloadAsync(PlatformKind platform, bool muted = false)
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(platform);
        if (muted)
        {
            fixture.Handler.Put("/redirected/index.m3u8", Encoding.UTF8.GetBytes(
                VodDownloadTestCatalog.DownloadFixture.SimplePlaylist.Replace("segment-a.ts", "segment-a-muted.ts", StringComparison.Ordinal)));
            fixture.Handler.Put("/redirected/segment-a-muted.ts", new byte[8]);
        }
        fixture.Client.Timeout = TimeSpan.FromMilliseconds(150);
        fixture.Service.SetBandwidthLimit(20);
        var item = await fixture.DownloadAsync();
        Assert.Equal(20L, item.BytesDownloaded);
        Assert.Equal(3, fixture.Handler.RequestCount);
    }

    private static async Task HttpReadBudgetAsync()
    {
        foreach (var copyBody in new[] { false, true })
        {
            var bytes = Enumerable.Range(0, 8).Select(index => (byte)index).ToArray();
            using var source = new PacedReadStream(bytes);
            using var client = new HttpClient(new FakeHttpMessageHandler(request => new(HttpStatusCode.OK)
            {
                Content = new StreamContent(source),
                RequestMessage = request
            }))
            { Timeout = TimeSpan.FromMilliseconds(150) };
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
            using var response = await BoundedHttpResponseSender.SendAsync(client, request, useReadTimeout: true);
            await Task.Delay(300);
            using var destination = new MemoryStream();
            if (copyBody) await response.Content.CopyToAsync(destination);
            else
            {
                await using var stream = await response.Content.ReadAsStreamAsync();
                await stream.CopyToAsync(destination);
            }
            Assert.SequenceEqual(bytes, destination.ToArray());
        }
    }

    private static async Task HttpReadTimeoutsAsync()
    {
        foreach (var copyBody in new[] { false, true })
        {
            using var source = new CodeReviewTestCatalog.StalledReadStream();
            using var client = new HttpClient(new FakeHttpMessageHandler(request => new(HttpStatusCode.OK)
            {
                Content = new StreamContent(source),
                RequestMessage = request
            }))
            { Timeout = TimeSpan.FromMilliseconds(150) };
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
            using var response = await BoundedHttpResponseSender.SendAsync(client, request, useReadTimeout: true);
            await Task.Delay(300);
            if (copyBody)
                await Assert.ThrowsAsync<OperationCanceledException>(() => response.Content.CopyToAsync(Stream.Null).WaitAsync(TimeSpan.FromSeconds(2)));
            else
            {
                await using var stream = await response.Content.ReadAsStreamAsync();
                await Assert.ThrowsAsync<OperationCanceledException>(() => stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            }
            Assert.True(source.SawCancellation);
        }
    }

    private static async Task HttpReadCancellationAsync()
    {
        foreach (var cancelSend in new[] { false, true })
        {
            using var source = new CodeReviewTestCatalog.StalledReadStream();
            using var client = new HttpClient(new FakeHttpMessageHandler(request => new(HttpStatusCode.OK)
            {
                Content = new StreamContent(source),
                RequestMessage = request
            }))
            { Timeout = Timeout.InfiniteTimeSpan };
            using var sendCancellation = new CancellationTokenSource();
            using var readCancellation = new CancellationTokenSource();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
            using var response = await BoundedHttpResponseSender.SendAsync(client, request, sendCancellation.Token, useReadTimeout: true);
            await using var stream = await response.Content.ReadAsStreamAsync(readCancellation.Token);
            var reading = stream.ReadAsync(new byte[1], readCancellation.Token).AsTask();
            await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            (cancelSend ? sendCancellation : readCancellation).Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(source.SawCancellation);
        }
    }

    private static async Task BandwidthRefundAsync()
    {
        var limiter = new VodDownloadBandwidthLimiter();
        limiter.SetLimit(1);
        using var source = new CodeReviewTestCatalog.StalledReadStream();
        using var stream = new VodDownloadThrottledStream(source, limiter);
        using var cancellation = new CancellationTokenSource();
        var reading = stream.ReadAsync(new byte[1], cancellation.Token).AsTask();
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => reading);
        Assert.Equal(1, (await limiter.ReserveAsync(1, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromMilliseconds(500))).Count);
    }

    private static async Task HttpCopyCancellationAsync()
    {
        foreach (var cancelSend in new[] { false, true })
        {
            using var client = new HttpClient(new FakeHttpMessageHandler(request => new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1]),
                RequestMessage = request
            }))
            { Timeout = Timeout.InfiniteTimeSpan };
            using var sendCancellation = new CancellationTokenSource();
            using var copyCancellation = new CancellationTokenSource();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
            using var response = await BoundedHttpResponseSender.SendAsync(client, request, sendCancellation.Token, useReadTimeout: true);
            using var destination = new StalledWriteStream();
            var copying = response.Content.CopyToAsync(destination, copyCancellation.Token);
            await destination.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            (cancelSend ? sendCancellation : copyCancellation).Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => copying.WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }

    private static async Task DownloadObserversAsync()
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(PlatformKind.Twitch);
        fixture.Service.DownloadChanged += _ => throw new InvalidOperationException("subscriber failed");
        var completed = new TaskCompletionSource<VodDownloadItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.DownloadChanged += item =>
        {
            if (item.State == VodDownloadState.Completed) completed.TrySetResult(item);
        };
        var downloaded = await fixture.DownloadAsync();
        Assert.Equal(downloaded.Id, (await completed.Task.WaitAsync(TimeSpan.FromSeconds(2))).Id);
    }

    private static async Task BookmarkObserversAsync()
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(PlatformKind.Twitch);
        var logger = new MemoryLogger();
        var history = new JsonVodPlaybackHistory(Path.Combine(fixture.Root, "history.json"), logger);
        history.BookmarkChanged += (_, _) => throw new InvalidOperationException("subscriber failed");
        VodPlaybackBookmark? observed = null;
        history.BookmarkChanged += (_, bookmark) => observed = bookmark;
        var bookmark = new VodPlaybackBookmark(TimeSpan.FromSeconds(12), TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow);
        history.Remember(fixture.Target, bookmark);
        Assert.Equal(bookmark, observed);
        await history.SaveAsync();
        var restored = new JsonVodPlaybackHistory(Path.Combine(fixture.Root, "history.json"), logger);
        Assert.Equal(bookmark, await restored.GetAsync(fixture.Target));
        Assert.True(logger.Entries.Any(entry => entry.Level == AppLogLevel.Warning));
    }

    private static async Task UpdateObserversAsync()
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(PlatformKind.Twitch);
        var requests = 0;
        using var client = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requests++;
            return new(HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
        }));
        using var service = new StagedAppUpdateService(new MemoryLogger(), client, fixture.Root,
            Path.Combine(fixture.Root, "updates"));
        service.StateChanged += (_, _) => throw new InvalidOperationException("subscriber failed");
        var phases = new List<AppUpdatePhase>();
        service.StateChanged += (_, arguments) => phases.Add(arguments.State.Phase);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckAsync(UpdateCheckReason.Manual));
        Assert.Equal(1, requests);
        Assert.SequenceEqual(new[] { AppUpdatePhase.Checking, AppUpdatePhase.Failed }, phases);
        Assert.Equal(AppUpdatePhase.Failed, service.State.Phase);
    }

    private static async Task DownloadQualityAsync()
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(PlatformKind.Twitch);
        var first = await fixture.DownloadAsync();
        var duplicate = await fixture.Service.EnqueueAsync(new VodDownloadRequest(fixture.Target, " BEST ", fixture.Options));
        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(1, (await fixture.Service.GetDownloadsAsync()).Count);
        Assert.Equal(3, fixture.Handler.RequestCount);
    }

    private static Task EventDispatchAllocationsAsync()
    {
        var logger = new MemoryLogger();
        EventHandler<EventArgs> handlers = static (_, _) => { };
        handlers += static (_, _) => { };
        Action<int> callbacks = static _ => { };
        callbacks += static _ => { };
        Action<int, int> pairCallbacks = static (_, _) => { };
        pairCallbacks += static (_, _) => { };

        void Dispatch()
        {
            SafeEventDispatcher.Invoke(handlers, logger, EventArgs.Empty, logger, "audit", "event");
            SafeEventDispatcher.Invoke(callbacks, 1, logger, "audit", "callback");
            SafeEventDispatcher.Invoke(pairCallbacks, 1, 2, logger, "audit", "pair");
        }

        for (var iteration = 0; iteration < 100; iteration++) Dispatch();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 1000; iteration++) Dispatch();
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        return Task.CompletedTask;
    }

    private sealed class StalledWriteStream : MemoryStream
    {
        internal TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    internal sealed class PacedReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(40, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
        }
    }
}

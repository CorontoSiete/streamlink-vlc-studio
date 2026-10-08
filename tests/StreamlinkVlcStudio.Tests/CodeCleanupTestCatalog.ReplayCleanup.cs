internal static partial class CodeCleanupTestCatalog
{
    private const string CleanupRepairPlaylist = "#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXTINF:10,\n0-muted.ts\n#EXT-X-ENDLIST\n";
    private static readonly Uri CleanupRepairPlaylistUri = new("https://d2vi6trrdongqn.cloudfront.net/review/chunked/index.m3u8");

    private static async Task RepairProxyDisposalReentryAsync()
    {
        using var http = new HttpClient();
        var proxy = new TwitchMutedVodRepairProxy(new MemoryLogger(), http, TestReplayUrlSecurity.PublicValidator);
        using var lease = OpenCleanupRepairSession(proxy);
        using var cancellation = (CancellationTokenSource)GetPrivateField(GetCleanupRepairSession(proxy), "requestsCancellation")!;
        Task? reentered = null;
        using var registration = cancellation.Token.Register(() => reentered = proxy.DisposeAsync().AsTask());
        try
        {
            var disposal = proxy.DisposeAsync().AsTask();
            await disposal.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(ReferenceEquals(disposal, reentered), "Session cancellation received a different disposal task.");
            AssertRepairProxyReleased(proxy);
        }
        finally
        {
            registration.Dispose();
            try { await proxy.DisposeAsync(); }
            catch (ObjectDisposedException) { }
        }
    }

    private static async Task RepairProxyCancellationFailureAsync()
    {
        using var http = new HttpClient();
        var proxy = new TwitchMutedVodRepairProxy(new MemoryLogger(), http, TestReplayUrlSecurity.PublicValidator);
        using var cancellation = (CancellationTokenSource)GetPrivateField(proxy, "cancellation")!;
        using var registration = cancellation.Token.Register(() => throw new IOException("Proxy cancellation failed."));
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TrackCleanupRepairWork(proxy, work.Task);
        try
        {
            var disposal = proxy.DisposeAsync().AsTask();
            await TestWait.UntilAsync(() => cancellation.IsCancellationRequested, TimeSpan.FromSeconds(2));
            Assert.True(!disposal.IsCompleted, "Cancellation failure skipped the unfinished request.");
            work.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(3));
            AssertRepairProxyReleased(proxy);
        }
        finally
        {
            work.TrySetResult();
            registration.Dispose();
            try { await proxy.DisposeAsync(); }
            catch (AggregateException) { }
        }
    }

    private static async Task RepairSessionCancellationFailureAsync()
    {
        using var http = new HttpClient();
        await using var proxy = new TwitchMutedVodRepairProxy(new MemoryLogger(), http, TestReplayUrlSecurity.PublicValidator);
        using var lease = OpenCleanupRepairSession(proxy);
        var session = GetCleanupRepairSession(proxy);
        using var cancellation = (CancellationTokenSource)GetPrivateField(session, "requestsCancellation")!;
        using var first = new System.Net.Sockets.TcpClient();
        using var second = new System.Net.Sockets.TcpClient();
        var clients = (HashSet<System.Net.Sockets.TcpClient>)GetPrivateField(session, "clients")!;
        clients.Add(first);
        clients.Add(second);
        var firstHandle = first.Client.SafeHandle;
        var secondHandle = second.Client.SafeHandle;
        using var registration = cancellation.Token.Register(() => throw new IOException("Session cancellation failed."));
        try
        {
            lease.Dispose();
            Assert.True(firstHandle.IsClosed && secondHandle.IsClosed);
            var detach = session.GetType().GetMethod("Detach", BindingFlags.Instance | BindingFlags.NonPublic)!;
            detach.Invoke(session, [first]);
            detach.Invoke(session, [second]);
            Assert.Throws<ObjectDisposedException>(() => _ = cancellation.Token);
        }
        finally
        {
            registration.Dispose();
        }
    }

    private static async Task RepairProxyWorkerFailureAsync()
    {
        using var http = new HttpClient();
        var proxy = new TwitchMutedVodRepairProxy(new MemoryLogger(), http, TestReplayUrlSecurity.PublicValidator);
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TrackCleanupRepairWork(proxy, work.Task);
        var disposal = proxy.DisposeAsync().AsTask();
        Assert.Equal(false, disposal.IsCompleted);
        work.SetException(new IOException("Request worker failed."));
        try
        {
            await Assert.ThrowsAsync<IOException>(() => disposal.WaitAsync(TimeSpan.FromSeconds(3)));
            AssertRepairProxyReleased(proxy);
        }
        finally
        {
            try { await proxy.DisposeAsync(); }
            catch (IOException) { }
        }
    }

    private static async Task RepairProxyDiagnosticsAsync()
    {
        using var http = new HttpClient();
        await using var proxy = new TwitchMutedVodRepairProxy(MemoryLogger.WithWriteFailure(), http, TestReplayUrlSecurity.PublicValidator);
        using var lease = OpenCleanupRepairSession(proxy);
        using var local = new HttpClient();
        var playlist = await local.GetStringAsync(lease.PlaylistUri).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(playlist.Contains("/s/0.ts", StringComparison.Ordinal));
        lease.Dispose();
        using var released = await local.GetAsync(lease.PlaylistUri).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(HttpStatusCode.NotFound, released.StatusCode);
    }

    private static async Task RepairGatewayDiagnosticsAsync()
    {
        using var http = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CleanupRepairPlaylist, Encoding.UTF8, "application/vnd.apple.mpegurl")
        }));
        await using var gateway = new TwitchMutedVodPlaybackGateway(MemoryLogger.WithWriteFailure(), http, TestReplayUrlSecurity.PublicValidator);
        using var source = await gateway.PrepareAsync(CleanupRepairPlaylistUri, new Version(3, 0, 23), default);
        Assert.True(source.PlaybackUri.IsLoopback, "Diagnostic failure discarded the repaired playback source.");
        using var local = new HttpClient();
        var playlist = await local.GetStringAsync(source.PlaybackUri).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(playlist.Contains("/s/0.ts", StringComparison.Ordinal));
    }

    private static async Task RepairGatewayFailureCleanupAsync()
    {
        var gateway = new TwitchMutedVodPlaybackGateway(new MemoryLogger());
        var proxy = (TwitchMutedVodRepairProxy)GetPrivateField(gateway, "proxy")!;
        using var http = (HttpClient)GetPrivateField(gateway, "httpClient")!;
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TrackCleanupRepairWork(proxy, work.Task);
        var disposal = gateway.DisposeAsync().AsTask();
        work.SetException(new IOException("Owned proxy worker failed."));
        try
        {
            await Assert.ThrowsAsync<IOException>(() => disposal.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Throws<ObjectDisposedException>(() => http.Timeout = TimeSpan.FromSeconds(1));
        }
        finally
        {
            try { await gateway.DisposeAsync(); }
            catch (IOException) { }
        }
    }

    private static async Task RepairGatewayConcurrentDisposalAsync()
    {
        using var http = new HttpClient();
        var gateway = new TwitchMutedVodPlaybackGateway(new MemoryLogger(), http, TestReplayUrlSecurity.PublicValidator);
        var proxy = (TwitchMutedVodRepairProxy)GetPrivateField(gateway, "proxy")!;
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TrackCleanupRepairWork(proxy, work.Task);
        try
        {
            var first = gateway.DisposeAsync().AsTask();
            var second = gateway.DisposeAsync().AsTask();
            Assert.True(ReferenceEquals(first, second));
            Assert.Equal(false, second.IsCompleted);
            work.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
            // The injected client belongs to the caller, so the gateway releases only its proxy.
            http.Timeout = TimeSpan.FromSeconds(1);
        }
        finally
        {
            work.TrySetResult();
            await gateway.DisposeAsync();
        }
    }

    private static TwitchMutedVodRepairSession OpenCleanupRepairSession(TwitchMutedVodRepairProxy proxy) =>
        proxy.OpenSession(new TwitchVodPlaylistSource(CleanupRepairPlaylistUri,
            _ => Task.FromResult(CleanupRepairPlaylist)), CleanupRepairPlaylist);

    private static object GetCleanupRepairSession(TwitchMutedVodRepairProxy proxy)
    {
        var sessions = GetPrivateField(proxy, "sessions")!;
        var values = (System.Collections.IEnumerable)sessions.GetType().GetProperty("Values")!.GetValue(sessions)!;
        return values.Cast<object>().Single();
    }

    private static void TrackCleanupRepairWork(TwitchMutedVodRepairProxy proxy, Task work) =>
        typeof(TwitchMutedVodRepairProxy).GetMethod("TrackBackgroundTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(proxy, [work]);

    private static void AssertRepairProxyReleased(TwitchMutedVodRepairProxy proxy)
    {
        Assert.Throws<ObjectDisposedException>(() => _ = ((CancellationTokenSource)GetPrivateField(proxy, "cancellation")!).Token);
        foreach (var name in new[] { "connectionSlots", "transferSlots" })
            Assert.Throws<ObjectDisposedException>(() => ((SemaphoreSlim)GetPrivateField(proxy, name)!).Wait(0));
        Assert.Equal<object?>(null, GetPrivateField(proxy, "listener"));
        Assert.Equal<object?>(null, GetPrivateField(proxy, "acceptLoop"));
    }
}

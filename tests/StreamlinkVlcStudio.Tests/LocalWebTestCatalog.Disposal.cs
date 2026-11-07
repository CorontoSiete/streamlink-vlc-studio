using Microsoft.AspNetCore.Http;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static Task PlayerCleanupAsync() => WithWorkspaceAsync(f => VerifyPlayerCleanupAsync(f.Dependencies));

    private static async Task VerifyPlayerCleanupAsync(LocalWebDependencies dependencies)
    {
        foreach (var closePlayer in new[] { true, false })
        {
            var factory = new FakeChatClientFactory();
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.Client.ConnectHandler = async (_, _, token) =>
            {
                using var registration = token.Register(() =>
                {
                    canceled.TrySetResult();
                    throw new IOException("Chat cancellation failed.");
                });
                connected.TrySetResult();
                await release.Task;
            };
            var workspace = new LocalWebWorkspace(dependencies with { ChatFactory = _ => factory });
            try
            {
                var opened = await Call(workspace, "open", new { input = "https://twitch.tv/streamer" });
                await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var closing = closePlayer
                    ? Call(workspace, "close", new { player = opened.GetProperty("id").GetString()! })
                    : workspace.DisposeAsync().AsTask();
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(false, closing.IsCompleted);
                release.TrySetResult();
                await Assert.ThrowsAsync<AggregateException>(() => closing.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(1, factory.Client.DisposeCount);
                Assert.Equal(32, dependencies.PlaybackSlots.CurrentCount);
                if (closePlayer) await workspace.DisposeAsync();
                else await Assert.ThrowsAsync<AggregateException>(() => workspace.DisposeAsync().AsTask());
            }
            finally
            {
                release.TrySetResult();
                try { await workspace.DisposeAsync(); }
                catch (AggregateException) { }
            }
        }

        var offline = new FailingOfflinePlayback();
        var offlineDependencies = dependencies with { Downloads = new VodLibraryDownloads([], offline) };
        await using var offlineWorkspace = new LocalWebWorkspace(offlineDependencies);
        await Assert.ThrowsAsync<IOException>(() => Call(offlineWorkspace, "open", new { downloadId = Guid.NewGuid() }));
        Assert.Equal(1, offline.DisposeCount);
        Assert.Equal(32, offlineDependencies.PlaybackSlots.CurrentCount);
    }

    private sealed class FailingOfflinePlayback : IVodDownloadPlayback
    {
        public StreamTarget Target { get; } = new(PlatformKind.Twitch, "streamer", "https://twitch.tv/videos/123",
            StreamTargetKind.TwitchVod, "123", LocalMediaPath: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "video.mp4"));
        internal int DisposeCount { get; private set; }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            throw new IOException("Offline playback cleanup failed.");
        }
    }

    private static async Task SharedDisposalAsync()
    {
        var sessionWorkspace = new BlockingDisposalWorkspace();
        await using (var session = new LocalWebSession(IPAddress.Loopback, "user", "tester", "version",
            sessionWorkspace, DateTimeOffset.UtcNow))
        {
            var first = session.DisposeAsync().AsTask();
            try
            {
                await sessionWorkspace.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var second = session.DisposeAsync().AsTask();
                Assert.Equal(false, second.IsCompleted);
                sessionWorkspace.Release.TrySetResult();
                await Task.WhenAll(first, second);
                Assert.Equal(1, sessionWorkspace.DisposeCalls);
            }
            finally
            {
                sessionWorkspace.Release.TrySetResult();
                await first.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        using var certificate = Certificate();
        var serverWorkspace = new BlockingDisposalWorkspace();
        await using var server = await Server(certificate, () => serverWorkspace);
        using var client = Client(certificate, server.Address);
        await Login(client);
        var firstServer = server.DisposeAsync().AsTask();
        try
        {
            await serverWorkspace.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondServer = server.DisposeAsync().AsTask();
            Assert.Equal(false, secondServer.IsCompleted);
            serverWorkspace.Release.TrySetResult();
            await Task.WhenAll(firstServer, secondServer);
            Assert.Equal(1, serverWorkspace.DisposeCalls);
        }
        finally
        {
            serverWorkspace.Release.TrySetResult();
            await firstServer.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class BlockingDisposalWorkspace : ILocalWebWorkspace
    {
        private int disposeCalls;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCalls => Volatile.Read(ref disposeCalls);

        public Task<object?> InvokeAsync(string action, JsonElement input, CancellationToken cancellationToken) =>
            Task.FromResult<object?>(new { ok = true });

        public Task ServeMediaAsync(HttpContext context, string playbackId, string resourceId) => Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref disposeCalls);
            Started.TrySetResult();
            await Release.Task.ConfigureAwait(false);
        }
    }
}

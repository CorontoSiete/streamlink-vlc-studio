using Microsoft.AspNetCore.Http;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
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

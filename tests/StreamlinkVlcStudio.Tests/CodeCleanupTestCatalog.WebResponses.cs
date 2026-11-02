using StreamlinkVlcStudio.App.Wpf.Services;
using StreamlinkVlcStudio.Infrastructure.Http;

internal static partial class CodeCleanupTestCatalog
{
    private static async Task BrowserResponseLateContentAsync()
    {
        foreach (var cancel in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var content = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reading = WebView2ResponseReader.ReadAsync(content.Task, 16,
                cancel ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(100), cancellation.Token);
            if (cancel) cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(3)));
            using var stream = new BrowserResponseStream([1, 2, 3]);
            content.SetResult(stream);
            await stream.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(false, stream.CanRead);
        }
    }

    private static async Task BrowserResponseStalledBodyAsync()
    {
        foreach (var cancel in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            using var stream = new BrowserResponseStream([], stall: true);
            var reading = WebView2ResponseReader.ReadAsync(Task.FromResult<Stream>(stream), 16,
                cancel ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(100), cancellation.Token);
            await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (cancel) cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(false, stream.CanRead);
        }
    }

    private static async Task BrowserResponseBoundsAsync()
    {
        using var valid = new BrowserResponseStream([1, 2, 3]);
        var bytes = await WebView2ResponseReader.ReadAsync(Task.FromResult<Stream>(valid), 3,
            TimeSpan.FromSeconds(3), CancellationToken.None);
        Assert.True(bytes.SequenceEqual(new byte[] { 1, 2, 3 }));
        Assert.Equal(false, valid.CanRead);

        using var oversized = new BrowserResponseStream([1, 2, 3, 4]);
        await Assert.ThrowsAsync<PayloadTooLargeException>(() => WebView2ResponseReader.ReadAsync(
            Task.FromResult<Stream>(oversized), 3, TimeSpan.FromSeconds(3), CancellationToken.None));
        Assert.Equal(false, oversized.CanRead);
    }

    private sealed class BrowserResponseStream(byte[] bytes, bool stall = false) : MemoryStream(bytes)
    {
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            if (stall) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) Disposed.TrySetResult();
        }
    }
}

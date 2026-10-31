using StreamlinkVlcStudio.Infrastructure.Io;

internal static partial class CodeReviewTestCatalog
{
    private static async Task AtomicWriteTemporaryLockAsync()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "download.json");
            await File.WriteAllTextAsync(path, "original");
            var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var writing = AtomicFile.WriteAsync(path,
                (stream, token) => stream.WriteAsync("committed"u8.ToArray(), token).AsTask(),
                CancellationToken.None, flushToDisk: true);
            try
            {
                await Task.Delay(100);
                Assert.Equal("original", await File.ReadAllTextAsync(path));
            }
            finally { await locked.DisposeAsync(); }
            await writing;
            Assert.Equal("committed", await File.ReadAllTextAsync(path));
            Assert.Equal(1, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task AtomicWriteRetryCancellationAsync()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "download.json");
            await File.WriteAllTextAsync(path, "original");
            await using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<OperationCanceledException>(() => AtomicFile.WriteAsync(path,
                (stream, token) => stream.WriteAsync("uncommitted"u8.ToArray(), token).AsTask(),
                cancellation.Token, flushToDisk: true));
            Assert.Equal("original", await File.ReadAllTextAsync(path));
            Assert.Equal(1, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task AtomicWritePermanentLockAsync()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "download.json");
            await File.WriteAllTextAsync(path, "original");
            await using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            await Assert.ThrowsAsync<IOException>(() => AtomicFile.WriteAsync(path,
                (stream, token) => stream.WriteAsync("uncommitted"u8.ToArray(), token).AsTask(),
                CancellationToken.None, flushToDisk: true));
            Assert.Equal("original", await File.ReadAllTextAsync(path));
            Assert.Equal(1, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task AtomicWriteRapidRevisionsAsync()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "download.json");
            for (var revision = 0; revision < 200; revision++)
            {
                var bytes = Encoding.UTF8.GetBytes("{\"Revision\":" + revision + "}");
                await AtomicFile.WriteAsync(path, (stream, token) => stream.WriteAsync(bytes, token).AsTask(),
                    CancellationToken.None, flushToDisk: true);
                Assert.SequenceEqual(bytes, await File.ReadAllBytesAsync(path));
            }
            Assert.Equal(1, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

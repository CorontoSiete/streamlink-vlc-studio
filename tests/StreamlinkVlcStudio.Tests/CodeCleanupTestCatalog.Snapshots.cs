using Microsoft.Win32.SafeHandles;

internal static partial class CodeCleanupTestCatalog
{
    private static async Task SnapshotReplacementSharingAsync(bool vodHistory,
        bool exclusiveReplacement = false, bool cancelRead = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "snapshot-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "snapshot.json");
        var target = StreamInputParser.Parse("https://www.twitch.tv/videos/123", PlatformKind.Twitch);
        var bookmark = new VodPlaybackBookmark(TimeSpan.FromSeconds(24), TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow);
        try
        {
            if (vodHistory)
            {
                var writer = new JsonVodPlaybackHistory(path, new MemoryLogger());
                writer.Remember(target, bookmark);
                await writer.SaveAsync();
            }
            else await new JsonSettingsService(path).SaveAsync(new AppSettings { DefaultQuality = "720p" });

            // File.Replace holds DELETE access on the destination and an exclusive handle
            // on the replacement. Its new directory entry can be visible before that
            // exclusive handle closes. Exercise both windows without changing the file.
            using var replacement = SnapshotFileAccess.CreateFile(path,
                exclusiveReplacement ? 0xc0010000u : 0x00010000u, exclusiveReplacement ? 0u : 7u,
                IntPtr.Zero, 3, 0, IntPtr.Zero);
            Assert.True(!replacement.IsInvalid, $"Could not open the replacement fixture: {Marshal.GetLastPInvokeError()}.");
            using var cancellation = new CancellationTokenSource();
            var historyReader = new JsonVodPlaybackHistory(path, new MemoryLogger());
            var settingsReader = new JsonSettingsService(path);
            var read = ReadExpectedSnapshotAsync(cancellation.Token);
            if (exclusiveReplacement)
            {
                Assert.True(!read.IsCompleted, "A temporary replacement lock was treated as a permanent read failure.");
                if (cancelRead) cancellation.Cancel();
                replacement.Dispose();
            }
            if (cancelRead)
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.True(await ReadExpectedSnapshotAsync(default));
                Assert.Equal(1, Directory.GetFiles(directory).Length);
            }
            else Assert.True(await read.WaitAsync(TimeSpan.FromSeconds(2)));

            async Task<bool> ReadExpectedSnapshotAsync(CancellationToken token) => vodHistory
                ? bookmark == await historyReader.GetAsync(target, token)
                : (await settingsReader.LoadAsync(token)).DefaultQuality == "720p";
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task VodHistoryRecoveryDiagnosticsAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        const string damaged = "{broken";
        await File.WriteAllTextAsync(files.Path, damaged);
        var logger = new MemoryLogger();
        logger.EntryWritten += (_, _) => throw new IOException("Diagnostic output unavailable.");
        var history = files.Create(logger);
        var target = VodResumeTestCatalog.Target();

        Assert.Equal<VodPlaybackBookmark?>(null, await history.GetAsync(target));
        Assert.Equal(damaged, await File.ReadAllTextAsync(Directory.GetFiles(files.Directory, "*.invalid-*").Single()));

        var bookmark = new VodPlaybackBookmark(TimeSpan.FromMinutes(2), TimeSpan.FromHours(2), DateTimeOffset.UtcNow);
        history.Remember(target, bookmark);
        await history.SaveAsync();
        Assert.Equal(bookmark, await files.Create().GetAsync(target));
    }

    private static class SnapshotFileAccess
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint share,
            IntPtr securityAttributes, uint creation, uint flags, IntPtr template);
    }
}

using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Logging;
using StreamlinkVlcStudio.Infrastructure.Threading;

namespace StreamlinkVlcStudio.Infrastructure.Vod;

public sealed partial class VodDownloadService
{
    // Accessed only while holding gate. A playlist lease protects all of its
    // segments, including ones VLC has not opened yet or will reopen on seek.
    private readonly Dictionary<(Guid Id, string Folder), int> playbackUsers = [];
    private readonly HashSet<(Guid Id, string Folder)> pendingCleanup = [];
    private readonly HashSet<Guid> pendingCleanupScans = [];

    private void EnsureDownloadNotInUseCore(Guid id)
    {
        if (playbackUsers.Keys.Any(entry => entry.Id == id))
            throw new InvalidOperationException("Close this VOD's offline playback tab before replacing or deleting its downloaded files.");
    }

    private void EnsureNoMediaWriterCore(Guid id)
    {
        if (jobs.TryGetValue(id, out var job) && job.WorkingFolder is not null)
            throw new InvalidOperationException("Wait for this VOD's download or conversion to stop before replacing or deleting its files.");
    }

    private ValueTask ReleaseOfflinePlaybackAsync(Guid id, string folder) => new(Task.Run(async () =>
    {
        // Player shutdown can finish after service shutdown. The state gate and
        // pending work remain usable so the final player still releases its copy.
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var key = (id, folder);
            if (!playbackUsers.TryGetValue(key, out var count)) return;
            if (count > 1) playbackUsers[key] = count - 1;
            else playbackUsers.Remove(key);
            RetryPendingCleanupCore(id);
        }
        finally { gate.Release(); }
    }));

    private void CleanupTemporaryMediaCore(Guid id)
    {
        QueueDirectoryCleanupCore(id, ".partial");
        QueueDirectoryCleanupCore(id, ".conversion");
    }

    private void CleanupObsoleteMediaCore(Guid id)
    {
        var firstAttempt = pendingCleanupScans.Add(id);
        try
        {
            ValidateItemDirectory(id);
            var activeFolder = GetItemCore(id).MediaFolder;
            foreach (var directory in Directory.GetDirectories(ItemDirectory(id)))
            {
                var folder = Path.GetFileName(directory);
                if (IsMediaFolder(folder) && !folder.Equals(activeFolder, StringComparison.OrdinalIgnoreCase))
                    QueueDirectoryCleanupCore(id, folder);
            }
            pendingCleanupScans.Remove(id);
        }
        catch (Exception exception) when (IsCleanupFailure(exception))
        {
            if (firstAttempt) LogCleanupFailure(id, exception);
        }
        CleanupTemporaryMediaCore(id);
    }

    private void QueueDirectoryCleanupCore(Guid id, string folder)
    {
        var entry = (id, folder);
        var firstAttempt = pendingCleanup.Add(entry);
        TryCleanupDirectoryCore(entry, firstAttempt);
    }

    private void TryCleanupDirectoryCore((Guid Id, string Folder) entry, bool reportFailure)
    {
        if (!items.TryGetValue(entry.Id, out var item) ||
            entry.Folder.Equals(item.MediaFolder, StringComparison.OrdinalIgnoreCase))
        {
            pendingCleanup.Remove(entry);
            return;
        }
        if (playbackUsers.Keys.Any(user => user.Id == entry.Id &&
                user.Folder.Equals(entry.Folder, StringComparison.OrdinalIgnoreCase)) ||
            (jobs.TryGetValue(entry.Id, out var job) && entry.Folder == job.WorkingFolder)) return;
        // A failed current copy may need an older generation for recovery. Retry
        // obsolete-media cleanup only once the download has a complete copy again.
        if (IsMediaFolder(entry.Folder) && item.State is not
            (VodDownloadState.Completed or VodDownloadState.ConversionQueued or VodDownloadState.Converting)) return;
        try
        {
            DeleteOwnedDirectory(entry.Id, Path.Combine(ItemDirectory(entry.Id), entry.Folder));
            pendingCleanup.Remove(entry);
        }
        catch (Exception exception) when (IsCleanupFailure(exception))
        {
            if (reportFailure) LogCleanupFailure(entry.Id, exception);
        }
    }

    private void RetryPendingCleanupCore(Guid? id = null)
    {
        foreach (var entry in pendingCleanupScans.Where(entry => id is null || entry == id).ToArray())
            CleanupObsoleteMediaCore(entry);
        foreach (var entry in pendingCleanup.Where(entry => id is null || entry.Id == id).ToArray())
            TryCleanupDirectoryCore(entry, reportFailure: false);
    }

    private async Task ProcessCleanupAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(shutdown.Token).ConfigureAwait(false))
            {
                await gate.WaitAsync(shutdown.Token).ConfigureAwait(false);
                try { RetryPendingCleanupCore(); }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    private void LogCleanupFailure(Guid id, Exception exception) =>
        logger.WriteSafely(AppLogLevel.Warning, "VOD downloads",
            $"Unused video files for download {id:N} could not be removed; cleanup will retry.", exception);

    private static bool IsCleanupFailure(Exception exception) => exception is IOException or UnauthorizedAccessException;

    private static void DeleteValidatedTree(string path)
    {
        OfflineVodPackage.RequireRegularPath(path);
        Exception? failure = null;
        foreach (var entry in Directory.GetFileSystemEntries(path))
        {
            try
            {
                OfflineVodPackage.RequireRegularPath(entry);
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.Directory)) DeleteValidatedTree(entry);
                else
                {
                    if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                    File.Delete(entry);
                }
            }
            catch (Exception exception) when (IsCleanupFailure(exception)) { failure ??= exception; }
        }
        try
        {
            OfflineVodPackage.RequireRegularPath(path);
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            Directory.Delete(path);
        }
        catch (Exception exception) when (IsCleanupFailure(exception)) { failure ??= exception; }
        if (failure is not null) throw new IOException("Some unused VOD files could not be removed.", failure);
    }

    private sealed class OfflinePlayback(VodDownloadService? owner, Guid id, string folder, StreamTarget target) : IVodDownloadPlayback
    {
        private VodDownloadService? owner = owner;
        private readonly object disposalGate = new();
        private Task? disposal;
        private bool disposed;
        public StreamTarget Target { get; } = target;

        public ValueTask DisposeAsync() => new(AsyncDisposal.Begin(disposalGate, ref disposed, ref disposal, DisposeCoreAsync));

        private Task DisposeCoreAsync() => Interlocked.Exchange(ref owner, null) is { } service
            ? service.ReleaseOfflinePlaybackAsync(id, folder).AsTask()
            : Task.CompletedTask;
    }
}

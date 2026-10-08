namespace StreamlinkVlcStudio.Infrastructure.Io;

/// <summary>
/// Reads complete snapshots and writes through a temporary sibling, replacing the destination in one
/// filesystem operation, so a reader never observes a partially written file. The temporary
/// file always lives in the same directory as the destination (a cross-volume rename is not
/// atomic) and is removed when the write fails. Reads allow replacement and briefly retry its locks.
/// </summary>
internal static class AtomicFile
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int ReadRetryCount = 20;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>Opens a complete snapshot, allowing replacement and retrying its brief exclusive lock.</summary>
    internal static async Task<FileStream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    bufferSize: 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (IOException exception) when (exception.HResult == SharingViolation && attempt < ReadRetryCount)
            {
                await Task.Delay(ReadRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static async Task WriteAsync(
        string destinationPath,
        Func<Stream, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken,
        bool flushToDisk = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(writeAsync);
        cancellationToken.ThrowIfCancellationRequested();

        destinationPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81_920,
                FileOptions.Asynchronous | (flushToDisk ? FileOptions.WriteThrough : FileOptions.None)))
            {
                await writeAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (flushToDisk)
                {
                    stream.Flush(flushToDisk: true);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destinationPath))
            {
                try
                {
                    File.Replace(temporaryPath, destinationPath, null, ignoreMetadataErrors: true);
                }
                catch (FileNotFoundException)
                {
                    File.Move(temporaryPath, destinationPath, overwrite: true);
                }
            }
            else
            {
                File.Move(temporaryPath, destinationPath, overwrite: true);
            }
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    internal static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a cleanup race only leaves a stale temporary file behind; the
            // destination is either fully written or untouched either way.
        }
    }
}

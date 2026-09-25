namespace StreamlinkVlcStudio.Infrastructure.Io;

/// <summary>
/// Writes a file through a temporary sibling and then replaces the destination in one
/// filesystem operation, so a reader never observes a partially written file. The temporary
/// file always lives in the same directory as the destination (a cross-volume rename is not
/// atomic) and is removed when the write fails.
/// </summary>
internal static class AtomicFile
{
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
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void TryDelete(string path)
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

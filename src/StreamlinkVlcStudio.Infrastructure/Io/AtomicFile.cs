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
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(writeAsync);

        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
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
                FileShare.None))
            {
                await writeAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
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

namespace StreamlinkVlcStudio.Infrastructure.Http;

/// <summary>
/// Reads small remote or local byte payloads without buffering more than the caller's limit.
/// A null result means that the payload was empty or exceeded <paramref name="maxBytes"/>.
/// </summary>
internal static class BoundedByteReader
{
    private const int BufferSize = 81_920;

    public static async Task<byte[]?> ReadAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateMaximum(maxBytes);

        try
        {
            var bytes = await ReadOrThrowAsync(content, maxBytes, cancellationToken)
                .ConfigureAwait(false);
            return bytes.Length == 0 ? null : bytes;
        }
        catch (PayloadTooLargeException)
        {
            return null;
        }
    }

    public static async Task<byte[]?> ReadFileAsync(
        string path,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateMaximum(maxBytes);

        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Length > maxBytes)
        {
            return null;
        }

        await using var stream = new FileStream(
            file.FullName,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
        return await ReadAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<byte[]?> ReadAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateMaximum(maxBytes);

        try
        {
            var bytes = await ReadOrThrowAsync(stream, maxBytes, cancellationToken)
                .ConfigureAwait(false);
            return bytes.Length == 0 ? null : bytes;
        }
        catch (PayloadTooLargeException)
        {
            return null;
        }
    }

    internal static async Task<byte[]> ReadOrThrowAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateMaximum(maxBytes);

        if (content.Headers.ContentLength is { } contentLength && contentLength > maxBytes)
        {
            throw new PayloadTooLargeException(maxBytes);
        }

        await using var stream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadOrThrowAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadOrThrowAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateMaximum(maxBytes);

        using var destination = new MemoryStream(Math.Min(maxBytes, BufferSize));
        var buffer = new byte[Math.Min(maxBytes, BufferSize)];
        long totalBytes = 0;
        while (true)
        {
            var bytesRead = await stream
                .ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytes += bytesRead;
            if (totalBytes > maxBytes)
            {
                throw new PayloadTooLargeException(maxBytes);
            }

            await destination
                .WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                .ConfigureAwait(false);
        }

        return destination.ToArray();
    }

    private static void ValidateMaximum(int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxBytes, 0);
    }
}

internal sealed class PayloadTooLargeException(int maximumBytes)
    : IOException($"Payload exceeded the {maximumBytes:N0}-byte limit.")
{
    internal int MaximumBytes { get; } = maximumBytes;
}

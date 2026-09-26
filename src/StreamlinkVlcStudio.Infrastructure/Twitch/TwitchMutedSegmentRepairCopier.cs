using System.Buffers;
using StreamlinkVlcStudio.Core.Twitch;

namespace StreamlinkVlcStudio.Infrastructure.Twitch;

internal readonly record struct TwitchMutedSegmentRepairResult(long BytesCopied, int Repairs);

/// <summary>
/// The segment could not be read completely from its source. Kept apart from a failing
/// destination, because the player dropping its connection (seek, stop) is routine.
/// </summary>
internal sealed class TwitchMutedSegmentSourceException(string message, Exception innerException)
    : IOException(message, innerException);

/// <summary>
/// Streams a muted Twitch segment to the player while repairing it, so playback can start on the
/// first packets instead of waiting for the whole segment to download.
/// </summary>
internal static class TwitchMutedSegmentRepairCopier
{
    private const int PacketsPerBuffer = 512;

    /// <exception cref="InvalidDataException">The segment is larger than <paramref name="maxBytes"/>.</exception>
    /// <exception cref="TwitchMutedSegmentSourceException">Reading <paramref name="source"/> failed.</exception>
    /// <exception cref="TimeoutException">
    /// Neither side made progress for <paramref name="idleTimeout"/>.
    /// </exception>
    internal static async Task<TwitchMutedSegmentRepairResult> CopyAsync(
        Stream source,
        Stream destination,
        long maxBytes,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken, bool repairTimestamps = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);

        const int bufferLength = TwitchMutedSegmentSanitizer.PacketSize * PacketsPerBuffer;
        var readBuffer = ArrayPool<byte>.Shared.Rent(bufferLength);
        var writeBuffer = ArrayPool<byte>.Shared.Rent(bufferLength);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            long bytesCopied = 0;
            var repairs = 0;
            // Bytes of a packet whose remainder has not arrived yet; network reads do not end on
            // packet boundaries, and only complete packets can be inspected.
            var buffered = 0;
            while (true)
            {
                idle.CancelAfter(idleTimeout);
                var read = await ReadSourceAsync(
                        source,
                        readBuffer.AsMemory(buffered, bufferLength - buffered),
                        idle.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                buffered += read;
                if (bytesCopied + buffered > maxBytes)
                {
                    throw new InvalidDataException("The muted segment exceeded the repair size limit.");
                }

                var complete = buffered - (buffered % TwitchMutedSegmentSanitizer.PacketSize);
                if (complete == 0)
                {
                    continue;
                }

                if (repairTimestamps)
                    repairs += TwitchMutedSegmentSanitizer.Repair(
                        readBuffer.AsSpan(0, complete), writeBuffer.AsSpan(0, complete));
                else
                    readBuffer.AsSpan(0, complete).CopyTo(writeBuffer);
                idle.CancelAfter(idleTimeout);
                await destination.WriteAsync(writeBuffer.AsMemory(0, complete), idle.Token).ConfigureAwait(false);
                bytesCopied += complete;
                buffered -= complete;
                readBuffer.AsSpan(complete, buffered).CopyTo(readBuffer);
            }

            if (buffered > 0)
            {
                // A trailing partial packet cannot be inspected; forward it unchanged.
                idle.CancelAfter(idleTimeout);
                await destination.WriteAsync(readBuffer.AsMemory(0, buffered), idle.Token).ConfigureAwait(false);
                bytesCopied += buffered;
            }

            return new TwitchMutedSegmentRepairResult(bytesCopied, repairs);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The muted segment transfer made no progress for {idleTimeout.TotalSeconds:0.#} seconds.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            ArrayPool<byte>.Shared.Return(writeBuffer);
        }
    }

    private static async ValueTask<int> ReadSourceAsync(
        Stream source,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        try
        {
            return await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // HttpClient reports a body that ends before its Content-Length this way.
            throw new TwitchMutedSegmentSourceException("The segment source ended before the segment was complete.", ex);
        }
    }
}

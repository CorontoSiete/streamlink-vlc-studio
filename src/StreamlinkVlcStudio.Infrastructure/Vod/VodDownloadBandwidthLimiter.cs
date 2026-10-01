using System.Diagnostics;

namespace StreamlinkVlcStudio.Infrastructure.Vod;

internal readonly record struct VodDownloadReadReservation(int Count, long Version);

internal sealed class VodDownloadBandwidthLimiter
{
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly object gate = new();
    private long bytesPerSecond;
    private long version;
    private long updatedAt = Stopwatch.GetTimestamp();
    private double availableBytes;

    internal void SetLimit(long limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        lock (gate)
        {
            if (bytesPerSecond == limit) return;
            bytesPerSecond = limit;
            availableBytes = 0;
            updatedAt = Stopwatch.GetTimestamp();
            version++;
        }
    }

    internal async ValueTask<VodDownloadReadReservation> ReserveAsync(int requestedBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestedBytes);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            if (bytesPerSecond == 0 || requestedBytes == 0) return new(requestedBytes, version);

        await readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan delay;
                lock (gate)
                {
                    if (bytesPerSecond == 0) return new(requestedBytes, version);
                    var now = Stopwatch.GetTimestamp();
                    availableBytes = Math.Min(Math.Max(1, bytesPerSecond / 10d),
                        availableBytes + Stopwatch.GetElapsedTime(updatedAt, now).TotalSeconds * bytesPerSecond);
                    updatedAt = now;
                    var count = (int)Math.Min(requestedBytes, Math.Max(1, bytesPerSecond / 20));
                    if (availableBytes >= count)
                    {
                        availableBytes -= count;
                        return new(count, version);
                    }
                    delay = TimeSpan.FromSeconds(Math.Min(0.1, (count - availableBytes) / bytesPerSecond));
                }
                await Task.Delay(delay < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : delay,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally { readGate.Release(); }
    }

    internal void ReturnUnusedBytes(VodDownloadReadReservation reservation, int receivedBytes)
    {
        lock (gate)
            if (bytesPerSecond != 0 && reservation.Version == version)
                availableBytes = Math.Min(Math.Max(1, bytesPerSecond / 10d), availableBytes + reservation.Count - receivedBytes);
    }
}

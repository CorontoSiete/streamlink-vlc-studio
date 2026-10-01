namespace StreamlinkVlcStudio.Infrastructure.Vod;

internal sealed class VodDownloadThrottledStream(Stream source, VodDownloadBandwidthLimiter limiter) : Stream
{
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var reservation = await limiter.ReserveAsync(buffer.Length, cancellationToken).ConfigureAwait(false);
        var received = 0;
        try
        {
            received = await source.ReadAsync(buffer[..reservation.Count], cancellationToken).ConfigureAwait(false);
            return received;
        }
        finally { limiter.ReturnUnusedBytes(reservation, received); }
    }
}

namespace StreamlinkVlcStudio.Infrastructure.Text;

/// <summary>Shares buffering, cancellation, and stream ownership for bounded line readers.</summary>
internal sealed class BufferedByteReader(Stream stream, bool leaveOpen) : IDisposable
{
    private readonly byte[] buffer = new byte[4096];
    private int offset;
    private int length;
    private bool disposed;

    internal ValueTask<int> ReadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return offset < length
            ? ValueTask.FromResult((int)buffer[offset++])
            : FillAndReadAsync(cancellationToken);
    }

    private async ValueTask<int> FillAndReadAsync(CancellationToken cancellationToken)
    {
        length = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        offset = 0;
        return length == 0 ? -1 : buffer[offset++];
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (!leaveOpen)
        {
            stream.Dispose();
        }
    }
}

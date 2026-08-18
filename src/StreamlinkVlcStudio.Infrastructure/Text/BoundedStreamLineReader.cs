using System.Text;

namespace StreamlinkVlcStudio.Infrastructure.Text;

/// <summary>Reads and drains byte-delimited lines while retaining at most a fixed number of bytes.</summary>
internal sealed class BoundedStreamLineReader : IDisposable
{
    private const int ReadBufferBytes = 4096;
    private readonly Stream stream;
    private readonly Encoding encoding;
    private readonly int maximumLineBytes;
    private readonly bool leaveOpen;
    private readonly byte[] readBuffer = new byte[ReadBufferBytes];
    private readonly byte[] lineBuffer;
    private int readOffset;
    private int readLength;
    private bool disposed;

    internal BoundedStreamLineReader(
        Stream stream,
        Encoding encoding,
        int maximumLineBytes,
        bool leaveOpen = true)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.encoding = encoding ?? throw new ArgumentNullException(nameof(encoding));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumLineBytes, 0);
        this.maximumLineBytes = maximumLineBytes;
        this.leaveOpen = leaveOpen;
        lineBuffer = new byte[maximumLineBytes];
    }

    internal async Task<BoundedTextLine?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var retainedLength = 0;
        var totalLength = 0;
        while (true)
        {
            var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (next < 0)
            {
                if (totalLength == 0)
                {
                    return null;
                }

                break;
            }

            if (next == '\n')
            {
                break;
            }

            totalLength = totalLength == int.MaxValue ? int.MaxValue : totalLength + 1;
            if (retainedLength < lineBuffer.Length)
            {
                lineBuffer[retainedLength++] = (byte)next;
            }
        }

        if (retainedLength > 0 && lineBuffer[retainedLength - 1] == '\r')
        {
            retainedLength--;
        }

        return new BoundedTextLine(
            encoding.GetString(lineBuffer, 0, retainedLength),
            totalLength > maximumLineBytes);
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

    private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (readOffset >= readLength)
        {
            readLength = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
            readOffset = 0;
            if (readLength == 0)
            {
                return -1;
            }
        }

        return readBuffer[readOffset++];
    }
}

internal readonly record struct BoundedTextLine(string Text, bool WasTruncated);

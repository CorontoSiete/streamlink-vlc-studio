using System.Text;

namespace StreamlinkVlcStudio.Infrastructure.Text;

/// <summary>Reads and drains byte-delimited lines while retaining at most a fixed number of bytes.</summary>
internal sealed class BoundedStreamLineReader : IDisposable
{
    private readonly BufferedByteReader reader;
    private readonly Encoding encoding;
    private readonly int maximumLineBytes;
    private readonly byte[] lineBuffer;

    internal BoundedStreamLineReader(
        Stream stream,
        Encoding encoding,
        int maximumLineBytes,
        bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        this.encoding = encoding ?? throw new ArgumentNullException(nameof(encoding));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumLineBytes, 0);
        this.maximumLineBytes = maximumLineBytes;
        reader = new BufferedByteReader(stream, leaveOpen);
        lineBuffer = new byte[maximumLineBytes];
    }

    internal async Task<BoundedTextLine?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        var retainedLength = 0;
        var totalLength = 0;
        while (true)
        {
            var next = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
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

    public void Dispose() => reader.Dispose();
}

internal readonly record struct BoundedTextLine(string Text, bool WasTruncated);

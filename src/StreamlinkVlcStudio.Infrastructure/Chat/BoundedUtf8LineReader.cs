using System.Text;
using StreamlinkVlcStudio.Infrastructure.Limits;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

/// <summary>Reads strict UTF-8 protocol lines without allowing an unbounded line buffer.</summary>
internal sealed class BoundedUtf8LineReader : IDisposable
{
    private const int ReadBufferBytes = 4096;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly int maximumLineBytes;
    private readonly byte[] readBuffer = new byte[ReadBufferBytes];
    private readonly byte[] lineBuffer;
    private int readOffset;
    private int readLength;
    private bool disposed;

    internal BoundedUtf8LineReader(
        Stream stream,
        int maximumLineBytes = PayloadLimits.TwitchInboundIrcBytes,
        bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumLineBytes, 0);
        this.stream = stream;
        this.maximumLineBytes = maximumLineBytes;
        this.leaveOpen = leaveOpen;
        lineBuffer = new byte[maximumLineBytes];
    }

    internal async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var lineLength = 0;
        var receivedBytes = 0;
        while (true)
        {
            var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (next < 0)
            {
                if (receivedBytes == 0)
                {
                    return null;
                }

                throw new InvalidDataException("IRC connection ended in the middle of a line.");
            }

            receivedBytes++;
            if (receivedBytes > maximumLineBytes)
            {
                throw new InvalidDataException(
                    $"IRC line exceeded the {maximumLineBytes:N0}-byte limit.");
            }

            if (next == '\n')
            {
                if (lineLength > 0 && lineBuffer[lineLength - 1] == '\r')
                {
                    lineLength--;
                }

                return StrictUtf8.GetString(lineBuffer, 0, lineLength);
            }

            lineBuffer[lineLength++] = (byte)next;
        }
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

    private ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (readOffset < readLength)
        {
            return ValueTask.FromResult((int)readBuffer[readOffset++]);
        }

        return FillAndReadByteAsync(cancellationToken);
    }

    private async ValueTask<int> FillAndReadByteAsync(CancellationToken cancellationToken)
    {
        readLength = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
        readOffset = 0;
        return readLength == 0 ? -1 : readBuffer[readOffset++];
    }
}

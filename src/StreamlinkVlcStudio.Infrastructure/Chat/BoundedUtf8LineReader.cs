using System.Text;
using StreamlinkVlcStudio.Infrastructure.Limits;
using StreamlinkVlcStudio.Infrastructure.Text;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

/// <summary>Reads strict UTF-8 protocol lines without allowing an unbounded line buffer.</summary>
internal sealed class BoundedUtf8LineReader : IDisposable
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly BufferedByteReader reader;
    private readonly int maximumLineBytes;
    private readonly byte[] lineBuffer;

    internal BoundedUtf8LineReader(
        Stream stream,
        int maximumLineBytes = PayloadLimits.TwitchInboundIrcBytes,
        bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumLineBytes, 0);
        reader = new BufferedByteReader(stream, leaveOpen);
        this.maximumLineBytes = maximumLineBytes;
        lineBuffer = new byte[maximumLineBytes];
    }

    internal async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        var lineLength = 0;
        var receivedBytes = 0;
        while (true)
        {
            var next = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
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

    public void Dispose() => reader.Dispose();
}

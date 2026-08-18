using System.Net.WebSockets;
using System.Text;
using StreamlinkVlcStudio.Infrastructure.Limits;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

/// <summary>Reads one complete, strictly encoded text message within a fixed byte limit.</summary>
internal static class BoundedWebSocketTextReader
{
    private const int ReceiveBufferBytes = 8192;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static async Task<string?> ReadAsync(
        WebSocket socket,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var receiveBuffer = new byte[ReceiveBufferBytes];
        using var payload = new MemoryStream(ReceiveBufferBytes);

        while (true)
        {
            var result = await socket
                .ReceiveAsync(new ArraySegment<byte>(receiveBuffer), cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("Only WebSocket text messages are accepted.");
            }

            if (payload.Length > PayloadLimits.WebSocketTextBytes - result.Count)
            {
                throw new InvalidDataException(
                    $"WebSocket text message exceeded the {PayloadLimits.WebSocketTextBytes:N0}-byte limit.");
            }

            payload.Write(receiveBuffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return StrictUtf8.GetString(payload.GetBuffer(), 0, checked((int)payload.Length));
            }
        }
    }
}

using System.Buffers.Binary;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

/// <summary>Single encoder/decoder and size authority for the native-overlay wire protocol.</summary>
internal static class NativeOverlayProtocolCodec
{
    internal const uint Magic = 0x564C4F56u;
    internal const uint Version = 1u;
    internal const byte FrameType = 1;
    internal const byte ScrollbarStateFrameType = 4;
    internal const uint ScrollEventType = 1u;
    internal const uint ScrollPositionEventType = 2u;
    internal const uint ResizeEventType = 3u;
    internal const uint ChatInputFocusEventType = 4u;
    internal const uint ShutdownEventType = 6u;
    internal const int HeaderSize = 36;
    internal const int EventMessageSize = 16;
    internal const int MaximumEncodedMessageBytes = 32 * 1024 * 1024;
    internal const int MaximumFramePayloadBytes = MaximumEncodedMessageBytes - HeaderSize;
    internal const int MaximumScrollNotches = 273;

    internal static bool TryGetFrameMessageSize(
        int width,
        int height,
        out int payloadBytes,
        out int encodedBytes)
    {
        payloadBytes = 0;
        encodedBytes = 0;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        try
        {
            var payload = checked((long)width * height * 4L);
            var encoded = checked(payload + HeaderSize);
            if (payload > MaximumFramePayloadBytes ||
                encoded > MaximumEncodedMessageBytes ||
                encoded > int.MaxValue)
            {
                return false;
            }

            payloadBytes = checked((int)payload);
            encodedBytes = checked((int)encoded);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    internal static (int Width, int Height) FitFrameDimensions(int width, int height)
    {
        if (TryGetFrameMessageSize(width, height, out _, out _))
        {
            return (width, height);
        }

        if (width <= 0 || height <= 0)
        {
            return (1, 1);
        }

        var maximumPixels = MaximumFramePayloadBytes / 4L;
        var sourcePixels = (double)width * height;
        var scale = Math.Sqrt(maximumPixels / sourcePixels);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return (1, 1);
        }

        var fittedWidth = Math.Max(1, (int)Math.Floor(width * Math.Min(1d, scale)));
        var fittedHeight = Math.Max(1, (int)Math.Floor(height * Math.Min(1d, scale)));
        while (!TryGetFrameMessageSize(fittedWidth, fittedHeight, out _, out _))
        {
            if (fittedWidth >= fittedHeight && fittedWidth > 1)
            {
                fittedWidth--;
            }
            else if (fittedHeight > 1)
            {
                fittedHeight--;
            }
            else
            {
                return (1, 1);
            }
        }

        return (fittedWidth, fittedHeight);
    }

    internal static byte[] CreateFrameMessage(int width, int height, byte frameType = FrameType)
    {
        if (!TryGetFrameMessageSize(width, height, out var payloadBytes, out var encodedBytes))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The encoded native-overlay frame exceeds 32 MiB.");
        }

        var message = new byte[encodedBytes];
        WriteFrameHeader(message, frameType, payloadBytes, width, height);
        return message;
    }

    internal static byte[] CreateHeaderMessage(byte frameType)
    {
        var message = new byte[HeaderSize];
        WritePrefix(message, payloadOrEventType: 0);
        message[12] = frameType;
        return message;
    }

    internal static void WriteFrameHeader(
        Span<byte> destination,
        byte frameType,
        int payloadBytes,
        int width,
        int height,
        byte opacity = 255)
    {
        if (destination.Length < HeaderSize ||
            payloadBytes < 0 ||
            payloadBytes > MaximumFramePayloadBytes)
        {
            throw new ArgumentException("The native-overlay frame header is invalid.", nameof(destination));
        }

        WritePrefix(destination, checked((uint)payloadBytes));
        destination[12] = frameType;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(24, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(28, 4), checked((uint)height));
        destination[32] = opacity;
    }

    internal static byte[] BuildEventMessage(uint eventType, int value)
    {
        var message = new byte[EventMessageSize];
        WritePrefix(message, eventType);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(12, 4), value);
        return message;
    }

    internal static bool TryReadEvent(ReadOnlySpan<byte> message, out uint eventType, out int value)
    {
        eventType = 0;
        value = 0;
        if (message.Length != EventMessageSize || !HasValidPrefix(message))
        {
            return false;
        }

        eventType = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(8, 4));
        value = BinaryPrimitives.ReadInt32LittleEndian(message.Slice(12, 4));
        return true;
    }

    internal static bool TryValidateEncodedMessage(ReadOnlySpan<byte> message, out string reason)
    {
        if (message.Length > MaximumEncodedMessageBytes)
        {
            reason = "encoded message exceeds 32 MiB";
            return false;
        }

        if (!HasValidPrefix(message))
        {
            reason = "protocol magic or version is invalid";
            return false;
        }

        if (message.Length == EventMessageSize)
        {
            reason = "";
            return true;
        }

        if (message.Length < HeaderSize)
        {
            reason = "message is shorter than a protocol header";
            return false;
        }

        var payloadBytes = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(8, 4));
        var expectedLength = checked((long)HeaderSize + payloadBytes);
        if (expectedLength != message.Length)
        {
            reason = "payload length does not match the encoded message";
            return false;
        }

        reason = "";
        return true;
    }

    internal static bool HasValidPrefix(ReadOnlySpan<byte> message)
    {
        return message.Length >= 8 &&
            BinaryPrimitives.ReadUInt32LittleEndian(message[..4]) == Magic &&
            BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(4, 4)) == Version;
    }

    private static void WritePrefix(Span<byte> destination, uint payloadOrEventType)
    {
        if (destination.Length < 12)
        {
            throw new ArgumentException("The native-overlay protocol prefix requires 12 bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4, 4), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(8, 4), payloadOrEventType);
    }
}

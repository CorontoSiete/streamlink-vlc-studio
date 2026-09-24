namespace StreamlinkVlcStudio.Core.Twitch;

/// <summary>
/// Repairs the MPEG-TS packets Twitch writes into muted VOD segments (<c>N-muted.ts</c>).
/// <para>
/// When Twitch remuxes a segment to silence its audio, every two seconds it splits one video
/// access unit across two PES packets and stamps the second with a program clock reference (PCR)
/// and presentation timestamp (PTS) of 2^33-1 -- an unset "-1" rather than a real time. The stream
/// clock therefore leaps forward 26.5 hours and straight back. libVLC's HLS demuxer orders and
/// paces everything by those values: it concludes a day of media is buffered, stops fetching
/// segments, and holds every track behind a clock that never arrives. Playback freezes two seconds
/// into the first muted segment while the player still reports "playing".
/// </para>
/// <para>
/// Both fields are optional in MPEG-TS, so the repair removes them instead of inventing values:
/// the continuation packet carries no access unit start and needs no timestamp, and the
/// surrounding packets already supply a clock reference every frame.
/// </para>
/// </summary>
public static class TwitchMutedSegmentSanitizer
{
    /// <summary>Size in bytes of one MPEG transport stream packet.</summary>
    public const int PacketSize = 188;

    // All 33 bits set: the value Twitch's muted-segment remuxer writes for an unset timestamp.
    private const long InvalidTimestamp = 0x1_FFFF_FFFF;
    private const byte SyncByte = 0x47;
    private const byte StuffingByte = 0xFF;
    private const int TransportHeaderLength = 4;
    private const byte PayloadUnitStartFlag = 0x40;
    private const int AdaptationFieldPresent = 0x2;
    private const int PayloadPresent = 0x1;
    private const byte PcrFlag = 0x10;
    private const int PcrLength = 6;
    private const int PesFixedHeaderLength = 9;
    private const byte PesPtsFlag = 0x80;
    private const byte PesDtsFlag = 0x40;
    private const byte PesTimestampFlagsMask = PesPtsFlag | PesDtsFlag;
    private const int PesTimestampLength = 5;
    private const byte FirstAudioStreamId = 0xC0;
    private const byte LastVideoStreamId = 0xEF;

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="destination"/>, repairing every
    /// complete packet on the way, and returns how many invalid fields were removed. Packets start
    /// at offset zero; bytes after the last complete packet are copied unchanged. The source is
    /// never modified and the output always has the same length as the input.
    /// </summary>
    public static int Repair(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException("The destination is smaller than the source.", nameof(destination));
        }

        source.CopyTo(destination);
        var repairs = 0;
        for (var offset = 0; offset + PacketSize <= source.Length; offset += PacketSize)
        {
            repairs += RepairPacket(destination.Slice(offset, PacketSize));
        }

        return repairs;
    }

    private static int RepairPacket(Span<byte> packet)
    {
        if (packet[0] != SyncByte)
        {
            return 0;
        }

        var adaptationFieldControl = (packet[3] >> 4) & 0x3;
        var payloadOffset = TransportHeaderLength;
        var repairs = 0;
        if ((adaptationFieldControl & AdaptationFieldPresent) != 0)
        {
            var adaptationFieldEnd = TransportHeaderLength + 1 + packet[TransportHeaderLength];
            if (adaptationFieldEnd > PacketSize)
            {
                return 0;
            }

            if (TryRemoveInvalidPcr(packet[(TransportHeaderLength + 1)..adaptationFieldEnd]))
            {
                repairs++;
            }

            payloadOffset = adaptationFieldEnd;
        }

        var startsPayloadUnit = (packet[1] & PayloadUnitStartFlag) != 0;
        if ((adaptationFieldControl & PayloadPresent) != 0 &&
            startsPayloadUnit &&
            TryRemoveInvalidPesTimestamps(packet[payloadOffset..]))
        {
            repairs++;
        }

        return repairs;
    }

    /// <param name="adaptationField">The adaptation field without its length byte: flags first.</param>
    private static bool TryRemoveInvalidPcr(Span<byte> adaptationField)
    {
        if (adaptationField.Length < 1 + PcrLength ||
            (adaptationField[0] & PcrFlag) == 0 ||
            ReadPcrBase(adaptationField.Slice(1, PcrLength)) != InvalidTimestamp)
        {
            return false;
        }

        adaptationField[0] &= unchecked((byte)~PcrFlag);
        RemoveLeadingField(adaptationField[1..], PcrLength);
        return true;
    }

    private static bool TryRemoveInvalidPesTimestamps(Span<byte> payload)
    {
        if (payload.Length < PesFixedHeaderLength ||
            payload[0] != 0x00 ||
            payload[1] != 0x00 ||
            payload[2] != 0x01 ||
            payload[3] is < FirstAudioStreamId or > LastVideoStreamId ||
            (payload[6] & 0xC0) != 0x80)
        {
            return false;
        }

        var flags = payload[7];
        if ((flags & PesPtsFlag) == 0)
        {
            return false;
        }

        var hasDts = (flags & PesDtsFlag) != 0;
        var timestampsLength = hasDts ? 2 * PesTimestampLength : PesTimestampLength;
        var headerEnd = PesFixedHeaderLength + payload[8];
        if (payload[8] < timestampsLength || headerEnd > payload.Length)
        {
            // The optional header fields continue in a later packet and cannot be relocated here.
            return false;
        }

        var timestamps = payload.Slice(PesFixedHeaderLength, timestampsLength);
        var isInvalid = ReadPesTimestamp(timestamps[..PesTimestampLength]) == InvalidTimestamp ||
            (hasDts && ReadPesTimestamp(timestamps[PesTimestampLength..]) == InvalidTimestamp);
        if (!isInvalid)
        {
            return false;
        }

        // Both timestamps go even when only one is invalid. A lone DTS cannot be encoded, and
        // keeping a lone PTS would declare DTS equal to it, which is wrong for reordered frames;
        // a packet without timestamps lets the decoder interpolate from its neighbours instead.

        payload[7] = (byte)(flags & ~PesTimestampFlagsMask);
        RemoveLeadingField(payload[PesFixedHeaderLength..headerEnd], timestampsLength);
        return true;
    }

    /// <summary>
    /// Drops the first <paramref name="fieldLength"/> bytes of a header region whose total length
    /// is fixed: later optional fields slide up to where a parser now expects them and the freed
    /// bytes become trailing stuffing, so the packet keeps its size and the payload its offset.
    /// </summary>
    private static void RemoveLeadingField(Span<byte> region, int fieldLength)
    {
        region[fieldLength..].CopyTo(region);
        region[^fieldLength..].Fill(StuffingByte);
    }

    private static long ReadPcrBase(ReadOnlySpan<byte> pcr) =>
        ((long)pcr[0] << 25) |
        ((long)pcr[1] << 17) |
        ((long)pcr[2] << 9) |
        ((long)pcr[3] << 1) |
        ((long)pcr[4] >> 7);

    private static long ReadPesTimestamp(ReadOnlySpan<byte> timestamp) =>
        ((long)((timestamp[0] >> 1) & 0x07) << 30) |
        ((long)timestamp[1] << 22) |
        ((long)(timestamp[2] >> 1) << 15) |
        ((long)timestamp[3] << 7) |
        ((long)timestamp[4] >> 1);
}

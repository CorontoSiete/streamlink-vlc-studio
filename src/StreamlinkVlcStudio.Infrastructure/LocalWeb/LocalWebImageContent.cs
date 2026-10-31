namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static class LocalWebImageContent
{
    // Provider filenames and Content-Type headers can disagree with the encoded image.
    internal static string? ContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes.Slice(1, 7).SequenceEqual("PNG\r\n\x1a\n"u8)) return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (bytes.StartsWith("BM"u8)) return "image/bmp";
        if (bytes.Length >= 16 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8) &&
            (bytes.Slice(8, 4).SequenceEqual("avif"u8) || bytes.Slice(8, 4).SequenceEqual("avis"u8))) return "image/avif";
        return null;
    }
}

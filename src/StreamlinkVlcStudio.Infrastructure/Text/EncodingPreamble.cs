using System.Text;

namespace StreamlinkVlcStudio.Infrastructure.Text;

internal static class EncodingPreamble
{
    internal static int GetLength(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        // A decoder configured not to emit a UTF-8 BOM must still accept one on input.
        var preamble = encoding.CodePage == Encoding.UTF8.CodePage
            ? Encoding.UTF8.Preamble
            : encoding.Preamble;
        return bytes.StartsWith(preamble) ? preamble.Length : 0;
    }
}

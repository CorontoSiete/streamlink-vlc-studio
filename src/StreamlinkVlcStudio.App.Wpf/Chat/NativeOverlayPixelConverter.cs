using System.Numerics;
using System.Runtime.InteropServices;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

internal static class NativeOverlayPixelConverter
{
    // Preserve the original integer rounding exactly, without three divisions for
    // every antialiased text pixel on every animation frame. Shared by all tabs.
    private static readonly byte[] StraightAlpha = CreateStraightAlphaTable();

    internal static void ConvertPbgraToRgba(Span<byte> pixels)
    {
        if (pixels.Length % 4 != 0)
            throw new ArgumentException("A pixel must contain four bytes.", nameof(pixels));

        var offset = 0;
        if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            var words = MemoryMarshal.Cast<byte, uint>(pixels);
            var alphaMask = new Vector<uint>(255);
            var keepGreenAlpha = new Vector<uint>(0xFF00FF00);
            for (; offset <= words.Length - Vector<uint>.Count; offset += Vector<uint>.Count)
            {
                var values = new Vector<uint>(words.Slice(offset, Vector<uint>.Count));
                var alpha = values >>> 24;
                var simple = Vector.Equals(alpha, Vector<uint>.Zero) | Vector.Equals(alpha, alphaMask);
                if (Vector.EqualsAll(simple, new Vector<uint>(uint.MaxValue)))
                {
                    // Blank/opaque runs need only an exact R/B channel swap. Retain
                    // even nonzero RGB under zero alpha, matching the scalar path.
                    var swapped = (values & keepGreenAlpha) | ((values & alphaMask) << 16) |
                        ((values >>> 16) & alphaMask);
                    swapped.CopyTo(words.Slice(offset, Vector<uint>.Count));
                }
                else
                {
                    ConvertScalar(pixels.Slice(offset * 4, Vector<uint>.Count * 4));
                }
            }
            offset *= 4;
        }
        ConvertScalar(pixels[offset..]);
    }

    private static void ConvertScalar(Span<byte> pixels)
    {
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var b = pixels[index];
            var g = pixels[index + 1];
            var r = pixels[index + 2];
            var alpha = pixels[index + 3];
            if (alpha is > 0 and < 255)
            {
                var row = alpha << 8;
                r = StraightAlpha[row | r];
                g = StraightAlpha[row | g];
                b = StraightAlpha[row | b];
            }
            pixels[index] = r;
            pixels[index + 1] = g;
            pixels[index + 2] = b;
        }
    }

    private static byte[] CreateStraightAlphaTable()
    {
        var table = new byte[256 * 256];
        for (var alpha = 1; alpha < 255; alpha++)
            for (var value = 0; value < 256; value++)
                table[(alpha << 8) | value] = (byte)Math.Min(255, (value * 255 + alpha / 2) / alpha);
        return table;
    }
}

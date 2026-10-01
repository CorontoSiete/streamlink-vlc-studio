using System.Globalization;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using IoMemoryStream = System.IO.MemoryStream;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

internal static class UnicodeEmojiRenderer
{
    internal static readonly FontFamily FallbackFontFamily = new("Segoe UI Emoji");
    private static readonly Lazy<SKTypeface?> EmojiTypeface = new(CreateEmojiTypeface);
    private static readonly object EmojiImageCacheLock = new();
    private const int MaximumEmojiImageCacheEntries = 512;
    private const long MaximumEmojiImageCacheBytes = 32L * 1024 * 1024;
    private static readonly Dictionary<EmojiImageCacheKey, EmojiImageCacheEntry> EmojiImageCache = [];
    private static readonly LinkedList<EmojiImageCacheKey> EmojiImageCacheLru = [];
    private static long emojiImageCacheBytes;

    internal static IEnumerable<TextRunSegment> EnumerateTextRunSegments(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var segmentText = new StringBuilder();

        while (enumerator.MoveNext())
        {
            var textElement = enumerator.GetTextElement();
            if (UsesEmojiImage(textElement))
            {
                if (segmentText.Length > 0)
                {
                    yield return new TextRunSegment(segmentText.ToString(), UseEmojiImage: false);
                    segmentText.Clear();
                }



                yield return new TextRunSegment(textElement, UseEmojiImage: true);
                continue;
            }

            segmentText.Append(textElement);
        }

        if (segmentText.Length > 0)
        {
            yield return new TextRunSegment(segmentText.ToString(), UseEmojiImage: false);
        }
    }

    private static bool UsesEmojiImage(string textElement)
    {
        var hasEmojiPresentationSelector = false;
        var hasTextPresentationSelector = false;
        var hasEmojiBase = false;
        var hasEmojiSequenceRune = false;

        foreach (var rune in textElement.EnumerateRunes())
        {
            if (rune.Value == 0xFE0F)
            {
                hasEmojiPresentationSelector = true;
                continue;
            }

            if (rune.Value == 0xFE0E)
            {
                hasTextPresentationSelector = true;
                continue;
            }

            hasEmojiSequenceRune |= IsEmojiSequenceRune(rune.Value);
            hasEmojiBase |= IsEmojiBaseRune(rune.Value);
        }

        if (hasTextPresentationSelector)
        {
            return false;
        }

        if (hasEmojiPresentationSelector)
        {
            return true;
        }

        return hasEmojiBase || hasEmojiSequenceRune;
    }

    internal static ImageSource? GetEmojiImageSource(string text, double fontSize, double pixelsPerDip = 1.0)
    {
        var pixelSize = Math.Clamp((int)Math.Ceiling(fontSize * Math.Max(2.4, pixelsPerDip)), 28, 96);
        var key = new EmojiImageCacheKey(text, pixelSize);
        lock (EmojiImageCacheLock)
        {
            if (EmojiImageCache.TryGetValue(key, out var cached))
            {
                TouchEmojiCacheEntryLocked(cached);
                return cached.Image;
            }
        }

        var rendered = RenderEmojiImageSource(text, pixelSize);
        lock (EmojiImageCacheLock)
        {
            if (EmojiImageCache.TryGetValue(key, out var cached))
            {
                TouchEmojiCacheEntryLocked(cached);
                return cached.Image;
            }

            AddEmojiCacheEntryLocked(key, rendered);
            return rendered;
        }
    }

    internal static IReadOnlyList<(string Text, bool UseEmojiImage)> SegmentTextForTest(string text) =>
        EnumerateTextRunSegments(text)
            .Select(segment => (segment.Text, segment.UseEmojiImage))
            .ToArray();

    internal static int EmojiImageCacheCountForTest
    {
        get
        {
            lock (EmojiImageCacheLock)
            {
                return EmojiImageCache.Count;
            }
        }
    }

    internal static void AddEmojiImageCacheEntryForTest(string text, int pixelSize)
    {
        lock (EmojiImageCacheLock)
        {
            var key = new EmojiImageCacheKey(text, pixelSize);
            if (!EmojiImageCache.ContainsKey(key))
            {
                AddEmojiCacheEntryLocked(key, image: null);
            }
        }
    }

    internal static void ClearEmojiImageCacheForTest()
    {
        lock (EmojiImageCacheLock)
        {
            EmojiImageCache.Clear();
            EmojiImageCacheLru.Clear();
            emojiImageCacheBytes = 0;
        }
    }

    private static void AddEmojiCacheEntryLocked(EmojiImageCacheKey key, ImageSource? image)
    {
        var estimatedBytes = image is BitmapSource bitmap
            ? Math.Max(0L, (long)bitmap.PixelWidth * bitmap.PixelHeight * 4)
            : 0L;
        var node = EmojiImageCacheLru.AddLast(key);
        EmojiImageCache[key] = new EmojiImageCacheEntry(image, estimatedBytes, node);
        emojiImageCacheBytes += estimatedBytes;
        while (EmojiImageCache.Count > MaximumEmojiImageCacheEntries ||
            emojiImageCacheBytes > MaximumEmojiImageCacheBytes)
        {
            var oldest = EmojiImageCacheLru.First;
            if (oldest is null)
            {
                break;
            }

            EmojiImageCacheLru.RemoveFirst();
            if (EmojiImageCache.Remove(oldest.Value, out var removed))
            {
                emojiImageCacheBytes -= removed.EstimatedBytes;
            }
        }
    }

    private static void TouchEmojiCacheEntryLocked(EmojiImageCacheEntry entry)
    {
        EmojiImageCacheLru.Remove(entry.LruNode);
        EmojiImageCacheLru.AddLast(entry.LruNode);
    }

    private static ImageSource? RenderEmojiImageSource(string text, int pixelSize)
    {
        try
        {
            var typeface = EmojiTypeface.Value;
            if (typeface is null)
            {
                return null;
            }




            var canvasWidth = pixelSize * 8;
            var canvasHeight = pixelSize * 3;
            using var font = new SKFont(typeface, pixelSize);
            using var shaper = new SKShaper(typeface);
            using var paint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true
            };
            using var bitmap = new SKBitmap(
                canvasWidth,
                canvasHeight,
                SKColorType.Rgba8888,
                SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawShapedText(
                    shaper,
                    text,
                    pixelSize,
                    pixelSize * 2,
                    font,
                    paint);
            }

            var bounds = GetVisibleBounds(bitmap);
            if (bounds is not { } visibleBounds)
            {
                return null;
            }

            using var cropped = new SKBitmap(
                visibleBounds.Width,
                visibleBounds.Height,
                SKColorType.Rgba8888,
                SKAlphaType.Premul);
            if (!bitmap.ExtractSubset(cropped, visibleBounds))
            {
                return null;
            }

            using var data = cropped.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new IoMemoryStream();
            data.SaveTo(stream);
            stream.Position = 0;

            var source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.StreamSource = stream;
            source.EndInit();
            if (source.CanFreeze)
            {
                source.Freeze();
            }

            return source;
        }
        catch (Exception ex) when (ex is ArgumentException or
            InvalidOperationException or
            DllNotFoundException or
            EntryPointNotFoundException or
            TypeInitializationException)
        {
            return null;
        }
    }

    private static SKTypeface? CreateEmojiTypeface()
    {
        try
        {
            return SKTypeface.FromFamilyName("Segoe UI Emoji");
        }
        catch (Exception ex) when (ex is DllNotFoundException or
            EntryPointNotFoundException or
            TypeInitializationException)
        {
            return null;
        }
    }

    private static SKRectI? GetVisibleBounds(SKBitmap bitmap)
    {
        var left = bitmap.Width;
        var top = bitmap.Height;
        var right = -1;
        var bottom = -1;

        for (var pixelY = 0; pixelY < bitmap.Height; pixelY++)
        {
            for (var pixelX = 0; pixelX < bitmap.Width; pixelX++)
            {
                if (bitmap.GetPixel(pixelX, pixelY).Alpha == 0)
                {
                    continue;
                }

                left = Math.Min(left, pixelX);
                top = Math.Min(top, pixelY);
                right = Math.Max(right, pixelX);
                bottom = Math.Max(bottom, pixelY);
            }
        }

        if (right < left || bottom < top)
        {
            return null;
        }

        const int padding = 1;
        left = Math.Max(0, left - padding);
        top = Math.Max(0, top - padding);
        right = Math.Min(bitmap.Width - 1, right + padding);
        bottom = Math.Min(bitmap.Height - 1, bottom + padding);
        return new SKRectI(left, top, right + 1, bottom + 1);
    }

    private static bool IsEmojiSequenceRune(int scalar)
    {
        return scalar is
            0x200D or
            0x20E3 or
            >= 0x1F3FB and <= 0x1F3FF;
    }

    private static bool IsEmojiBaseRune(int scalar)
    {
        return scalar is
            >= 0x1F000 and <= 0x1FAFF or
            0x231A or
            0x231B or
            >= 0x23E9 and <= 0x23EC or
            0x23F0 or
            0x23F3 or
            0x25FD or
            0x25FE or
            0x2614 or
            0x2615 or
            >= 0x2648 and <= 0x2653 or
            0x267F or
            0x2693 or
            0x26A1 or
            0x26AA or
            0x26AB or
            0x26BD or
            0x26BE or
            0x26C4 or
            0x26C5 or
            0x26CE or
            0x26D4 or
            0x26EA or
            0x26F2 or
            0x26F3 or
            0x26F5 or
            0x26FA or
            0x26FD or
            0x2705 or
            0x270A or
            0x270B or
            0x2728 or
            0x274C or
            0x274E or
            >= 0x2753 and <= 0x2755 or
            0x2757 or
            >= 0x2795 and <= 0x2797 or
            0x27B0 or
            0x27BF or
            0x2B1B or
            0x2B1C or
            0x2B50 or
            0x2B55 or
            0x3030 or
            0x303D or
            0x3297 or
            0x3299;
    }

    internal sealed record TextRunSegment(string Text, bool UseEmojiImage);
    private sealed record EmojiImageCacheKey(string Text, int PixelSize);
    private sealed record EmojiImageCacheEntry(
        ImageSource? Image,
        long EstimatedBytes,
        LinkedListNode<EmojiImageCacheKey> LruNode);
}

using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using StreamlinkVlcStudio.App.Wpf.Controls;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

internal static class NativeOverlayChatFrameRenderer
{
    private const uint NativeOverlayMagic = 0x564C4F56u;
    private const uint NativeOverlayVersion = 1u;
    private const byte NativeOverlayFrameType = 1;
    private const int NativeOverlayHeaderSize = 36;
    private const int NativeOverlayBlankFramePayloadSize = 4;
    private const int NativeOverlayDefaultHeight = 292;
    private const int NativeOverlayInputReserveHeight = 36;
    private const int NativeOverlayPadding = 8;
    private const int NativeOverlayMessageGap = 8;
    private const int NativeOverlayMaxRenderedMessages = 40;

    public static bool CanRenderOnCurrentThread =>
        Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;

    public static byte[]? TryBuildFrame(
        IReadOnlyList<ChatMessage> messages,
        ChatSettings settings,
        double fontSize,
        int videoHeight,
        string? positionStatePath,
        out int width,
        out int height)
    {
        var result = TryBuildFrame(
            messages,
            settings,
            fontSize,
            videoHeight,
            positionStatePath,
            TimeSpan.Zero,
            out width,
            out height);
        return result?.Frame;
    }

    public static NativeOverlayChatFrame? TryBuildFrame(
        IReadOnlyList<ChatMessage> messages,
        ChatSettings settings,
        double fontSize,
        int videoHeight,
        string? positionStatePath,
        TimeSpan animationClock,
        out int width,
        out int height)
    {
        var layout = ResolveReplayOverlayLayout(settings, fontSize, videoHeight, positionStatePath);
        width = layout.FrameWidth;
        height = layout.FrameHeight;
        if (!CanRenderOnCurrentThread)
        {
            return null;
        }

        var payloadSize = (long)width * height * 4;
        if (payloadSize <= 0 || payloadSize > NativeOverlaySizing.MaxFramePayloadBytes)
        {
            return null;
        }

        var frame = new byte[NativeOverlayHeaderSize + payloadSize];
        WriteFrameHeader(frame, width, height, (uint)payloadSize);
        if (messages.Count == 0)
        {
            return new NativeOverlayChatFrame(frame, false, false, null, []);
        }

        var rendered = RenderMessages(messages, layout, animationClock);
        CopyPbgraToRgba(rendered.Bitmap, frame.AsSpan(NativeOverlayHeaderSize));
        return new NativeOverlayChatFrame(
            frame,
            rendered.HasAnimatedContent,
            rendered.PendingImageLoads.Count > 0,
            rendered.NextAnimationFrameDelay,
            rendered.PendingImageLoads);
    }

    private static RenderedChatMessages RenderMessages(
        IReadOnlyList<ChatMessage> messages,
        NativeReplayOverlayLayout layout,
        TimeSpan animationClock)
    {
        var width = layout.FrameWidth;
        var height = layout.FrameHeight;
        var padding = ScaleContentPixels(layout, NativeOverlayPadding);
        var bottomReserve = ScaleContentPixels(layout, NativeOverlayInputReserveHeight);
        var messageGap = ScaleContentPixels(layout, NativeOverlayMessageGap);
        var scaledFontSize = ScaleReferencePixels(layout.VideoScale, layout.EffectiveReferenceFontSize);
        var messageBlocks = new List<DockedChatMessageTextBlock>();

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Bottom
        };

        foreach (var message in messages.TakeLast(NativeOverlayMaxRenderedMessages))
        {
            var messageBlock = new DockedChatMessageTextBlock
            {
                Message = message,
                ChatFontSize = scaledFontSize,
                Margin = new Thickness(0, 0, 0, messageGap),
                Effect = new DropShadowEffect
                {
                    BlurRadius = Math.Max(1, ScaleReferencePixels(layout.VideoScale, 2)),
                    ShadowDepth = Math.Max(1, ScaleReferencePixels(layout.VideoScale, 1)),
                    Opacity = 0.9,
                    Color = Colors.Black
                }
            };
            messageBlocks.Add(messageBlock);
            stack.Children.Add(messageBlock);
        }

        var root = new Border
        {
            Width = width,
            Height = height,
            Background = Brushes.Transparent,
            Padding = new Thickness(padding, padding, padding, padding + bottomReserve),
            ClipToBounds = true,
            Child = stack
        };
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(root, TextRenderingMode.Grayscale);
        RenderOptions.SetBitmapScalingMode(root, BitmapScalingMode.HighQuality);

        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        var hasAnimatedContent = false;
        TimeSpan? nextAnimationFrameDelay = null;
        var pendingImageLoads = new HashSet<AnimatedEmoteImageCacheKey>();
        foreach (var image in messageBlocks.SelectMany(block => block.AnimatedEmoteImages))
        {
            if (image.ApplyAnimationClock(animationClock, out var imageNextFrameDelay))
            {
                hasAnimatedContent = true;
                nextAnimationFrameDelay = nextAnimationFrameDelay is null ||
                    imageNextFrameDelay < nextAnimationFrameDelay.Value
                        ? imageNextFrameDelay
                        : nextAnimationFrameDelay;
            }

            if (image.IsImageLoadPending &&
                image.CurrentImageCacheKey is { } pendingImageLoad)
            {
                pendingImageLoads.Add(pendingImageLoad);
            }
        }

        if (hasAnimatedContent)
        {
            root.UpdateLayout();
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        return new RenderedChatMessages(
            bitmap,
            hasAnimatedContent,
            nextAnimationFrameDelay,
            pendingImageLoads.ToArray());
    }

    internal static NativeReplayOverlayLayout ResolveReplayOverlayLayout(
        ChatSettings settings,
        double fontSize,
        int videoHeight,
        string? positionStatePath)
    {
        var defaultReferenceWidth = NativeOverlaySizing.ClampReferenceWidth((int)Math.Round(settings.DockWidth));
        var referenceWidth = defaultReferenceWidth;
        var referenceHeight = NativeOverlayDefaultHeight;
        var scale = GetVideoScale(videoHeight);

        if (!string.IsNullOrWhiteSpace(positionStatePath) &&
            TryReadNativeOverlaySizeFile(
                $"{positionStatePath}.size",
                out var savedWidth,
                out var savedHeight,
                out var referenceSize))
        {
            if (referenceSize)
            {
                referenceWidth = NativeOverlaySizing.ClampReferenceWidth(savedWidth);
                referenceHeight = NativeOverlaySizing.ClampReferenceHeight(savedHeight);
            }
            else
            {
                var width = Math.Clamp(
                    savedWidth,
                    ScaleReferencePixels(scale, NativeOverlaySizing.MinWidth),
                    ScaleReferencePixels(scale, NativeOverlaySizing.MaxWidth));
                var height = Math.Clamp(
                    savedHeight,
                    ScaleReferencePixels(scale, NativeOverlaySizing.MinHeight),
                    ScaleReferencePixels(scale, NativeOverlaySizing.MaxHeight));
                (referenceWidth, referenceHeight) = NativeOverlaySizing.NormalizeToReferenceSize(
                    width,
                    height,
                    videoHeight);
                return CreateReplayOverlayLayout(
                    width,
                    height,
                    referenceWidth,
                    referenceHeight,
                    defaultReferenceWidth,
                    scale,
                    fontSize);
            }
        }

        return CreateReplayOverlayLayout(
            ScaleReferencePixels(scale, referenceWidth),
            ScaleReferencePixels(scale, referenceHeight),
            referenceWidth,
            referenceHeight,
            defaultReferenceWidth,
            scale,
            fontSize);
    }

    private static NativeReplayOverlayLayout CreateReplayOverlayLayout(
        int frameWidth,
        int frameHeight,
        int referenceWidth,
        int referenceHeight,
        int defaultReferenceWidth,
        double videoScale,
        double fontSize)
    {
        var widthScale = referenceWidth / (double)defaultReferenceWidth;
        var heightScale = referenceHeight / (double)NativeOverlayDefaultHeight;
        var contentScale = Math.Max(1.0, Math.Max(widthScale, heightScale));
        var baseReferenceFontSize = ChatSettings.NormalizeFontSize(fontSize, ChatSettings.DefaultVlcOverlayFontSize);

        return new NativeReplayOverlayLayout(
            frameWidth,
            frameHeight,
            referenceWidth,
            referenceHeight,
            videoScale,
            contentScale,
            baseReferenceFontSize);
    }

    private static bool TryReadNativeOverlaySizeFile(
        string path,
        out int width,
        out int height,
        out bool referenceSize)
    {
        width = 0;
        height = 0;
        referenceSize = false;

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var text = File.ReadAllText(path);
            var values = text
                .Split(
                    new[] { ' ', '\t', '\r', '\n', ':', ',', '{', '}' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : (int?)null)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToArray();
            if (values.Length < 2)
            {
                return false;
            }

            width = values[0];
            height = values[1];
            referenceSize =
                text.Contains("reference", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("normalized", StringComparison.OrdinalIgnoreCase);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static double GetVideoScale(int videoHeight)
    {
        return NativeOverlaySizing.GetVideoScale(videoHeight);
    }

    private static int ScaleReferencePixels(double scale, double value)
    {
        return NativeOverlaySizing.ScaleReferencePixels(scale, value);
    }

    private static int ScaleContentPixels(NativeReplayOverlayLayout layout, double value)
    {
        return ScaleReferencePixels(layout.VideoScale, value * layout.ContentScale);
    }

    private static void WriteFrameHeader(byte[] frame, int width, int height, uint payloadSize)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), NativeOverlayMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), NativeOverlayVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8, 4), payloadSize);
        frame[12] = NativeOverlayFrameType;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(24, 4), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(28, 4), (uint)height);
        frame[32] = 255;
    }

    internal static byte[] BuildTransparentBlankFrameMessage()
    {
        var message = new byte[NativeOverlayHeaderSize + NativeOverlayBlankFramePayloadSize];
        WriteFrameHeader(message, 1, 1, NativeOverlayBlankFramePayloadSize);
        message[32] = 0;
        return message;
    }

    internal static byte[] BuildTransparentFrameMessage(
        ChatSettings settings,
        int videoHeight,
        string? positionStatePath,
        out int width,
        out int height)
    {
        var layout = ResolveReplayOverlayLayout(
            settings,
            ChatSettings.DefaultVlcOverlayFontSize,
            videoHeight,
            positionStatePath);
        width = layout.FrameWidth;
        height = layout.FrameHeight;
        var payloadSize = checked(width * height * 4);
        var message = new byte[NativeOverlayHeaderSize + payloadSize];
        WriteFrameHeader(message, width, height, (uint)payloadSize);
        return message;
    }

    private static void CopyPbgraToRgba(BitmapSource bitmap, Span<byte> destination)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var stride = width * 4;
        var pixelBytes = stride * height;
        var pixels = ArrayPool<byte>.Shared.Rent(pixelBytes);
        try
        {
            bitmap.CopyPixels(pixels, stride, 0);

            var outputIndex = 0;
            for (var index = 0; index < pixelBytes; index += 4)
            {
                var b = pixels[index];
                var g = pixels[index + 1];
                var r = pixels[index + 2];
                var a = pixels[index + 3];
                if (a is > 0 and < 255)
                {
                    r = Unpremultiply(r, a);
                    g = Unpremultiply(g, a);
                    b = Unpremultiply(b, a);
                }

                destination[outputIndex++] = r;
                destination[outputIndex++] = g;
                destination[outputIndex++] = b;
                destination[outputIndex++] = a;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private static byte Unpremultiply(byte value, byte alpha)
    {
        return (byte)Math.Clamp((value * 255 + alpha / 2) / alpha, 0, 255);
    }

    private sealed record RenderedChatMessages(
        RenderTargetBitmap Bitmap,
        bool HasAnimatedContent,
        TimeSpan? NextAnimationFrameDelay,
        IReadOnlyCollection<AnimatedEmoteImageCacheKey> PendingImageLoads);
}

internal sealed record NativeOverlayChatFrame(
    byte[] Frame,
    bool HasAnimatedContent,
    bool HasPendingImageLoads,
    TimeSpan? NextAnimationFrameDelay,
    IReadOnlyCollection<AnimatedEmoteImageCacheKey> PendingImageLoads);

internal readonly record struct NativeReplayOverlayLayout(
    int FrameWidth,
    int FrameHeight,
    int ReferenceWidth,
    int ReferenceHeight,
    double VideoScale,
    double ContentScale,
    double EffectiveReferenceFontSize);

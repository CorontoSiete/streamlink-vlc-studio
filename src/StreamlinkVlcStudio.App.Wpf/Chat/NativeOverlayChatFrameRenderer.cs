using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamlinkVlcStudio.App.Wpf.Controls;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

internal static class NativeOverlayChatFrameRenderer
{
    private const int NativeOverlayDefaultHeight = 292;
    private const int NativeOverlayHorizontalPadding = 8;
    private const int NativeOverlayTopPadding = 8;
    private const int NativeOverlayInputHeight = 30;
    private const int NativeOverlayInputGap = 6;
    private const int NativeOverlayBottomPadding = 8;
    private const int NativeOverlayMinimumCandidateMessages = 18;
    internal const int NativeOverlayMaximumCandidateMessages = 256;

    public static bool CanRenderOnCurrentThread =>
        Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;

    public static NativeOverlayChatFrame? TryBuildFrame(
        IReadOnlyList<ChatMessage> messages,
        ChatSettings settings,
        double fontSize,
        int videoHeight,
        string? positionStatePath,
        TimeSpan animationClock,
        out int width,
        out int height,
        int messageOffset = 0,
        object? imageCachePinOwner = null,
        NativeReplayOverlayFrameRenderContext? renderContext = null)
    {
        var layout = FitLayoutToProtocolBudget(
            ResolveReplayOverlayLayout(settings, fontSize, videoHeight, positionStatePath));
        width = layout.FrameWidth;
        height = layout.FrameHeight;
        if (!CanRenderOnCurrentThread)
        {
            return null;
        }

        if (!NativeOverlayProtocolCodec.TryGetFrameMessageSize(
                width,
                height,
                out _,
                out _))
        {
            width = 1;
            height = 1;
            return new NativeOverlayChatFrame(
                BuildTransparentBlankFrameMessage(),
                false,
                false,
                null,
                [],
                NativeReplayOverlayRenderedSelection.Empty);
        }

        var frame = NativeOverlayProtocolCodec.CreateFrameMessage(width, height);
        if (messages.Count == 0)
        {
            if (imageCachePinOwner is not null)
            {
                AnimatedEmoteImage.ClearCachePins(imageCachePinOwner);
            }

            return new NativeOverlayChatFrame(
                frame,
                false,
                false,
                null,
                [],
                NativeReplayOverlayRenderedSelection.Empty);
        }

        var rendered = RenderMessages(
            messages,
            layout,
            animationClock,
            messageOffset,
            imageCachePinOwner,
            renderContext);
        CopyPbgraToRgba(rendered.Bitmap, frame.AsSpan(NativeOverlayProtocolCodec.HeaderSize));
        return new NativeOverlayChatFrame(
            frame,
            rendered.HasAnimatedContent,
            rendered.PendingImageLoads.Count > 0,
            rendered.NextAnimationFrameDelay,
            rendered.PendingImageLoads,
            rendered.RenderedSelection);
    }

    private static RenderedChatMessages RenderMessages(
        IReadOnlyList<ChatMessage> messages,
        NativeReplayOverlayLayout layout,
        TimeSpan animationClock,
        int messageOffset,
        object? imageCachePinOwner,
        NativeReplayOverlayFrameRenderContext? renderContext)
    {
        var width = layout.FrameWidth;
        var height = layout.FrameHeight;
        var horizontalPadding = ScaleReferencePixels(layout.VideoHeight, NativeOverlayHorizontalPadding);
        var topPadding = ScaleReferencePixels(layout.VideoHeight, NativeOverlayTopPadding);
        var inputHeight = ScaleReferencePixels(layout.VideoHeight, NativeOverlayInputHeight);
        var inputGap = ScaleReferencePixels(layout.VideoHeight, NativeOverlayInputGap);
        var bottomPadding = ScaleReferencePixels(layout.VideoHeight, NativeOverlayBottomPadding);
        var bottomReserve = inputHeight + inputGap + bottomPadding;
        var selection = MeasureVisibleMessages(messages, layout, messageOffset, renderContext);
        var messageBlocks = selection.MessageBlocks;
        var images = messageBlocks
            .SelectMany(block => block.AnimatedEmoteImages)
            .ToArray();
        if (imageCachePinOwner is not null)
        {
            AnimatedEmoteImage.UpdateCachePins(
                imageCachePinOwner,
                images
                    .Select(image => image.CurrentImageCacheKey)
                    .Where(key => key.HasValue)
                    .Select(key => key!.Value));
        }

        var stack = renderContext?.Stack ?? new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        stack.Children.Clear();

        for (var index = 0; index < messageBlocks.Count; index++)
        {
            var messageBlock = messageBlocks[index];
            messageBlock.Margin = new Thickness(
                0,
                0,
                0,
                index + 1 < messageBlocks.Count ? layout.Presentation.MessageGap : 0);
            stack.Children.Add(messageBlock);
        }

        var root = renderContext?.PrepareRoot(
            width,
            height,
            new Thickness(horizontalPadding, topPadding, horizontalPadding, bottomReserve)) ?? new Border
            {
                Width = width,
                Height = height,
                Background = Brushes.Transparent,
                Padding = new Thickness(horizontalPadding, topPadding, horizontalPadding, bottomReserve),
                ClipToBounds = true,
                Child = stack
            };
        if (renderContext is null)
        {
            TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(root, TextRenderingMode.Grayscale);
            RenderOptions.SetBitmapScalingMode(root, BitmapScalingMode.HighQuality);
        }

        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        var hasAnimatedContent = false;
        TimeSpan? nextAnimationFrameDelay = null;
        var pendingImageLoads = new HashSet<AnimatedEmoteImageCacheKey>();
        foreach (var image in images)
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
            pendingImageLoads.ToArray(),
            selection.RenderedSelection);
    }

    internal static NativeReplayOverlayMessageSelection MeasureVisibleMessages(
        IReadOnlyList<ChatMessage> messages,
        NativeReplayOverlayLayout layout,
        int messageOffset = 0,
        NativeReplayOverlayFrameRenderContext? renderContext = null)
    {
        var horizontalPadding = ScaleReferencePixels(layout.VideoHeight, NativeOverlayHorizontalPadding);
        var topPadding = ScaleReferencePixels(layout.VideoHeight, NativeOverlayTopPadding);
        var inputHeight = ScaleReferencePixels(layout.VideoHeight, NativeOverlayInputHeight);
        var inputGap = ScaleReferencePixels(layout.VideoHeight, NativeOverlayInputGap);
        var bottomPadding = ScaleReferencePixels(layout.VideoHeight, NativeOverlayBottomPadding);
        var availableHeight = Math.Max(
            0,
            layout.FrameHeight - topPadding - inputHeight - inputGap - bottomPadding);
        var availableWidth = Math.Max(0, layout.FrameWidth - horizontalPadding * 2);
        var plainMessageStride = layout.Presentation.MessageFontCellHeight +
            layout.Presentation.LineGap +
            layout.Presentation.MessageGap;
        var heightCandidateLimit = availableHeight > 0
            ? (availableHeight + plainMessageStride - 1) / plainMessageStride
            : 1;
        var candidateLimit = Math.Clamp(
            Math.Max(heightCandidateLimit, NativeOverlayMinimumCandidateMessages),
            1,
            NativeOverlayMaximumCandidateMessages);
        var measuredBlocks = new Dictionary<int, (DockedChatMessageTextBlock Block, int Height)>();

        (DockedChatMessageTextBlock Block, int Height) MeasureBlock(int index)
        {
            if (measuredBlocks.TryGetValue(index, out var measured))
            {
                return measured;
            }

            var block = renderContext?.GetMessageBlock(messages[index], layout.Presentation) ??
                CreateMessageBlock(messages[index], layout.Presentation);
            block.Height = double.NaN;
            block.Measure(new Size(availableWidth, double.PositiveInfinity));
            measured = (block, Math.Max(1, (int)Math.Ceiling(block.DesiredSize.Height)));
            measuredBlocks[index] = measured;
            return measured;
        }

        var oldestPageEndIndex = -1;
        var oldestPageUsedHeight = 0;
        var oldestPageMessageCount = 0;
        for (var index = 0; index < messages.Count && index < candidateLimit; index++)
        {
            var measured = MeasureBlock(index);
            var requiredHeight = measured.Height +
                (oldestPageMessageCount > 0 ? layout.Presentation.MessageGap : 0);
            if (oldestPageUsedHeight + requiredHeight > availableHeight)
            {
                break;
            }

            oldestPageEndIndex = index;
            oldestPageUsedHeight += requiredHeight;
            oldestPageMessageCount++;
        }

        var maximumMessageOffset = messages.Count == 0
            ? 0
            : oldestPageEndIndex >= 0
                ? Math.Max(0, messages.Count - 1 - oldestPageEndIndex)
                : messages.Count - 1;
        var effectiveMessageOffset = Math.Clamp(messageOffset, 0, maximumMessageOffset);
        var candidateEnd = messages.Count - 1 - effectiveMessageOffset;
        var candidateStart = Math.Max(0, candidateEnd - candidateLimit + 1);
        var selectedNewestFirst = new List<(int Index, DockedChatMessageTextBlock Block)>();
        var usedHeight = 0;

        for (var index = candidateEnd; index >= candidateStart; index--)
        {
            var measured = MeasureBlock(index);
            var requiredHeight = measured.Height +
                (selectedNewestFirst.Count > 0 ? layout.Presentation.MessageGap : 0);
            if (usedHeight + requiredHeight > availableHeight)
            {
                break;
            }

            measured.Block.Height = measured.Height;
            selectedNewestFirst.Add((index, measured.Block));
            usedHeight += requiredHeight;
        }

        var newestMessageIndex = selectedNewestFirst.Count > 0
            ? selectedNewestFirst[0].Index
            : -1;
        var oldestMessageIndex = selectedNewestFirst.Count > 0
            ? selectedNewestFirst[^1].Index
            : -1;
        selectedNewestFirst.Reverse();
        return new NativeReplayOverlayMessageSelection(
            selectedNewestFirst.Select(item => item.Block).ToArray(),
            availableWidth,
            availableHeight,
            usedHeight,
            candidateLimit,
            new NativeReplayOverlayRenderedSelection(
                effectiveMessageOffset,
                maximumMessageOffset,
                oldestMessageIndex,
                newestMessageIndex));
    }

    internal static DockedChatMessageTextBlock CreateMessageBlock(
        ChatMessage message,
        NativeOverlayChatPresentation presentation)
    {
        var messageBlock = new DockedChatMessageTextBlock();
        messageBlock.ApplyNativeOverlayPresentation(presentation);
        messageBlock.Message = message;
        return messageBlock;
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
        var normalizedVideoHeight = videoHeight > 0 ? videoHeight : NativeOverlaySizing.ReferenceVideoHeight;
        var scale = GetVideoScale(normalizedVideoHeight);

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
                    normalizedVideoHeight,
                    scale,
                    fontSize);
            }
        }

        return CreateReplayOverlayLayout(
            ScaleReferencePixels(scale, referenceWidth),
            ScaleReferencePixels(scale, referenceHeight),
            referenceWidth,
            referenceHeight,
            normalizedVideoHeight,
            scale,
            fontSize);
    }

    private static NativeReplayOverlayLayout CreateReplayOverlayLayout(
        int frameWidth,
        int frameHeight,
        int referenceWidth,
        int referenceHeight,
        int videoHeight,
        double videoScale,
        double fontSize)
    {
        var baseReferenceFontSize = (int)Math.Round(
            ChatSettings.NormalizeFontSize(fontSize, ChatSettings.DefaultVlcOverlayFontSize));

        return new NativeReplayOverlayLayout(
            frameWidth,
            frameHeight,
            referenceWidth,
            referenceHeight,
            videoHeight,
            videoScale,
            baseReferenceFontSize,
            NativeOverlayChatPresentation.Create(baseReferenceFontSize, videoHeight));
    }

    private static NativeReplayOverlayLayout FitLayoutToProtocolBudget(NativeReplayOverlayLayout layout)
    {
        var fitted = NativeOverlayProtocolCodec.FitFrameDimensions(layout.FrameWidth, layout.FrameHeight);
        if (fitted.Width == layout.FrameWidth && fitted.Height == layout.FrameHeight)
        {
            return layout;
        }

        var scale = Math.Min(
            fitted.Width / (double)Math.Max(1, layout.FrameWidth),
            fitted.Height / (double)Math.Max(1, layout.FrameHeight));
        var fittedVideoHeight = Math.Max(1, (int)Math.Round(layout.VideoHeight * scale));
        return CreateReplayOverlayLayout(
            fitted.Width,
            fitted.Height,
            layout.ReferenceWidth,
            layout.ReferenceHeight,
            fittedVideoHeight,
            NativeOverlaySizing.GetVideoScale(fittedVideoHeight),
            layout.EffectiveReferenceFontSize);
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

    private static int ScaleReferencePixels(int videoHeight, int value)
    {
        return NativeOverlaySizing.ScaleReferencePixels(videoHeight, value);
    }

    internal static byte[] BuildTransparentBlankFrameMessage()
    {
        var message = NativeOverlayProtocolCodec.CreateFrameMessage(1, 1);
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
        var layout = FitLayoutToProtocolBudget(ResolveReplayOverlayLayout(
            settings,
            ChatSettings.DefaultVlcOverlayFontSize,
            videoHeight,
            positionStatePath));
        width = layout.FrameWidth;
        height = layout.FrameHeight;
        try
        {
            return NativeOverlayProtocolCodec.CreateFrameMessage(width, height);
        }
        catch (ArgumentOutOfRangeException)
        {
            width = 1;
            height = 1;
            return BuildTransparentBlankFrameMessage();
        }
    }

    internal static byte[] BuildScrollbarStateFrameMessage(
        NativeReplayOverlayRenderedSelection selection,
        int totalMessageCount)
    {
        var totalMessages = Math.Max(0, totalMessageCount);
        var maximumAllowedOffset = Math.Max(0, totalMessages - 1);
        var maximumMessageOffset = Math.Clamp(selection.MaximumMessageOffset, 0, maximumAllowedOffset);
        var messageOffset = Math.Clamp(selection.MessageOffset, 0, maximumMessageOffset);
        var visibleMessages = selection.OldestMessageIndex >= 0 &&
            selection.NewestMessageIndex >= selection.OldestMessageIndex
                ? (int)Math.Clamp(
                    (long)selection.NewestMessageIndex - selection.OldestMessageIndex + 1,
                    0L,
                    totalMessages)
                : 0;

        var message = NativeOverlayProtocolCodec.CreateHeaderMessage(
            NativeOverlayProtocolCodec.ScrollbarStateFrameType);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(16, 4), messageOffset);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(20, 4), maximumMessageOffset);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(24, 4), visibleMessages);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(28, 4), totalMessages);
        message[32] = 255;
        return message;
    }

    private static void CopyPbgraToRgba(BitmapSource bitmap, Span<byte> destination)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var stride = checked((int)((long)width * 4L));
        var pixelBytes = checked((int)((long)stride * height));
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
        IReadOnlyCollection<AnimatedEmoteImageCacheKey> PendingImageLoads,
        NativeReplayOverlayRenderedSelection RenderedSelection);
}

internal sealed class NativeReplayOverlayFrameRenderContext
{
    private const int MaximumCachedMessageBlocks = 512;
    private readonly Dictionary<ChatMessage, DockedChatMessageTextBlock> messageBlocks =
        new(ReferenceEqualityComparer.Instance);
    private NativeOverlayChatPresentation? presentation;
    private long contentVersion = long.MinValue;

    internal StackPanel Stack { get; } = new()
    {
        Orientation = Orientation.Vertical,
        VerticalAlignment = VerticalAlignment.Bottom
    };

    private Border? root;

    internal void EnsureContentVersion(long nextContentVersion)
    {
        if (contentVersion == nextContentVersion)
        {
            return;
        }

        contentVersion = nextContentVersion;
        messageBlocks.Clear();
    }

    internal Border PrepareRoot(int width, int height, Thickness padding)
    {
        root ??= new Border
        {
            Background = Brushes.Transparent,
            ClipToBounds = true,
            Child = Stack
        };
        root.Width = width;
        root.Height = height;
        root.Padding = padding;
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(root, TextRenderingMode.Grayscale);
        RenderOptions.SetBitmapScalingMode(root, BitmapScalingMode.HighQuality);
        return root;
    }

    internal DockedChatMessageTextBlock GetMessageBlock(
        ChatMessage message,
        NativeOverlayChatPresentation nextPresentation)
    {
        if (presentation is null || !presentation.Equals(nextPresentation))
        {
            presentation = nextPresentation;
            messageBlocks.Clear();
        }

        if (messageBlocks.TryGetValue(message, out var block))
        {
            return block;
        }

        block = NativeOverlayChatFrameRenderer.CreateMessageBlock(message, nextPresentation);
        messageBlocks[message] = block;
        if (messageBlocks.Count > MaximumCachedMessageBlocks)
        {
            var oldest = messageBlocks.Keys.First();
            messageBlocks.Remove(oldest);
        }

        return block;
    }
}

internal sealed record NativeOverlayChatFrame(
    byte[] Frame,
    bool HasAnimatedContent,
    bool HasPendingImageLoads,
    TimeSpan? NextAnimationFrameDelay,
    IReadOnlyCollection<AnimatedEmoteImageCacheKey> PendingImageLoads,
    NativeReplayOverlayRenderedSelection RenderedSelection);

internal readonly record struct NativeReplayOverlayLayout(
    int FrameWidth,
    int FrameHeight,
    int ReferenceWidth,
    int ReferenceHeight,
    int VideoHeight,
    double VideoScale,
    int EffectiveReferenceFontSize,
    NativeOverlayChatPresentation Presentation);

internal sealed record NativeReplayOverlayMessageSelection(
    IReadOnlyList<DockedChatMessageTextBlock> MessageBlocks,
    int AvailableWidth,
    int AvailableHeight,
    int UsedHeight,
    int CandidateLimit,
    NativeReplayOverlayRenderedSelection RenderedSelection);

internal readonly record struct NativeReplayOverlayRenderedSelection(
    int MessageOffset,
    int MaximumMessageOffset,
    int OldestMessageIndex,
    int NewestMessageIndex)
{
    public static NativeReplayOverlayRenderedSelection Empty { get; } = new(0, 0, -1, -1);
}

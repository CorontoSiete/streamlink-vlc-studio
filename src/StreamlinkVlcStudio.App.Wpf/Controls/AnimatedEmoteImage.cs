using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

public sealed class AnimatedEmoteImage : Image
{
    private const int DefaultMaxImageBytes = 2 * 1024 * 1024;
    private const int AbsoluteMaxImageBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan DefaultFrameDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MinimumFrameDelay = TimeSpan.FromMilliseconds(20);
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly ConcurrentDictionary<AnimatedEmoteImageCacheKey, Lazy<Task<DecodedEmoteImage?>>> ImageCache = new();

    private readonly DispatcherTimer frameTimer;
    private AnimatedEmoteImageCacheKey? currentImageCacheKey;
    private DecodedEmoteImage? decodedImage;
    private int frameIndex;
    private bool imageLoadPending;
    private long loadVersion;

    internal static event EventHandler<AnimatedEmoteImageCacheCompletedEventArgs>? ImageCacheEntryCompleted;

    public static readonly DependencyProperty ImageUrlProperty = DependencyProperty.Register(
        nameof(ImageUrl),
        typeof(string),
        typeof(AnimatedEmoteImage),
        new PropertyMetadata("", OnImageUrlChanged));

    public static readonly DependencyProperty MaxImageBytesProperty = DependencyProperty.Register(
        nameof(MaxImageBytes),
        typeof(int),
        typeof(AnimatedEmoteImage),
        new PropertyMetadata(DefaultMaxImageBytes, OnImageUrlChanged));

    public AnimatedEmoteImage()
    {
        frameTimer = new DispatcherTimer(DispatcherPriority.Render);
        frameTimer.Tick += (_, _) => AdvanceFrame();
        Loaded += (_, _) => StartAnimationIfNeeded();
        Unloaded += (_, _) => frameTimer.Stop();
    }

    public string ImageUrl
    {
        get => (string)GetValue(ImageUrlProperty);
        set => SetValue(ImageUrlProperty, value);
    }

    public int MaxImageBytes
    {
        get => (int)GetValue(MaxImageBytesProperty);
        set => SetValue(MaxImageBytesProperty, value);
    }

    internal AnimatedEmoteImageCacheKey? CurrentImageCacheKey => currentImageCacheKey;

    internal bool IsImageLoadPending => imageLoadPending;

    internal bool HasAnimatedFrames => decodedImage is { Frames.Count: > 1 };

    internal bool ApplyAnimationClock(TimeSpan animationClock, out TimeSpan nextFrameDelay)
    {
        nextFrameDelay = TimeSpan.Zero;
        if (decodedImage is not { Frames.Count: > 1 } image)
        {
            return false;
        }

        var timing = SelectFrameForClock(image.Delays, animationClock);
        frameIndex = timing.FrameIndex;
        Source = image.Frames[frameIndex];
        nextFrameDelay = timing.NextFrameDelay;
        return true;
    }

    internal static bool IsCacheEntryCompleted(AnimatedEmoteImageCacheKey key)
    {
        return ImageCache.TryGetValue(key, out var cached) &&
            cached.IsValueCreated &&
            cached.Value.IsCompleted;
    }

    internal static void SetCachedSolidColorImageForTest(
        string imageUrl,
        int maxImageBytes,
        IReadOnlyList<Color> frameColors,
        IReadOnlyList<TimeSpan> delays,
        int width = 8,
        int height = 8)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Image URL must be absolute.", nameof(imageUrl));
        }

        if (frameColors.Count == 0)
        {
            throw new ArgumentException("At least one frame is required.", nameof(frameColors));
        }

        var frames = frameColors
            .Select(color => CreateSolidColorBitmap(color, Math.Max(1, width), Math.Max(1, height)))
            .Cast<ImageSource>()
            .ToArray();
        var image = new DecodedEmoteImage(frames, NormalizeDelays(frames.Length, delays));
        var key = CreateCacheKey(uri, Math.Clamp(maxImageBytes, 1, AbsoluteMaxImageBytes));
        ImageCache[key] = new Lazy<Task<DecodedEmoteImage?>>(
            () => Task.FromResult<DecodedEmoteImage?>(image),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal static void RemoveCachedImageForTest(string imageUrl, int maxImageBytes)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
        {
            return;
        }

        ImageCache.TryRemove(CreateCacheKey(uri, Math.Clamp(maxImageBytes, 1, AbsoluteMaxImageBytes)), out _);
    }

    private static void OnImageUrlChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is AnimatedEmoteImage image)
        {
            image.LoadImageAsync();
        }
    }

    private async void LoadImageAsync()
    {
        var version = Interlocked.Increment(ref loadVersion);
        var url = ImageUrl;

        frameTimer.Stop();
        currentImageCacheKey = null;
        decodedImage = null;
        frameIndex = 0;
        imageLoadPending = false;
        Source = null;

        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !IsSupportedImageUri(uri))
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        var maxImageBytes = Math.Clamp(MaxImageBytes, 1, AbsoluteMaxImageBytes);
        var key = CreateCacheKey(uri, maxImageBytes);
        currentImageCacheKey = key;
        imageLoadPending = true;
        var loadTask = GetOrLoadImageAsync(key);
        var image = await loadTask.ConfigureAwait(false);
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => ApplyLoadedImage(version, image)));
            return;
        }

        ApplyLoadedImage(version, image);
    }

    private void ApplyLoadedImage(long version, DecodedEmoteImage? image)
    {
        if (version != loadVersion)
        {
            return;
        }

        imageLoadPending = false;
        if (image is null || image.Frames.Count == 0)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        decodedImage = image;
        Source = image.Frames[0];
        Visibility = Visibility.Visible;
        StartAnimationIfNeeded();
    }

    private void StartAnimationIfNeeded()
    {
        frameTimer.Stop();
        if (!IsLoaded || decodedImage is not { Frames.Count: > 1 })
        {
            return;
        }

        frameTimer.Interval = decodedImage.Delays[frameIndex];
        frameTimer.Start();
    }

    private void AdvanceFrame()
    {
        if (decodedImage is not { Frames.Count: > 1 } image)
        {
            frameTimer.Stop();
            return;
        }

        frameIndex = (frameIndex + 1) % image.Frames.Count;
        Source = image.Frames[frameIndex];
        frameTimer.Interval = image.Delays[frameIndex];
    }

    private static Task<DecodedEmoteImage?> GetOrLoadImageAsync(AnimatedEmoteImageCacheKey key)
    {
        return ImageCache.GetOrAdd(
            key,
            static key => new Lazy<Task<DecodedEmoteImage?>>(
                () => LoadAndDecodeImageAndNotifyAsync(key),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static async Task<DecodedEmoteImage?> LoadAndDecodeImageAndNotifyAsync(AnimatedEmoteImageCacheKey key)
    {
        try
        {
            return await LoadAndDecodeImageAsync(key.Url, key.MaxImageBytes).ConfigureAwait(false);
        }
        finally
        {
            NotifyImageCacheEntryCompleted(key);
        }
    }

    private static void NotifyImageCacheEntryCompleted(AnimatedEmoteImageCacheKey key)
    {
        var handler = ImageCacheEntryCompleted;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(null, new AnimatedEmoteImageCacheCompletedEventArgs(key));
        }
        catch
        {
        }
    }

    private static Task<DecodedEmoteImage?> LoadAndDecodeImageAsync(string url, int maxImageBytes)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return Task.FromResult<DecodedEmoteImage?>(null);
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase) && uri.IsFile
            ? ReadAndDecodeFileAsync(uri.LocalPath, maxImageBytes)
            : DownloadAndDecodeImageAsync(url, maxImageBytes);
    }

    private static async Task<DecodedEmoteImage?> DownloadAndDecodeImageAsync(string url, int maxImageBytes)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (IsKickAssetHost(uri))
            {
                request.Headers.Referrer = new Uri("https://kick.com/");
            }

            using var response = await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength > maxImageBytes)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > maxImageBytes)
            {
                return null;
            }

            return await Task.Run(() => DecodeImage(bytes)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static async Task<DecodedEmoteImage?> ReadAndDecodeFileAsync(string path, int maxImageBytes)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length <= 0 || file.Length > maxImageBytes)
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(file.FullName).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > maxImageBytes)
            {
                return null;
            }

            return await Task.Run(() => DecodeImage(bytes)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static DecodedEmoteImage? DecodeImage(byte[] bytes)
    {
        return DecodeImageWithWpf(bytes) ?? DecodeStaticImageWithSkia(bytes);
    }

    internal static bool TryDecodeImageForTest(byte[] bytes, out int frameCount, out int width, out int height)
    {
        var image = DecodeImage(bytes);
        frameCount = image?.Frames.Count ?? 0;
        if (image?.Frames.FirstOrDefault() is BitmapSource firstFrame)
        {
            width = firstFrame.PixelWidth;
            height = firstFrame.PixelHeight;
            return true;
        }

        width = 0;
        height = 0;
        return false;
    }

    private static DecodedEmoteImage? DecodeImageWithWpf(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            var frames = IsGifDecoder(decoder)
                ? ComposeGifFrames(decoder)
                : DecodeStaticFrames(decoder);
            if (frames.Count == 0)
            {
                return null;
            }

            var delays = new List<TimeSpan>(frames.Count);
            for (var index = 0; index < frames.Count; index++)
            {
                delays.Add(ReadGifFrameDelay(decoder.Frames[Math.Min(index, decoder.Frames.Count - 1)]));
            }

            return new DecodedEmoteImage(frames, delays);
        }
        catch (Exception ex) when (ex is FileFormatException or NotSupportedException)
        {
            return null;
        }
    }

    private static DecodedEmoteImage? DecodeStaticImageWithSkia(byte[] bytes)
    {
        try
        {
            using var decoded = SKBitmap.Decode(bytes);
            if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
            {
                return null;
            }

            var imageInfo = new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var normalized = new SKBitmap(imageInfo);
            using (var canvas = new SKCanvas(normalized))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(decoded, 0, 0);
                canvas.Flush();
            }

            var frame = CreateBitmapSource(normalized);
            return new DecodedEmoteImage([frame], [DefaultFrameDelay]);
        }
        catch (Exception ex) when (ex is ArgumentException or DllNotFoundException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static ImageSource CreateBitmapSource(SKBitmap bitmap)
    {
        var pixels = new byte[bitmap.ByteCount];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        var source = BitmapSource.Create(
            bitmap.Width,
            bitmap.Height,
            96,
            96,
            PixelFormats.Pbgra32,
            null,
            pixels,
            bitmap.RowBytes);
        return FreezeBitmap(source);
    }

    private static BitmapSource CreateSolidColorBitmap(Color color, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = color.B;
            pixels[index + 1] = color.G;
            pixels[index + 2] = color.R;
            pixels[index + 3] = color.A;
        }

        var source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32,
            null,
            pixels,
            width * 4);
        return FreezeBitmap(source);
    }

    private static IReadOnlyList<ImageSource> DecodeStaticFrames(BitmapDecoder decoder)
    {
        var frames = new List<ImageSource>(decoder.Frames.Count);
        foreach (var frame in decoder.Frames)
        {
            frames.Add(FreezeImage(frame));
        }

        return frames;
    }

    private static IReadOnlyList<ImageSource> ComposeGifFrames(BitmapDecoder decoder)
    {
        var width = Math.Max(1, decoder.Frames.Max(frame => ReadGifFrameLeft(frame) + frame.PixelWidth));
        var height = Math.Max(1, decoder.Frames.Max(frame => ReadGifFrameTop(frame) + frame.PixelHeight));
        var composedFrames = new List<ImageSource>(decoder.Frames.Count);
        BitmapSource canvas = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

        foreach (var frame in decoder.Frames)
        {
            var beforeFrame = ReadGifDisposal(frame) == GifDisposal.RestoreToPrevious
                ? CloneBitmap(canvas)
                : null;

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawImage(canvas, new Rect(0, 0, width, height));
                context.DrawImage(
                    frame,
                    new Rect(
                        ReadGifFrameLeft(frame),
                        ReadGifFrameTop(frame),
                        frame.PixelWidth,
                        frame.PixelHeight));
            }

            var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);
            var displayFrame = CloneBitmap(rendered);
            composedFrames.Add(displayFrame);

            canvas = ReadGifDisposal(frame) switch
            {
                GifDisposal.RestoreToBackground => ClearFrameArea(rendered, frame, width, height),
                GifDisposal.RestoreToPrevious when beforeFrame is not null => CopyToRenderTarget(beforeFrame, width, height),
                _ => rendered
            };
        }

        return composedFrames;
    }

    private static BitmapSource ClearFrameArea(BitmapSource source, BitmapFrame frame, int width, int height)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, width, height));
            context.PushOpacity(0);
            context.DrawRectangle(
                Brushes.Transparent,
                null,
                new Rect(
                    ReadGifFrameLeft(frame),
                    ReadGifFrameTop(frame),
                    frame.PixelWidth,
                    frame.PixelHeight));
            context.Pop();
        }

        var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);

        var pixels = new byte[width * height * 4];
        rendered.CopyPixels(pixels, width * 4, 0);
        var left = Math.Clamp(ReadGifFrameLeft(frame), 0, width);
        var top = Math.Clamp(ReadGifFrameTop(frame), 0, height);
        var right = Math.Clamp(left + frame.PixelWidth, 0, width);
        var bottom = Math.Clamp(top + frame.PixelHeight, 0, height);
        for (var y = top; y < bottom; y++)
        {
            Array.Clear(pixels, (y * width + left) * 4, (right - left) * 4);
        }

        var cleared = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        return FreezeBitmap(cleared);
    }

    private static RenderTargetBitmap CopyToRenderTarget(BitmapSource source, int width, int height)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, width, height));
        }

        var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        return rendered;
    }

    private static BitmapSource CloneBitmap(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var clone = BitmapSource.Create(
            converted.PixelWidth,
            converted.PixelHeight,
            96,
            96,
            PixelFormats.Pbgra32,
            null,
            pixels,
            stride);
        return FreezeBitmap(clone);
    }

    private static ImageSource FreezeImage(BitmapSource image)
    {
        if (image.CanFreeze)
        {
            image.Freeze();
        }

        return image;
    }

    private static BitmapSource FreezeBitmap(BitmapSource bitmap)
    {
        if (bitmap.CanFreeze)
        {
            bitmap.Freeze();
        }

        return bitmap;
    }

    private static bool IsGifDecoder(BitmapDecoder decoder)
    {
        return decoder.CodecInfo?.FileExtensions?.Contains("gif", StringComparison.OrdinalIgnoreCase) == true ||
            decoder.GetType().Name.Contains("Gif", StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadGifFrameLeft(BitmapFrame frame)
    {
        return ReadMetadataInt(frame, "/imgdesc/Left", 0);
    }

    private static int ReadGifFrameTop(BitmapFrame frame)
    {
        return ReadMetadataInt(frame, "/imgdesc/Top", 0);
    }

    private static GifDisposal ReadGifDisposal(BitmapFrame frame)
    {
        return ReadMetadataInt(frame, "/grctlext/Disposal", 0) switch
        {
            2 => GifDisposal.RestoreToBackground,
            3 => GifDisposal.RestoreToPrevious,
            _ => GifDisposal.None
        };
    }

    private static int ReadMetadataInt(BitmapFrame frame, string query, int fallback)
    {
        if (frame.Metadata is not BitmapMetadata metadata)
        {
            return fallback;
        }

        try
        {
            return metadata.GetQuery(query) switch
            {
                byte value => value,
                ushort value => value,
                short value => value,
                uint value => value <= int.MaxValue ? (int)value : fallback,
                int value => value,
                _ => fallback
            };
        }
        catch (NotSupportedException)
        {
            return fallback;
        }
    }

    private static TimeSpan ReadGifFrameDelay(BitmapFrame frame)
    {
        if (frame.Metadata is not BitmapMetadata metadata)
        {
            return DefaultFrameDelay;
        }

        try
        {
            var delay = metadata.GetQuery("/grctlext/Delay");
            var delayUnits = delay switch
            {
                byte value => value,
                ushort value => value,
                short value => value,
                uint value => value <= int.MaxValue ? (int)value : 0,
                int value => value,
                _ => 0
            };

            if (delayUnits <= 0)
            {
                return DefaultFrameDelay;
            }

            var frameDelay = TimeSpan.FromMilliseconds(delayUnits * 10);
            return frameDelay < MinimumFrameDelay ? MinimumFrameDelay : frameDelay;
        }
        catch (NotSupportedException)
        {
            return DefaultFrameDelay;
        }
    }

    private static AnimatedEmoteImageCacheKey CreateCacheKey(Uri uri, int maxImageBytes)
    {
        return new AnimatedEmoteImageCacheKey(uri.ToString(), maxImageBytes);
    }

    private static IReadOnlyList<TimeSpan> NormalizeDelays(int frameCount, IReadOnlyList<TimeSpan> delays)
    {
        var normalized = new TimeSpan[frameCount];
        for (var index = 0; index < frameCount; index++)
        {
            var delay = index < delays.Count ? delays[index] : DefaultFrameDelay;
            normalized[index] = delay < MinimumFrameDelay ? MinimumFrameDelay : delay;
        }

        return normalized;
    }

    private static AnimationFrameTiming SelectFrameForClock(IReadOnlyList<TimeSpan> delays, TimeSpan animationClock)
    {
        if (delays.Count == 0)
        {
            return new AnimationFrameTiming(0, DefaultFrameDelay);
        }

        var totalTicks = 0L;
        foreach (var delay in delays)
        {
            totalTicks += Math.Max(MinimumFrameDelay.Ticks, delay.Ticks);
        }

        if (totalTicks <= 0)
        {
            return new AnimationFrameTiming(0, DefaultFrameDelay);
        }

        var positionTicks = animationClock.Ticks % totalTicks;
        if (positionTicks < 0)
        {
            positionTicks += totalTicks;
        }

        for (var index = 0; index < delays.Count; index++)
        {
            var delayTicks = Math.Max(MinimumFrameDelay.Ticks, delays[index].Ticks);
            if (positionTicks < delayTicks)
            {
                return new AnimationFrameTiming(
                    index,
                    TimeSpan.FromTicks(Math.Max(MinimumFrameDelay.Ticks, delayTicks - positionTicks)));
            }

            positionTicks -= delayTicks;
        }

        return new AnimationFrameTiming(0, delays[0]);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        client.DefaultRequestHeaders.Accept.ParseAdd("image/png,image/apng,image/gif,image/jpeg,image/bmp,image/*;q=0.8,*/*;q=0.5");
        return client;
    }

    private static bool IsKickAssetHost(Uri uri)
    {
        return string.Equals(uri.Host, "kick.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".kick.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedImageUri(Uri uri)
    {
        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase) && uri.IsFile);
    }

    private sealed record DecodedEmoteImage(IReadOnlyList<ImageSource> Frames, IReadOnlyList<TimeSpan> Delays);
    private readonly record struct AnimationFrameTiming(int FrameIndex, TimeSpan NextFrameDelay);

    private enum GifDisposal
    {
        None,
        RestoreToBackground,
        RestoreToPrevious
    }
}

internal readonly record struct AnimatedEmoteImageCacheKey(string Url, int MaxImageBytes);

internal sealed class AnimatedEmoteImageCacheCompletedEventArgs : EventArgs
{
    public AnimatedEmoteImageCacheCompletedEventArgs(AnimatedEmoteImageCacheKey key)
    {
        Key = key;
    }

    public AnimatedEmoteImageCacheKey Key { get; }
}

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using StreamlinkVlcStudio.Infrastructure.Replay;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

/// <summary>A bounded, per-overlay storyboard cache. All decoding runs away from the dispatcher.</summary>
internal sealed class ReplaySeekPreviewImages
{
    private readonly TwitchSeekPreviewClient client = new();
    private readonly LiveSeekPreviewImages liveImages = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Uri, BitmapSource> sheets = [];
    private string? cachedVideoId;
    private TwitchSeekStoryboard? storyboard;
    private long metadataExpires;
    private Uri? failedImage;
    private long imageRetryAfter;

    internal async Task<BitmapSource?> GetAsync(ReplaySeekPreviewSource source, double seconds, CancellationToken token)
    {
        if (source.VideoId is { } videoId)
        {
            try
            {
                var image = await GetAsync(videoId, seconds, token).ConfigureAwait(false);
                if (image is not null) return image;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        return await liveImages.GetAsync(source, seconds, token).ConfigureAwait(false);
    }

    internal async Task<BitmapSource?> GetAsync(string videoId, double seconds, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cachedVideoId != videoId || Environment.TickCount64 >= metadataExpires)
            {
                storyboard = null;
                sheets.Clear();
                failedImage = null;
                cachedVideoId = videoId;
                // Cancellation should allow the next hover to retry immediately.
                metadataExpires = 0;
                try
                {
                    storyboard = await client.GetStoryboardAsync(videoId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (IsPreviewFailure(ex)) { }
                metadataExpires = Environment.TickCount64 + 60_000;
            }
            var frame = storyboard?.GetFrame(seconds);
            if (frame is null || (failedImage == frame.ImageUri && Environment.TickCount64 < imageRetryAfter)) return null;
            if (!sheets.TryGetValue(frame.ImageUri, out var sheet))
            {
                var bytes = await client.GetImageAsync(frame.ImageUri, cancellationToken).ConfigureAwait(false);
                sheet = DecodeSheet(bytes);
                if (sheet is null) throw new InvalidDataException("Invalid preview sprite image.");
                cancellationToken.ThrowIfCancellationRequested();
                if (sheets.Count >= 2) sheets.Remove(sheets.Keys.First());
                sheets.Add(frame.ImageUri, sheet);
            }
            if (frame.X + frame.Width > sheet.PixelWidth || frame.Y + frame.Height > sheet.PixelHeight) return null;
            var crop = new CroppedBitmap(sheet, new Int32Rect(frame.X, frame.Y, frame.Width, frame.Height));
            crop.Freeze();
            return crop;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsPreviewFailure(ex))
        {
            failedImage = storyboard?.GetFrame(seconds)?.ImageUri;
            imageRetryAfter = Environment.TickCount64 + 30_000;
            return null;
        }
        finally { gate.Release(); }
    }

    internal static BitmapSource? DecodeSheet(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.FrameCount > 1 || codec.Info.Width is < 1 or > 8192 ||
            codec.Info.Height is < 1 or > 8192 || (long)codec.Info.Width * codec.Info.Height > 16_000_000) return null;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success) return null;
        var source = BitmapSource.Create(info.Width, info.Height, 96, 96, PixelFormats.Pbgra32, null,
            bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
        source.Freeze();
        return source;
    }

    private static bool IsPreviewFailure(Exception exception) => exception is
        System.Net.Http.HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or
        InvalidOperationException or ArgumentException or NotSupportedException;
}

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Infrastructure.Replay;
using StreamlinkVlcStudio.Infrastructure.Vlc;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

internal sealed record ReplaySeekPreviewSource(string ReplayId, string? VideoId, Uri? PlaylistUri,
    PlatformKind Platform, DateTimeOffset? StartedAt, string VlcDirectory);

internal sealed class LiveSeekPreviewImages
{
    private readonly LiveSeekPreviewClient client;
    private readonly Func<byte[], byte[]?, string, CancellationToken, Task<byte[]?>> decode;
    private readonly Func<long> clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<(Uri Segment, Uri? Initialization), BitmapSource> frames = [];
    private readonly Dictionary<Uri, byte[]> initializationSections = [];
    private ReplaySeekPreviewSource? source;
    private LiveSeekPlaylist? playlist;
    private long refreshAfter;
    private (Uri Segment, Uri? Initialization)? failedSegment;
    private long retryAfter;

    internal LiveSeekPreviewImages() : this(new(), LibVlcPreviewDecoder.DecodeAsync, () => Environment.TickCount64) { }

    internal LiveSeekPreviewImages(LiveSeekPreviewClient client,
        Func<byte[], byte[]?, string, CancellationToken, Task<byte[]?>> decode, Func<long> clock)
    {
        this.client = client;
        this.decode = decode;
        this.clock = clock;
    }

    internal async Task<BitmapSource?> GetAsync(ReplaySeekPreviewSource request, double seconds, CancellationToken token)
    {
        if (request.PlaylistUri is null || string.IsNullOrWhiteSpace(request.VlcDirectory) ||
            !double.IsFinite(seconds) || seconds < 0) return null;
        await gate.WaitAsync(token).ConfigureAwait(false);
        (Uri Segment, Uri? Initialization)? segmentKey = null;
        try
        {
            if (source != request)
            {
                source = request;
                playlist = null;
                refreshAfter = 0;
                failedSegment = null;
                frames.Clear();
                initializationSections.Clear();
            }
            if (clock() >= refreshAfter)
            {
                // Refresh the manifest as the live archive grows; retain already-decoded frames.
                playlist = await client.GetPlaylistAsync(request.PlaylistUri, request.Platform, request.StartedAt, token)
                    .ConfigureAwait(false);
                refreshAfter = clock() + (playlist is null ? 15_000 : 5_000);
            }
            var segment = playlist?.GetSegment(seconds);
            if (segment is null) return null;
            segmentKey = (segment.Uri, segment.InitializationUri);
            if (frames.TryGetValue(segmentKey.Value, out var cached)) return cached;
            if (failedSegment == segmentKey && clock() < retryAfter) return null;
            byte[]? initialization = null;
            if (segment.InitializationUri is { } initializationUri &&
                !initializationSections.TryGetValue(initializationUri, out initialization))
            {
                initialization = await client.GetSegmentAsync(initializationUri, request.Platform, token,
                    initialization: true).ConfigureAwait(false);
                if (initializationSections.Count >= 2) initializationSections.Remove(initializationSections.Keys.First());
                initializationSections.Add(initializationUri, initialization);
            }
            var bytes = await client.GetSegmentAsync(segment.Uri, request.Platform, token).ConfigureAwait(false);
            var pixels = await decode(bytes, initialization, request.VlcDirectory, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (pixels is null) throw new InvalidDataException("No preview frame in the DVR segment.");
            var image = BitmapSource.Create(LibVlcPreviewDecoder.Width, LibVlcPreviewDecoder.Height, 96, 96,
                PixelFormats.Bgr32, null, pixels, LibVlcPreviewDecoder.Width * 4);
            image.Freeze();
            if (frames.Count >= 24) frames.Remove(frames.Keys.First());
            frames.Add(segmentKey.Value, image);
            return image;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or System.Net.Http.HttpRequestException or
            IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException or
            System.Runtime.InteropServices.ExternalException or DllNotFoundException or EntryPointNotFoundException)
        {
            if (segmentKey is null) { playlist = null; refreshAfter = clock() + 15_000; }
            else { failedSegment = segmentKey; retryAfter = clock() + 15_000; }
            return null;
        }
        finally { gate.Release(); }
    }
}

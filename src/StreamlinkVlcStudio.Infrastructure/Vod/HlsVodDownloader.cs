using System.Net;
using System.Net.Http.Headers;
using System.Text;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Limits;
using StreamlinkVlcStudio.Infrastructure.Replay;
using StreamlinkVlcStudio.Infrastructure.Twitch;

namespace StreamlinkVlcStudio.Infrastructure.Vod;

internal sealed record VodDownloadProgress(int CompletedSegments, int TotalSegments, long BytesDownloaded, TimeSpan Duration);

internal sealed class HlsVodDownloader(HttpClient client, ReplayUrlSecurityValidator validator,
    VodDownloadBandwidthLimiter bandwidthLimiter)
{
    internal async Task<OfflineVodPackage> DownloadAsync(Uri playlistUri, PlatformKind platform, string directory,
        Func<VodDownloadProgress, Task> progress, CancellationToken cancellationToken, bool allowLocalPlaylist = false)
    {
        string content;
        Uri effectiveUri;
        if (allowLocalPlaylist && platform == PlatformKind.Twitch && playlistUri.IsFile && !playlistUri.IsUnc)
        {
            OfflineVodPackage.RequireRegularPath(playlistUri.LocalPath);
            var bytes = await BoundedByteReader.ReadFileAsync(playlistUri.LocalPath, PayloadLimits.PlaylistBytes, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The Twitch fallback playlist is missing or too large.");
            content = new UTF8Encoding(false, true).GetString(bytes);
            effectiveUri = playlistUri;
        }
        else (content, effectiveUri) = await ValidatedReplayHttpClient.ReadPlaylistAsync(client, validator,
            playlistUri, platform, cancellationToken).ConfigureAwait(false);
        var playlist = OfflineHlsPlaylist.Parse(content, effectiveUri);
        foreach (var asset in playlist.Assets)
            if (!ReplayUrlSecurityValidator.TryValidateProviderUri(asset.Uri, platform))
                throw new InvalidDataException("The VOD references media outside the approved provider CDN.");
        await progress(new VodDownloadProgress(0, playlist.SegmentCount, 0, playlist.Duration)).ConfigureAwait(false);
        Directory.CreateDirectory(directory);
        OfflineVodPackage.RequireRegularPath(directory);
        var progressGate = new object();
        var completedSegments = 0;
        long downloadedBytes = 0;
        await Parallel.ForEachAsync(playlist.Assets, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken
        }, async (asset, token) =>
        {
            var length = await DownloadAssetAsync(asset, platform, Path.Combine(directory, asset.FileName), token).ConfigureAwait(false);
            VodDownloadProgress update;
            lock (progressGate)
            {
                downloadedBytes += length;
                if (asset.IsSegment) completedSegments++;
                update = new VodDownloadProgress(completedSegments, playlist.SegmentCount, downloadedBytes, playlist.Duration);
            }
            await progress(update).ConfigureAwait(false);
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await OfflineVodPackage.CreateAsync(directory, playlist, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> DownloadAssetAsync(OfflineHlsAsset asset, PlatformKind platform, string path,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await ValidatedReplayHttpClient.SendGetAsync(client, validator, asset.Uri, platform,
                    address =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, address);
                        if (asset.Offset is { } offset && asset.Length is { } length)
                            request.Headers.Range = new RangeHeaderValue(offset, checked(offset + length - 1));
                        return request;
                    }, cancellationToken, useReadTimeout: true).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (asset.Offset is { } expectedOffset && asset.Length is { } expectedLength)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" ||
                        range.From != expectedOffset || range.To != expectedOffset + expectedLength - 1)
                        throw new InvalidDataException("The VOD server did not return the requested media byte range.");
                }
                else if (response.StatusCode != HttpStatusCode.OK)
                    throw new InvalidDataException("The VOD server returned an unexpected partial media response.");
                if (response.Content.Headers.ContentType?.MediaType is "text/html" or "application/json" ||
                    response.Content.Headers.ContentEncoding.Count != 0)
                    throw new InvalidDataException("The VOD server returned an error page instead of uncompressed media.");
                var limit = asset.IsKey ? 16 : asset.Length ?? OfflineHlsPlaylist.MaximumAssetBytes;
                var declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength is <= 0 || declaredLength > limit ||
                    (asset.Length is { } rangeLength && declaredLength is { } bodyLength && bodyLength != rangeLength))
                    throw new InvalidDataException("The VOD media response has an invalid size.");
                OfflineVodPackage.RequireRegularPath(path);
                await using var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var source = new VodDownloadThrottledStream(responseStream, bandwidthLimiter);
                long received = 0;
                if (platform == PlatformKind.Twitch && asset.IsSegment && !asset.IsEncrypted &&
                    asset.Uri.AbsolutePath.EndsWith("-muted.ts", StringComparison.OrdinalIgnoreCase))
                {
                    var repaired = await TwitchMutedSegmentRepairCopier.CopyAsync(source, destination, limit,
                        TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
                    received = repaired.BytesCopied;
                }
                else
                {
                    var buffer = new byte[65536];
                    while (true)
                    {
                        var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (count == 0) break;
                        received += count;
                        if (received > limit) throw new InvalidDataException("A VOD media resource exceeds the supported size limit.");
                        await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    }
                }
                if (received == 0 || (declaredLength is { } declared && received != declared) ||
                    (asset.Length is { } expected && received != expected) || (asset.IsKey && received != 16))
                    throw new InvalidDataException("A VOD media resource is empty or truncated.");
                if (asset.IsEncrypted && received % 16 != 0)
                    throw new InvalidDataException("An encrypted VOD media resource is truncated or has an invalid AES block size.");
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                return received;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && attempt < 2 && IsTransient(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception exception) => exception is OperationCanceledException or TimeoutException or IOException ||
        exception is HttpRequestException { StatusCode: null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests } ||
        exception is HttpRequestException { StatusCode: { } code } && (int)code >= 500;
}

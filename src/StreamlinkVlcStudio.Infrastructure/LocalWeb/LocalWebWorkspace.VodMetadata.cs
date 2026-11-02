using System.Collections.Concurrent;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

public sealed partial class LocalWebWorkspace
{
    private sealed record VodMetadata(string ThumbnailUrl, DateTimeOffset? Published);
    private sealed record CachedVodMetadata(VodMetadata Value, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, CachedVodMetadata> vodMetadata = new(StringComparer.Ordinal);

    private async Task<object> DownloadsAsync(AppSettings settings, CancellationToken token)
    {
        var downloads = await services.Downloads.GetDownloadsAsync(token).ConfigureAwait(false);
        await LoadDownloadMetadataAsync(downloads.Select(d => d.Target).ToArray(), settings, token).ConfigureAwait(false);
        return new
        {
            items = downloads.Select(d => (Download: d, Metadata: DownloadMetadata(d.Target)))
                .OrderByDescending(entry => entry.Metadata.Published)
                .ThenByDescending(entry => entry.Download.CreatedAtUtc)
                .Select(entry => DownloadCard(entry.Download, entry.Metadata)).ToArray()
        };
    }

    private object DownloadCard(VodDownloadItem download, VodMetadata metadata) => new
    {
        download.Id,
        key = Register(download.Target),
        title = download.Target.TabTitle,
        channel = download.Target.Channel,
        platform = download.Target.Platform.ToString(),
        state = download.State.ToString(),
        download.Quality,
        profileImageUrl = LocalWebProfileImages.PublicUrl(download.Target.ProfileImageUrl),
        thumbnailUrl = metadata.ThumbnailUrl,
        published = metadata.Published,
        duration = (download.Duration > TimeSpan.Zero ? download.Duration : download.Target.MediaDuration).TotalSeconds,
        download.CompletedSegments,
        download.TotalSegments,
        download.BytesDownloaded,
        format = download.Format.ToString(),
        error = download.State == VodDownloadState.Failed ? "Download failed. Check the desktop app for details." : ""
    };

    private VodMetadata DownloadMetadata(StreamTarget target)
    {
        vodMetadata.TryGetValue(target.TabIdentityKey, out var cached);
        return new(LocalWebProfileImages.PublicUrl(string.IsNullOrWhiteSpace(target.MediaThumbnailUrl)
                ? cached?.Value.ThumbnailUrl ?? "" : target.MediaThumbnailUrl),
            target.MediaPublishedAtUtc ?? cached?.Value.Published);
    }

    private void RememberVodMetadata(StreamTarget target, VodMetadata metadata)
    {
        // Bound session memory, including unsuccessful lookups. A missing provider result
        // is retried later, while download progress polls reuse successful metadata.
        if (vodMetadata.Count >= 5000)
            foreach (var entry in vodMetadata.OrderBy(entry => entry.Value.Expires).Take(1000))
                vodMetadata.TryRemove(entry.Key, out _);
        var complete = metadata.ThumbnailUrl.Length > 0 && metadata.Published is not null;
        vodMetadata[target.TabIdentityKey] = new(metadata, DateTimeOffset.UtcNow.AddMinutes(complete ? 30 : 1));
    }

    private async Task LoadDownloadMetadataAsync(IReadOnlyList<StreamTarget> downloads, AppSettings settings, CancellationToken token)
    {
        var missing = downloads.DistinctBy(target => target.TabIdentityKey).Where(target =>
            (string.IsNullOrWhiteSpace(target.MediaThumbnailUrl) || target.MediaPublishedAtUtc is null) &&
            (!vodMetadata.TryGetValue(target.TabIdentityKey, out var cached) || cached.Expires <= DateTimeOffset.UtcNow));
        // Kick lists broadcasts by channel; share each page between all downloads from
        // that channel, including older library entries saved before artwork was stored.
        var groups = missing.GroupBy(target => target.Platform == PlatformKind.Kick
            ? target.StateKey : target.TabIdentityKey).Select(group => group.ToArray()).ToArray();
        if (groups.Length == 0) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            await Parallel.ForEachAsync(groups, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = budget.Token },
                async (group, cancellation) =>
                {
                    try
                    {
                        if (group[0].Platform == PlatformKind.Twitch)
                        {
                            var video = await services.TwitchVods.GetVideoAsync(group[0].MediaId, cancellation).ConfigureAwait(false);
                            if (video is not null)
                                RememberVodMetadata(group[0], new(LocalWebProfileImages.PublicUrl(video.ThumbnailUrl), video.PublishedAtUtc ?? video.CreatedAtUtc));
                        }
                        else
                        {
                            var pending = group.ToList();
                            var cursor = "";
                            var cursors = new HashSet<string>(StringComparer.Ordinal);
                            for (var page = 0; page < 10 && pending.Count > 0; page++)
                            {
                                var result = await services.KickVods.SearchAsync(new(group[0].Channel, cursor), settings, cancellation).ConfigureAwait(false);
                                foreach (var target in pending.ToArray())
                                {
                                    var video = result.Videos.FirstOrDefault(video =>
                                        video.ChannelSlug.Equals(target.Channel, StringComparison.OrdinalIgnoreCase) &&
                                        new[] { video.Id, video.Uuid, video.LiveStreamId }.Contains(target.MediaId, StringComparer.OrdinalIgnoreCase));
                                    if (video is null) continue;
                                    RememberVodMetadata(target, new(LocalWebProfileImages.PublicUrl(video.ThumbnailUrl), video.CreatedAtUtc ?? video.StartedAtUtc));
                                    pending.Remove(target);
                                }
                                cursor = result.NextCursor;
                                if (!result.IsAvailable || string.IsNullOrWhiteSpace(cursor) || !cursors.Add(cursor)) break;
                            }
                        }
                    }
                    catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException ||
                        exception is OperationCanceledException && !token.IsCancellationRequested)
                    {
                        // Artwork availability must not prevent opening the offline library.
                    }
                    foreach (var target in group)
                        if (!vodMetadata.TryGetValue(target.TabIdentityKey, out var cached) || cached.Expires <= DateTimeOffset.UtcNow)
                            RememberVodMetadata(target, DownloadMetadata(target));
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // Return the library with whatever metadata was available within the budget.
        }
        token.ThrowIfCancellationRequested();
    }
}

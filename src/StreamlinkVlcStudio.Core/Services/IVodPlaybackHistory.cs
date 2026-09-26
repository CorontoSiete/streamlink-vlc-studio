using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Services;

public sealed record VodPlaybackBookmark(
    TimeSpan Position, TimeSpan Duration, DateTimeOffset UpdatedAtUtc, bool Completed = false,
    bool HasBeenWatched = false);

public interface IVodPlaybackHistory
{
    event Action<StreamTarget, VodPlaybackBookmark>? BookmarkChanged;

    Task<VodPlaybackBookmark?> GetAsync(StreamTarget target, CancellationToken cancellationToken = default);
    // Updates memory immediately so closing and immediately reopening a tab sees the new position.
    void Remember(StreamTarget target, VodPlaybackBookmark bookmark);
    Task SaveAsync(CancellationToken cancellationToken = default);
}

using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Core.Services;

public interface ITwitchVodService
{
    Task<TwitchVodSearchResult> SearchAsync(
        TwitchVodSearchRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default);
}

public enum TwitchVodSearchStatus
{
    Available,
    NotConfigured,
    NotFound,
    Unavailable
}

public sealed record TwitchVodSearchResult(
    TwitchVodSearchStatus Status,
    TwitchVodBroadcaster? Broadcaster,
    IReadOnlyList<TwitchVodItem> Videos,
    string NextCursor,
    string Message)
{
    public bool IsAvailable => Status == TwitchVodSearchStatus.Available;
}

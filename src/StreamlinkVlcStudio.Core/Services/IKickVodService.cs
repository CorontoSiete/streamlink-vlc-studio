using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Core.Services;

public interface IKickVodService
{
    Task<KickVodSearchResult> SearchAsync(
        KickVodSearchRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default);
}

public enum KickVodSearchStatus
{
    Available,
    NotFound,
    Unavailable
}

public sealed record KickVodSearchResult(
    KickVodSearchStatus Status,
    IReadOnlyList<KickVodItem> Videos,
    string NextCursor,
    string Message)
{
    public bool IsAvailable => Status == KickVodSearchStatus.Available;
}

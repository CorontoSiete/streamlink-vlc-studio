using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Core.Services;

public interface IFollowedStreamsService
{
    Task<FollowedLiveStreamsResult> GetLiveFollowedStreamsAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default);
}

public sealed record FollowedLiveStreamsResult(
    IReadOnlyList<FollowedLiveStream> Streams,
    IReadOnlyList<string> Messages,
    IReadOnlyList<PlatformKind>? SucceededPlatforms = null);

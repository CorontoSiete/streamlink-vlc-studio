using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Core.Services;

public interface IReplayResolver
{
    Task<ReplaySessionInfo> ResolveCurrentReplayAsync(
        StreamTarget target,
        string quality,
        AppSettings settings,
        CancellationToken cancellationToken = default);
}

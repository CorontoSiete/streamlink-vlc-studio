using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Core.Services;

public interface IReplayChatProvider
{
    Task<ReplayChatLoadResult> LoadChatAsync(
        ReplaySessionInfo replay,
        AppSettings settings,
        TimeSpan offset,
        CancellationToken cancellationToken = default);
}

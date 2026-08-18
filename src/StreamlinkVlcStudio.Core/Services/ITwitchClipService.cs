using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Core.Services;

public interface ITwitchClipService
{
    Task<TwitchClipResult> CreateLiveClipAsync(
        StreamTarget target,
        ChatSettings settings,
        CancellationToken cancellationToken = default);
}

public sealed record TwitchClipResult(string ClipId, Uri ClipUri);

using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Services;

public interface IKickClipService
{
    /// <summary>Creates a clip in the background and returns its confirmed publication, or null if unconfirmed.</summary>
    Task<KickClipResult?> CreateLiveClipAsync(StreamTarget target, CancellationToken cancellationToken = default);
}

public sealed record KickClipResult(string ClipId, Uri ClipUri);

using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Services;

public interface IChannelFollowService
{
    /// <summary>Reads website follow state without prompting for sign-in. Null means no verified session.</summary>
    Task<ChannelFollowResult?> GetFollowStatusAsync(StreamTarget target, CancellationToken cancellationToken = default);

    /// <summary>Follows the broadcaster using the platform website account, with server confirmation.</summary>
    Task<ChannelFollowResult?> FollowAsync(StreamTarget target, CancellationToken cancellationToken = default);

    /// <summary>Unfollows the broadcaster using the platform website account, with server confirmation.</summary>
    Task<ChannelFollowResult?> UnfollowAsync(StreamTarget target, CancellationToken cancellationToken = default);
}

public sealed record ChannelFollowResult(
    PlatformKind Platform,
    string Channel,
    IReadOnlyList<string>? KickFollowedChannels = null,
    bool IsFollowing = true);

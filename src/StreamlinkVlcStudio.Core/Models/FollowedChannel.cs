namespace StreamlinkVlcStudio.Core.Models;

public sealed record FollowedChannel(
    PlatformKind Platform,
    string Channel,
    string DisplayName,
    string Url,
    string ProfileImageUrl = "")
{
    public StreamTarget Target => new(Platform, Channel, Url, ProfileImageUrl: ProfileImageUrl);
}

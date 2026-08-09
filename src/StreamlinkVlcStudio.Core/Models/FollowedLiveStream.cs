namespace StreamlinkVlcStudio.Core.Models;

public sealed record FollowedLiveStream(
    PlatformKind Platform,
    string Channel,
    string DisplayName,
    string Title,
    string CategoryName,
    int? ViewerCount,
    string ThumbnailUrl,
    DateTimeOffset? StartedAtUtc,
    bool? IsMature,
    string Language,
    string Url,
    string ProfileImageUrl = "")
{
    public StreamTarget Target => new(
        Platform,
        Channel,
        Url,
        CategoryName: CategoryName,
        ProfileImageUrl: ProfileImageUrl);
}

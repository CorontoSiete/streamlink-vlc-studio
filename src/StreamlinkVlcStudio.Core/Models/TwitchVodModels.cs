namespace StreamlinkVlcStudio.Core.Models;

public enum TwitchVodTypeFilter
{
    Archive,
    Highlight,
    Upload,
    All
}

public enum TwitchVodAccessKind
{
    Unknown,
    Public,
    SubscriberOnly
}

public sealed record TwitchVodSearchRequest(
    string Streamer,
    TwitchVodTypeFilter Type = TwitchVodTypeFilter.Archive,
    string Cursor = "",
    int PageSize = 100);

public sealed record TwitchVodBroadcaster(
    string Id,
    string Login,
    string DisplayName,
    string ProfileImageUrl = "");

public sealed record TwitchVodItem(
    string Id,
    string StreamId,
    string BroadcasterId,
    string ChannelLogin,
    string ChannelDisplayName,
    string Title,
    string Description,
    string Url,
    string ThumbnailUrl,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    TimeSpan Duration,
    int? ViewCount,
    TwitchVodTypeFilter Type,
    TwitchVodAccessKind AccessKind = TwitchVodAccessKind.Unknown,
    string ProfileImageUrl = "");

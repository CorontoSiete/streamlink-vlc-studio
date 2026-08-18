namespace StreamlinkVlcStudio.Core.Models;

public sealed record KickVodSearchRequest(
    string Channel,
    string Cursor = "",
    int PageSize = 50);

public sealed record KickVodItem(
    string Id,
    string LiveStreamId,
    string Uuid,
    string ChannelSlug,
    string ChannelDisplayName,
    string Title,
    string Url,
    string Source,
    string ThumbnailUrl,
    string CategoryName,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    TimeSpan Duration,
    int? ViewCount,
    string ChannelId = "",
    string ProfileImageUrl = "");

namespace StreamlinkVlcStudio.Core.Models;

public sealed record BrowseCategory(
    PlatformKind Platform,
    string Id,
    string Name,
    string ThumbnailUrl,
    IReadOnlyList<string> Tags,
    int? ViewerCount = null);

public sealed record BrowseLiveStream(
    PlatformKind Platform,
    string Channel,
    string DisplayName,
    string Title,
    string CategoryId,
    string CategoryName,
    int? ViewerCount,
    string ThumbnailUrl,
    DateTimeOffset? StartedAtUtc,
    bool? IsMature,
    string Language,
    string Url)
{
    public StreamTarget Target => new(Platform, Channel, Url, CategoryName: CategoryName);
}

public sealed record BrowseCategoryRequest(
    PlatformKind Platform,
    string Query = "",
    string Cursor = "",
    int PageSize = 50);

public sealed record BrowseCategoryViewerCountRequest(
    PlatformKind Platform,
    IReadOnlyList<string> CategoryIds);

public sealed record BrowseCategoryViewerCount(
    string CategoryId,
    int ViewerCount);

public sealed record BrowseStreamRequest(
    PlatformKind Platform,
    string CategoryId,
    string CategoryName = "",
    string Cursor = "",
    int PageSize = 50);

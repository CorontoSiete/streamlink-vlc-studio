using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Core.Services;

public interface IBrowseService
{
    Task<BrowseResult<BrowseCategory>> GetCategoriesAsync(
        BrowseCategoryRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default);

    Task<BrowseResult<BrowseCategoryViewerCount>> GetCategoryViewerCountsAsync(
        BrowseCategoryViewerCountRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default);

    Task<BrowseResult<BrowseLiveStream>> GetStreamsAsync(
        BrowseStreamRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default);
}

public enum BrowseResultStatus
{
    Available,
    NotConfigured,
    Unauthorized,
    Unavailable
}

public sealed record BrowseResult<T>(
    BrowseResultStatus Status,
    IReadOnlyList<T> Items,
    string NextCursor,
    string Message)
{
    public bool IsAvailable => Status == BrowseResultStatus.Available;

    public static BrowseResult<T> NotConfigured(string message) => new(
        BrowseResultStatus.NotConfigured,
        [],
        "",
        message);

    public static BrowseResult<T> Unauthorized(string message) => new(
        BrowseResultStatus.Unauthorized,
        [],
        "",
        message);

    public static BrowseResult<T> Unavailable(string message) => new(
        BrowseResultStatus.Unavailable,
        [],
        "",
        message);
}

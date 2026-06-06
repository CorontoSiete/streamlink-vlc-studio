namespace StreamlinkVlcStudio.Core.Models;

public sealed record StreamSearchRequest(
    string Query,
    string Quality = "best",
    int PageSize = 10);

public enum StreamSearchChannelState
{
    Live,
    Offline,
    Unavailable
}

public enum StreamSearchSourceStatus
{
    Available,
    NotConfigured,
    Unavailable
}

public sealed record StreamSearchChannel(
    PlatformKind Platform,
    string Channel,
    string DisplayName,
    string Url,
    string ThumbnailUrl,
    string Title,
    string CategoryName,
    StreamSearchChannelState State,
    StreamSearchSourceStatus SourceStatus,
    string StatusMessage,
    bool CanPlay,
    int Order = 0)
{
    public StreamTarget Target => new(Platform, Channel, Url, CategoryName: CategoryName);
}

public enum StreamSearchResultStatus
{
    Available,
    NotFound,
    NotConfigured,
    Unavailable
}

public sealed record StreamSearchResult(
    StreamSearchResultStatus Status,
    IReadOnlyList<StreamSearchChannel> Channels,
    string Message)
{
    public bool IsAvailable => Status == StreamSearchResultStatus.Available;
}

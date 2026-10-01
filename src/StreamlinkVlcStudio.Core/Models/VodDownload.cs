using System.Text.Json.Serialization;

namespace StreamlinkVlcStudio.Core.Models;

public enum VodDownloadState
{
    Queued,
    Resolving,
    Downloading,
    Completed,
    Failed,
    Canceled,
    Interrupted
}

public sealed record VodDownloadOptions(string StreamlinkPath, IReadOnlyList<string> CustomArguments);

public sealed record VodDownloadRequest(StreamTarget Target, string Quality, VodDownloadOptions Options);

public sealed record VodDownloadItem(
    Guid Id,
    StreamTarget Target,
    string Quality,
    DateTimeOffset CreatedAtUtc,
    VodDownloadState State = VodDownloadState.Queued,
    int CompletedSegments = 0,
    int TotalSegments = 0,
    long BytesDownloaded = 0,
    TimeSpan Duration = default,
    string Error = "",
    string LocalMediaPath = "",
    long Revision = 0)
{
    [JsonIgnore]
    public bool IsActive => State is VodDownloadState.Queued or VodDownloadState.Resolving or VodDownloadState.Downloading;

    [JsonIgnore]
    public StreamTarget OfflineTarget => State == VodDownloadState.Completed && !string.IsNullOrWhiteSpace(LocalMediaPath)
        ? Target with { LocalMediaPath = LocalMediaPath, MediaDuration = Duration }
        : throw new InvalidOperationException("This VOD has not finished downloading.");
}

namespace StreamlinkVlcStudio.Core.Models;

public sealed record ReplaySessionInfo(
    PlatformKind Platform,
    string Channel,
    string ReplayUrl,
    string ReplayId,
    DateTimeOffset? StreamStartedAtUtc,
    TimeSpan Duration,
    bool IsSeekable,
    string UnavailableReason,
    string StreamlinkQuality = "",
    ReplayMediaKind MediaKind = ReplayMediaKind.Archive,
    string ChatRoomId = "")
{
    public bool IsAvailable =>
        IsSeekable &&
        !string.IsNullOrWhiteSpace(ReplayUrl) &&
        !string.IsNullOrWhiteSpace(ReplayId) &&
        Duration > TimeSpan.Zero;

    public string GetStreamlinkQuality(string requestedQuality)
    {
        var quality = string.IsNullOrWhiteSpace(StreamlinkQuality)
            ? requestedQuality
            : StreamlinkQuality;
        return quality?.Trim() ?? "";
    }

    public static ReplaySessionInfo Unavailable(
        PlatformKind platform,
        string channel,
        string reason,
        DateTimeOffset? streamStartedAtUtc = null) =>
        new(
            platform,
            channel,
            "",
            "",
            streamStartedAtUtc,
            TimeSpan.Zero,
            false,
            string.IsNullOrWhiteSpace(reason) ? "Replay is unavailable." : reason.Trim());
}

public enum ReplayMediaKind
{
    Archive,
    CurrentLiveDvr
}

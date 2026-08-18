using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Settings;

public sealed class RecentStreamSettings
{
    public PlatformKind Platform { get; set; }

    public string Channel { get; set; } = "";

    public string Url { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string CategoryName { get; set; } = "";

    public string ThumbnailUrl { get; set; } = "";

    public string LastQuality { get; set; } = "";

    public DateTimeOffset LastWatchedAtUtc { get; set; }
}

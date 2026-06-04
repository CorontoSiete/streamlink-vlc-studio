namespace StreamlinkVlcStudio.Core.Models;

public sealed record StreamTarget(
    PlatformKind Platform,
    string Channel,
    string Url,
    StreamTargetKind Kind = StreamTargetKind.Live,
    string MediaId = "",
    string DisplayTitle = "",
    string BroadcasterId = "",
    TimeSpan MediaDuration = default)
{
    public string DisplayName => Kind == StreamTargetKind.TwitchVod && !string.IsNullOrWhiteSpace(DisplayTitle)
        ? $"{Platform}: {DisplayTitle.Trim()}"
        : $"{Platform}: {Channel}";

    public string TabTitle => string.IsNullOrWhiteSpace(DisplayTitle)
        ? Channel
        : DisplayTitle.Trim();

    public string StateKey => $"{Platform}:{Channel.Trim().ToLowerInvariant()}";

    public string TabIdentityKey
    {
        get
        {
            var mediaId = MediaId.Trim();
            if (!string.IsNullOrWhiteSpace(mediaId))
            {
                return $"{Kind}:{Platform}:{mediaId.ToLowerInvariant()}";
            }

            return $"{Kind}:{Platform}:{Channel.Trim().ToLowerInvariant()}";
        }
    }

    public bool IsExplicitTwitchVod => Kind == StreamTargetKind.TwitchVod && Platform == PlatformKind.Twitch;
}

public enum StreamTargetKind
{
    Live,
    TwitchVod
}

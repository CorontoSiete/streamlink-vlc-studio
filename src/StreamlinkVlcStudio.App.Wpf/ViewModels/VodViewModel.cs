using System.Globalization;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using static StreamlinkVlcStudio.Core.Text.StringValues;
using static StreamlinkVlcStudio.App.Wpf.ViewModels.StreamViewModelHelpers;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class VodViewModel : ObservableObject, IHomeStreamOpenItemViewModel
{
    private TwitchVodItem? twitchVod;
    private KickVodItem? kickVod;
    private VodPlaybackBookmark? playbackBookmark;

    public VodViewModel(
        TwitchVodItem vod,
        Func<VodViewModel, bool, Task> openAsync)
    {
        twitchVod = vod;
        OpenCommand = new AsyncRelayCommand(() => openAsync(this, ShouldStayOnHomeForOpenCommand()));
        OpenAndStayOnHomeCommand = new AsyncRelayCommand(() => openAsync(this, true));
    }

    public VodViewModel(
        KickVodItem vod,
        Func<VodViewModel, bool, Task> openAsync)
    {
        kickVod = vod;
        OpenCommand = new AsyncRelayCommand(() => openAsync(this, ShouldStayOnHomeForOpenCommand()));
        OpenAndStayOnHomeCommand = new AsyncRelayCommand(() => openAsync(this, true));
    }

    public AsyncRelayCommand OpenCommand { get; }

    public AsyncRelayCommand OpenAndStayOnHomeCommand { get; }

    public StreamTarget Target
    {
        get
        {
            if (twitchVod is { } twitch)
            {
                return new StreamTarget(
                    PlatformKind.Twitch,
                    twitch.ChannelLogin,
                    twitch.Url,
                    StreamTargetKind.TwitchVod,
                    twitch.Id,
                    Title,
                    twitch.BroadcasterId,
                    twitch.Duration,
                    ProfileImageUrl: twitch.ProfileImageUrl);
            }

            var kick = kickVod!;
            return new StreamTarget(
                PlatformKind.Kick,
                kick.ChannelSlug,
                kick.Source,
                StreamTargetKind.KickVod,
                FirstNonEmpty(kick.Uuid, kick.Id, kick.LiveStreamId),
                Title,
                "",
                kick.Duration,
                kick.StartedAtUtc,
                kick.ChannelId,
                kick.CategoryName,
                kick.ProfileImageUrl);
        }
    }

    public PlatformKind Platform => twitchVod is not null ? PlatformKind.Twitch : PlatformKind.Kick;

    public string PlatformText => Platform.ToString();

    public bool IsSubscriberOnly => twitchVod?.AccessKind == TwitchVodAccessKind.SubscriberOnly;

    public bool IsTwitchVodAccessUnknown => twitchVod?.AccessKind == TwitchVodAccessKind.Unknown;

    public string Id => twitchVod?.Id ?? FirstNonEmpty(kickVod?.Uuid, kickVod?.Id, kickVod?.LiveStreamId);

    internal string Identity => twitchVod is { } twitch ? GetIdentity(twitch) : GetIdentity(kickVod!);

    internal static string GetIdentity(TwitchVodItem vod) =>
        GetIdentity(PlatformKind.Twitch, vod.Id, vod.ChannelLogin);

    internal static string GetIdentity(KickVodItem vod) =>
        GetIdentity(PlatformKind.Kick, FirstNonEmpty(vod.Uuid, vod.Id, vod.LiveStreamId), vod.ChannelSlug);

    private static string GetIdentity(PlatformKind platform, string id, string channel) =>
        $"{platform}:{(string.IsNullOrWhiteSpace(id) ? channel : id).Trim()}";

    internal void Update(TwitchVodItem vod) => Update(vod, null);

    internal void Update(KickVodItem vod) => Update(null, vod);

    private void Update(TwitchVodItem? twitch, KickVodItem? kick)
    {
        if (twitchVod == twitch && kickVod == kick) return;
        var previous = (Target, Platform, Id, Title, ChannelDisplayName, ThumbnailUrl, ProfileImageUrl,
            DurationText, TypeText, PublishedText, ViewCountText, IsSubscriberOnly, IsTwitchVodAccessUnknown);
        twitchVod = twitch;
        kickVod = kick;
        NotifyChanged(previous.Target, Target, nameof(Target));
        if (previous.Platform != Platform)
        {
            OnPropertyChanged(nameof(Platform));
            OnPropertyChanged(nameof(PlatformText));
        }
        NotifyChanged(previous.Id, Id, nameof(Id));
        NotifyChanged(previous.Title, Title, nameof(Title));
        NotifyChanged(previous.ChannelDisplayName, ChannelDisplayName, nameof(ChannelDisplayName));
        if (previous.ThumbnailUrl != ThumbnailUrl)
        {
            OnPropertyChanged(nameof(ThumbnailUrl));
            OnPropertyChanged(nameof(HasThumbnail));
        }
        if (previous.ProfileImageUrl != ProfileImageUrl)
        {
            OnPropertyChanged(nameof(ProfileImageUrl));
            OnPropertyChanged(nameof(HasProfileImage));
        }
        NotifyChanged(previous.IsSubscriberOnly, IsSubscriberOnly, nameof(IsSubscriberOnly));
        NotifyChanged(previous.IsTwitchVodAccessUnknown, IsTwitchVodAccessUnknown, nameof(IsTwitchVodAccessUnknown));
        NotifyChanged(previous.DurationText, DurationText, nameof(DurationText));
        NotifyChanged(previous.TypeText, TypeText, nameof(TypeText));
        NotifyChanged(previous.PublishedText, PublishedText, nameof(PublishedText));
        NotifyChanged(previous.ViewCountText, ViewCountText, nameof(ViewCountText));
        if (previous.DurationText != DurationText || previous.TypeText != TypeText ||
            previous.PublishedText != PublishedText || previous.ViewCountText != ViewCountText)
            OnPropertyChanged(nameof(MetadataText));
    }

    private void NotifyChanged<T>(T previous, T current, string propertyName)
    {
        if (!EqualityComparer<T>.Default.Equals(previous, current)) OnPropertyChanged(propertyName);
    }

    public string Title
    {
        get
        {
            var title = twitchVod?.Title ?? kickVod?.Title;
            return string.IsNullOrWhiteSpace(title)
                ? "Untitled VOD"
                : title.Trim();
        }
    }

    public string ChannelDisplayName
    {
        get
        {
            if (twitchVod is { } twitch)
            {
                return string.IsNullOrWhiteSpace(twitch.ChannelDisplayName)
                    ? twitch.ChannelLogin
                    : twitch.ChannelDisplayName;
            }

            return string.IsNullOrWhiteSpace(kickVod?.ChannelDisplayName)
                ? kickVod?.ChannelSlug ?? ""
                : kickVod.ChannelDisplayName;
        }
    }

    public string ThumbnailUrl => twitchVod?.ThumbnailUrl ?? kickVod?.ThumbnailUrl ?? "";

    public string ProfileImageUrl => twitchVod?.ProfileImageUrl ?? kickVod?.ProfileImageUrl ?? "";

    public bool HasProfileImage => !string.IsNullOrWhiteSpace(ProfileImageUrl);

    public bool HasThumbnail => !string.IsNullOrWhiteSpace(ThumbnailUrl);

    public bool IsWatched => playbackBookmark is { Completed: true } or { HasBeenWatched: true };

    public bool HasWatchProgress => !IsWatched && playbackBookmark is
    { Position: var position, Duration: var duration } && position > TimeSpan.Zero && duration > TimeSpan.Zero;

    public double WatchProgressPercent => IsWatched ? 100 : HasWatchProgress
        ? Math.Clamp(playbackBookmark!.Position.TotalSeconds / playbackBookmark.Duration.TotalSeconds * 100, 0, 100)
        : 0;

    public string WatchProgressText => IsWatched ? "Watched" : HasWatchProgress
        ? $"Watched {FormatClockTime(playbackBookmark!.Position)} of {FormatClockTime(playbackBookmark.Duration)} ({WatchProgressPercent:0}%)"
        : "Not watched";

    internal void UpdateWatchProgress(VodPlaybackBookmark? bookmark)
    {
        // A page load or queued notification must not replace a newer playback sample.
        if (playbackBookmark is not null && (bookmark is null || bookmark.UpdatedAtUtc < playbackBookmark.UpdatedAtUtc)) return;
        var previous = (IsWatched, HasWatchProgress, WatchProgressPercent, WatchProgressText);
        playbackBookmark = bookmark;
        NotifyChanged(previous.IsWatched, IsWatched, nameof(IsWatched));
        NotifyChanged(previous.HasWatchProgress, HasWatchProgress, nameof(HasWatchProgress));
        NotifyChanged(previous.WatchProgressPercent, WatchProgressPercent, nameof(WatchProgressPercent));
        NotifyChanged(previous.WatchProgressText, WatchProgressText, nameof(WatchProgressText));
    }

    public string DurationText => FormatClockTime(twitchVod?.Duration ?? kickVod?.Duration ?? TimeSpan.Zero);

    public string TypeText
    {
        get
        {
            if (twitchVod is not { } twitch)
            {
                return "Kick VOD";
            }

            return twitch.Type switch
            {
                TwitchVodTypeFilter.Highlight => "Highlight",
                TwitchVodTypeFilter.Upload => "Upload",
                TwitchVodTypeFilter.All => "VOD",
                _ => "Past broadcast"
            };
        }
    }

    public string PublishedText
    {
        get
        {
            var publishedAt = twitchVod is { } twitch
                ? twitch.PublishedAtUtc ?? twitch.CreatedAtUtc
                : kickVod?.StartedAtUtc ?? kickVod?.CreatedAtUtc;
            return publishedAt is { } value
                ? value.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture)
                : "Published date unknown";
        }
    }

    public string ViewCountText
    {
        get
        {
            var viewCount = twitchVod?.ViewCount ?? kickVod?.ViewCount;
            return viewCount is { } value
                ? FormatViewCount(value)
                : "Views unknown";
        }
    }

    public string MetadataText => string.Join(
        " | ",
        new[] { PublishedText, DurationText, ViewCountText, TypeText }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string FormatViewCount(int value)
    {
        return value == 1 ? "1 view" : $"{FormatViewerCount(value)} views";
    }

}

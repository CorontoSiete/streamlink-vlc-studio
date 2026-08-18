using System.Globalization;
using StreamlinkVlcStudio.Core.Models;
using static StreamlinkVlcStudio.Core.Text.StringValues;
using static StreamlinkVlcStudio.App.Wpf.ViewModels.StreamViewModelHelpers;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class VodViewModel : ObservableObject, IHomeStreamOpenItemViewModel
{
    private readonly TwitchVodItem? twitchVod;
    private readonly KickVodItem? kickVod;

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

    public TwitchVodItem? TwitchVod => twitchVod;

    public KickVodItem? KickVod => kickVod;

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

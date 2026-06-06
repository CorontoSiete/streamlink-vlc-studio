using System.Globalization;
using System.Windows.Input;
using StreamlinkVlcStudio.Core.Models;

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
                    twitch.Duration);
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
                kick.CategoryName);
        }
    }

    public PlatformKind Platform => twitchVod is not null ? PlatformKind.Twitch : PlatformKind.Kick;

    public string PlatformText => Platform.ToString();

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

    public bool HasThumbnail => !string.IsNullOrWhiteSpace(ThumbnailUrl);

    public string DurationText => FormatDuration(twitchVod?.Duration ?? kickVod?.Duration ?? TimeSpan.Zero);

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

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "0:00";
        }

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    }

    private static string FormatViewCount(int value)
    {
        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.#}M views";
        }

        if (value >= 1_000)
        {
            return $"{value / 1_000d:0.#}K views";
        }

        return value == 1 ? "1 view" : $"{value} views";
    }

    private static bool ShouldStayOnHomeForOpenCommand()
    {
        return (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }
}

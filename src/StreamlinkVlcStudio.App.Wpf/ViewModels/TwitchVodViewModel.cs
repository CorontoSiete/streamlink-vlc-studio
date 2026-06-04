using System.Globalization;
using System.Windows.Input;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class TwitchVodViewModel : ObservableObject
{
    private readonly TwitchVodItem vod;

    public TwitchVodViewModel(
        TwitchVodItem vod,
        Func<TwitchVodViewModel, bool, Task> openAsync)
    {
        this.vod = vod;
        OpenCommand = new AsyncRelayCommand(() => openAsync(this, ShouldStayOnHomeForOpenCommand()));
        OpenAndStayOnHomeCommand = new AsyncRelayCommand(() => openAsync(this, true));
    }

    public AsyncRelayCommand OpenCommand { get; }

    public AsyncRelayCommand OpenAndStayOnHomeCommand { get; }

    public TwitchVodItem Vod => vod;

    public StreamTarget Target => new(
        PlatformKind.Twitch,
        vod.ChannelLogin,
        vod.Url,
        StreamTargetKind.TwitchVod,
        vod.Id,
        Title,
        vod.BroadcasterId,
        vod.Duration);

    public PlatformKind Platform => PlatformKind.Twitch;

    public string PlatformText => "Twitch";

    public string Id => vod.Id;

    public string Title => string.IsNullOrWhiteSpace(vod.Title)
        ? "Untitled VOD"
        : vod.Title.Trim();

    public string ChannelDisplayName => string.IsNullOrWhiteSpace(vod.ChannelDisplayName)
        ? vod.ChannelLogin
        : vod.ChannelDisplayName;

    public string ThumbnailUrl => vod.ThumbnailUrl;

    public bool HasThumbnail => !string.IsNullOrWhiteSpace(vod.ThumbnailUrl);

    public string DurationText => FormatDuration(vod.Duration);

    public string TypeText => vod.Type switch
    {
        TwitchVodTypeFilter.Highlight => "Highlight",
        TwitchVodTypeFilter.Upload => "Upload",
        TwitchVodTypeFilter.All => "VOD",
        _ => "Past broadcast"
    };

    public string PublishedText
    {
        get
        {
            var publishedAt = vod.PublishedAtUtc ?? vod.CreatedAtUtc;
            return publishedAt is { } value
                ? value.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture)
                : "Published date unknown";
        }
    }

    public string ViewCountText => vod.ViewCount is { } viewCount
        ? FormatViewCount(viewCount)
        : "Views unknown";

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
}

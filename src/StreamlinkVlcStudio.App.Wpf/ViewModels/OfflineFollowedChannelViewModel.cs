using StreamlinkVlcStudio.Core.Commands;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class OfflineFollowedChannelViewModel : ObservableObject
{
    private FollowedChannel channel;
    private bool isPinned;
    private string lastLiveText;

    public OfflineFollowedChannelViewModel(FollowedChannel channel,
        Func<FollowedChannel, Task> openAsync, Func<bool>? canOpen = null,
        Action<FollowedChannel>? togglePin = null, bool isPinned = false)
    {
        this.channel = channel;
        this.isPinned = isPinned;
        lastLiveText = FormatLastLive(channel.LastLiveAtUtc, DateTimeOffset.UtcNow);
        OpenCommand = new AsyncRelayCommand(() => openAsync(this.channel), canOpen);
        TogglePinCommand = new RelayCommand(() =>
        {
            if (togglePin is not null && (canOpen?.Invoke() ?? true)) togglePin(this.channel);
        }, () => togglePin is not null && (canOpen?.Invoke() ?? true));
    }

    public AsyncRelayCommand OpenCommand { get; }
    public RelayCommand TogglePinCommand { get; }
    public StreamTarget Target => channel.Target;
    public PlatformKind Platform => channel.Platform;
    public string PlatformText => channel.Platform.ToString();
    public string Channel => channel.Channel;
    public string DisplayName => string.IsNullOrWhiteSpace(channel.DisplayName) ? channel.Channel : channel.DisplayName;
    public string ProfileImageUrl => channel.ProfileImageUrl;
    public bool HasProfileImage => !string.IsNullOrWhiteSpace(channel.ProfileImageUrl);
    public string BannerImageUrl => channel.BannerImageUrl;
    public DateTimeOffset? LastLiveAtUtc => channel.LastLiveAtUtc;
    public string LastLiveText => lastLiveText;
    public string LastLiveToolTip => channel.LastLiveAtUtc is { } lastLive &&
        lastLive > DateTimeOffset.UnixEpoch && lastLive <= DateTimeOffset.UtcNow
        ? $"Latest available broadcast started {lastLive.ToLocalTime():f}"
        : "Broadcast history is unavailable for this channel.";

    internal static string FormatLastLive(DateTimeOffset? lastLive, DateTimeOffset now)
    {
        if (lastLive is not { } timestamp || timestamp <= DateTimeOffset.UnixEpoch || timestamp > now)
            return "Last live unavailable";
        var age = now - timestamp;
        if (age.TotalMinutes < 1) return "Last live just now";
        var (count, unit) = age.TotalHours < 1 ? ((int)age.TotalMinutes, "minute")
            : age.TotalDays < 1 ? ((int)age.TotalHours, "hour")
            : age.TotalDays < 30 ? ((int)age.TotalDays, "day")
            : age.TotalDays < 365 ? ((int)(age.TotalDays / 30), "month")
            : ((int)(age.TotalDays / 365), "year");
        return $"Last live {count} {unit}{(count == 1 ? "" : "s")} ago";
    }

    public bool IsPinned
    {
        get => isPinned;
        internal set
        {
            if (!SetProperty(ref isPinned, value)) return;
            OnPropertyChanged(nameof(PinActionText));
            OnPropertyChanged(nameof(PinStatusText));
        }
    }

    public string PinActionText => IsPinned
        ? $"Unpin {DisplayName}"
        : $"Pin {DisplayName} to the top of offline channels";

    public string PinStatusText => IsPinned ? "Pinned" : "Not pinned";

    internal void Update(FollowedChannel updated)
    {
        var previous = channel;
        channel = updated;
        if (previous.Target != updated.Target) OnPropertyChanged(nameof(Target));
        if (previous.Platform != updated.Platform)
        {
            OnPropertyChanged(nameof(Platform));
            OnPropertyChanged(nameof(PlatformText));
        }
        if (previous.Channel != updated.Channel) OnPropertyChanged(nameof(Channel));
        if (previous.DisplayName != updated.DisplayName || previous.Channel != updated.Channel)
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(PinActionText));
        }
        if (previous.ProfileImageUrl != updated.ProfileImageUrl)
        {
            OnPropertyChanged(nameof(ProfileImageUrl));
            OnPropertyChanged(nameof(HasProfileImage));
        }
        if (previous.BannerImageUrl != updated.BannerImageUrl) OnPropertyChanged(nameof(BannerImageUrl));
        if (previous.LastLiveAtUtc != updated.LastLiveAtUtc)
        {
            OnPropertyChanged(nameof(LastLiveAtUtc));
            OnPropertyChanged(nameof(LastLiveToolTip));
        }
        // Followed-channel refreshes run every minute, including when metadata is cached.
        // Recompute elapsed time even when the broadcast timestamp itself did not change.
        SetProperty(ref lastLiveText, FormatLastLive(updated.LastLiveAtUtc, DateTimeOffset.UtcNow), nameof(LastLiveText));
    }
}

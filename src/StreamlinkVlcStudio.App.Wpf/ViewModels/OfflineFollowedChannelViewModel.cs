using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class OfflineFollowedChannelViewModel : ObservableObject
{
    private FollowedChannel channel;
    private bool isPinned;

    public OfflineFollowedChannelViewModel(FollowedChannel channel,
        Func<FollowedChannel, Task> openAsync, Func<bool>? canOpen = null,
        Action<FollowedChannel>? togglePin = null, bool isPinned = false)
    {
        this.channel = channel;
        this.isPinned = isPinned;
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
    }
}

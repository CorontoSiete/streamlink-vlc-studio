using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class OfflineFollowedChannelViewModel : ObservableObject
{
    private FollowedChannel channel;

    public OfflineFollowedChannelViewModel(FollowedChannel channel,
        Func<FollowedChannel, Task> openAsync, Func<bool>? canOpen = null)
    {
        this.channel = channel;
        OpenCommand = new AsyncRelayCommand(() => openAsync(this.channel), canOpen);
    }

    public AsyncRelayCommand OpenCommand { get; }
    public StreamTarget Target => channel.Target;
    public PlatformKind Platform => channel.Platform;
    public string PlatformText => channel.Platform.ToString();
    public string Channel => channel.Channel;
    public string DisplayName => string.IsNullOrWhiteSpace(channel.DisplayName) ? channel.Channel : channel.DisplayName;
    public string ProfileImageUrl => channel.ProfileImageUrl;
    public bool HasProfileImage => !string.IsNullOrWhiteSpace(channel.ProfileImageUrl);

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
            OnPropertyChanged(nameof(DisplayName));
        if (previous.ProfileImageUrl != updated.ProfileImageUrl)
        {
            OnPropertyChanged(nameof(ProfileImageUrl));
            OnPropertyChanged(nameof(HasProfileImage));
        }
    }
}

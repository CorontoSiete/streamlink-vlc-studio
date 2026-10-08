using StreamlinkVlcStudio.Core.Models;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class VodChannelSearchResultViewModel
{
    private readonly StreamSearchChannel channel;

    internal VodChannelSearchResultViewModel(StreamSearchChannel channel,
        Func<VodChannelSearchResultViewModel, AsyncRelayCommand> createCommand)
    {
        this.channel = channel;
        SelectCommand = createCommand(this);
    }

    public PlatformKind Platform => channel.Platform;
    public string Channel => channel.Channel;
    public string DisplayName => FirstNonEmpty(channel.DisplayName, channel.Channel);
    public string ProfileImageUrl => FirstNonEmpty(channel.ProfileImageUrl, channel.ThumbnailUrl);
    public string Details => $"{Platform} · @{Channel}";
    public string SelectLabel => $"Browse {DisplayName}'s {Platform} broadcasts";
    public AsyncRelayCommand SelectCommand { get; }
}

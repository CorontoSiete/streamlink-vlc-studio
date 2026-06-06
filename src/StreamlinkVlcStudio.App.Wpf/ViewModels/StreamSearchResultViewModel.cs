using System.Windows.Input;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class StreamSearchResultViewModel : ObservableObject, IHomeStreamOpenItemViewModel
{
    private readonly StreamSearchChannel? channel;
    private readonly StreamlinkProbeResult probeResult;
    private readonly StreamMetadataResult? metadata;

    public StreamSearchResultViewModel(
        StreamSearchChannel channel,
        Func<StreamSearchResultViewModel, bool, Task> openAsync)
    {
        this.channel = channel;
        Target = channel.Target;
        probeResult = new StreamlinkProbeResult(channel.CanPlay, channel.StatusMessage);
        OpenCommand = new AsyncRelayCommand(
            () => openAsync(this, ShouldStayOnHomeForOpenCommand()),
            () => CanOpen);
        OpenAndStayOnHomeCommand = new AsyncRelayCommand(
            () => openAsync(this, true),
            () => CanOpen);
    }

    public StreamSearchResultViewModel(
        StreamTarget target,
        StreamlinkProbeResult probeResult,
        StreamMetadataResult? metadata,
        Func<StreamSearchResultViewModel, bool, Task> openAsync)
    {
        this.probeResult = probeResult;
        this.metadata = metadata?.State == StreamMetadataState.Available ? metadata : null;
        Target = string.IsNullOrWhiteSpace(this.metadata?.CategoryName)
            ? target
            : target with { CategoryName = this.metadata.CategoryName.Trim() };
        OpenCommand = new AsyncRelayCommand(
            () => openAsync(this, ShouldStayOnHomeForOpenCommand()),
            () => CanOpen);
        OpenAndStayOnHomeCommand = new AsyncRelayCommand(
            () => openAsync(this, true),
            () => CanOpen);
    }

    public AsyncRelayCommand OpenCommand { get; }

    public AsyncRelayCommand OpenAndStayOnHomeCommand { get; }

    public StreamTarget Target { get; }

    public PlatformKind Platform => Target.Platform;

    public string PlatformText => Target.Platform.ToString();

    public string Channel => Target.Channel;

    public string DisplayName => FirstNonEmpty(channel?.DisplayName, metadata?.DisplayName, Target.Channel);

    public string ThumbnailUrl => FirstNonEmpty(channel?.ThumbnailUrl, metadata?.ThumbnailUrl);

    public bool HasThumbnail => !string.IsNullOrWhiteSpace(ThumbnailUrl);

    public string CategoryName => FirstNonEmpty(channel?.CategoryName, metadata?.CategoryName);

    public bool HasCategory => !string.IsNullOrWhiteSpace(CategoryName);

    public string Url => Target.Url;

    public StreamSearchChannelState State => channel?.State ??
        (probeResult.HasPlayableStream ? StreamSearchChannelState.Live : StreamSearchChannelState.Unavailable);

    public bool CanPlay => channel?.CanPlay ?? probeResult.HasPlayableStream;

    public bool IsOffline => State == StreamSearchChannelState.Offline;

    public bool CanOpen => CanPlay || IsOffline;

    public string StateText => State switch
    {
        StreamSearchChannelState.Live => "Live",
        StreamSearchChannelState.Offline => "Offline",
        _ => "Unavailable"
    };

    public string StatusText => string.IsNullOrWhiteSpace(probeResult.Message)
        ? State switch
        {
            StreamSearchChannelState.Live => "Playable stream found.",
            StreamSearchChannelState.Offline => "Offline. Open VODs.",
            _ => "Stream unavailable."
        }
        : probeResult.Message;

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

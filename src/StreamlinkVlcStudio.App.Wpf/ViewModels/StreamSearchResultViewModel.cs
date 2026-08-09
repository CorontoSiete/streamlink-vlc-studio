using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using static StreamlinkVlcStudio.Core.Text.StringValues;
using static StreamlinkVlcStudio.App.Wpf.ViewModels.StreamViewModelHelpers;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class StreamSearchResultViewModel : ObservableObject, IHomeStreamOpenItemViewModel
{
    private readonly StreamSearchChannel? channel;
    private readonly StreamlinkProbeResult probeResult;
    private readonly StreamMetadataResult? metadata;

    public StreamSearchResultViewModel(
        StreamSearchChannel channel,
        Func<StreamSearchResultViewModel, bool, Task> openAsync,
        int? viewerCount = null)
    {
        this.channel = channel;
        ViewerCount = NormalizeViewerCount(viewerCount ?? channel.ViewerCount);
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
        Func<StreamSearchResultViewModel, bool, Task> openAsync,
        int? viewerCount = null)
    {
        this.probeResult = probeResult;
        this.metadata = metadata?.State == StreamMetadataState.Available ? metadata : null;
        ViewerCount = NormalizeViewerCount(viewerCount);
        Target = target with
        {
            CategoryName = string.IsNullOrWhiteSpace(this.metadata?.CategoryName)
                ? target.CategoryName
                : this.metadata.CategoryName.Trim(),
            ProfileImageUrl = FirstNonEmpty(target.ProfileImageUrl, this.metadata?.ProfileImageUrl)
        };
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

    public bool IsLive => channel?.IsLive ?? probeResult.HasPlayableStream;

    public bool CanPlay => channel?.CanPlay ?? probeResult.HasPlayableStream;

    public bool IsOffline => State == StreamSearchChannelState.Offline;

    public bool CanOpen => CanPlay || IsOffline;

    public string StateText => IsLive ? "Live" : State switch
    {
        StreamSearchChannelState.Offline => "Offline",
        _ => "Unavailable"
    };

    public int? ViewerCount { get; }

    public bool HasViewerCount => IsLive && ViewerCount is not null;

    public string ViewerCountText => ViewerCount is { } value
        ? $"{FormatViewerCount(value)} viewer{(value == 1 ? "" : "s")}"
        : "";

    public string StatusText => string.IsNullOrWhiteSpace(probeResult.Message)
        ? State switch
        {
            StreamSearchChannelState.Live => "Playable stream found.",
            StreamSearchChannelState.Offline => "Offline. Open VODs.",
            _ => "Stream unavailable."
        }
        : probeResult.Message;

    private static int? NormalizeViewerCount(int? viewerCount)
    {
        return viewerCount is { } value ? Math.Max(0, value) : null;
    }

}

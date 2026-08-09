using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;
using static StreamlinkVlcStudio.App.Wpf.ViewModels.StreamViewModelHelpers;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class RecentStreamViewModel : ObservableObject, IHomeStreamOpenItemViewModel
{
    private readonly RecentStreamSettings stream;
    private readonly RecentStreamLiveStatus liveStatus;

    public RecentStreamViewModel(
        RecentStreamSettings stream,
        Func<RecentStreamViewModel, bool, Task> openAsync,
        Func<RecentStreamViewModel, Task> deleteAsync,
        RecentStreamLiveStatus? liveStatus = null)
    {
        this.stream = stream;
        this.liveStatus = liveStatus ?? RecentStreamLiveStatus.Unknown;
        OpenCommand = new AsyncRelayCommand(() => openAsync(this, ShouldStayOnHomeForOpenCommand()));
        OpenAndStayOnHomeCommand = new AsyncRelayCommand(() => openAsync(this, true));
        DeleteCommand = new AsyncRelayCommand(() => deleteAsync(this));
    }

    public AsyncRelayCommand OpenCommand { get; }

    public AsyncRelayCommand OpenAndStayOnHomeCommand { get; }

    public AsyncRelayCommand DeleteCommand { get; }

    public StreamTarget Target => new(stream.Platform, stream.Channel, stream.Url, CategoryName: stream.CategoryName);

    public PlatformKind Platform => stream.Platform;

    public string PlatformText => stream.Platform.ToString();

    public string Channel => stream.Channel;

    public string DisplayName => string.IsNullOrWhiteSpace(stream.DisplayName)
        ? stream.Channel
        : stream.DisplayName;

    public string CategoryName => stream.CategoryName;

    public string Url => stream.Url;

    public string DeleteToolTip => $"Remove {DisplayName} from recent streams";

    public string ThumbnailUrl => stream.ThumbnailUrl;

    public bool HasThumbnail => !string.IsNullOrWhiteSpace(stream.ThumbnailUrl);

    public string LiveStatusKey => liveStatus.State switch
    {
        RecentStreamLiveState.Checking => "Checking",
        RecentStreamLiveState.Live => "Live",
        RecentStreamLiveState.Offline => "Offline",
        _ => "Unknown"
    };

    public string LiveStatusText => liveStatus.State switch
    {
        RecentStreamLiveState.Checking => "Checking",
        RecentStreamLiveState.Live => "Live",
        RecentStreamLiveState.Offline => "Offline",
        _ => "Unknown"
    };

    public string LiveStatusToolTip
    {
        get
        {
            var message = string.IsNullOrWhiteSpace(liveStatus.Message)
                ? "Live status has not been checked yet."
                : liveStatus.Message;
            return liveStatus.CheckedAtUtc is { } checkedAt
                ? $"{message} Checked {checkedAt.ToLocalTime():g}."
                : message;
        }
    }

    public string LastWatchedText => stream.LastWatchedAtUtc == DateTimeOffset.MinValue
        ? "Watched"
        : $"Watched {stream.LastWatchedAtUtc.ToLocalTime():g}";

    public string MetadataText
    {
        get
        {
            var parts = new List<string> { LastWatchedText };
            if (!string.IsNullOrWhiteSpace(stream.CategoryName))
            {
                parts.Add(stream.CategoryName);
            }

            if (!string.IsNullOrWhiteSpace(stream.LastQuality))
            {
                parts.Add(stream.LastQuality);
            }

            return string.Join(" | ", parts);
        }
    }

}

public enum RecentStreamLiveState
{
    Unknown,
    Checking,
    Live,
    Offline
}

public sealed record RecentStreamLiveStatus(
    RecentStreamLiveState State,
    DateTimeOffset? CheckedAtUtc,
    string Message)
{
    public static RecentStreamLiveStatus Unknown { get; } = new(
        RecentStreamLiveState.Unknown,
        null,
        "Live status has not been checked yet.");
}

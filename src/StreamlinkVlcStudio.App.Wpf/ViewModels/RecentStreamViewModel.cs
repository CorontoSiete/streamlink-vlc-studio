using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;
using static StreamlinkVlcStudio.App.Wpf.ViewModels.StreamViewModelHelpers;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class RecentStreamViewModel : ObservableObject, IHomeStreamOpenItemViewModel
{
    private StreamSnapshot stream;
    private RecentStreamLiveStatus liveStatus;

    public RecentStreamViewModel(
        RecentStreamSettings stream,
        Func<RecentStreamViewModel, bool, Task> openAsync,
        Func<RecentStreamViewModel, Task> deleteAsync,
        RecentStreamLiveStatus? liveStatus = null)
    {
        this.stream = StreamSnapshot.From(stream);
        this.liveStatus = liveStatus ?? RecentStreamLiveStatus.Unknown;
        OpenCommand = new AsyncRelayCommand(() => openAsync(this, ShouldStayOnHomeForOpenCommand()));
        OpenAndStayOnHomeCommand = new AsyncRelayCommand(() => openAsync(this, true));
        DeleteCommand = new AsyncRelayCommand(() => deleteAsync(this));
    }

    public AsyncRelayCommand OpenCommand { get; }

    public AsyncRelayCommand OpenAndStayOnHomeCommand { get; }

    public AsyncRelayCommand DeleteCommand { get; }

    internal void Update(RecentStreamSettings settings, RecentStreamLiveStatus status)
    {
        var previous = stream;
        var previousDisplayName = DisplayName;
        stream = StreamSnapshot.From(settings);
        if (previous.Platform != stream.Platform)
        {
            OnPropertyChanged(nameof(Platform));
            OnPropertyChanged(nameof(PlatformText));
        }
        if (previous.Channel != stream.Channel) OnPropertyChanged(nameof(Channel));
        if (previous.Url != stream.Url) OnPropertyChanged(nameof(Url));
        if (previous.CategoryName != stream.CategoryName) OnPropertyChanged(nameof(CategoryName));
        if (previous.Platform != stream.Platform || previous.Channel != stream.Channel ||
            previous.Url != stream.Url || previous.CategoryName != stream.CategoryName)
            OnPropertyChanged(nameof(Target));
        if (previousDisplayName != DisplayName)
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(DeleteToolTip));
        }
        if (previous.ThumbnailUrl != stream.ThumbnailUrl)
        {
            OnPropertyChanged(nameof(ThumbnailUrl));
            if (string.IsNullOrWhiteSpace(previous.ThumbnailUrl) != string.IsNullOrWhiteSpace(stream.ThumbnailUrl))
                OnPropertyChanged(nameof(HasThumbnail));
        }
        if (previous.LastWatchedAtUtc != stream.LastWatchedAtUtc) OnPropertyChanged(nameof(LastWatchedText));
        if (previous.LastWatchedAtUtc != stream.LastWatchedAtUtc ||
            previous.CategoryName != stream.CategoryName || previous.LastQuality != stream.LastQuality)
            OnPropertyChanged(nameof(MetadataText));

        var previousStatus = liveStatus;
        liveStatus = status;
        if (previousStatus.State != status.State)
        {
            OnPropertyChanged(nameof(LiveStatusKey));
            OnPropertyChanged(nameof(LiveStatusText));
        }
        if (previousStatus.CheckedAtUtc != status.CheckedAtUtc || previousStatus.Message != status.Message)
            OnPropertyChanged(nameof(LiveStatusToolTip));
    }

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

    public string LiveStatusText => LiveStatusKey;

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

    // Settings can be edited in place. Keep values rather than their mutable owner so
    // refreshes can notify bindings accurately without recreating the row or commands.
    private readonly record struct StreamSnapshot(
        PlatformKind Platform, string Channel, string Url, string DisplayName,
        string CategoryName, string ThumbnailUrl, string LastQuality, DateTimeOffset LastWatchedAtUtc)
    {
        internal static StreamSnapshot From(RecentStreamSettings settings) => new(
            settings.Platform, settings.Channel, settings.Url, settings.DisplayName,
            settings.CategoryName, settings.ThumbnailUrl, settings.LastQuality, settings.LastWatchedAtUtc);
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

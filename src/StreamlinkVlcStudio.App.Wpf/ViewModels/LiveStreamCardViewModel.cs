using StreamlinkVlcStudio.App.Wpf.Controls;
using StreamlinkVlcStudio.Core.Models;
using static StreamlinkVlcStudio.App.Wpf.ViewModels.StreamViewModelHelpers;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public enum LiveStreamCardSource
{
    Followed,
    Browse
}

public sealed record LiveStreamCardData(
    LiveStreamCardSource Source,
    StreamTarget Target,
    PlatformKind Platform,
    string Channel,
    string DisplayName,
    string Title,
    string CategoryName,
    int? ViewerCount,
    string ThumbnailUrl,
    string ProfileImageUrl,
    DateTimeOffset? StartedAtUtc,
    bool? IsMature,
    string Language)
{
    public static LiveStreamCardData FromFollowedStream(FollowedLiveStream stream) => new(
        LiveStreamCardSource.Followed,
        stream.Target,
        stream.Platform,
        stream.Channel,
        stream.DisplayName,
        stream.Title,
        stream.CategoryName,
        stream.ViewerCount,
        stream.ThumbnailUrl,
        stream.ProfileImageUrl,
        stream.StartedAtUtc,
        stream.IsMature,
        stream.Language);

    public static LiveStreamCardData FromBrowseStream(BrowseLiveStream stream) => new(
        LiveStreamCardSource.Browse,
        stream.Target,
        stream.Platform,
        stream.Channel,
        stream.DisplayName,
        stream.Title,
        stream.CategoryName,
        stream.ViewerCount,
        stream.ThumbnailUrl,
        stream.ProfileImageUrl,
        stream.StartedAtUtc,
        stream.IsMature,
        stream.Language);
}

public sealed class LiveStreamCardViewModel : ObservableObject, IHomeStreamOpenItemViewModel
{
    private LiveStreamCardData stream;

    public LiveStreamCardViewModel(
        LiveStreamCardData stream,
        Func<LiveStreamCardViewModel, bool, Task> openAsync,
        long thumbnailCacheVersion = 0)
    {
        this.stream = stream;
        ThumbnailCacheVersion = thumbnailCacheVersion;
        ThumbnailImageRequest = new AnimatedImageRequest(stream.ThumbnailUrl, thumbnailCacheVersion);
        OpenCommand = new AsyncRelayCommand(() => openAsync(this, ShouldStayOnHomeForOpenCommand()));
        OpenAndStayOnHomeCommand = new AsyncRelayCommand(() => openAsync(this, true));
    }

    public AsyncRelayCommand OpenCommand { get; }

    public AsyncRelayCommand OpenAndStayOnHomeCommand { get; }

    public LiveStreamCardSource Source => stream.Source;

    public StreamTarget Target => stream.Target;

    public PlatformKind Platform => stream.Platform;

    public string PlatformText => stream.Platform.ToString();

    public string Channel => stream.Channel;

    public string DisplayName => string.IsNullOrWhiteSpace(stream.DisplayName)
        ? stream.Channel
        : stream.DisplayName;

    public string Title => string.IsNullOrWhiteSpace(stream.Title)
        ? "Untitled stream"
        : stream.Title;

    public string CategoryName => stream.CategoryName;

    public string ThumbnailUrl => stream.ThumbnailUrl;

    public string ProfileImageUrl => stream.ProfileImageUrl;

    public bool HasProfileImage => !string.IsNullOrWhiteSpace(stream.ProfileImageUrl);

    public long ThumbnailCacheVersion { get; private set; }

    public AnimatedImageRequest ThumbnailImageRequest { get; private set; }

    public bool HasThumbnail => !string.IsNullOrWhiteSpace(stream.ThumbnailUrl);

    public string ViewerCountText => stream.ViewerCount is { } viewerCount
        ? FormatViewerCount(viewerCount)
        : "Live";

    internal void Update(LiveStreamCardData updated, long thumbnailCacheVersion)
    {
        var previous = stream;
        stream = updated;
        if (previous.Source != updated.Source) OnPropertyChanged(nameof(Source));
        if (previous.Target != updated.Target) OnPropertyChanged(nameof(Target));
        if (previous.Platform != updated.Platform)
        {
            OnPropertyChanged(nameof(Platform));
            OnPropertyChanged(nameof(PlatformText));
        }
        if (previous.Channel != updated.Channel) OnPropertyChanged(nameof(Channel));
        if (previous.DisplayName != updated.DisplayName || previous.Channel != updated.Channel)
            OnPropertyChanged(nameof(DisplayName));
        if (previous.Title != updated.Title) OnPropertyChanged(nameof(Title));
        if (previous.CategoryName != updated.CategoryName) OnPropertyChanged(nameof(CategoryName));
        if (previous.ViewerCount != updated.ViewerCount) OnPropertyChanged(nameof(ViewerCountText));
        if (previous.ProfileImageUrl != updated.ProfileImageUrl)
        {
            OnPropertyChanged(nameof(ProfileImageUrl));
            OnPropertyChanged(nameof(HasProfileImage));
        }
        if (previous.ThumbnailUrl != updated.ThumbnailUrl)
        {
            OnPropertyChanged(nameof(ThumbnailUrl));
            OnPropertyChanged(nameof(HasThumbnail));
        }
        if (ThumbnailCacheVersion != thumbnailCacheVersion || previous.ThumbnailUrl != updated.ThumbnailUrl)
        {
            ThumbnailCacheVersion = thumbnailCacheVersion;
            ThumbnailImageRequest = new AnimatedImageRequest(updated.ThumbnailUrl, thumbnailCacheVersion);
            OnPropertyChanged(nameof(ThumbnailCacheVersion));
            OnPropertyChanged(nameof(ThumbnailImageRequest));
        }

        // Elapsed live time changes even when the provider's metadata does not.
        OnPropertyChanged(nameof(MetadataText));
    }

    public string MetadataText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(stream.CategoryName))
            {
                parts.Add(stream.CategoryName);
            }

            if (!string.IsNullOrWhiteSpace(stream.Language))
            {
                parts.Add(stream.Language.ToUpperInvariant());
            }

            if (stream.StartedAtUtc is { } startedAt)
            {
                var elapsed = DateTimeOffset.UtcNow - startedAt.ToUniversalTime();
                if (elapsed > TimeSpan.Zero)
                {
                    parts.Add($"{FormatElapsed(elapsed)} live");
                }
            }

            if (stream.IsMature == true)
            {
                parts.Add("Mature");
            }

            return parts.Count == 0 ? stream.Platform.ToString() : string.Join(" | ", parts);
        }
    }
}

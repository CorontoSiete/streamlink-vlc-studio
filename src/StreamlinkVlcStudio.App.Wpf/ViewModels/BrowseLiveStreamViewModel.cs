using StreamlinkVlcStudio.Core.Models;
using static StreamlinkVlcStudio.App.Wpf.ViewModels.StreamViewModelHelpers;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class BrowseLiveStreamViewModel : ObservableObject, IHomeStreamOpenItemViewModel
{
    private readonly BrowseLiveStream stream;

    public BrowseLiveStreamViewModel(
        BrowseLiveStream stream,
        Func<BrowseLiveStreamViewModel, bool, Task> openAsync)
    {
        this.stream = stream;
        OpenCommand = new AsyncRelayCommand(() => openAsync(this, ShouldStayOnHomeForOpenCommand()));
        OpenAndStayOnHomeCommand = new AsyncRelayCommand(() => openAsync(this, true));
    }

    public AsyncRelayCommand OpenCommand { get; }

    public AsyncRelayCommand OpenAndStayOnHomeCommand { get; }

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

    public bool HasThumbnail => !string.IsNullOrWhiteSpace(stream.ThumbnailUrl);

    public string ViewerCountText => stream.ViewerCount is { } viewerCount
        ? FormatViewerCount(viewerCount)
        : "Live";

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

using StreamlinkVlcStudio.Core.Models;
using static StreamlinkVlcStudio.App.Wpf.ViewModels.StreamViewModelHelpers;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class BrowseCategoryViewModel : ObservableObject
{
    private BrowseCategory category;

    public BrowseCategoryViewModel(
        BrowseCategory category,
        Func<BrowseCategoryViewModel, Task> selectAsync)
    {
        this.category = category;
        SelectCommand = new AsyncRelayCommand(() => selectAsync(this));
    }

    public AsyncRelayCommand SelectCommand { get; }

    public BrowseCategory Category => category;

    public PlatformKind Platform => category.Platform;

    public string PlatformText => category.Platform.ToString();

    public string Id => category.Id;

    public string Name => string.IsNullOrWhiteSpace(category.Name)
        ? "Untitled category"
        : category.Name;

    public string ThumbnailUrl => category.ThumbnailUrl;

    public bool HasThumbnail => !string.IsNullOrWhiteSpace(category.ThumbnailUrl);

    public string ViewerCountText => category.ViewerCount is { } viewerCount
        ? FormatViewerCountLabel(viewerCount)
        : "";

    public string MetadataText
    {
        get
        {
            var parts = new List<string>();
            if (category.ViewerCount is { } viewerCount)
            {
                parts.Add(FormatViewerCountLabel(viewerCount));
            }

            var tags = category.Tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Take(3);
            parts.AddRange(tags);

            return parts.Count == 0
                ? category.Platform.ToString()
                : string.Join(" | ", parts);
        }
    }

    public void SetViewerCount(int viewerCount)
    {
        if (category.ViewerCount != viewerCount) Update(category with { ViewerCount = viewerCount });
    }

    internal void Update(BrowseCategory updated)
    {
        var previous = category;
        var tagsChanged = !previous.Tags.SequenceEqual(updated.Tags);
        if (previous.Platform == updated.Platform && previous.Id == updated.Id &&
            previous.Name == updated.Name && previous.ThumbnailUrl == updated.ThumbnailUrl &&
            previous.ViewerCount == updated.ViewerCount && !tagsChanged) return;
        category = updated;
        OnPropertyChanged(nameof(Category));
        if (previous.Platform != updated.Platform)
        {
            OnPropertyChanged(nameof(Platform));
            OnPropertyChanged(nameof(PlatformText));
        }
        if (previous.Id != updated.Id) OnPropertyChanged(nameof(Id));
        if (previous.Name != updated.Name) OnPropertyChanged(nameof(Name));
        if (previous.ThumbnailUrl != updated.ThumbnailUrl)
        {
            OnPropertyChanged(nameof(ThumbnailUrl));
            OnPropertyChanged(nameof(HasThumbnail));
        }
        if (previous.ViewerCount != updated.ViewerCount) OnPropertyChanged(nameof(ViewerCountText));
        if (previous.ViewerCount != updated.ViewerCount || previous.Platform != updated.Platform || tagsChanged)
            OnPropertyChanged(nameof(MetadataText));
    }

    private static string FormatViewerCountLabel(int value)
    {
        var suffix = value == 1 ? "viewer" : "viewers";
        return $"{FormatViewerCount(value)} {suffix}";
    }
}

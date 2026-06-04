using StreamlinkVlcStudio.Core.Models;

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
        if (category.ViewerCount == viewerCount)
        {
            return;
        }

        category = category with
        {
            ViewerCount = viewerCount
        };
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(ViewerCountText));
        OnPropertyChanged(nameof(MetadataText));
    }

    public void ClearViewerCount()
    {
        if (category.ViewerCount is null)
        {
            return;
        }

        category = category with
        {
            ViewerCount = null
        };
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(ViewerCountText));
        OnPropertyChanged(nameof(MetadataText));
    }

    private static string FormatViewerCountLabel(int value)
    {
        var suffix = value == 1 ? "viewer" : "viewers";
        return $"{FormatViewerCount(value)} {suffix}";
    }

    private static string FormatViewerCount(int value)
    {
        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.#}M";
        }

        if (value >= 1_000)
        {
            return $"{value / 1_000d:0.#}K";
        }

        return value.ToString();
    }
}

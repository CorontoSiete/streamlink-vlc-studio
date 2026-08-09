using System.ComponentModel;
using System.Globalization;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class TabStripItemViewModel : ObservableObject, IDisposable
{
    private readonly StreamTabViewModel[] tabs;
    private readonly StreamTabViewModel activeTab;

    public TabStripItemViewModel(IReadOnlyList<StreamTabViewModel> tabs, StreamTabViewModel? selectedTab)
    {
        if (tabs.Count == 0)
        {
            throw new ArgumentException("At least one tab is required.", nameof(tabs));
        }

        this.tabs = tabs.Distinct().ToArray();
        PrimaryTab = this.tabs[0];
        activeTab = selectedTab is not null && this.tabs.Contains(selectedTab)
            ? selectedTab
            : PrimaryTab;

        foreach (var tab in this.tabs)
        {
            tab.PropertyChanged += TabOnPropertyChanged;
        }
    }

    public IReadOnlyList<StreamTabViewModel> Tabs => tabs;
    public StreamTabViewModel PrimaryTab { get; }
    public StreamTabViewModel ActiveTab => activeTab;
    public bool IsGroup => tabs.Length > 1;
    public bool IsDetached => tabs.All(tab => tab.IsDetached);
    public string ProfileImageUrl => IsGroup ? "" : PrimaryTab.ProfileImageUrl;
    public bool HasProfileImage => !string.IsNullOrWhiteSpace(ProfileImageUrl);

    public string Title => IsGroup
        ? string.Join(" + ", tabs.Select(tab => tab.Title))
        : PrimaryTab.Title;

    public string StatusText
    {
        get
        {
            if (!IsGroup)
            {
                return PrimaryTab.StatusText;
            }

            return IsDetached
                ? "Picture-in-picture group"
                : $"{tabs.Length.ToString(CultureInfo.InvariantCulture)} streams";
        }
    }

    public string SubtitleText => !IsGroup && PrimaryTab.HasCategory
        ? PrimaryTab.CategoryName
        : StatusText;

    public string ViewerCountText => IsGroup
        ? tabs.Length.ToString(CultureInfo.InvariantCulture)
        : PrimaryTab.ViewerCountText;

    public string ViewerCountToolTip => IsGroup
        ? string.Join(
            Environment.NewLine,
            tabs.Select(tab => $"{tab.Target.DisplayName}: {tab.ViewerCountText} viewers, {tab.StatusText}"))
        : PrimaryTab.ViewerCountToolTip;

    public string ToolTip => IsGroup
        ? string.Join(
            Environment.NewLine,
            tabs.Select(tab => $"{tab.Target.DisplayName} - {FormatTabDetails(tab)}"))
        : FormatSingleTabToolTip(PrimaryTab);

    public PlatformKind Platform => PrimaryTab.Target.Platform;

    public bool Contains(StreamTabViewModel tab)
    {
        return tabs.Contains(tab);
    }

    public void Dispose()
    {
        foreach (var tab in tabs)
        {
            tab.PropertyChanged -= TabOnPropertyChanged;
        }
    }

    private void TabOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StreamTabViewModel.Title) or
            nameof(StreamTabViewModel.StreamTitle) or
            nameof(StreamTabViewModel.Status) or
            nameof(StreamTabViewModel.CategoryName) or
            nameof(StreamTabViewModel.ViewerCountText) or
            nameof(StreamTabViewModel.ViewerCountToolTip) or
            nameof(StreamTabViewModel.ProfileImageUrl) or
            nameof(StreamTabViewModel.IsDetached))
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(SubtitleText));
            OnPropertyChanged(nameof(ViewerCountText));
            OnPropertyChanged(nameof(ViewerCountToolTip));
            OnPropertyChanged(nameof(ProfileImageUrl));
            OnPropertyChanged(nameof(HasProfileImage));
            OnPropertyChanged(nameof(ToolTip));
            OnPropertyChanged(nameof(IsDetached));
        }
    }

    private static string FormatSingleTabToolTip(StreamTabViewModel tab)
    {
        if (!tab.HasCategory && string.IsNullOrWhiteSpace(tab.StreamTitle))
        {
            return tab.Target.DisplayName;
        }

        var lines = new List<string> { tab.Target.DisplayName };
        if (!string.IsNullOrWhiteSpace(tab.StreamTitle))
        {
            lines.Add($"Title: {tab.StreamTitle}");
        }

        if (tab.HasCategory)
        {
            lines.Add($"Category: {tab.CategoryName}");
            lines.Add($"Status: {tab.StatusText}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatTabDetails(StreamTabViewModel tab)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(tab.StreamTitle))
        {
            details.Add($"Title: {tab.StreamTitle}");
        }

        if (tab.HasCategory)
        {
            details.Add(tab.CategoryName);
        }

        details.Add(tab.StatusText);
        return string.Join(", ", details);
    }
}

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

/// <summary>
/// Owns the mutable membership lists for docked multi-view and detached
/// picture-in-picture groups. Layout calculation stays in the view model, but
/// group state no longer competes with UI property state there.
/// </summary>
internal sealed class TabGroupingController
{
    public List<List<StreamTabViewModel>> MultiViewGroups { get; } = [];
    public List<List<StreamTabViewModel>> PictureInPictureGroups { get; } = [];
    public List<List<StreamTabViewModel>> PictureInPictureVisibleGroups { get; } = [];

    public bool RemoveFromMultiViewGroups(IReadOnlyCollection<StreamTabViewModel> tabs) =>
        RemoveFromGroups(MultiViewGroups, tabs);

    public bool RemoveFromPictureInPictureGroups(IReadOnlyCollection<StreamTabViewModel> tabs) =>
        RemoveFromGroups(PictureInPictureGroups, tabs);

    public bool RemoveFromPictureInPictureVisibleGroups(IReadOnlyCollection<StreamTabViewModel> tabs)
    {
        if (tabs.Count == 0 || PictureInPictureVisibleGroups.Count == 0) return false;
        var removed = tabs.ToHashSet();
        // Visibility groups describe a whole detached layout. Any membership change invalidates it.
        return PictureInPictureVisibleGroups.RemoveAll(group => group.Any(removed.Contains)) > 0;
    }

    private static bool RemoveFromGroups(
        List<List<StreamTabViewModel>> groups,
        IReadOnlyCollection<StreamTabViewModel> tabs)
    {
        if (tabs.Count == 0 || groups.Count == 0) return false;
        var removed = tabs.ToHashSet();
        var changed = false;
        for (var index = groups.Count - 1; index >= 0; index--)
        {
            changed |= groups[index].RemoveAll(removed.Contains) > 0;
            if (groups[index].Count <= 1)
            {
                groups.RemoveAt(index);
                changed = true;
            }
        }

        return changed;
    }
}

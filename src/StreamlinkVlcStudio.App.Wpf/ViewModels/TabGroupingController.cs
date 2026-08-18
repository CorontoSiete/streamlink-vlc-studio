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

    public void RemoveTabs(IReadOnlyCollection<StreamTabViewModel> tabs)
    {
        if (tabs.Count == 0)
        {
            return;
        }

        RemoveFromGroups(MultiViewGroups, tabs);
        RemoveFromGroups(PictureInPictureGroups, tabs);
        RemoveFromGroups(PictureInPictureVisibleGroups, tabs);
    }

    private static void RemoveFromGroups(
        List<List<StreamTabViewModel>> groups,
        IReadOnlyCollection<StreamTabViewModel> tabs)
    {
        var removed = tabs.ToHashSet();
        for (var index = groups.Count - 1; index >= 0; index--)
        {
            groups[index].RemoveAll(removed.Contains);
            if (groups[index].Count <= 1)
            {
                groups.RemoveAt(index);
            }
        }
    }
}

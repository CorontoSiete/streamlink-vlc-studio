using System.Windows.Input;

namespace StreamlinkVlcStudio.App.Wpf;

internal static class ReplaySeekBarShortcutKeyPolicy
{
    public static bool ShouldHandle(Key key, ModifierKeys modifiers)
    {
        return key == Key.S && modifiers == ModifierKeys.Control;
    }
}

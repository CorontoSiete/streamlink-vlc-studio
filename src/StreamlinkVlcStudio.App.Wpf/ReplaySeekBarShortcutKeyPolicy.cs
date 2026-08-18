using System.Windows.Input;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.App.Wpf;

internal static class ReplaySeekBarShortcutKeyPolicy
{
    public static bool ShouldHandle(
        Key key,
        ModifierKeys modifiers,
        HotkeySettings? settings = null)
    {
        settings ??= new HotkeySettings();
        return HotkeyBindingPolicy.Matches(
            settings,
            AppHotkeyAction.ToggleReplaySeekBar,
            key,
            modifiers);
    }
}

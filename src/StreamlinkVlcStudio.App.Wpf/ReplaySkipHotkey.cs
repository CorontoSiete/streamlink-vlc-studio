using System.Windows;
using StreamlinkVlcStudio.App.Wpf.Controls;
using StreamlinkVlcStudio.App.Wpf.ViewModels;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.App.Wpf;

internal static class ReplaySkipHotkey
{
    internal static bool TryExecute(
        StreamTabViewModel? tab,
        HotkeySettings? settings,
        HotkeyGesture input,
        IInputElement? focusedElement,
        bool isRepeat = false)
    {
        if (tab is null || settings is null ||
            focusedElement is HotkeyRecorderButton { IsCapturingInput: true })
        {
            return false;
        }

        var action = HotkeyBindingPolicy.Matches(settings, AppHotkeyAction.SkipBackward, input)
            ? AppHotkeyAction.SkipBackward
            : HotkeyBindingPolicy.Matches(settings, AppHotkeyAction.SkipForward, input)
                ? AppHotkeyAction.SkipForward
                : (AppHotkeyAction?)null;
        if (action is null ||
            HotkeyBindingPolicy.ShouldSuppressForTextInput(settings, action.Value, focusedElement) ||
            !tab.IsReplaySeekEnabled)
        {
            return false;
        }

        var command = action == AppHotkeyAction.SkipBackward ? tab.SkipBackwardCommand : tab.SkipForwardCommand;
        if (!isRepeat && command.CanExecute(null))
        {
            command.Execute(null);
        }

        return true;
    }
}

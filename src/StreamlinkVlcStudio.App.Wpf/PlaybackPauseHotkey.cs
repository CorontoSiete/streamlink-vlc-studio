using System.Windows;
using StreamlinkVlcStudio.App.Wpf.Controls;
using StreamlinkVlcStudio.App.Wpf.ViewModels;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.App.Wpf;

internal static class PlaybackPauseHotkey
{
    internal static bool TryExecute(
        StreamTabViewModel? tab,
        HotkeySettings? settings,
        HotkeyGesture input,
        IInputElement? focusedElement,
        bool isRepeat = false,
        AsyncRelayCommand? command = null)
    {
        if (tab is null || settings is null ||
            focusedElement is HotkeyRecorderButton { IsCapturingInput: true } ||
            !HotkeyBindingPolicy.Matches(settings, AppHotkeyAction.TogglePause, input) ||
            HotkeyBindingPolicy.HasConflictingBinding(settings, AppHotkeyAction.TogglePause, input) ||
            HotkeyBindingPolicy.ShouldSuppressForTextInput(settings, AppHotkeyAction.TogglePause, focusedElement) ||
            tab.Status is not (PlaybackStatus.Playing or PlaybackStatus.Paused))
        {
            return false;
        }

        command ??= tab.PauseOrResumeCommand;
        if (!isRepeat && !tab.IsBusy && tab.PauseOrResumeCommand.CanExecute(null) && command.CanExecute(null))
        {
            command.Execute(null);
        }

        // Consume repeats and busy presses so Space cannot also activate a focused control.
        return true;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace StreamlinkVlcStudio.App.Wpf;

internal static class TabNavigationKeyPolicy
{
    public static bool ShouldHandle(
        Key key,
        ModifierKeys modifiers,
        bool isFullscreen,
        bool isFullscreenModeActive,
        bool isSettingsOpen,
        IInputElement? focusedElement)
    {
        if (key is not (Key.Left or Key.Right) ||
            modifiers != ModifierKeys.None ||
            IsTextEditingElement(focusedElement))
        {
            return false;
        }

        return CanNavigate(isFullscreen, isFullscreenModeActive, isSettingsOpen);
    }

    public static bool CanNavigate(
        bool isFullscreen,
        bool isFullscreenModeActive,
        bool isSettingsOpen)
    {
        return isFullscreen
            ? isFullscreenModeActive
            : !isSettingsOpen;
    }

    public static int DirectionFor(Key key)
    {
        return key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            _ => 0
        };
    }

    internal static bool IsTextEditingElement(IInputElement? element)
    {
        return element is TextBoxBase { Visibility: Visibility.Visible } or PasswordBox { Visibility: Visibility.Visible };
    }
}

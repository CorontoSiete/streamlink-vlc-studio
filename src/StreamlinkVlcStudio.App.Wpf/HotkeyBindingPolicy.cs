using System.Windows;
using System.Windows.Input;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.App.Wpf;

internal enum AppHotkeyAction
{
    DismissFullscreenOrAutoScroll,
    ToggleReplaySeekBar,
    PreviousTab,
    NextTab
}

internal static class HotkeyBindingPolicy
{
    private static readonly AppHotkeyAction[] AllActions = Enum.GetValues<AppHotkeyAction>();

    public static bool Matches(
        HotkeySettings settings,
        AppHotkeyAction action,
        Key key,
        ModifierKeys modifiers)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return HotkeyGesture.Matches(
            GetConfiguredGesture(settings, action),
            GetDefaultGesture(action),
            key,
            modifiers);
    }

    public static bool ShouldSuppressForTextInput(
        HotkeySettings settings,
        AppHotkeyAction action,
        IInputElement? focusedElement)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!TabNavigationKeyPolicy.IsTextEditingElement(focusedElement))
        {
            return false;
        }

        var gesture = HotkeyGesture.ParseOrDefault(
            GetConfiguredGesture(settings, action),
            GetDefaultGesture(action));
        return (gesture.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0 &&
            IsTextEditingKey(gesture.Key);
    }

    public static string GetEffectiveGesture(HotkeySettings settings, AppHotkeyAction action)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return HotkeyGesture.ParseOrDefault(
                GetConfiguredGesture(settings, action),
                GetDefaultGesture(action))
            .Serialize();
    }

    public static AppHotkeyAction? SwapConflictingBinding(
        HotkeySettings settings,
        AppHotkeyAction action,
        string previousGesture,
        string newGesture)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var previous = HotkeyGesture.ParseOrDefault(previousGesture, GetDefaultGesture(action)).Serialize();
        if (!HotkeyGesture.TryParse(newGesture, out var parsedNewGesture))
        {
            throw new ArgumentException("The new hotkey is invalid.", nameof(newGesture));
        }

        var candidate = parsedNewGesture.Serialize();
        foreach (var otherAction in AllActions)
        {
            if (otherAction == action ||
                !string.Equals(
                    GetEffectiveGesture(settings, otherAction),
                    candidate,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SetConfiguredGesture(settings, otherAction, previous);
            return otherAction;
        }

        return null;
    }

    public static string GetConfiguredGesture(HotkeySettings settings, AppHotkeyAction action)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return action switch
        {
            AppHotkeyAction.DismissFullscreenOrAutoScroll => settings.DismissFullscreenOrAutoScroll,
            AppHotkeyAction.ToggleReplaySeekBar => settings.ToggleReplaySeekBar,
            AppHotkeyAction.PreviousTab => settings.PreviousTab,
            AppHotkeyAction.NextTab => settings.NextTab,
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
    }

    public static void SetConfiguredGesture(
        HotkeySettings settings,
        AppHotkeyAction action,
        string gesture)
    {
        ArgumentNullException.ThrowIfNull(settings);
        switch (action)
        {
            case AppHotkeyAction.DismissFullscreenOrAutoScroll:
                settings.DismissFullscreenOrAutoScroll = gesture;
                break;
            case AppHotkeyAction.ToggleReplaySeekBar:
                settings.ToggleReplaySeekBar = gesture;
                break;
            case AppHotkeyAction.PreviousTab:
                settings.PreviousTab = gesture;
                break;
            case AppHotkeyAction.NextTab:
                settings.NextTab = gesture;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    public static string GetDefaultGesture(AppHotkeyAction action)
    {
        return action switch
        {
            AppHotkeyAction.DismissFullscreenOrAutoScroll => HotkeySettings.DefaultDismissFullscreenOrAutoScroll,
            AppHotkeyAction.ToggleReplaySeekBar => HotkeySettings.DefaultToggleReplaySeekBar,
            AppHotkeyAction.PreviousTab => HotkeySettings.DefaultPreviousTab,
            AppHotkeyAction.NextTab => HotkeySettings.DefaultNextTab,
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
    }

    private static bool IsTextEditingKey(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9 ||
            key is >= Key.A and <= Key.Z ||
            key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return true;
        }

        return key is
            Key.Back or Key.Tab or Key.Clear or Key.Return or Key.Space or
            Key.Prior or Key.Next or Key.End or Key.Home or
            Key.Left or Key.Up or Key.Right or Key.Down or
            Key.Insert or Key.Delete or
            Key.Multiply or Key.Add or Key.Separator or Key.Subtract or Key.Decimal or Key.Divide or
            Key.ImeConvert or Key.ImeNonConvert or Key.ImeAccept or Key.ImeModeChange or
            Key.OemSemicolon or Key.OemPlus or Key.OemComma or Key.OemMinus or Key.OemPeriod or
            Key.OemQuestion or Key.OemTilde or Key.OemOpenBrackets or Key.OemPipe or
            Key.OemCloseBrackets or Key.OemQuotes or Key.Oem8 or Key.OemBackslash;
    }
}

using System.Globalization;
using System.Windows.Input;

namespace StreamlinkVlcStudio.App.Wpf;

internal readonly record struct HotkeyGesture(Key Key, ModifierKeys Modifiers)
{
    private const ModifierKeys SupportedModifiers =
        ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows;

    public static HotkeyGesture FromKeyEvent(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new HotkeyGesture(GetEventKey(e), NormalizeModifiers(Keyboard.Modifiers));
    }

    public static Key GetEventKey(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return NormalizeEventKey(e.Key, e.SystemKey, e.ImeProcessedKey, e.DeadCharProcessedKey);
    }

    internal static Key NormalizeEventKey(
        Key key,
        Key systemKey = Key.None,
        Key imeProcessedKey = Key.None,
        Key deadCharProcessedKey = Key.None)
    {
        return key switch
        {
            Key.System => systemKey,
            Key.ImeProcessed => imeProcessedKey,
            Key.DeadCharProcessed => deadCharProcessedKey,
            _ => key
        };
    }

    public static ModifierKeys NormalizeModifiers(ModifierKeys modifiers) => modifiers & SupportedModifiers;

    public static bool IsBindableKey(Key key)
    {
        return key is not (
            Key.None or
            Key.LeftAlt or Key.RightAlt or
            Key.LeftCtrl or Key.RightCtrl or
            Key.LeftShift or Key.RightShift or
            Key.LWin or Key.RWin or
            Key.System or Key.ImeProcessed or Key.DeadCharProcessed);
    }

    public static bool TryParse(string? value, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var tokens = value.Split('+', StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || tokens.Any(string.IsNullOrEmpty))
        {
            return false;
        }

        var modifiers = ModifierKeys.None;
        for (var index = 0; index < tokens.Length - 1; index++)
        {
            if (!TryParseModifier(tokens[index], out var modifier) || modifiers.HasFlag(modifier))
            {
                return false;
            }

            modifiers |= modifier;
        }

        if (!Enum.TryParse(tokens[^1], ignoreCase: true, out Key key) ||
            !Enum.IsDefined(key) ||
            !IsBindableKey(key))
        {
            return false;
        }

        gesture = new HotkeyGesture(key, NormalizeModifiers(modifiers));
        return true;
    }

    public static HotkeyGesture ParseOrDefault(string? value, string fallback)
    {
        if (TryParse(value, out var gesture))
        {
            return gesture;
        }

        if (TryParse(fallback, out gesture))
        {
            return gesture;
        }

        throw new ArgumentException("The fallback hotkey is invalid.", nameof(fallback));
    }

    public static bool Matches(
        string? configuredGesture,
        string defaultGesture,
        Key key,
        ModifierKeys modifiers)
    {
        var configured = ParseOrDefault(configuredGesture, defaultGesture);
        return configured.Key == key && configured.Modifiers == NormalizeModifiers(modifiers);
    }

    public string Serialize()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(Key.ToString());
        return string.Join('+', parts);
    }

    public string ToDisplayString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(GetKeyDisplayName(Key));
        return string.Join(" + ", parts);
    }

    private static bool TryParseModifier(string value, out ModifierKeys modifier)
    {
        modifier = value.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => ModifierKeys.Control,
            "ALT" => ModifierKeys.Alt,
            "SHIFT" => ModifierKeys.Shift,
            "WIN" or "WINDOWS" => ModifierKeys.Windows,
            _ => ModifierKeys.None
        };
        return modifier != ModifierKeys.None;
    }

    private static string GetKeyDisplayName(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)key - (int)Key.D0).ToString(CultureInfo.InvariantCulture);
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return $"Num {((int)key - (int)Key.NumPad0).ToString(CultureInfo.InvariantCulture)}";
        }

        return key switch
        {
            Key.Back => "Backspace",
            Key.Capital => "Caps Lock",
            Key.Return => "Enter",
            Key.Prior => "Page Up",
            Key.Next => "Page Down",
            Key.Snapshot => "Print Screen",
            Key.NumLock => "Num Lock",
            Key.Scroll => "Scroll Lock",
            Key.OemPlus => "=/+",
            Key.OemMinus => "-/_",
            Key.OemComma => ",/<",
            Key.OemPeriod => "./>",
            Key.OemSemicolon => ";/:",
            Key.OemQuestion => "/?",
            Key.OemTilde => "`/~",
            Key.OemOpenBrackets => "[/{",
            Key.OemPipe => "\\/|",
            Key.OemCloseBrackets => "]/}",
            Key.OemQuotes => "'/\"",
            _ => key.ToString()
        };
    }
}

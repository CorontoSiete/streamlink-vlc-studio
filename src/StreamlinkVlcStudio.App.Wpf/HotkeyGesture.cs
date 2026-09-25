using System.Globalization;
using System.Windows.Input;

namespace StreamlinkVlcStudio.App.Wpf;

internal readonly record struct HotkeyGesture(Key Key, ModifierKeys Modifiers, MouseButton? MouseButton = null)
{
    private const ModifierKeys SupportedModifiers =
        ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows;

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

    public static bool IsBindableMouseButton(MouseButton button)
        => button is System.Windows.Input.MouseButton.XButton1 or System.Windows.Input.MouseButton.XButton2;

    public static HotkeyGesture FromMouseButton(MouseButton button, ModifierKeys modifiers)
    {
        if (!IsBindableMouseButton(button))
        {
            throw new ArgumentOutOfRangeException(nameof(button));
        }

        return new HotkeyGesture(Key.None, NormalizeModifiers(modifiers), button);
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

        var mouseButton = tokens[^1].ToUpperInvariant() switch
        {
            "MOUSE4" or "XBUTTON1" => System.Windows.Input.MouseButton.XButton1,
            "MOUSE5" or "XBUTTON2" => System.Windows.Input.MouseButton.XButton2,
            _ => (MouseButton?)null
        };
        if (mouseButton is { } button)
        {
            gesture = FromMouseButton(button, modifiers);
            return true;
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
        return Matches(configuredGesture, defaultGesture, new HotkeyGesture(key, NormalizeModifiers(modifiers)));
    }

    public static bool Matches(string? configuredGesture, string defaultGesture, HotkeyGesture input)
        => ParseOrDefault(configuredGesture, defaultGesture) == input;

    public string Serialize() => Format(MouseButton is null ? Key.ToString() : GetMouseButtonName(), "+");

    public string ToDisplayString() => Format(MouseButton is null ? GetKeyDisplayName(Key) : GetMouseButtonName(), " + ");

    private string Format(string keyName, string separator)
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

        parts.Add(keyName);
        return string.Join(separator, parts);
    }

    private string GetMouseButtonName() => MouseButton switch
    {
        System.Windows.Input.MouseButton.XButton1 => "Mouse4",
        System.Windows.Input.MouseButton.XButton2 => "Mouse5",
        _ => throw new InvalidOperationException("The mouse button is not bindable.")
    };

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

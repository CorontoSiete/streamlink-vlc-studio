using System.Globalization;
using System.Windows.Input;
using System.Windows.Interop;

namespace StreamlinkVlcStudio.App.Wpf;

internal readonly record struct HotkeyGesture(Key Key, ModifierKeys Modifiers, MouseButton? MouseButton = null)
{
    private const ModifierKeys SupportedModifiers =
        ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows;

    public static Key GetEventKey(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var key = NormalizeEventKey(e.Key, e.SystemKey, e.ImeProcessedKey, e.DeadCharProcessedKey);
        var message = ComponentDispatcher.CurrentKeyboardMessage;
        // WPF can report the previous message's timestamp while synchronously
        // processing a new native key. Match the source HWND rather than the time.
        if (e.InputSource is not HwndSource source || message.hwnd != source.Handle ||
            message.message is not (0x0100 or 0x0101 or 0x0104 or 0x0105))
        {
            return key;
        }

        return NormalizeNumpadArrow(key, message.wParam.ToInt32(), message.lParam.ToInt64());
    }

    internal static Key NormalizeNumpadArrow(Key key, int virtualKey, long keyData)
    {
        // With Num Lock off, keypad 4/6 share VK_LEFT/VK_RIGHT with the arrow cluster.
        // Windows marks the separate arrow cluster as extended (bit 24); the keypad
        // keeps its non-extended 0x4B/0x4D scan code. Use WPF's current native message
        // for both key-down and key-up so recording and playback agree in either mode.
        if ((keyData & 0x01000000) != 0) return key;
        var scanCode = (keyData >> 16) & 0xFF;
        return (key, virtualKey, scanCode) switch
        {
            (Key.Left, 0x25, 0x4B) => Key.NumPad4,
            (Key.Right, 0x27, 0x4D) => Key.NumPad6,
            _ => key
        };
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
        if (key == Key.NumPad4) return "Num 4 (Left)";
        if (key == Key.NumPad6) return "Num 6 (Right)";

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

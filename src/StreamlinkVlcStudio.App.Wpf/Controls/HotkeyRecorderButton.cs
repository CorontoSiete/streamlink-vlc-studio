using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

public sealed class HotkeyRecorderButton : Button
{
    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
        nameof(Gesture),
        typeof(string),
        typeof(HotkeyRecorderButton),
        new FrameworkPropertyMetadata(
            "",
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnGestureChanged));

    public static readonly DependencyProperty DefaultGestureProperty = DependencyProperty.Register(
        nameof(DefaultGesture),
        typeof(string),
        typeof(HotkeyRecorderButton),
        new PropertyMetadata("", OnGestureChanged));

    public static readonly DependencyProperty ActionNameProperty = DependencyProperty.Register(
        nameof(ActionName),
        typeof(string),
        typeof(HotkeyRecorderButton),
        new PropertyMetadata("Hotkey", OnGestureChanged));

    private bool isCapturing;
    private Key suppressedKeyUp = Key.None;

    public HotkeyRecorderButton()
    {
        Focusable = true;
        IsTabStop = true;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        UpdateVisualState();
    }

    public event EventHandler<HotkeyGestureChangingEventArgs>? GestureChanging;

    public string Gesture
    {
        get => (string)GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    public string DefaultGesture
    {
        get => (string)GetValue(DefaultGestureProperty);
        set => SetValue(DefaultGestureProperty, value);
    }

    public string ActionName
    {
        get => (string)GetValue(ActionNameProperty);
        set => SetValue(ActionNameProperty, value);
    }

    internal bool IsCapturing => isCapturing;

    internal bool IsCapturingInput => isCapturing || suppressedKeyUp != Key.None;

    protected override void OnClick()
    {
        base.OnClick();
        isCapturing = true;
        _ = Focus();
        UpdateVisualState();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (!IsCapturingInput)
        {
            return;
        }

        isCapturing = false;
        suppressedKeyUp = Key.None;
        UpdateVisualState();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!isCapturing)
        {
            if (suppressedKeyUp != Key.None)
            {
                e.Handled = true;
                return;
            }

            base.OnPreviewKeyDown(e);
            return;
        }

        var key = HotkeyGesture.GetEventKey(e);
        e.Handled = true;
        if (!HotkeyGesture.IsBindableKey(key))
        {
            Content = "Press another key…";
            return;
        }

        var newGesture = new HotkeyGesture(key, Keyboard.Modifiers).Serialize();
        var previousGesture = TryGetEffectiveGesture(out var effectiveGesture)
            ? effectiveGesture.Serialize()
            : newGesture;
        var changing = new HotkeyGestureChangingEventArgs(previousGesture, newGesture);
        GestureChanging?.Invoke(this, changing);

        isCapturing = false;
        suppressedKeyUp = key;
        if (!changing.Cancel)
        {
            SetCurrentValue(GestureProperty, newGesture);
            BindingOperations.GetBindingExpression(this, GestureProperty)?.UpdateSource();
        }

        UpdateVisualState();
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (suppressedKeyUp != Key.None && HotkeyGesture.GetEventKey(e) == suppressedKeyUp)
        {
            suppressedKeyUp = Key.None;
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyUp(e);
    }

    private static void OnGestureChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is HotkeyRecorderButton button)
        {
            button.UpdateVisualState();
        }
    }

    private void UpdateVisualState()
    {
        if (isCapturing)
        {
            Content = "Press a key combination…";
            ToolTip = "Press the shortcut you want to assign.";
            AutomationProperties.SetName(this, $"{GetActionName()}: press a key combination");
            AutomationProperties.SetHelpText(this, "Press the shortcut you want to assign.");
            return;
        }

        var displayText = TryGetEffectiveGesture(out var effective)
            ? effective.ToDisplayString()
            : "Not assigned";
        Content = displayText;
        ToolTip = $"{displayText}\nClick, then press a new shortcut.";
        AutomationProperties.SetName(this, $"{GetActionName()}: {displayText}");
        AutomationProperties.SetHelpText(this, "Click, then press a new shortcut.");
    }

    private bool TryGetEffectiveGesture(out HotkeyGesture gesture)
    {
        return HotkeyGesture.TryParse(Gesture, out gesture) ||
            HotkeyGesture.TryParse(DefaultGesture, out gesture);
    }

    private string GetActionName()
        => string.IsNullOrWhiteSpace(ActionName) ? "Hotkey" : ActionName.Trim();
}

public sealed class HotkeyGestureChangingEventArgs : EventArgs
{
    public HotkeyGestureChangingEventArgs(string previousGesture, string newGesture)
    {
        PreviousGesture = previousGesture;
        NewGesture = newGesture;
    }

    public string PreviousGesture { get; }

    public string NewGesture { get; }

    public bool Cancel { get; set; }
}

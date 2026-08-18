namespace StreamlinkVlcStudio.Core.Settings;

public sealed class HotkeySettings : NotifyPropertyChangedObject
{
    public const string DefaultDismissFullscreenOrAutoScroll = "Escape";
    public const string DefaultToggleReplaySeekBar = "Ctrl+S";
    public const string DefaultPreviousTab = "Left";
    public const string DefaultNextTab = "Right";

    private string dismissFullscreenOrAutoScroll = DefaultDismissFullscreenOrAutoScroll;
    private string toggleReplaySeekBar = DefaultToggleReplaySeekBar;
    private string previousTab = DefaultPreviousTab;
    private string nextTab = DefaultNextTab;

    public string DismissFullscreenOrAutoScroll
    {
        get => dismissFullscreenOrAutoScroll;
        set => SetProperty(
            ref dismissFullscreenOrAutoScroll,
            Normalize(value, DefaultDismissFullscreenOrAutoScroll));
    }

    public string ToggleReplaySeekBar
    {
        get => toggleReplaySeekBar;
        set => SetProperty(ref toggleReplaySeekBar, Normalize(value, DefaultToggleReplaySeekBar));
    }

    public string PreviousTab
    {
        get => previousTab;
        set => SetProperty(ref previousTab, Normalize(value, DefaultPreviousTab));
    }

    public string NextTab
    {
        get => nextTab;
        set => SetProperty(ref nextTab, Normalize(value, DefaultNextTab));
    }

    public void ResetToDefaults()
    {
        DismissFullscreenOrAutoScroll = DefaultDismissFullscreenOrAutoScroll;
        ToggleReplaySeekBar = DefaultToggleReplaySeekBar;
        PreviousTab = DefaultPreviousTab;
        NextTab = DefaultNextTab;
    }

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

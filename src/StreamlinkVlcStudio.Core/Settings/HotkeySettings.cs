namespace StreamlinkVlcStudio.Core.Settings;

public sealed class HotkeySettings : NotifyPropertyChangedObject
{
    public const string DefaultDismissFullscreenOrAutoScroll = "Escape";
    public const string DefaultToggleReplaySeekBar = "Ctrl+S";
    public const string DefaultPreviousTab = "Left";
    public const string DefaultNextTab = "Right";
    public const string DefaultToggleMultiStream = "M";
    public const string DefaultVolumeUp = "Up";
    public const string DefaultVolumeDown = "Down";
    public const string DefaultGoBack = "Mouse4";
    public const string DefaultSkipBackward = "NumPad4";
    public const string DefaultSkipForward = "NumPad6";
    public const string DefaultTogglePause = "Space";
    public const int DefaultSkipSeconds = 30;
    public const int MinimumSkipSeconds = 1;
    public const int MaximumSkipSeconds = 300;

    private string dismissFullscreenOrAutoScroll = DefaultDismissFullscreenOrAutoScroll;
    private string toggleReplaySeekBar = DefaultToggleReplaySeekBar;
    private string previousTab = DefaultPreviousTab;
    private string nextTab = DefaultNextTab;
    private string toggleMultiStream = DefaultToggleMultiStream;
    private string volumeUp = DefaultVolumeUp;
    private string volumeDown = DefaultVolumeDown;
    private string goBack = DefaultGoBack;
    private string skipBackward = DefaultSkipBackward;
    private string skipForward = DefaultSkipForward;
    private string togglePause = DefaultTogglePause;
    private int skipBackwardSeconds = DefaultSkipSeconds;
    private int skipForwardSeconds = DefaultSkipSeconds;

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

    public string ToggleMultiStream
    {
        get => toggleMultiStream;
        set => SetProperty(ref toggleMultiStream, Normalize(value, DefaultToggleMultiStream));
    }

    public string VolumeUp
    {
        get => volumeUp;
        set => SetProperty(ref volumeUp, Normalize(value, DefaultVolumeUp));
    }

    public string VolumeDown
    {
        get => volumeDown;
        set => SetProperty(ref volumeDown, Normalize(value, DefaultVolumeDown));
    }

    public string GoBack
    {
        get => goBack;
        set => SetProperty(ref goBack, Normalize(value, DefaultGoBack));
    }

    public string SkipBackward
    {
        get => skipBackward;
        set => SetProperty(ref skipBackward, Normalize(value, DefaultSkipBackward));
    }

    public string SkipForward
    {
        get => skipForward;
        set => SetProperty(ref skipForward, Normalize(value, DefaultSkipForward));
    }

    public string TogglePause
    {
        get => togglePause;
        set => SetProperty(ref togglePause, Normalize(value, DefaultTogglePause));
    }

    public int SkipBackwardSeconds
    {
        get => skipBackwardSeconds;
        set => SetProperty(ref skipBackwardSeconds, Math.Clamp(value, MinimumSkipSeconds, MaximumSkipSeconds));
    }

    public int SkipForwardSeconds
    {
        get => skipForwardSeconds;
        set => SetProperty(ref skipForwardSeconds, Math.Clamp(value, MinimumSkipSeconds, MaximumSkipSeconds));
    }

    public void ResetToDefaults()
    {
        DismissFullscreenOrAutoScroll = DefaultDismissFullscreenOrAutoScroll;
        ToggleReplaySeekBar = DefaultToggleReplaySeekBar;
        PreviousTab = DefaultPreviousTab;
        NextTab = DefaultNextTab;
        ToggleMultiStream = DefaultToggleMultiStream;
        VolumeUp = DefaultVolumeUp;
        VolumeDown = DefaultVolumeDown;
        GoBack = DefaultGoBack;
        SkipBackward = DefaultSkipBackward;
        SkipForward = DefaultSkipForward;
        TogglePause = DefaultTogglePause;
        SkipBackwardSeconds = DefaultSkipSeconds;
        SkipForwardSeconds = DefaultSkipSeconds;
    }

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

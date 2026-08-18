using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using StreamlinkVlcStudio.App.Wpf.ViewModels;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

/// <summary>
/// A transient on-screen volume indicator (icon + level bar + percentage) shown over the video
/// when the volume changes. Hosted in a Popup so it renders above the native libVLC video
/// surface, and auto-hides a short time after the last change.
/// </summary>
public partial class VolumeOverlay : UserControl
{
    private const double TrackWidth = 160;   // must match the bar Grid width in the XAML
    private const double PillHeight = 32;     // approx pill height, for the bottom offset
    private const double BottomMargin = 26;   // gap from the target's bottom edge
    private const int MaxVolume = VolumeLimits.Max; // display ceiling shared with playback + settings
    internal const int WheelStep = 5;         // volume percent applied per mouse-wheel notch
    private static readonly TimeSpan DisplayDuration = TimeSpan.FromMilliseconds(1000);

    private DispatcherTimer? hideTimer;
    private UIElement? lastTarget;
    private Window? ownerWindow;

    public VolumeOverlay()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Shows the current <paramref name="volume"/> (0-125) centred near the bottom of
    /// <paramref name="target"/> and restarts the auto-hide timer. No-op when target is null.
    /// </summary>
    public void Show(UIElement? target, int volume, bool muted)
    {
        if (target is null)
        {
            return;
        }

        var level = Math.Clamp(volume, 0, MaxVolume);
        Fill.Width = TrackWidth * level / MaxVolume;
        Percent.Text = volume + "%";
        Icon.Text = GetGlyph(muted, volume);

        // Center placement + a downward offset parks the pill near the bottom of the target.
        Popup.VerticalOffset = Math.Max(
            0,
            (target.RenderSize.Height / 2) - BottomMargin - (PillHeight / 2));

        // Only re-anchor (close/reopen) when the target changed, to avoid flicker on repeats.
        if (!ReferenceEquals(target, lastTarget))
        {
            Popup.IsOpen = false;
            Popup.PlacementTarget = target;
            lastTarget = target;
        }

        Popup.IsOpen = true;

        var timer = EnsureHideTimer();
        timer.Stop();
        timer.Start();
    }

    /// <summary>
    /// Applies a mouse-wheel delta to the tab's volume and shows <paramref name="osd"/> over
    /// <paramref name="osdTarget"/>. Shared by the main and detached video windows.
    /// </summary>
    internal static void AdjustVolume(StreamTabViewModel tab, int wheelDelta, VolumeOverlay osd, UIElement osdTarget)
    {
        var notches = Math.Max(1, Math.Abs(wheelDelta) / Mouse.MouseWheelDeltaForOneLine);
        tab.Volume += Math.Sign(wheelDelta) * WheelStep * notches;
        osd.Show(osdTarget, tab.Volume, tab.IsMuted);
    }

    private DispatcherTimer EnsureHideTimer()
    {
        if (hideTimer is not null)
        {
            return hideTimer;
        }

        hideTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = DisplayDuration
        };
        hideTimer.Tick += OnHideTimerTick;
        return hideTimer;
    }

    private void OnHideTimerTick(object? sender, EventArgs e)
    {
        hideTimer?.Stop();
        Popup.IsOpen = false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (ReferenceEquals(window, ownerWindow))
        {
            return;
        }

        DetachOwnerWindow();
        ownerWindow = window;
        if (ownerWindow is not null)
        {
            ownerWindow.Closed += OnOwnerWindowClosed;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ReleasePopup();
        DetachOwnerWindow();
    }

    private void OnOwnerWindowClosed(object? sender, EventArgs e)
    {
        ReleasePopup();
        DetachOwnerWindow();
    }

    private void ReleasePopup()
    {
        if (hideTimer is not null)
        {
            hideTimer.Stop();
            hideTimer.Tick -= OnHideTimerTick;
            hideTimer = null;
        }

        Popup.IsOpen = false;
        Popup.PlacementTarget = null;
        lastTarget = null;
    }

    private void DetachOwnerWindow()
    {
        if (ownerWindow is not null)
        {
            ownerWindow.Closed -= OnOwnerWindowClosed;
            ownerWindow = null;
        }
    }

    // Segoe MDL2 Assets volume glyphs, built from code points to stay encoding-safe in source.
    private static string GetGlyph(bool muted, int level)
    {
        if (muted)
        {
            return ((char)0xE74F).ToString(); // Mute
        }

        var codePoint = level switch
        {
            <= 0 => 0xE992,  // Volume0
            <= 32 => 0xE993, // Volume1 (low)
            <= 65 => 0xE994, // Volume2 (medium)
            _ => 0xE995      // Volume3 (high)
        };
        return ((char)codePoint).ToString();
    }
}

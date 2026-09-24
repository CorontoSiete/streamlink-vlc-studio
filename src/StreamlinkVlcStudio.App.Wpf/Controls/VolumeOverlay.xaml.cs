using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    private const double BottomMargin = 26;   // gap from the target's bottom edge
    private const double SideMargin = 8;
    private const int MaxVolume = VolumeLimits.Max; // display ceiling shared with playback + settings
    internal const int WheelStep = 5;         // volume percent applied per mouse-wheel notch
    private static readonly TimeSpan DisplayDuration = TimeSpan.FromMilliseconds(1000);

    private DispatcherTimer? hideTimer;
    private UIElement? lastTarget;
    private FrameworkElement? subscribedTarget;
    private Window? ownerWindow;
    private IntPtr popupHandle;

    public VolumeOverlay()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Popup.Opened += (_, _) =>
        {
            popupHandle = (PresentationSource.FromVisual(Popup.Child) as HwndSource)?.Handle ?? IntPtr.Zero;
            NativeMouseWheelTarget.RegisterWindow(popupHandle, acceptsInactiveWheel: Window.GetWindow(this) is DetachedVideoWindow);
            if (Window.GetWindow(this) is DetachedVideoWindow detached)
            {
                NativePictureInPictureContextMenuTarget.RegisterAlias(popupHandle, new WindowInteropHelper(detached).Handle);
            }
            UpdatePlacement();
        };
        Popup.Closed += (_, _) =>
        {
            UnregisterPopupWindow();
            DetachTarget();
        };
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

        // Only re-anchor (close/reopen) when the target changed, to avoid flicker on repeats.
        if (!ReferenceEquals(target, lastTarget))
        {
            Popup.IsOpen = false;
            Popup.PlacementTarget = target;
            lastTarget = target;
        }

        AttachTarget(target as FrameworkElement);
        if (!UpdatePlacement())
        {
            return;
        }

        Popup.IsOpen = true;
        UpdatePlacement();

        var timer = EnsureHideTimer();
        timer.Stop();
        timer.Start();
    }

    private bool UpdatePlacement()
    {
        if (lastTarget is not { IsVisible: true } target ||
            target.RenderSize.Width <= 0 || target.RenderSize.Height <= 0)
        {
            Popup.IsOpen = false;
            DetachTarget();
            return false;
        }

        var width = target.RenderSize.Width;
        var height = target.RenderSize.Height;
        var replayInset = VisualTreeHelper.GetParent(target) is Panel panel
            ? panel.Children.OfType<ReplaySeekOverlay>().Select(overlay => overlay.ReservedBottomHeight).DefaultIfEmpty(0).Max()
            : 0;
        var availableHeight = Math.Max(0, height - replayInset);
        if (availableHeight <= 0)
        {
            Popup.IsOpen = false;
            DetachTarget();
            return false;
        }

        // Only the WPF popup chrome scales. Native video HWNDs retain their actual layout
        // dimensions. Reduce the insets with the target so even a tiny video contains it.
        var horizontalInset = Math.Min(SideMargin, width / 4);
        var verticalInset = Math.Min(BottomMargin, availableHeight / 4);
        OverlayScale.MaxWidth = width - 2 * horizontalInset;
        OverlayScale.MaxHeight = availableHeight - 2 * verticalInset;
        OverlayScale.Measure(new Size(OverlayScale.MaxWidth, OverlayScale.MaxHeight));
        var popupSize = OverlayScale.DesiredSize;

        // PlacementRectangle also invalidates native popup placement when the target
        // resizes. Popup otherwise retains its old desktop position during layout changes.
        Popup.PlacementRectangle = new Rect(0, 0, width, height);
        Popup.HorizontalOffset = Math.Max(0, (width - popupSize.Width) / 2);
        Popup.VerticalOffset = Math.Clamp(
            availableHeight - verticalInset - popupSize.Height,
            0,
            Math.Max(0, height - popupSize.Height));
        return true;
    }

    private void AttachTarget(FrameworkElement? target)
    {
        if (ReferenceEquals(subscribedTarget, target))
        {
            return;
        }

        DetachTarget();
        subscribedTarget = target;
        if (subscribedTarget is not null)
        {
            subscribedTarget.SizeChanged += OnTargetSizeChanged;
            subscribedTarget.IsVisibleChanged += OnTargetVisibilityChanged;
        }
    }

    private void DetachTarget()
    {
        if (subscribedTarget is not null)
        {
            subscribedTarget.SizeChanged -= OnTargetSizeChanged;
            subscribedTarget.IsVisibleChanged -= OnTargetVisibilityChanged;
            subscribedTarget = null;
        }
    }

    private void OnTargetSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePlacement();

    private void OnTargetVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            Popup.IsOpen = false;
        }
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
            ownerWindow.LocationChanged += OnOwnerWindowPlacementChanged;
            ownerWindow.StateChanged += OnOwnerWindowPlacementChanged;
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

    private void OnOwnerWindowPlacementChanged(object? sender, EventArgs e)
    {
        // A Popup is a desktop window; dismiss this transient indicator when its owner
        // moves or minimizes rather than leaving it floating at the previous position.
        Popup.IsOpen = false;
    }

    private void ReleasePopup()
    {
        UnregisterPopupWindow();
        if (hideTimer is not null)
        {
            hideTimer.Stop();
            hideTimer.Tick -= OnHideTimerTick;
            hideTimer = null;
        }

        Popup.IsOpen = false;
        DetachTarget();
        Popup.PlacementTarget = null;
        lastTarget = null;
    }

    private void UnregisterPopupWindow()
    {
        NativeMouseWheelTarget.UnregisterWindow(popupHandle);
        NativePictureInPictureContextMenuTarget.UnregisterWindow(popupHandle);
        popupHandle = IntPtr.Zero;
    }

    private void DetachOwnerWindow()
    {
        if (ownerWindow is not null)
        {
            ownerWindow.Closed -= OnOwnerWindowClosed;
            ownerWindow.LocationChanged -= OnOwnerWindowPlacementChanged;
            ownerWindow.StateChanged -= OnOwnerWindowPlacementChanged;
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

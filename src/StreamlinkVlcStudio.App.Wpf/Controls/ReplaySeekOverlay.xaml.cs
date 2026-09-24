using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using StreamlinkVlcStudio.App.Wpf.ViewModels;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

/// <summary>Interactive replay chrome embedded inside the native video host.</summary>
public partial class ReplaySeekOverlay : UserControl
{
    internal static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(180);
    public static readonly DependencyProperty PlacementTargetProperty = DependencyProperty.Register(
        nameof(PlacementTarget), typeof(FrameworkElement), typeof(ReplaySeekOverlay),
        new PropertyMetadata(null, OnTargetChanged));
    public static readonly DependencyProperty IsOverlayEnabledProperty = DependencyProperty.Register(
        nameof(IsOverlayEnabled), typeof(bool), typeof(ReplaySeekOverlay),
        new PropertyMetadata(true, OnEnabledChanged));
    public static readonly DependencyProperty IsCompactLayoutProperty = DependencyProperty.Register(
        nameof(IsCompactLayout), typeof(bool), typeof(ReplaySeekOverlay), new PropertyMetadata(false));

    private readonly DispatcherTimer pointerTimer;
    private Window? owner;
    private FrameworkElement? subscribedTarget;
    private Point? lastPointer;
    private long lastActivity;
    private int animationVersion;
    private bool fading;
    private StreamTabViewModel? seekTab;
    private bool keyboardSeeking;

    private enum PointerSampleTarget
    {
        None,
        Video,
        Overlay
    }

    public ReplaySeekOverlay()
    {
        InitializeComponent();
        InitializeSeekHover();
        OverlayChrome.Tag = this;
        OverlayHost.PlacementInvalidated += OnNativeBoundsChanged;
        pointerTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        pointerTimer.Tick += OnPointerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += (_, _) => { HideImmediately(); previewImages = new(); lastPointer = null; };
        // Move-to-point marks mouse down handled; preview must also start for track clicks.
        ReplaySeekSlider.AddHandler(PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnSeekPointerDown), true);
        ReplaySeekSlider.AddHandler(PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnSeekPointerUp), true);
        ReplaySeekSlider.AddHandler(PreviewMouseMoveEvent, new MouseEventHandler(OnSeekPointerMove), true);
        ReplaySeekSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnSeekDragStarted));
        ReplaySeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnSeekDragCompleted));
        ReplaySeekSlider.LostMouseCapture += OnSeekLostCapture;
        ReplaySeekSlider.PreviewKeyDown += OnSeekKeyDown;
        ReplaySeekSlider.KeyUp += OnSeekKeyUp;
        ReplaySeekSlider.LostKeyboardFocus += (_, _) =>
        {
            if (keyboardSeeking) CancelSeek();
        };
        OverlayChrome.PreviewMouseDown += (_, _) => Reveal(Environment.TickCount64);
        OverlayChrome.PreviewMouseUp += (_, e) =>
        {
            // This chrome lives in its own HWND, outside the main window's input route.
            if (owner is MainWindow main &&
                main.TryExecuteMouseHotkey(e.ChangedButton, Keyboard.Modifiers, Keyboard.FocusedElement))
            {
                e.Handled = true;
            }
        };
        OverlayChrome.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { HideImmediately(); e.Handled = true; }
            else Reveal(Environment.TickCount64);
        };
    }

    public FrameworkElement? PlacementTarget
    {
        get => (FrameworkElement?)GetValue(PlacementTargetProperty);
        set => SetValue(PlacementTargetProperty, value);
    }

    public bool IsOverlayEnabled
    {
        get => (bool)GetValue(IsOverlayEnabledProperty);
        set => SetValue(IsOverlayEnabledProperty, value);
    }

    public bool IsCompactLayout
    {
        get => (bool)GetValue(IsCompactLayoutProperty);
        private set => SetValue(IsCompactLayoutProperty, value);
    }

    internal bool IsOverlayOpen => OverlayHost.IsOpen;
    internal double ReservedBottomHeight => OverlayHost.IsOpen
        ? OverlayChrome.ActualHeight + (PlacementTarget?.ActualWidth < 400 ? 8 : 16)
        : 0;

    private bool CanDisplay => IsLoaded && IsOverlayEnabled &&
        DataContext is StreamTabViewModel && IsOwnerAvailable &&
        PlacementTarget is VideoSurface { IsVisible: true, ActualWidth: >= 196, ActualHeight: >= 100 } target &&
        target.Handle != IntPtr.Zero &&
        (target.ActualWidth >= 296 || target.ActualHeight >= 126);

    private bool IsOwnerAvailable => owner is { IsVisible: true, WindowState: not WindowState.Minimized } &&
        (owner.IsActive || owner is DetachedVideoWindow { Topmost: true });

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var overlay = (ReplaySeekOverlay)d;
        overlay.DetachTarget();
        if (overlay.IsLoaded) overlay.AttachTarget();
        overlay.HideImmediately();
        overlay.OverlayHost.PlacementTarget = overlay.IsLoaded ? e.NewValue as VideoSurface : null;
        overlay.SeekPreviewHost.PlacementTarget = overlay.IsLoaded ? e.NewValue as VideoSurface : null;
        overlay.lastPointer = null;
        overlay.UpdateTimer();
    }

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ReplaySeekOverlay)d).UpdateTimer();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DetachOwner();
        AttachTarget();
        OverlayHost.PlacementTarget = PlacementTarget as VideoSurface;
        SeekPreviewHost.PlacementTarget = PlacementTarget as VideoSurface;
        owner = Window.GetWindow(this);
        if (owner is not null)
        {
            owner.Deactivated += OnOwnerDeactivated;
            owner.Activated += OnOwnerStateChanged;
            owner.IsVisibleChanged += OnTargetVisibilityChanged;
            owner.StateChanged += OnOwnerStateChanged;
            owner.Closed += OnOwnerClosed;
        }
        UpdateTimer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        pointerTimer.Stop();
        HideImmediately();
        OverlayHost.PlacementTarget = null;
        SeekPreviewHost.PlacementTarget = null;
        previewImages = new();
        DetachTarget();
        DetachOwner();
        lastPointer = null;
    }

    private void AttachTarget()
    {
        DetachTarget();
        subscribedTarget = PlacementTarget;
        if (subscribedTarget is null) return;
        subscribedTarget.IsVisibleChanged += OnTargetVisibilityChanged;
        subscribedTarget.SizeChanged += OnTargetSizeChanged;
        if (subscribedTarget is VideoSurface surface) surface.NativeBoundsChanged += OnNativeBoundsChanged;
    }

    private void DetachTarget()
    {
        if (subscribedTarget is null) return;
        subscribedTarget.IsVisibleChanged -= OnTargetVisibilityChanged;
        subscribedTarget.SizeChanged -= OnTargetSizeChanged;
        if (subscribedTarget is VideoSurface surface) surface.NativeBoundsChanged -= OnNativeBoundsChanged;
        subscribedTarget = null;
    }

    private void DetachOwner()
    {
        if (owner is null) return;
        owner.Deactivated -= OnOwnerDeactivated;
        owner.Activated -= OnOwnerStateChanged;
        owner.IsVisibleChanged -= OnTargetVisibilityChanged;
        owner.StateChanged -= OnOwnerStateChanged;
        owner.Closed -= OnOwnerClosed;
        owner = null;
    }

    private void OnOwnerDeactivated(object? sender, EventArgs e)
    {
        HideImmediately();
        UpdateTimer();
    }
    private void OnOwnerStateChanged(object? sender, EventArgs e) => UpdateTimer();
    private void OnOwnerClosed(object? sender, EventArgs e) => OnUnloaded(this, new RoutedEventArgs());
    private void OnTargetVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateTimer();
    private void OnTargetSizeChanged(object sender, SizeChangedEventArgs e) => UpdateOpenPlacement();
    private void OnNativeBoundsChanged(object? sender, EventArgs e) => UpdateOpenPlacement();

    private void UpdateOpenPlacement()
    {
        if (!OverlayHost.IsOpen) return;
        if (!CanDisplay) HideImmediately();
        else UpdatePlacement();
    }

    private void UpdateTimer()
    {
        // Size can still be zero during Loaded; layout finishes before the first sample.
        if (IsLoaded && IsOverlayEnabled && PlacementTarget?.IsVisible == true &&
            IsOwnerAvailable)
            pointerTimer.Start();
        else
        {
            pointerTimer.Stop();
            HideImmediately();
        }
    }

    private void OnPointerTick(object? sender, EventArgs e)
    {
        if (!GetCursorPos(out var point)) return;
        var position = new Point(point.X, point.Y);
        var target = GetPointerSampleTarget(position);
        ProcessPointerSample(
            position,
            target is PointerSampleTarget.Video or PointerSampleTarget.Overlay,
            target == PointerSampleTarget.Overlay,
            Environment.TickCount64);
        SampleSeekHover(position, target == PointerSampleTarget.Overlay);
    }

    // Poll physical movement rather than WPF MouseMove: VLC's child renderer owns mouse messages.
    internal void ProcessPointerSample(Point? position, bool overVideoOrOverlay, long nowMilliseconds)
    {
        ProcessPointerSample(position, overVideoOrOverlay, overReplayOverlay: false, nowMilliseconds);
    }

    internal void ProcessPointerSample(
        Point? position,
        bool overVideoOrOverlay,
        bool overReplayOverlay,
        long nowMilliseconds)
    {
        var moved = position.HasValue && position != lastPointer;
        lastPointer = position;
        if (!CanDisplay) { HideImmediately(); return; }
        if (moved && overVideoOrOverlay) Reveal(nowMilliseconds);
        var pointerParkedOverReplayControls = position.HasValue && overReplayOverlay;
        if (OverlayHost.IsOpen && !fading && nowMilliseconds - lastActivity >= IdleDelay.TotalMilliseconds &&
            !pointerParkedOverReplayControls && !OverlayChrome.IsMouseCaptureWithin &&
            seekTab is null && !keyboardSeeking)
            FadeOut();
    }

    private PointerSampleTarget GetPointerSampleTarget(Point position)
    {
        var hitTester = NativeWindowHitTester.Instance;
        var hit = hitTester.WindowFromPoint((int)position.X, (int)position.Y);
        if (OverlayHost.IsOpen && PresentationSource.FromVisual(OverlayChrome) is HwndSource overlaySource &&
            (hit == overlaySource.Handle || hitTester.IsChild(overlaySource.Handle, hit)))
            return PointerSampleTarget.Overlay;
        if (PlacementTarget is not { IsVisible: true } target ||
            PresentationSource.FromVisual(target) is not HwndSource source) return PointerSampleTarget.None;
        var origin = target.PointToScreen(new Point());
        var end = target.PointToScreen(new Point(target.ActualWidth, target.ActualHeight));
        if (!new Rect(origin, end).Contains(position)) return PointerSampleTarget.None;
        var isOverVideo = target is VideoSurface surface
            ? hit == surface.Handle || hitTester.IsChild(surface.Handle, hit)
            : hit == source.Handle || hitTester.IsChild(source.Handle, hit);
        return isOverVideo ? PointerSampleTarget.Video : PointerSampleTarget.None;
    }

    private void Reveal(long now)
    {
        if (!CanDisplay) return;
        lastActivity = now;
        if (OverlayHost.IsOpen && !fading) return;
        animationVersion++;
        fading = false;
        OverlayChrome.IsHitTestVisible = true;
        var from = OverlayHost.IsOpen ? OverlayChrome.Opacity : 0;
        OverlayHost.Open(UpdatePlacement);
        OverlayChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(120)));
    }

    private void FadeOut()
    {
        HideSeekHover();
        fading = true;
        var version = ++animationVersion;
        var fade = new DoubleAnimation(0, FadeDuration);
        fade.Completed += (_, _) =>
        {
            if (version == animationVersion) HideImmediately();
        };
        OverlayChrome.BeginAnimation(OpacityProperty, fade);
    }

    private void HideImmediately()
    {
        animationVersion++;
        fading = false;
        HideSeekHover();
        CancelSeek();
        OverlayChrome.BeginAnimation(OpacityProperty, null);
        OverlayHost.Close();
    }

    private void UpdatePlacement()
    {
        if (PlacementTarget is not { } target || PresentationSource.FromVisual(target) is null) return;
        var size = OverlayHost.TargetSize;
        if (size.IsEmpty) return;
        var inset = size.Width < 400 ? 8 : 16;
        OverlayChrome.Width = Math.Min(960, Math.Max(1, size.Width - 2 * inset));
        IsCompactLayout = OverlayChrome.Width < 280;
        OverlayChrome.Measure(new Size(OverlayChrome.Width, double.PositiveInfinity));
        var left = (size.Width - OverlayChrome.Width) / 2;
        var top = Math.Max(0, size.Height - OverlayChrome.DesiredSize.Height - inset);
        replayOverlayBounds = new Rect(left, top, OverlayChrome.Width, OverlayChrome.DesiredSize.Height);
        OverlayHost.SetBounds(replayOverlayBounds);
        if (SeekPreviewHost.IsOpen) UpdateSeekHoverPlacement();
    }

    private void BeginSeek()
    {
        if (seekTab is not null || DataContext is not StreamTabViewModel { CanSeekReplay: true } tab) return;
        seekTab = tab;
        tab.BeginReplaySeekPreview(ReplaySeekSlider.Value);
        Reveal(Environment.TickCount64);
    }

    private void OnSeekPointerDown(object sender, MouseButtonEventArgs e)
    {
        // Use the initiating hit, since move-to-point/focus/layout can move the
        // thumb under the pointer before this handler acquires capture.
        var pressedThumb =
            ReplaySeekSlider.Template.FindName("PART_Track", ReplaySeekSlider) is Track { Thumb: { } thumb } &&
            e.OriginalSource is Visual source && (ReferenceEquals(source, thumb) || thumb.IsAncestorOf(source));
        // Track clicks are handled during preview by Slider's move-to-point path.
        // Acquire keyboard focus here as well as capture for the continued gesture.
        ReplaySeekSlider.Focus();
        BeginSeek();
        // Leave thumb presses to Thumb's own capture/drag handling. Capturing the
        // slider during preview redirects mouse-down away from the thumb and loses
        // the user's grab offset inside it.
        if (seekTab is not null && !pressedThumb && !ReplaySeekSlider.IsMouseCaptureWithin)
            ReplaySeekSlider.CaptureMouse();
    }

    private void OnSeekPointerMove(object sender, MouseEventArgs e)
    {
        UpdateSeekHover(e.GetPosition(ReplaySeekSlider));
        // Thumb drags are handled by Slider. A click directly on the track instead captures
        // the slider so the same gesture can continue scrubbing, including outside its bounds.
        if (seekTab is not null && ReferenceEquals(Mouse.Captured, ReplaySeekSlider) &&
            ReplaySeekSlider.Template.FindName("PART_Track", ReplaySeekSlider) is Track track)
        {
            var value = track.ValueFromPoint(e.GetPosition(track));
            if (double.IsFinite(value))
                ReplaySeekSlider.SetCurrentValue(Slider.ValueProperty,
                    Math.Clamp(value, ReplaySeekSlider.Minimum, ReplaySeekSlider.Maximum));
            e.Handled = true;
        }
    }

    private void OnSeekDragStarted(object sender, DragStartedEventArgs e) => BeginSeek();
    private async void OnSeekDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (e.Canceled) CancelSeek();
        else await CommitSeekAsync();
    }
    private async void OnSeekPointerUp(object sender, MouseButtonEventArgs e) => await CommitSeekAsync();

    private void OnSeekLostCapture(object sender, MouseEventArgs e)
    {
        // Thumb releases capture just before DragCompleted; allow its commit to run first.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (seekTab is not null && !keyboardSeeking && !ReplaySeekSlider.IsMouseCaptureWithin) CancelSeek();
        }));
    }

    private void OnSeekKeyDown(object sender, KeyEventArgs e)
    {
        if (IsSeekKey(e.Key))
        {
            BeginSeek();
            keyboardSeeking = seekTab is not null;
        }
    }

    private async void OnSeekKeyUp(object sender, KeyEventArgs e)
    {
        if (!IsSeekKey(e.Key)) return;
        keyboardSeeking = false;
        await CommitSeekAsync();
    }

    private static bool IsSeekKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    private async Task CommitSeekAsync()
    {
        var tab = seekTab;
        var value = ReplaySeekSlider.Value;
        seekTab = null;
        keyboardSeeking = false;
        if (ReferenceEquals(Mouse.Captured, ReplaySeekSlider)) ReplaySeekSlider.ReleaseMouseCapture();
        Reveal(Environment.TickCount64);
        if (tab is null) return;
        if (!ReferenceEquals(tab, DataContext)) { tab.CancelReplaySeekPreview(); return; }
        await tab.CommitReplaySeekPreviewAsync(value);
    }

    private void CancelSeek()
    {
        var tab = seekTab;
        seekTab = null;
        keyboardSeeking = false;
        tab?.CancelReplaySeekPreview();
        if (ReplaySeekSlider.IsMouseCaptureWithin) Mouse.Capture(null);
    }

    internal bool ContainsScreenPoint(int x, int y) => OverlayHost.IsOpen &&
        PresentationSource.FromVisual(OverlayChrome) is not null &&
        new Rect(OverlayChrome.PointToScreen(new Point()),
            OverlayChrome.PointToScreen(new Point(OverlayChrome.ActualWidth, OverlayChrome.ActualHeight)))
            .Contains(new Point(x, y));

    internal static bool IsReplayOverlayWindow(IntPtr hwnd) =>
        HwndSource.FromHwnd(hwnd)?.RootVisual is { } root && ContainsOverlay(root);

    internal static bool IsReplayOverlayInput(object? originalSource)
    {
        // Inline timestamps are Run elements, which aren't Visuals. Resolve their containing
        // TextBlock before checking the source HWND, just as for buttons and the slider.
        var element = originalSource as DependencyObject;
        while (element is not null && element is not Visual)
            element = element is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(element);
        return element is Visual visual && PresentationSource.FromVisual(visual) is HwndSource source &&
            IsReplayOverlayWindow(source.Handle);
    }

    private static bool ContainsOverlay(DependencyObject element)
    {
        if (element is FrameworkElement { Tag: ReplaySeekOverlay }) return true;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (ContainsOverlay(VisualTreeHelper.GetChild(element, i))) return true;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint { public int X; public int Y; }

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out ScreenPoint point);
}

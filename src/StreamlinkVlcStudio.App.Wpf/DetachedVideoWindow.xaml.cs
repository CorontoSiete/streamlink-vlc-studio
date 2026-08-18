using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using StreamlinkVlcStudio.App.Wpf.Controls;
using StreamlinkVlcStudio.App.Wpf.ViewModels;
using StreamlinkVlcStudio.Core.Settings;
using static StreamlinkVlcStudio.App.Wpf.WindowInteropHelpers;

namespace StreamlinkVlcStudio.App.Wpf;

public partial class DetachedVideoWindow : Window, INotifyPropertyChanged
{
    private const int WmCancelMode = 0x001F;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int WmSysCommand = 0x0112;
    private const int WmMouseMove = 0x0200;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmCaptureChanged = 0x0215;
    private const int MkLeftButton = 0x0001;
    private const int HtCaption = 2;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int IdcSizeNwse = 32642;
    private const int IdcSizeNesw = 32643;
    private const int IdcSizeNs = 32645;
    private const int SmCxDoubleClick = 36;
    private const int SmCyDoubleClick = 37;
    private const int SmCxDrag = 68;
    private const int SmCyDrag = 69;
    private const int SmCxSizeFrame = 32;
    private const int SmCySizeFrame = 33;
    private const int SmCxPaddedBorder = 92;
    private const int SmCyPaddedBorder = 92;
    private const int ScMove = 0xF010;
    private const int MinimumBottomResizeGripPixels = 10;
    private const int MinimumCornerResizeGripPixels = 24;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndNoTopmost = new(-2);
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly int WmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private static readonly GridLength VisibleTitleBarHeight = new(34);
    private static readonly GridLength VisibleBottomResizeGripHeight = new(10);
    private static readonly Thickness WindowChromeResizeBorderThickness = new(6);
    private static ITaskbarFullscreenController taskbarFullscreenController = WindowsTaskbarFullscreenController.Instance;
    private readonly Dictionary<StreamTabViewModel, VideoSurface> detachedSurfaces = [];
    private readonly Dictionary<StreamTabViewModel, DetachedVideoItem> videoItemByTab = [];
    private readonly IWindowHitTester windowHitTester;
    private readonly List<StreamTabViewModel> tabs = [];
    private readonly PictureInPictureDragCandidate maximizedWindowMoveCandidate = new();
    private readonly PictureInPictureDragCandidate videoMoveCandidate = new();
    private readonly PictureInPictureWindowMoveSession windowMoveSession = new();
    private bool closeWithoutReattach;
    private bool showTopBar = true;
    private bool streamFullscreen;
    private PictureInPictureFullscreenMode streamFullscreenMode = PictureInPictureFullscreenMode.StreamOnly;
    private bool streamFullscreenPlacementChanging;
    private Rect streamFullscreenRestoreBounds;
    private ResizeMode streamFullscreenRestoreResizeMode;
    private WindowState streamFullscreenRestoreWindowState = WindowState.Normal;
    private bool streamFullscreenRestoreTopmost = true;
    private IntPtr taskbarFullscreenWindowHandle;
    private bool fullscreenNativePlacementPending;
    private StreamTabViewModel? activeTab;
    private string headerTitle = "";
    private string headerStatusText = "";
    private string windowTitle = "";
    private int videoGridRows = VideoGridLayoutCalculator.BaseGridSize;
    private int videoGridColumns = VideoGridLayoutCalculator.BaseGridSize;
    private StreamTabViewModel[] visibleTabs = [];
    private long lastStreamLeftButtonDownAt = long.MinValue;
    private int lastStreamLeftButtonDownX;
    private int lastStreamLeftButtonDownY;

    public DetachedVideoWindow(StreamTabViewModel tab)
        : this([tab], tab)
    {
    }

    public DetachedVideoWindow(
        IReadOnlyList<StreamTabViewModel> tabs,
        StreamTabViewModel? activeTab = null,
        bool showTopBar = true)
        : this(tabs, activeTab, showTopBar, NativeWindowHitTester.Instance)
    {
    }

    internal DetachedVideoWindow(
        IReadOnlyList<StreamTabViewModel> tabs,
        StreamTabViewModel? activeTab,
        bool showTopBar,
        IWindowHitTester windowHitTester)
    {
        if (tabs.Count == 0)
        {
            throw new ArgumentException("At least one stream tab is required.", nameof(tabs));
        }

        foreach (var tab in tabs.Distinct())
        {
            this.tabs.Add(tab);
        }

        this.activeTab = activeTab is not null && this.tabs.Contains(activeTab)
            ? activeTab
            : this.tabs[0];
        this.showTopBar = showTopBar;
        this.windowHitTester = windowHitTester ?? throw new ArgumentNullException(nameof(windowHitTester));
        InitializeComponent();
        DataContext = this;
        foreach (var tab in this.tabs)
        {
            AddVideoTab(tab);
            tab.PropertyChanged += TabOnPropertyChanged;
        }

        UpdateVideoLayout();
        Activated += DetachedVideoWindowActivated;
        SourceInitialized += DetachedVideoWindowSourceInitialized;
        Closed += DetachedVideoWindowClosed;
        IsVisibleChanged += (_, _) => SyncDetachedVideoSurfaces();
        SizeChanged += (_, _) => SyncDetachedVideoSurfaces();
        UpdateWindowTitle();
        UpdateTopmostButton();
        ApplyChromeVisibility();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal static ITaskbarFullscreenController TaskbarFullscreenController
    {
        get => taskbarFullscreenController;
        set => taskbarFullscreenController = value ?? WindowsTaskbarFullscreenController.Instance;
    }

    public IReadOnlyList<StreamTabViewModel> Tabs => tabs;
    public ObservableCollection<DetachedVideoItem> VideoItems { get; } = [];
    public ObservableCollection<DetachedVideoItem> MountedVideoItems { get; } = [];
    public StreamTabViewModel? ActiveTab => activeTab;
    public int TabCount => tabs.Count;
    public bool IsClosing { get; private set; }
    public bool IsStreamFullscreen => streamFullscreen;
    public bool IsTopBarShown => showTopBar;
    internal bool HasVideoMoveCandidate => videoMoveCandidate.IsActive;
    internal bool HasActiveWindowMove =>
        maximizedWindowMoveCandidate.IsActive || windowMoveSession.IsActive;
    internal Func<StreamTabViewModel, int, int, bool>? IsPointerOverOverlayChat { get; set; }
    public string HeaderTitle
    {
        get => headerTitle;
        private set => SetWindowProperty(ref headerTitle, value);
    }

    public string HeaderStatusText
    {
        get => headerStatusText;
        private set => SetWindowProperty(ref headerStatusText, value);
    }

    public string WindowTitle
    {
        get => windowTitle;
        private set => SetWindowProperty(ref windowTitle, value);
    }

    public int VideoGridRows
    {
        get => videoGridRows;
        private set => SetWindowProperty(ref videoGridRows, value);
    }

    public int VideoGridColumns
    {
        get => videoGridColumns;
        private set => SetWindowProperty(ref videoGridColumns, value);
    }

    public double ContentAspectRatio => GetContentAspectRatio();

    public event EventHandler? ReattachRequested;
    public event Action<StreamTabViewModel>? TabActivated;
    public event EventHandler? RestorableBoundsChanged;
    public event EventHandler? VisibleTabsChanged;
    public event Action<StreamTabViewModel, bool>? TopBarVisibilityChanged;
    internal event EventHandler? VideoMoveCandidateChanged;
    public IReadOnlyList<StreamTabViewModel> VisibleTabs => visibleTabs;

    public void BeginInteractiveMove()
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(TryBeginWindowMoveFromCurrentPointer));
    }

    public void AttachVideoSurface()
    {
        foreach (var (tab, surface) in detachedSurfaces)
        {
            surface.SyncNativeBounds();
            if (surface.Handle != IntPtr.Zero)
            {
                tab.SetVideoHandle(surface.Handle);
            }
        }
    }

    public void CloseForTabDisposal()
    {
        closeWithoutReattach = true;
        Close();
    }

    internal bool TryAddTabs(IReadOnlyList<StreamTabViewModel> tabsToAdd, StreamTabViewModel? requestedActiveTab = null)
    {
        var added = false;
        foreach (var tab in tabsToAdd.Distinct())
        {
            if (tabs.Contains(tab))
            {
                continue;
            }

            tabs.Add(tab);
            AddVideoTab(tab);
            tab.PropertyChanged += TabOnPropertyChanged;
            added = true;
        }

        var activeChanged = false;
        if (requestedActiveTab is not null &&
            tabs.Contains(requestedActiveTab) &&
            !ReferenceEquals(activeTab, requestedActiveTab))
        {
            activeTab = requestedActiveTab;
            activeChanged = true;
        }

        if (!added && !activeChanged)
        {
            return false;
        }

        if (added || (activeChanged && streamFullscreen))
        {
            UpdateVideoLayout();
            SyncDetachedVideoSurfaces();
        }

        if (added)
        {
            OnWindowPropertyChanged(nameof(TabCount));
        }

        UpdateWindowTitle();
        UpdateFullscreenButton();
        return true;
    }

    internal bool RemoveTabForTransfer(StreamTabViewModel tab)
    {
        return RemoveTab(tab, clearVideoHandle: true);
    }

    public bool RemoveTabForDisposal(StreamTabViewModel tab)
    {
        return RemoveTab(tab, clearVideoHandle: true);
    }

    private bool RemoveTab(StreamTabViewModel tab, bool clearVideoHandle)
    {
        if (!videoItemByTab.TryGetValue(tab, out var item))
        {
            return false;
        }

        tab.PropertyChanged -= TabOnPropertyChanged;
        if (detachedSurfaces.TryGetValue(tab, out var surface))
        {
            UntrackDetachedSurface(tab, surface, clearVideoHandle);
        }

        videoItemByTab.Remove(tab);
        tabs.Remove(tab);
        VideoItems.Remove(item);
        MountedVideoItems.Remove(item);
        if (ReferenceEquals(activeTab, tab))
        {
            activeTab = tabs.FirstOrDefault();
        }

        UpdateVideoLayout();
        UpdateWindowTitle();
        OnWindowPropertyChanged(nameof(TabCount));
        UpdateFullscreenButton();
        SyncDetachedVideoSurfaces();
        return true;
    }

    public bool TryToggleStreamFullscreenFromScreenClick(int screenX, int screenY)
    {
        if (!IsScreenPointInThisWindow(screenX, screenY) ||
            !IsScreenPointOverStreamArea(screenX, screenY))
        {
            ResetStreamDoubleClickTracking();
            return false;
        }

        var now = Environment.TickCount64;
        var isDoubleClick = IsTrackedStreamDoubleClick(now, screenX, screenY);
        CaptureStreamLeftButtonDown(now, screenX, screenY);
        if (!isDoubleClick)
        {
            return false;
        }

        ResetStreamDoubleClickTracking();
        CancelVideoMoveCandidate();
        NotifyTabActivated(GetTabAtScreenPoint(screenX, screenY) ?? activeTab);
        ToggleStreamFullscreen();
        return true;
    }

    public bool TryActivateTabFromScreenClick(int screenX, int screenY)
    {
        if (!IsScreenPointInThisWindow(screenX, screenY) ||
            GetTabAtScreenPoint(screenX, screenY) is not { } tab)
        {
            return false;
        }

        NotifyTabActivated(tab);
        return true;
    }

    public bool TryRouteMouseWheel(int screenX, int screenY, int delta)
    {
        if (delta == 0 ||
            !IsScreenPointInThisWindow(screenX, screenY) ||
            GetTabAtScreenPoint(screenX, screenY) is not { } tab)
        {
            return false;
        }

        NotifyTabActivated(tab);
        AdjustVolume(tab, delta);
        return true;
    }

    public bool TryBeginBottomResizeFromScreenClick(int screenX, int screenY)
    {
        if (!IsScreenPointInThisWindow(screenX, screenY) ||
            !TryGetBottomResizeHitTest(screenX, screenY, out var hitTest))
        {
            return false;
        }

        BeginNativeResize(hitTest, screenX, screenY);
        return true;
    }

    public bool TryBeginVideoMoveFromScreenClick(int screenX, int screenY)
    {
        if (streamFullscreen ||
            WindowState != WindowState.Normal ||
            !IsScreenPointInThisWindow(screenX, screenY) ||
            !IsScreenPointOverStreamArea(screenX, screenY) ||
            GetTabAtScreenPoint(screenX, screenY) is not { } tab ||
            TryGetBottomResizeHitTest(screenX, screenY, out _))
        {
            CancelVideoMoveCandidate();
            return false;
        }

        if (IsPointerOverOverlayChat?.Invoke(tab, screenX, screenY) == true)
        {
            CancelVideoMoveCandidate();
            return false;
        }

        NotifyTabActivated(tab);
        var wasActive = videoMoveCandidate.IsActive;
        videoMoveCandidate.Begin(screenX, screenY);
        if (!wasActive)
        {
            VideoMoveCandidateChanged?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    public bool TryContinueVideoMove(int screenX, int screenY)
    {
        if (!videoMoveCandidate.TryStartDrag(
                screenX,
                screenY,
                GetSystemMetrics(SmCxDrag),
                GetSystemMetrics(SmCyDrag)))
        {
            return false;
        }

        VideoMoveCandidateChanged?.Invoke(this, EventArgs.Empty);
        ResetStreamDoubleClickTracking();
        return TryBeginWindowMove(screenX, screenY);
    }

    public void CancelVideoMoveCandidate()
    {
        var candidateCancelled = videoMoveCandidate.Cancel();
        EndWindowMove(releaseCapture: true);
        if (!candidateCancelled)
        {
            return;
        }

        VideoMoveCandidateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool TryOpenVideoContextMenu(int screenX, int screenY)
    {
        if (!IsScreenPointInThisWindow(screenX, screenY) ||
            GetTabAtScreenPoint(screenX, screenY) is not { } tab)
        {
            return false;
        }

        NotifyTabActivated(tab);
        ShowTopBarMenuItem.IsChecked = showTopBar;
        var placementPoint = this.ToDeviceIndependentPoint(new Point(screenX, screenY));
        VideoContextMenu.IsOpen = false;
        VideoContextMenu.PlacementTarget = VideoHost;
        VideoContextMenu.Placement = PlacementMode.AbsolutePoint;
        VideoContextMenu.HorizontalOffset = placementPoint.X;
        VideoContextMenu.VerticalOffset = placementPoint.Y;
        VideoContextMenu.IsOpen = true;
        return true;
    }

    public Rect GetRestorableBounds()
    {
        if (streamFullscreen && IsUsableWindowBounds(streamFullscreenRestoreBounds))
        {
            return streamFullscreenRestoreBounds;
        }

        if (WindowState == WindowState.Normal)
        {
            return GetCurrentNormalBounds();
        }

        return IsUsableWindowBounds(RestoreBounds)
            ? RestoreBounds
            : GetCurrentNormalBounds();
    }

    public PictureInPictureFullscreenMode GetRestorableFullscreenMode()
    {
        if (streamFullscreen)
        {
            return streamFullscreenMode;
        }

        return tabs.Count > 1
            ? PictureInPictureFullscreenMode.MultiView
            : PictureInPictureFullscreenMode.StreamOnly;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        NotifyTabActivated(activeTab);

        if (e.ClickCount == 2)
        {
            ToggleTopmost();
            e.Handled = true;
            return;
        }

        var screenPoint = TitleBar.PointToScreen(e.GetPosition(TitleBar));
        _ = TryBeginWindowMove(
            (int)Math.Round(screenPoint.X),
            (int)Math.Round(screenPoint.Y));
        e.Handled = true;
    }

    private void DockButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TopmostButton_Click(object sender, RoutedEventArgs e)
    {
        NotifyTabActivated(activeTab);
        ToggleTopmost();
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        NotifyTabActivated(activeTab);
        ToggleFullscreenButtonMode();
    }

    private void HideTopBarButton_Click(object sender, RoutedEventArgs e)
    {
        SetTopBarVisibility(show: false, persist: true);
    }

    private void ShowTopBarMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetTopBarVisibility(ShowTopBarMenuItem.IsChecked, persist: true);
    }

    private void VideoHost_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (TryGetCursorPos(out var screenPoint) &&
            GetTabAtScreenPoint(screenPoint.X, screenPoint.Y) is { } tab)
        {
            NotifyTabActivated(tab);
            ShowTopBarMenuItem.IsChecked = showTopBar;
            return;
        }

        e.Handled = true;
    }

    private void BottomResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            WindowState != WindowState.Normal ||
            ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize)
        {
            return;
        }

        var point = e.GetPosition(BottomResizeGrip);
        var hitTest = point.X < BottomResizeGrip.ActualWidth / 3
            ? HtBottomLeft
            : point.X >= BottomResizeGrip.ActualWidth * 2 / 3
                ? HtBottomRight
                : HtBottom;
        var screenPoint = BottomResizeGrip.PointToScreen(point);
        BeginNativeResize(
            hitTest,
            (int)Math.Round(screenPoint.X),
            (int)Math.Round(screenPoint.Y));
        e.Handled = true;
    }

    private void VideoSurface_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not VideoSurface surface ||
            surface.Tag is not StreamTabViewModel tab ||
            !videoItemByTab.ContainsKey(tab))
        {
            return;
        }

        if (detachedSurfaces.TryGetValue(tab, out var previousSurface))
        {
            UntrackDetachedSurface(tab, previousSurface, clearVideoHandle: false);
        }

        detachedSurfaces[tab] = surface;
        surface.NativeSetCursorRequested += DetachedSurfaceOnNativeSetCursorRequested;
        surface.NativeMouseLeftButtonDown += DetachedSurfaceOnNativeMouseLeftButtonDown;
        surface.NativeMouseMoved += DetachedSurfaceOnNativeMouseMoved;
        surface.NativeMouseLeftButtonUp += DetachedSurfaceOnNativeMouseLeftButtonUp;
        surface.NativeMouseRightButtonDown += DetachedSurfaceOnNativeMouseRightButtonDown;
        surface.SyncNativeBounds();
        if (surface.Handle != IntPtr.Zero)
        {
            tab.SetVideoHandle(surface.Handle);
        }
    }

    private void VideoSurface_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is VideoSurface surface &&
            surface.Tag is StreamTabViewModel tab &&
            detachedSurfaces.TryGetValue(tab, out var trackedSurface) &&
            ReferenceEquals(surface, trackedSurface))
        {
            UntrackDetachedSurface(tab, surface, clearVideoHandle: true);
        }
    }

    private void VideoSurface_MouseLeftButtonPressed(object? sender, EventArgs e)
    {
        NotifyTabActivated((sender as FrameworkElement)?.Tag as StreamTabViewModel);
    }

    private void VideoSurface_MouseWheelScrolled(object sender, VideoSurfaceMouseWheelEventArgs e)
    {
        if (e.Delta == 0)
        {
            return;
        }

        if (TryGetCursorPos(out var screenPoint) &&
            TryRouteMouseWheel(screenPoint.X, screenPoint.Y, e.Delta))
        {
            return;
        }

        if (sender is FrameworkElement { Tag: StreamTabViewModel tab } &&
            IsVideoSurfaceCurrentlyHovered(tab))
        {
            NotifyTabActivated(tab);
            AdjustVolume(tab, e.Delta);
        }
    }

    private void VideoSurface_MouseLeftButtonDoubleClicked(object? sender, EventArgs e)
    {
        CancelVideoMoveCandidate();
        NotifyTabActivated((sender as FrameworkElement)?.Tag as StreamTabViewModel);
        ToggleStreamFullscreen();
    }

    private void DetachedVideoWindowClosed(object? sender, EventArgs e)
    {
        CancelVideoMoveCandidate();
        ClearTaskbarFullscreen();

        if (!closeWithoutReattach && tabs.Count > 0)
        {
            ReattachRequested?.Invoke(this, EventArgs.Empty);
        }

        foreach (var (tab, surface) in detachedSurfaces.ToArray())
        {
            UntrackDetachedSurface(tab, surface, clearVideoHandle: true);
        }

        Activated -= DetachedVideoWindowActivated;
        foreach (var tab in tabs)
        {
            tab.PropertyChanged -= TabOnPropertyChanged;
        }
    }

    private void DetachedVideoWindowActivated(object? sender, EventArgs e)
    {
        NotifyTabActivated(activeTab);
        if (streamFullscreen)
        {
            ApplyFullscreenNativePlacement();
            MarkTaskbarFullscreen();
            QueueFullscreenNativePlacement();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        IsClosing = true;
        base.OnClosing(e);
        if (e.Cancel)
        {
            IsClosing = false;
            return;
        }

        // Notify the shell while the HWND is still valid. Waiting for Closed can leave the
        // taskbar's fullscreen registration attached to a destroyed handle.
        ClearTaskbarFullscreen();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        NotifyRestorableBoundsChanged();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        SyncDetachedVideoSurfaces();
        NotifyRestorableBoundsChanged();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState != WindowState.Normal || maximizedWindowMoveCandidate.IsActive)
        {
            CancelVideoMoveCandidate();
        }

        ApplyWindowChromeHitTestState();
        if (streamFullscreen)
        {
            ApplyFullscreenNativePlacement();
            MarkTaskbarFullscreen();
            QueueFullscreenNativePlacement();
        }
    }

    private void DetachedVideoWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WindowMessageHook);
        }

        if (streamFullscreen)
        {
            ApplyFullscreenNativePlacement();
            MarkTaskbarFullscreen();
            QueueFullscreenNativePlacement();
        }
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmNcLeftButtonDown && wParam.ToInt32() == HtCaption)
        {
            BeginWindowMoveFromMessagePoint(lParam);
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WmSysCommand && IsCaptionMoveCommand(wParam))
        {
            TryBeginWindowMoveFromCurrentPointer();
            handled = true;
            return IntPtr.Zero;
        }

        if (HasActiveWindowMove)
        {
            if (msg == WmMouseMove)
            {
                if ((wParam.ToInt64() & MkLeftButton) == 0)
                {
                    EndWindowMove(releaseCapture: true);
                }
                else if (TryGetCursorPos(out var screenPoint))
                {
                    ContinueWindowMoveInput(hwnd, screenPoint.X, screenPoint.Y);
                }

                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WmLeftButtonUp || msg == WmCancelMode)
            {
                EndWindowMove(releaseCapture: true);
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WmCaptureChanged)
            {
                EndWindowMove(releaseCapture: false);
                return IntPtr.Zero;
            }
        }

        if (msg == WmGetMinMaxInfo)
        {
            ApplyMonitorMaxInfo(hwnd, lParam, useFullMonitor: streamFullscreen);
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == PictureInPictureWindowSizing.WmMoving && ConstrainWindowMoving(lParam))
        {
            handled = true;
            return new IntPtr(1);
        }

        if (msg == PictureInPictureWindowSizing.WmSizing)
        {
            // Leave WM_SIZING unhandled so the normal non-client resize code applies the
            // rectangle after this hook has adjusted it.
            ConstrainWindowSizing(hwnd, wParam, lParam);
            return IntPtr.Zero;
        }

        if (msg == WmTaskbarCreated && streamFullscreen)
        {
            ApplyFullscreenNativePlacement();
            MarkTaskbarFullscreen(force: true);
            QueueFullscreenNativePlacement();
            return IntPtr.Zero;
        }

        if (msg == WmNcHitTest &&
            TryGetBottomResizeHitTest(
                GetLParamSignedLowWord(lParam),
                GetLParamSignedHighWord(lParam),
                out var hitTest))
        {
            handled = true;
            return new IntPtr(hitTest);
        }

        return IntPtr.Zero;
    }

    private bool ConstrainWindowMoving(IntPtr rectPointer)
    {
        if (streamFullscreen ||
            WindowState != WindowState.Normal ||
            rectPointer == IntPtr.Zero)
        {
            return false;
        }

        var proposed = Marshal.PtrToStructure<NativeRectangle>(rectPointer);
        if (!TryGetMovingMonitorInfo(proposed, out var monitorInfo) ||
            !PictureInPictureWindowSizing.TryConstrainMoveRect(
                proposed,
                monitorInfo.WorkArea,
                out var constrained))
        {
            return false;
        }

        Marshal.StructureToPtr(constrained, rectPointer, fDeleteOld: false);
        return true;
    }

    private static bool TryGetMovingMonitorInfo(
        NativeRectangle proposed,
        out MonitorInfo monitorInfo)
    {
        if (TryGetCursorPos(out var cursorPoint) &&
            TryGetMonitorInfoForPoint(cursorPoint, out monitorInfo))
        {
            return true;
        }

        return TryGetMonitorInfoForRect(proposed, out monitorInfo);
    }

    private void ConstrainWindowSizing(IntPtr hwnd, IntPtr sizingEdgePointer, IntPtr rectPointer)
    {
        var aspectRatio = ContentAspectRatio;
        if (streamFullscreen ||
            WindowState != WindowState.Normal ||
            ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize ||
            rectPointer == IntPtr.Zero ||
            !double.IsFinite(aspectRatio) ||
            aspectRatio <= 0.2 ||
            !TryGetVideoHostWindowInsets(hwnd, out var insets, out var minimumWidth, out var minimumHeight))
        {
            return;
        }

        var proposed = Marshal.PtrToStructure<NativeRectangle>(rectPointer);
        if (!PictureInPictureWindowSizing.TryConstrainRect(
            proposed,
            sizingEdgePointer.ToInt32(),
            aspectRatio,
                insets,
                minimumWidth,
                minimumHeight,
                out var constrained))
        {
            return;
        }

        Marshal.StructureToPtr(constrained, rectPointer, fDeleteOld: false);
    }

    private bool TryGetVideoHostWindowInsets(
        IntPtr hwnd,
        out PictureInPictureWindowInsets insets,
        out int minimumWidth,
        out int minimumHeight)
    {
        insets = default;
        minimumWidth = 0;
        minimumHeight = 0;
        if (hwnd == IntPtr.Zero ||
            !GetWindowRect(hwnd, out var windowBounds) ||
            !VideoHost.IsVisible ||
            VideoHost.ActualWidth <= 0 ||
            VideoHost.ActualHeight <= 0 ||
            PresentationSource.FromVisual(this) is not { CompositionTarget: { } compositionTarget })
        {
            return false;
        }

        var topLeft = VideoHost.PointToScreen(new Point(0, 0));
        var bottomRight = VideoHost.PointToScreen(new Point(VideoHost.ActualWidth, VideoHost.ActualHeight));
        var left = Math.Max(0, (int)Math.Round(topLeft.X) - windowBounds.Left);
        var top = Math.Max(0, (int)Math.Round(topLeft.Y) - windowBounds.Top);
        var right = Math.Max(0, windowBounds.Right - (int)Math.Round(bottomRight.X));
        var bottom = Math.Max(0, windowBounds.Bottom - (int)Math.Round(bottomRight.Y));
        var windowWidth = windowBounds.Right - (long)windowBounds.Left;
        var windowHeight = windowBounds.Bottom - (long)windowBounds.Top;
        if (windowWidth <= left + right || windowHeight <= top + bottom)
        {
            return false;
        }

        var transformToDevice = compositionTarget.TransformToDevice;
        minimumWidth = ToDevicePixels(MinWidth, transformToDevice.M11);
        minimumHeight = ToDevicePixels(MinHeight, transformToDevice.M22);
        insets = new PictureInPictureWindowInsets(left, top, right, bottom);
        return true;
    }

    private static int ToDevicePixels(double value, double scale)
    {
        if (!double.IsFinite(value) || value <= 0 || !double.IsFinite(scale) || scale <= 0)
        {
            return 1;
        }

        return Math.Max(1, (int)Math.Ceiling(value * scale));
    }

    private void DetachedSurfaceOnNativeSetCursorRequested(object? sender, VideoSurfaceNativeMouseEventArgs e)
    {
        if (!TryGetBottomResizeHitTest(e.ScreenX, e.ScreenY, out var hitTest))
        {
            return;
        }

        SetResizeCursor(hitTest);
        e.Handled = true;
        e.Result = new IntPtr(1);
    }

    private void DetachedSurfaceOnNativeMouseLeftButtonDown(object? sender, VideoSurfaceNativeMouseEventArgs e)
    {
        if (TryGetBottomResizeHitTest(e.ScreenX, e.ScreenY, out var hitTest))
        {
            BeginNativeResize(hitTest, e.ScreenX, e.ScreenY);
            e.Handled = true;
            return;
        }

        _ = TryBeginVideoMoveFromScreenClick(e.ScreenX, e.ScreenY);
    }

    private void DetachedSurfaceOnNativeMouseMoved(object? sender, VideoSurfaceNativeMouseEventArgs e)
    {
        if (TryContinueVideoMove(e.ScreenX, e.ScreenY))
        {
            e.Handled = true;
        }
    }

    private void DetachedSurfaceOnNativeMouseLeftButtonUp(object? sender, VideoSurfaceNativeMouseEventArgs e)
    {
        CancelVideoMoveCandidate();
    }

    private void DetachedSurfaceOnNativeMouseRightButtonDown(object? sender, VideoSurfaceNativeMouseEventArgs e)
    {
        if (TryOpenVideoContextMenu(e.ScreenX, e.ScreenY))
        {
            e.Handled = true;
        }
    }

    private bool TryGetBottomResizeHitTest(int x, int y, out int hitTest)
    {
        hitTest = 0;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (WindowState != WindowState.Normal ||
            ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize ||
            hwnd == IntPtr.Zero ||
            !GetWindowRect(hwnd, out var bounds))
        {
            return false;
        }

        var bottomGripHeight = Math.Max(
            MinimumBottomResizeGripPixels,
            GetSystemMetrics(SmCySizeFrame) + GetSystemMetrics(SmCyPaddedBorder));
        if (y < bounds.Bottom - bottomGripHeight || y >= bounds.Bottom)
        {
            return false;
        }

        var cornerGripWidth = Math.Max(
            MinimumCornerResizeGripPixels,
            (GetSystemMetrics(SmCxSizeFrame) + GetSystemMetrics(SmCxPaddedBorder)) * 2);
        if (x < bounds.Left || x >= bounds.Right)
        {
            return false;
        }

        hitTest = x < bounds.Left + cornerGripWidth
            ? HtBottomLeft
            : x >= bounds.Right - cornerGripWidth
                ? HtBottomRight
                : HtBottom;
        return true;
    }

    private void BeginNativeResize(int hitTest, int screenX, int screenY)
    {
        CancelVideoMoveCandidate();
        NotifyTabActivated(GetTabAtScreenPoint(screenX, screenY) ?? activeTab);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(hwnd, WmNcLeftButtonDown, new IntPtr(hitTest), MakeMouseLParam(screenX, screenY));
    }

    private void TryBeginWindowMoveFromCurrentPointer()
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed &&
            TryGetCursorPos(out var screenPoint))
        {
            _ = TryBeginWindowMove(screenPoint.X, screenPoint.Y);
        }
    }

    private void BeginWindowMoveFromMessagePoint(IntPtr lParam)
    {
        if (TryGetCursorPos(out var screenPoint))
        {
            _ = TryBeginWindowMove(screenPoint.X, screenPoint.Y);
            return;
        }

        _ = TryBeginWindowMove(
            GetLParamSignedLowWord(lParam),
            GetLParamSignedHighWord(lParam));
    }

    private bool TryBeginWindowMove(int screenX, int screenY)
    {
        if (streamFullscreen)
        {
            return false;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        EndWindowMove(releaseCapture: true);
        if (WindowState == WindowState.Maximized)
        {
            maximizedWindowMoveCandidate.Begin(screenX, screenY);
            if (TryCaptureWindowMove(hwnd))
            {
                return true;
            }

            maximizedWindowMoveCandidate.Cancel();
            return false;
        }

        if (WindowState != WindowState.Normal || !GetWindowRect(hwnd, out _))
        {
            return false;
        }

        windowMoveSession.Begin(screenX, screenY);
        if (TryCaptureWindowMove(hwnd))
        {
            return true;
        }

        windowMoveSession.End();
        return false;
    }

    private static bool TryCaptureWindowMove(IntPtr hwnd)
    {
        _ = SetCapture(hwnd);
        return GetCapture() == hwnd;
    }

    private bool RestoreWindowForMove(
        IntPtr hwnd,
        int anchorScreenX,
        int anchorScreenY,
        int screenX,
        int screenY)
    {
        if (!GetWindowRect(hwnd, out var maximizedBounds))
        {
            WindowState = WindowState.Normal;
            return WindowState == WindowState.Normal;
        }

        var maximizedWidth = maximizedBounds.Right - (long)maximizedBounds.Left;
        var horizontalAnchor = maximizedWidth > 0
            ? Math.Clamp((anchorScreenX - (double)maximizedBounds.Left) / maximizedWidth, 0, 1)
            : 0.5;
        var transformToDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var titleBarHeight = ToDevicePixels(VisibleTitleBarHeight.Value, transformToDevice.M22);
        var verticalAnchor = Math.Clamp(
            anchorScreenY - (long)maximizedBounds.Top,
            0,
            Math.Max(0, titleBarHeight - 1));

        WindowState = WindowState.Normal;
        if (!GetWindowRect(hwnd, out var restoredBounds))
        {
            return WindowState == WindowState.Normal;
        }

        var restoredWidth = restoredBounds.Right - (long)restoredBounds.Left;
        var restoredHeight = restoredBounds.Bottom - (long)restoredBounds.Top;
        if (restoredWidth <= 0 || restoredHeight <= 0)
        {
            return true;
        }

        var left = (long)Math.Round(screenX - restoredWidth * horizontalAnchor);
        var top = screenY - Math.Min(verticalAnchor, restoredHeight - 1);
        var right = left + restoredWidth;
        var bottom = top + restoredHeight;
        if (left < int.MinValue ||
            top < int.MinValue ||
            right > int.MaxValue ||
            bottom > int.MaxValue ||
            !TryGetMonitorInfoForPoint(
                new WindowPoint { X = screenX, Y = screenY },
                out var monitorInfo) ||
            !PictureInPictureWindowSizing.TryConstrainMoveRect(
                new NativeRectangle
                {
                    Left = (int)left,
                    Top = (int)top,
                    Right = (int)right,
                    Bottom = (int)bottom
                },
                monitorInfo.WorkArea,
                out var constrained))
        {
            return true;
        }

        _ = SetWindowPos(
            hwnd,
            IntPtr.Zero,
            constrained.Left,
            constrained.Top,
            constrained.Right - constrained.Left,
            constrained.Bottom - constrained.Top,
            SwpNoZOrder | SwpNoActivate);
        return true;
    }

    private void ContinueWindowMoveInput(IntPtr hwnd, int screenX, int screenY)
    {
        if (maximizedWindowMoveCandidate.IsActive)
        {
            var anchorScreenX = maximizedWindowMoveCandidate.StartScreenX;
            var anchorScreenY = maximizedWindowMoveCandidate.StartScreenY;
            if (!maximizedWindowMoveCandidate.TryStartDrag(
                    screenX,
                    screenY,
                    GetSystemMetrics(SmCxDrag),
                    GetSystemMetrics(SmCyDrag)))
            {
                return;
            }

            if (!RestoreWindowForMove(
                    hwnd,
                    anchorScreenX,
                    anchorScreenY,
                    screenX,
                    screenY) ||
                GetCapture() != hwnd)
            {
                ReleaseWindowMoveCapture(hwnd);
                return;
            }

            windowMoveSession.Begin(screenX, screenY);
            return;
        }

        ContinueWindowMove(hwnd, screenX, screenY);
    }

    private void ContinueWindowMove(IntPtr hwnd, int screenX, int screenY)
    {
        if (!windowMoveSession.IsActive ||
            hwnd == IntPtr.Zero ||
            !GetWindowRect(hwnd, out var currentBounds) ||
            !TryGetMonitorInfoForPoint(
                new WindowPoint { X = screenX, Y = screenY },
                out var monitorInfo) ||
            !windowMoveSession.TryGetNextBounds(
                currentBounds,
                screenX,
                screenY,
                monitorInfo.WorkArea,
                out var nextBounds) ||
            AreEqual(currentBounds, nextBounds))
        {
            return;
        }

        _ = SetWindowPos(
            hwnd,
            IntPtr.Zero,
            nextBounds.Left,
            nextBounds.Top,
            nextBounds.Right - nextBounds.Left,
            nextBounds.Bottom - nextBounds.Top,
            SwpNoZOrder | SwpNoActivate);
    }

    private void EndWindowMove(bool releaseCapture)
    {
        var moveEnded = windowMoveSession.End();
        var candidateCancelled = maximizedWindowMoveCandidate.Cancel();
        if ((!moveEnded && !candidateCancelled) || !releaseCapture)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        ReleaseWindowMoveCapture(hwnd);
    }

    private static void ReleaseWindowMoveCapture(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && GetCapture() == hwnd)
        {
            ReleaseCapture();
        }
    }

    private static bool AreEqual(NativeRectangle left, NativeRectangle right) =>
        left.Left == right.Left &&
        left.Top == right.Top &&
        left.Right == right.Right &&
        left.Bottom == right.Bottom;

    private static bool IsCaptionMoveCommand(IntPtr wParam)
    {
        var command = unchecked((int)wParam.ToInt64());
        return (command & 0xFFF0) == ScMove &&
            (command & 0x000F) == HtCaption;
    }

    private static void SetResizeCursor(int hitTest)
    {
        var cursorId = hitTest switch
        {
            HtBottomLeft => IdcSizeNesw,
            HtBottomRight => IdcSizeNwse,
            _ => IdcSizeNs
        };

        var cursor = LoadCursor(IntPtr.Zero, new IntPtr(cursorId));
        if (cursor != IntPtr.Zero)
        {
            SetCursor(cursor);
        }
    }

    private void TabOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StreamTabViewModel.Title) or nameof(StreamTabViewModel.StatusText))
        {
            UpdateWindowTitle();
        }
        else if (e.PropertyName == nameof(StreamTabViewModel.VideoAspectRatio))
        {
            OnWindowPropertyChanged(nameof(ContentAspectRatio));
            FitNormalWindowToContent();
        }
    }

    private void ToggleTopmost()
    {
        Topmost = !Topmost;
        ApplyNativeTopmostState();
        QueueNativeTopmostStateSync();
        UpdateTopmostButton();
    }

    public void EnterStreamFullscreen()
    {
        EnterFullscreen(PictureInPictureFullscreenMode.StreamOnly);
    }

    public void EnterMultiViewFullscreen()
    {
        EnterFullscreen(PictureInPictureFullscreenMode.MultiView);
    }

    private void EnterFullscreen(PictureInPictureFullscreenMode mode)
    {
        if (streamFullscreen)
        {
            if (streamFullscreenMode != mode)
            {
                streamFullscreenMode = mode;
                UpdateVideoLayout();
                UpdateFullscreenButton();
                SyncDetachedVideoSurfaces();
                NotifyPictureInPictureWindowLocationChanged();
            }

            return;
        }

        CancelVideoMoveCandidate();
        streamFullscreenMode = mode;
        streamFullscreenRestoreBounds = GetRestorableBounds();
        streamFullscreenRestoreResizeMode = ResizeMode;
        streamFullscreenRestoreWindowState = WindowState;
        streamFullscreenRestoreTopmost = Topmost;
        streamFullscreenPlacementChanging = true;
        try
        {
            if (WindowState != WindowState.Normal)
            {
                WindowState = WindowState.Normal;
            }

            if (IsUsableWindowBounds(streamFullscreenRestoreBounds))
            {
                ApplyWindowBounds(streamFullscreenRestoreBounds);
                TryApplyNativeWindowBounds(streamFullscreenRestoreBounds);
            }

            streamFullscreen = true;
            Topmost = false;
            ApplyNativeTopmostState();
            QueueNativeTopmostStateSync();
            UpdateVideoLayout();
            ApplyChromeVisibility();
            ApplyFullscreenNativePlacement();
            MarkTaskbarFullscreen();
            QueueFullscreenNativePlacement();
        }
        finally
        {
            streamFullscreenPlacementChanging = false;
        }

        SyncDetachedVideoSurfaces();
        NotifyPictureInPictureWindowLocationChanged();
    }

    public void ExitStreamFullscreen()
    {
        if (!streamFullscreen)
        {
            return;
        }

        var restoreBounds = streamFullscreenRestoreBounds;
        var restoreWindowState = streamFullscreenRestoreWindowState;
        streamFullscreenPlacementChanging = true;
        try
        {
            ClearTaskbarFullscreen();
            WindowState = WindowState.Normal;
            if (IsUsableWindowBounds(restoreBounds))
            {
                ApplyWindowBounds(restoreBounds);
                TryApplyNativeWindowBounds(restoreBounds);
            }

            streamFullscreen = false;
            if (streamFullscreenRestoreTopmost)
            {
                // An inactive WPF window can expose Topmost=true before Windows reapplies
                // the HWND's topmost bit. Restore activation before restoring that state.
                Activate();
            }
            Topmost = streamFullscreenRestoreTopmost;
            UpdateVideoLayout();
            ApplyChromeVisibility();
            ResizeMode = streamFullscreenRestoreResizeMode;
            UpdateTopmostButton();

            if (IsUsableWindowBounds(restoreBounds))
            {
                ApplyWindowBounds(restoreBounds);
                TryApplyNativeWindowBounds(restoreBounds);
            }

            if (restoreWindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Maximized;
            }

            ApplyNativeTopmostState();
        }
        finally
        {
            streamFullscreenPlacementChanging = false;
        }

        QueueNativeTopmostStateSync();
        streamFullscreenMode = PictureInPictureFullscreenMode.StreamOnly;
        SyncDetachedVideoSurfaces();
        NotifyRestorableBoundsChanged();
    }

    private void NotifyPictureInPictureWindowLocationChanged()
    {
        if (!IsClosing)
        {
            RestorableBoundsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyFullscreenNativePlacement()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !TryGetMonitorInfo(hwnd, out var monitorInfo))
        {
            return;
        }

        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            monitorInfo.Monitor.Left,
            monitorInfo.Monitor.Top,
            monitorInfo.Monitor.Right - monitorInfo.Monitor.Left,
            monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top,
            SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpShowWindow);
    }

    private void ApplyNativeTopmostState()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // WPF updates its Topmost property before all native placement messages
        // have necessarily completed. Reassert the HWND z-order at the end of
        // fullscreen transitions so the managed and native states cannot drift.
        _ = SetWindowPos(
            hwnd,
            Topmost ? HwndTopmost : HwndNoTopmost,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoActivate);
    }

    private void QueueNativeTopmostStateSync()
    {
        if (IsClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (!IsClosing)
            {
                ApplyNativeTopmostState();
            }
        }));
    }

    private void QueueFullscreenNativePlacement()
    {
        if (fullscreenNativePlacementPending)
        {
            return;
        }

        fullscreenNativePlacementPending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            fullscreenNativePlacementPending = false;
            if (IsClosing || !streamFullscreen)
            {
                return;
            }

            ApplyFullscreenNativePlacement();
            MarkTaskbarFullscreen();
        }));
    }

    private void MarkTaskbarFullscreen(bool force = false)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || (!force && taskbarFullscreenWindowHandle == hwnd))
        {
            return;
        }

        if (taskbarFullscreenWindowHandle != IntPtr.Zero && taskbarFullscreenWindowHandle != hwnd)
        {
            ClearTaskbarFullscreen();
        }

        if (TaskbarFullscreenController.TrySetFullscreen(hwnd, fullscreen: true))
        {
            taskbarFullscreenWindowHandle = hwnd;
        }
    }

    private void ClearTaskbarFullscreen()
    {
        var hwnd = taskbarFullscreenWindowHandle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        taskbarFullscreenWindowHandle = IntPtr.Zero;
        TaskbarFullscreenController.TrySetFullscreen(hwnd, fullscreen: false);
    }

    private void ToggleStreamFullscreen()
    {
        if (streamFullscreen)
        {
            ExitStreamFullscreen();
        }
        else
        {
            EnterStreamFullscreen();
        }
    }

    private void ToggleFullscreenButtonMode()
    {
        if (streamFullscreen)
        {
            ExitStreamFullscreen();
        }
        else
        {
            EnterFullscreen(GetFullscreenButtonMode());
        }
    }

    private PictureInPictureFullscreenMode GetFullscreenButtonMode()
    {
        return tabs.Count > 1
            ? PictureInPictureFullscreenMode.MultiView
            : PictureInPictureFullscreenMode.StreamOnly;
    }

    private void ApplyChromeVisibility()
    {
        var chromeVisible = !streamFullscreen && showTopBar;
        TitleBar.Visibility = chromeVisible ? Visibility.Visible : Visibility.Collapsed;
        BottomResizeGrip.Visibility = chromeVisible ? Visibility.Visible : Visibility.Collapsed;
        TitleBarRow.Height = chromeVisible ? VisibleTitleBarHeight : new GridLength(0);
        BottomResizeGripRow.Height = chromeVisible ? VisibleBottomResizeGripHeight : new GridLength(0);
        ShowTopBarMenuItem.IsChecked = showTopBar;
        if (streamFullscreen)
        {
            ResizeMode = ResizeMode.NoResize;
        }

        UpdateFullscreenButton();
        ApplyWindowChromeHitTestState();
        FitNormalWindowToContent();
        SyncDetachedVideoSurfaces();
    }

    private void FitNormalWindowToContent()
    {
        // Chrome, grid, and detected video-aspect changes must update the outer window too;
        // otherwise the aspect-aware surface exposes the black VideoHost around the stream.
        if (!IsLoaded ||
            streamFullscreen ||
            streamFullscreenPlacementChanging ||
            WindowState != WindowState.Normal)
        {
            return;
        }

        var aspectRatio = ContentAspectRatio;
        if (!double.IsFinite(aspectRatio) || aspectRatio <= 0.2)
        {
            return;
        }

        var currentWidth = GetCurrentWindowLength(ActualWidth, Width);
        if (!double.IsFinite(currentWidth) || currentWidth <= 0)
        {
            return;
        }

        var titleBarHeight = showTopBar ? VisibleTitleBarHeight.Value : 0;
        var fittedSize = PictureInPictureWindowSizing.FitWindowSize(
            new Size(currentWidth, titleBarHeight + currentWidth / aspectRatio),
            aspectRatio,
            leftInset: 0,
            topInset: titleBarHeight,
            rightInset: 0,
            bottomInset: 0,
            MinWidth,
            MinHeight);
        Width = fittedSize.Width;
        Height = fittedSize.Height;
    }

    private void ApplyWindowChromeHitTestState()
    {
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            chrome.CaptionHeight = streamFullscreen || !showTopBar ? 0 : 34;
            chrome.ResizeBorderThickness = streamFullscreen || WindowState == WindowState.Maximized
                ? new Thickness(0)
                : WindowChromeResizeBorderThickness;
        }
    }

    private void UpdateTopmostButton()
    {
        TopmostButton.Opacity = Topmost ? 1.0 : 0.55;
        TopmostButton.ToolTip = Topmost ? "Always on top" : "Keep above other windows";
    }

    private void UpdateFullscreenButton()
    {
        FullscreenButton.Content = streamFullscreen ? "\uE73F" : "\uE740";
        FullscreenButton.ToolTip = streamFullscreen
            ? "Exit fullscreen"
            : tabs.Count > 1
                ? "Fullscreen multiview"
                : "Fullscreen stream";
    }

    private void SetTopBarVisibility(bool show, bool persist)
    {
        if (showTopBar == show)
        {
            return;
        }

        showTopBar = show;
        OnWindowPropertyChanged(nameof(IsTopBarShown));
        ApplyChromeVisibility();
        if (persist && activeTab is { } tab)
        {
            TopBarVisibilityChanged?.Invoke(tab, show);
        }
    }

    private void UpdateWindowTitle()
    {
        if (tabs.Count == 0)
        {
            HeaderTitle = "Picture-in-picture";
            HeaderStatusText = "";
        }
        else if (tabs.Count == 1)
        {
            var tab = activeTab ?? tabs[0];
            HeaderTitle = tab.Title;
            HeaderStatusText = tab.StatusText;
        }
        else
        {
            var tab = activeTab is not null && tabs.Contains(activeTab)
                ? activeTab
                : tabs[0];
            HeaderTitle = $"Multi-stream ({tabs.Count})";
            HeaderStatusText = $"{tab.Title}: {tab.StatusText}";
        }

        WindowTitle = $"{HeaderTitle} - Picture-in-picture";
        Title = WindowTitle;
    }

    private void NotifyTabActivated(StreamTabViewModel? tab)
    {
        if (tab is null || !tabs.Contains(tab))
        {
            return;
        }

        var activeChanged = !ReferenceEquals(activeTab, tab);
        activeTab = tab;
        UpdateWindowTitle();
        if (activeChanged && streamFullscreen)
        {
            UpdateVideoLayout();
        }

        TabActivated?.Invoke(tab);
    }

    private void NotifyRestorableBoundsChanged()
    {
        if (!IsClosing &&
            !streamFullscreenPlacementChanging &&
            !streamFullscreen &&
            WindowState == WindowState.Normal &&
            IsUsableWindowBounds(GetCurrentNormalBounds()))
        {
            RestorableBoundsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void AddVideoTab(StreamTabViewModel tab)
    {
        if (videoItemByTab.ContainsKey(tab))
        {
            return;
        }

        var item = new DetachedVideoItem(tab);
        videoItemByTab[tab] = item;
    }

    private void UpdateVideoLayout()
    {
        var visibleItems = GetVisibleVideoItems();
        SyncMountedVideoItems();
        SyncVideoItems(visibleItems);
        ApplyVideoItemVisibility(visibleItems);
        UpdateVisibleTabs(visibleItems);

        var layout = VideoGridLayoutCalculator.GetLayout(visibleItems.Count);
        VideoGridRows = layout.Rows;
        VideoGridColumns = layout.Columns;
        for (var index = 0; index < visibleItems.Count; index++)
        {
            var placement = VideoGridLayoutCalculator.GetPlacement(index, visibleItems.Count, layout);
            visibleItems[index].SetPlacement(
                placement.Row,
                placement.Column,
                placement.RowSpan,
                placement.ColumnSpan);
        }

        OnWindowPropertyChanged(nameof(ContentAspectRatio));
        FitNormalWindowToContent();
    }

    private List<DetachedVideoItem> GetVisibleVideoItems()
    {
        if (streamFullscreen && streamFullscreenMode == PictureInPictureFullscreenMode.StreamOnly)
        {
            var fullscreenTab = activeTab is not null && tabs.Contains(activeTab)
                ? activeTab
                : tabs.FirstOrDefault();
            return fullscreenTab is not null && videoItemByTab.TryGetValue(fullscreenTab, out var item)
                ? [item]
                : [];
        }

        return tabs
            .Select(tab => videoItemByTab.TryGetValue(tab, out var item) ? item : null)
            .OfType<DetachedVideoItem>()
            .ToList();
    }

    private void SyncMountedVideoItems()
    {
        var mountedItems = tabs
            .Select(tab => videoItemByTab.TryGetValue(tab, out var item) ? item : null)
            .OfType<DetachedVideoItem>()
            .ToList();

        for (var index = 0; index < MountedVideoItems.Count; index++)
        {
            if (!mountedItems.Contains(MountedVideoItems[index]))
            {
                MountedVideoItems.RemoveAt(index);
                index--;
            }
        }

        for (var index = 0; index < mountedItems.Count; index++)
        {
            var item = mountedItems[index];
            var currentIndex = MountedVideoItems.IndexOf(item);
            if (currentIndex < 0)
            {
                MountedVideoItems.Insert(index, item);
            }
            else if (currentIndex != index)
            {
                MountedVideoItems.Move(currentIndex, index);
            }
        }
    }

    private void SyncVideoItems(IReadOnlyList<DetachedVideoItem> visibleItems)
    {
        for (var index = 0; index < VideoItems.Count; index++)
        {
            if (!visibleItems.Contains(VideoItems[index]))
            {
                VideoItems.RemoveAt(index);
                index--;
            }
        }

        for (var index = 0; index < visibleItems.Count; index++)
        {
            var item = visibleItems[index];
            var currentIndex = VideoItems.IndexOf(item);
            if (currentIndex < 0)
            {
                VideoItems.Insert(index, item);
            }
            else if (currentIndex != index)
            {
                VideoItems.Move(currentIndex, index);
            }
        }
    }

    private void ApplyVideoItemVisibility(IReadOnlyList<DetachedVideoItem> visibleItems)
    {
        var visibleSet = visibleItems.ToHashSet();
        foreach (var item in MountedVideoItems)
        {
            item.IsVisible = visibleSet.Contains(item);
        }
    }

    private void UpdateVisibleTabs(IReadOnlyList<DetachedVideoItem> visibleItems)
    {
        var updatedVisibleTabs = visibleItems
            .Select(item => item.Tab)
            .ToArray();
        if (visibleTabs.SequenceEqual(updatedVisibleTabs))
        {
            return;
        }

        visibleTabs = updatedVisibleTabs;
        VisibleTabsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SyncDetachedVideoSurfaces()
    {
        foreach (var surface in detachedSurfaces.Values)
        {
            surface.SyncNativeBounds();
        }
    }

    private void UntrackDetachedSurface(StreamTabViewModel tab, VideoSurface surface, bool clearVideoHandle)
    {
        if (detachedSurfaces.TryGetValue(tab, out var trackedSurface) &&
            ReferenceEquals(surface, trackedSurface))
        {
            detachedSurfaces.Remove(tab);
        }

        surface.NativeSetCursorRequested -= DetachedSurfaceOnNativeSetCursorRequested;
        surface.NativeMouseLeftButtonDown -= DetachedSurfaceOnNativeMouseLeftButtonDown;
        surface.NativeMouseMoved -= DetachedSurfaceOnNativeMouseMoved;
        surface.NativeMouseLeftButtonUp -= DetachedSurfaceOnNativeMouseLeftButtonUp;
        surface.NativeMouseRightButtonDown -= DetachedSurfaceOnNativeMouseRightButtonDown;
        if (clearVideoHandle)
        {
            tab.ClearVideoHandle(surface.Handle);
        }
    }

    private StreamTabViewModel? GetTabAtScreenPoint(int screenX, int screenY)
    {
        foreach (var (tab, surface) in detachedSurfaces)
        {
            if (surface.IsVisible &&
                surface.ActualWidth > 0 &&
                surface.ActualHeight > 0 &&
                IsScreenPointOverElement(surface, screenX, screenY))
            {
                return tab;
            }
        }

        return null;
    }

    private bool IsVideoSurfaceCurrentlyHovered(StreamTabViewModel tab)
    {
        if (!detachedSurfaces.TryGetValue(tab, out var surface) ||
            !surface.IsMouseOver)
        {
            return false;
        }

        return true;
    }

    private void AdjustVolume(StreamTabViewModel tab, int delta)
    {
        VolumeOverlay.AdjustVolume(tab, delta, VolumeOsd, ResolveVolumeOsdTarget(tab));
    }

    private UIElement ResolveVolumeOsdTarget(StreamTabViewModel tab)
    {
        return detachedSurfaces.TryGetValue(tab, out var surface) && surface.IsVisible
            ? surface
            : VideoHost;
    }

    private bool IsScreenPointOverStreamArea(int screenX, int screenY)
    {
        return IsVisible &&
            VideoHost.IsVisible &&
            VideoHost.ActualWidth > 0 &&
            VideoHost.ActualHeight > 0 &&
            IsScreenPointOverElement(VideoHost, screenX, screenY);
    }

    /// <summary>
    /// True when this window is what the user would actually hit at the screen point. Unlike the
    /// element bounds checks this respects z-order, so a window buried behind another one never
    /// claims the point. Every route fed by the app-wide mouse hook has to go through here first:
    /// those events arrive for clicks anywhere on the desktop, and acting on one that belongs to
    /// another window would drag, resize, or raise this window out from under the user.
    /// </summary>
    private bool IsScreenPointInThisWindow(int screenX, int screenY)
    {
        if (!IsVisible)
        {
            return false;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        // Root-owner matching accepts our own popups (volume OSD, context menu), which are
        // separate owned top-level windows that legitimately float over this window's surfaces.
        return WindowHitTestPolicy.IsPointInWindow(
            windowHitTester,
            hwnd,
            screenX,
            screenY,
            includeOwnedPopups: true);
    }

    internal bool ContainsScreenPoint(int screenX, int screenY)
    {
        return IsScreenPointInThisWindow(screenX, screenY);
    }

    private bool IsTrackedStreamDoubleClick(long now, int screenX, int screenY)
    {
        if (lastStreamLeftButtonDownAt == long.MinValue)
        {
            return false;
        }

        var elapsed = now - lastStreamLeftButtonDownAt;
        return elapsed >= 0 &&
            elapsed <= GetDoubleClickTime() &&
            Math.Abs(screenX - lastStreamLeftButtonDownX) <= GetSystemMetrics(SmCxDoubleClick) &&
            Math.Abs(screenY - lastStreamLeftButtonDownY) <= GetSystemMetrics(SmCyDoubleClick);
    }

    private void CaptureStreamLeftButtonDown(long now, int screenX, int screenY)
    {
        lastStreamLeftButtonDownAt = now;
        lastStreamLeftButtonDownX = screenX;
        lastStreamLeftButtonDownY = screenY;
    }

    private void ResetStreamDoubleClickTracking()
    {
        lastStreamLeftButtonDownAt = long.MinValue;
        lastStreamLeftButtonDownX = 0;
        lastStreamLeftButtonDownY = 0;
    }

    private Rect GetCurrentNormalBounds()
    {
        if (TryGetNativeWindowBounds(out var nativeBounds))
        {
            return nativeBounds;
        }

        var width = GetCurrentWindowLength(ActualWidth, Width);
        var height = GetCurrentWindowLength(ActualHeight, Height);
        return new Rect(new Point(Left, Top), new Size(width, height));
    }

    private bool TryGetNativeWindowBounds(out Rect bounds)
    {
        bounds = default;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        bounds = this.ToDeviceIndependentRect(rect);
        return IsUsableWindowBounds(bounds);
    }

    private static double GetCurrentWindowLength(double actualLength, double configuredLength)
    {
        return double.IsFinite(actualLength) && actualLength > 0
            ? actualLength
            : configuredLength;
    }

    private void ApplyWindowBounds(Rect bounds)
    {
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    private bool TryApplyNativeWindowBounds(Rect bounds)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !IsUsableWindowBounds(bounds))
        {
            return false;
        }

        var topLeft = this.ToDevicePoint(new Point(bounds.Left, bounds.Top));
        var bottomRight = this.ToDevicePoint(new Point(bounds.Right, bounds.Bottom));
        var left = (int)Math.Round(topLeft.X);
        var top = (int)Math.Round(topLeft.Y);
        var width = Math.Max(1, (int)Math.Round(bottomRight.X - topLeft.X));
        var height = Math.Max(1, (int)Math.Round(bottomRight.Y - topLeft.Y));
        return SetWindowPos(
            hwnd,
            IntPtr.Zero,
            left,
            top,
            width,
            height,
            SwpNoZOrder | SwpNoActivate);
    }

    private double GetContentAspectRatio()
    {
        var ratios = VideoItems
            .Select(item => item.Tab.VideoAspectRatio)
            .Where(ratio => double.IsFinite(ratio) && ratio > 0.2)
            .Order()
            .ToArray();
        var cellAspectRatio = ratios.Length switch
        {
            0 => 16.0 / 9.0,
            var count when count % 2 == 1 => ratios[count / 2],
            var count => (ratios[count / 2 - 1] + ratios[count / 2]) / 2
        };

        return cellAspectRatio * Math.Max(1, VideoGridColumns) / Math.Max(1, VideoGridRows);
    }

    private bool SetWindowProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnWindowPropertyChanged(propertyName);
        return true;
    }

    private void OnWindowPropertyChanged(string? propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static int GetLParamSignedLowWord(IntPtr lParam)
    {
        var value = unchecked((long)lParam);
        return unchecked((short)(value & 0xFFFF));
    }

    private static int GetLParamSignedHighWord(IntPtr lParam)
    {
        var value = unchecked((long)lParam);
        return unchecked((short)((value >> 16) & 0xFFFF));
    }

    private static IntPtr MakeMouseLParam(int x, int y)
    {
        return new IntPtr(unchecked((short)x & 0xFFFF | ((short)y << 16)));
    }

    public sealed class DetachedVideoItem : ObservableObject
    {
        private int row;
        private int column;
        private int rowSpan = 1;
        private int columnSpan = 1;
        private bool isVisible = true;

        public DetachedVideoItem(StreamTabViewModel tab)
        {
            Tab = tab;
        }

        public StreamTabViewModel Tab { get; }

        public int Row
        {
            get => row;
            private set => SetProperty(ref row, value);
        }

        public int Column
        {
            get => column;
            private set => SetProperty(ref column, value);
        }

        public int RowSpan
        {
            get => rowSpan;
            private set => SetProperty(ref rowSpan, value);
        }

        public int ColumnSpan
        {
            get => columnSpan;
            private set => SetProperty(ref columnSpan, value);
        }

        public bool IsVisible
        {
            get => isVisible;
            set => SetProperty(ref isVisible, value);
        }

        public void SetPlacement(int row, int column, int rowSpan, int columnSpan)
        {
            Row = Math.Max(0, row);
            Column = Math.Max(0, column);
            RowSpan = Math.Max(1, rowSpan);
            ColumnSpan = Math.Max(1, columnSpan);
        }
    }

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out NativeRectangle rect);

    [LibraryImport("user32", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterWindowMessage(string message);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReleaseCapture();

    [LibraryImport("user32")]
    private static partial IntPtr SetCapture(IntPtr hwnd);

    [LibraryImport("user32")]
    private static partial IntPtr GetCapture();

    [LibraryImport("user32", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32", EntryPoint = "LoadCursorW", SetLastError = true)]
    private static partial IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [LibraryImport("user32")]
    private static partial IntPtr SetCursor(IntPtr cursor);

    [LibraryImport("user32")]
    private static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TryGetCursorPos(out WindowPoint point);

    [LibraryImport("user32")]
    private static partial uint GetDoubleClickTime();
}

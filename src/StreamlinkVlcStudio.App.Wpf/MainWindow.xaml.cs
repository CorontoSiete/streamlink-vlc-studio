using System.Buffers.Binary;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using StreamlinkVlcStudio.App.Wpf.Chat;
using StreamlinkVlcStudio.App.Wpf.Controls;
using StreamlinkVlcStudio.App.Wpf.Notifications;
using StreamlinkVlcStudio.App.Wpf.ViewModels;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Logging;
using StreamlinkVlcStudio.Infrastructure.Processes;
using StreamlinkVlcStudio.Infrastructure.Replay;
using StreamlinkVlcStudio.Infrastructure.Settings;
using StreamlinkVlcStudio.Infrastructure.Streamlink;
using StreamlinkVlcStudio.Infrastructure.Twitch;
using StreamlinkVlcStudio.Infrastructure.Updates;
using StreamlinkVlcStudio.Infrastructure.Vlc;
using StreamlinkVlcStudio.Infrastructure.Viewers;
using StreamlinkVlcStudio.App.Wpf.Themes;
using static StreamlinkVlcStudio.App.Wpf.WindowInteropHelpers;

namespace StreamlinkVlcStudio.App.Wpf;

public partial class MainWindow : Window
{
    private enum FullscreenMode
    {
        None,
        StreamOnly,
        MultiView,
        Theatre
    }

    private const int WmLeftButtonUp = 0x0202;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const int WmMouseMove = 0x0200;
    private const int WmRightButtonUp = 0x0205;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmAppTrayIcon = 0x8001;
    private const int SmCxDoubleClick = 36;
    private const int SmCyDoubleClick = 37;
    private const int SmCxDrag = 68;
    private const int SmCyDrag = 69;
    private const int VkLeftButton = 0x01;
    private const int VkControl = 0x11;
    private const int VkLeftControl = 0xA2;
    private const int VkRightControl = 0xA3;
    private const int TrayCommandOpen = 1001;
    private const int TrayCommandExit = 1002;
    private const uint TrayIconId = 1;
    private const int NimAdd = 0x00000000;
    private const int NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x00000002;
    private const uint TpmReturnCommand = 0x00000100;
    private const int IdiApplication = 32512;
    private const double ChatBottomFollowTolerance = 2;
    private static readonly HotkeySettings DefaultHotkeys = new();
    private const double ChatPixelsPerWheelNotch = 54;
    private const double TabPixelsPerWheelNotch = 96;
    private const double TitleBarChromeCaptionHeight = 36;
    private const double BrowseCategoryLoadMoreBottomThreshold = 120;
    private const double TabDetachOuterMargin = 10;
    private const double DetachedWindowDefaultWidth = 520;
    private const double DetachedWindowTitleBarHeight = 34;
    private const double DetachedWindowCascadeOffset = 36;
    private const double DetachedWindowCascadeDuplicateTolerance = 24;
    private const int DetachedWindowCascadeAttempts = 12;
    private static readonly Thickness WindowChromeResizeBorderThickness = new(6);
    private const int DefaultOverlayChatX = 24;
    private const int DefaultOverlayChatY = 24;
    private const int DefaultOverlayChatHeight = 292;
    private const double OverlayChatHitInsetPixels = 3;
    private const int OverlayMessagePadding = 8;
    private const int OverlayChatInputHeight = 30;
    private const int OverlayChatInputGap = 6;
    private const int OverlayButtonMargin = 6;
    private const int OverlayHideButtonWidth = 58;
    private const int OverlayHideButtonHeight = 22;
    private const int VideoReorderPollIntervalMilliseconds = 16;
    private static readonly TimeSpan HomeAutoScrollInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan BrowserClickDuplicateWindow = TimeSpan.FromSeconds(8);
    private static readonly int WmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private static ITaskbarFullscreenController taskbarFullscreenController = WindowsTaskbarFullscreenController.Instance;
    private static readonly HashSet<string> SupportedBrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "arc",
        "brave",
        "chrome",
        "chromium",
        "firefox",
        "msedge",
        "opera",
        "opera_gx",
        "vivaldi"
    };

    private readonly Dictionary<StreamTabViewModel, bool> fullscreenChatVisibility = [];
    private readonly Dictionary<StreamTabViewModel, bool> fullscreenDockedChatPanelVisibility = [];
    private readonly Dictionary<StreamTabViewModel, VideoSurface> videoSurfaces = [];
    private readonly Dictionary<StreamTabViewModel, DetachedVideoWindow> detachedWindows = [];
    private readonly IWindowHitTester windowHitTester;
    private MainViewModel? viewModel;
    private ISettingsService? settingsService;
    private IAppLogger? appLogger;
    private KickOfficialChatReplayStore? kickOfficialChatReplayStore;
    private ReplayChatProvider? replayChatProvider;
    private KickChatHistoryProvider? kickChatHistoryProvider;
    private BrowserCaptureServer? browserCaptureServer;
    private KickWebhookChatServer? kickWebhookChatServer;
    private KickEventSubscriptionService? kickEventSubscriptionService;
    private ToastLiveNotificationService? liveNotificationService;
    private LowLevelMouseHookPump? mouseHookPump;
    private DispatcherTimer? videoReorderPollTimer;
    private DispatcherTimer? homeAutoScrollTimer;
    private IntPtr windowHandle;
    private IntPtr taskbarFullscreenWindowHandle;
    private IntPtr trayIconHandle;
    private ScrollViewer? dockedChatScrollViewer;
    private ScrollViewer? homeAutoScrollViewer;
    private string lastDetectedStreamUrl = "";
    private DateTimeOffset lastDetectedStreamAt = DateTimeOffset.MinValue;
    private bool suppressNextBrowserMouseUp;
    private bool browserClickFallbackEnabled;
    private bool exitRequested;
    private bool trayIconVisible;
    private bool destroyTrayIconHandle;
    private bool fullscreen;
    private bool fullscreenChatStateCaptured;
    private bool closeConfirmed;
    private bool shutdownStarted;
    private bool dockedChatScrollPending;
    private bool dockedChatForceScrollPending;
    private bool dockedChatShouldFollowBottom = true;
    private bool dockedChatManualScrollOverride;
    private bool dockedChatScrollThumbDragging;
    private bool dockedChatAnchorRestorePending;
    private long lastVideoLeftButtonDownAt = long.MinValue;
    private int lastVideoLeftButtonDownX;
    private int lastVideoLeftButtonDownY;
    private object? dockedChatAnchorItem;
    private FrameworkElement? tabDetachDragSource;
    private StreamTabViewModel? tabDetachDragTab;
    private StreamTabViewModel? tabDetachDragMergeTarget;
    private StreamTabViewModel[] tabDetachDragTabs = [];
    private StreamTabViewModel? videoReorderDragTab;
    private Func<bool> isLeftMouseButtonPressed = IsLeftMouseButtonPressed;
    private Func<bool> isControlModifierPressed = IsControlModifierPressed;
    private Func<NativePoint?> getVideoReorderCursorScreenPoint = GetCursorScreenPoint;
    private Point tabDetachDragStartPoint;
    private NativePoint tabDetachDragStartScreenPoint;
    private NativePoint videoReorderDragStartScreenPoint;
    private Point homeAutoScrollAnchorPoint;
    private Point homeAutoScrollCursorPoint;
    private bool videoReorderDragStarted;
    private bool videoReorderDragReordered;
    private bool videoReorderPollLeftButtonWasPressed;
    private bool tabStripReorderDragReordered;
    private bool tabDetachDragStartedWithControlModifier;
    private bool replaySeekPointerCommitPending;
    private volatile bool hasActiveLowLevelMouseMoveRoute;
    private readonly SemaphoreSlim kickWebhookLifecycleGate = new(1, 1);
    private readonly bool setupRequested;
    private int kickWebhookActiveSettingsPort = -1;
    private double dockedChatAnchorTop;
    private long homeAutoScrollLastTickTimestamp;
    private Cursor? homeAutoScrollPreviousCursor;
    private FullscreenMode fullscreenMode = FullscreenMode.None;
    private WindowState previousWindowState;
    private WindowStyle previousWindowStyle;
    private ResizeMode previousResizeMode;
    private Rect previousWindowBounds;
    private bool previousTopmost;
    private GridLength previousTitleRowHeight;
    private GridLength previousTopControlsRowHeight;
    private ChatLayout? previousChatLayout;

    public MainWindow(bool setupRequested = false)
        : this(setupRequested, NativeWindowHitTester.Instance)
    {
    }

    internal MainWindow(bool setupRequested, IWindowHitTester windowHitTester)
    {
        this.setupRequested = setupRequested;
        this.windowHitTester = windowHitTester ?? throw new ArgumentNullException(nameof(windowHitTester));
        InitializeComponent();
        ApplyWindowChromeHitTestState();
        ((INotifyCollectionChanged)DockedChatListBox.Items).CollectionChanged += DockedChatItemsOnCollectionChanged;
        DockedChatListBox.Loaded += (_, _) =>
        {
            EnsureDockedChatScrollViewer();
            QueueDockedChatScrollToBottom(force: true);
        };
        DockedChatPanel.IsVisibleChanged += (_, _) =>
        {
            if (DockedChatPanel.IsVisible)
            {
                LockDockedChatToBottom();
                QueueDockedChatScrollToBottom(force: true);
            }
        };
        SourceInitialized += MainWindowSourceInitialized;
        Loaded += MainWindowLoaded;
        Activated += MainWindowActivated;
        StateChanged += MainWindowStateChanged;
        PreviewMouseDown += MainWindowPreviewMouseDown;
        PreviewGotKeyboardFocus += MainWindowPreviewGotKeyboardFocus;
        PreviewMouseMove += MainWindowPreviewMouseMove;
        PreviewMouseLeftButtonUp += MainWindowPreviewMouseLeftButtonUp;
        PreviewKeyDown += MainWindowPreviewKeyDown;
        Closing += MainWindowClosing;
        Closed += MainWindowClosed;

        // The replay seek slider has IsMoveToPointEnabled, so its built-in class handler marks
        // PreviewMouseLeftButtonDown as handled when the track is clicked. Register with
        // handledEventsToo so begin-preview still arms a click-to-seek, not just thumb drags.
        ReplaySeekSlider.AddHandler(
            PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(ReplaySeekSlider_BeginPreview),
            handledEventsToo: true);
    }

    internal static ITaskbarFullscreenController TaskbarFullscreenController
    {
        get => taskbarFullscreenController;
        set => taskbarFullscreenController = value ?? WindowsTaskbarFullscreenController.Instance;
    }

    private void MainWindowSourceInitialized(object? sender, EventArgs e)
    {
        windowHandle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(windowHandle)?.AddHook(WindowMessageHook);
        InitializeTrayIcon();
        InstallMouseWheelHook();
        StartVideoReorderPolling();
        if (ShouldMarkTaskbarFullscreen())
        {
            MarkTaskbarFullscreen();
        }
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            ApplyMonitorMaxInfo(hwnd, lParam, useFullMonitor: fullscreen && fullscreenMode != FullscreenMode.Theatre);
            handled = true;
        }
        else if (msg == WmTaskbarCreated && ShouldMarkTaskbarFullscreen())
        {
            MarkTaskbarFullscreen(force: true);
        }
        else if (msg == WmAppTrayIcon)
        {
            HandleTrayIconMessage(lParam);
            handled = true;
        }
        else if (msg == WmMouseMove &&
            (tabDetachDragTab is not null || videoReorderDragTab is not null))
        {
            var screenPoint = GetCursorScreenPoint() ?? ClientMessagePointToNativeScreenPoint(lParam);
            var handledDrag = TryContinueVideoReorderDrag(screenPoint);
            if (tabDetachDragTab is not null &&
                TryContinueTabDetachDrag(screenPoint, continueDrag: true))
            {
                handledDrag = true;
            }

            handled = handledDrag;
        }
        else if (msg == WmLeftButtonUp &&
            (tabDetachDragTab is not null || videoReorderDragTab is not null))
        {
            var screenPoint = GetCursorScreenPoint() ?? ClientMessagePointToNativeScreenPoint(lParam);
            var handledDrag = TryCompleteVideoReorderDrag(screenPoint);
            if (TryCompleteTabDetachDrag(screenPoint))
            {
                handledDrag = true;
            }

            ClearVideoReorderDrag();
            ClearTabDetachDrag();
            handled = handledDrag;
        }

        return IntPtr.Zero;
    }

    private void MainWindowStateChanged(object? sender, EventArgs e)
    {
        ApplyWindowChromeHitTestState();
        UpdateMaximizeRestoreButton();
        if (ShouldMarkTaskbarFullscreen())
        {
            MarkTaskbarFullscreen();
        }
    }

    private void MainWindowActivated(object? sender, EventArgs e)
    {
        if (ShouldMarkTaskbarFullscreen())
        {
            MarkTaskbarFullscreen();
        }
    }

    private void MainWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is HotkeyRecorderButton { IsCapturingInput: true })
        {
            return;
        }

        var hotkeys = viewModel?.Settings.Hotkeys ?? DefaultHotkeys;
        var key = HotkeyGesture.GetEventKey(e);
        var modifiers = Keyboard.Modifiers;
        var dismissShortcutPressed = HotkeyBindingPolicy.Matches(
            hotkeys,
            AppHotkeyAction.DismissFullscreenOrAutoScroll,
            key,
            modifiers);
        var dismissShortcutSuppressed = HotkeyBindingPolicy.ShouldSuppressForTextInput(
            hotkeys,
            AppHotkeyAction.DismissFullscreenOrAutoScroll,
            Keyboard.FocusedElement);

        if (homeAutoScrollViewer is not null && dismissShortcutPressed && !dismissShortcutSuppressed)
        {
            ClearHomeAutoScroll();
            e.Handled = true;
            return;
        }

        if (fullscreen && dismissShortcutPressed && !dismissShortcutSuppressed)
        {
            ExitFullscreenMode();
            e.Handled = true;
            return;
        }

        if (ReplaySeekBarShortcutKeyPolicy.ShouldHandle(key, modifiers, hotkeys) &&
            !HotkeyBindingPolicy.ShouldSuppressForTextInput(
                hotkeys,
                AppHotkeyAction.ToggleReplaySeekBar,
                Keyboard.FocusedElement))
        {
            TryExecuteReplaySeekBarShortcut(viewModel);
            e.Handled = true;
            return;
        }

        var tabAction = HotkeyBindingPolicy.Matches(
            hotkeys,
            AppHotkeyAction.PreviousTab,
            key,
            modifiers)
            ? AppHotkeyAction.PreviousTab
            : HotkeyBindingPolicy.Matches(
                hotkeys,
                AppHotkeyAction.NextTab,
                key,
                modifiers)
                ? AppHotkeyAction.NextTab
                : (AppHotkeyAction?)null;
        if (tabAction is null ||
            HotkeyBindingPolicy.ShouldSuppressForTextInput(
                hotkeys,
                tabAction.Value,
                Keyboard.FocusedElement) ||
            !TabNavigationKeyPolicy.CanNavigate(
                fullscreen,
                fullscreenMode != FullscreenMode.None,
                viewModel?.IsSettingsOpen == true))
        {
            return;
        }

        var direction = tabAction == AppHotkeyAction.PreviousTab ? -1 : 1;
        if (viewModel?.SelectAdjacentTab(direction) == true)
        {
            if (fullscreen)
            {
                ApplyFullscreenSelectedTabState();
            }

            e.Handled = true;
        }
    }

    internal static bool TryExecuteReplaySeekBarShortcut(MainViewModel? viewModel)
    {
        var command = viewModel?.ToggleReplaySeekBarCommand;
        if (command?.CanExecute(null) != true)
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    private void HotkeyRecorder_GestureChanging(object? sender, HotkeyGestureChangingEventArgs e)
    {
        if (sender is not HotkeyRecorderButton { Tag: string actionName } recorder ||
            !Enum.TryParse(actionName, ignoreCase: false, out AppHotkeyAction action) ||
            !Enum.IsDefined(action))
        {
            e.Cancel = true;
            return;
        }

        var currentViewModel = viewModel ?? recorder.DataContext as MainViewModel;
        if (currentViewModel is null)
        {
            e.Cancel = true;
            return;
        }

        _ = HotkeyBindingPolicy.SwapConflictingBinding(
            currentViewModel.Settings.Hotkeys,
            action,
            e.PreviousGesture,
            e.NewGesture);
    }

    private void ResetHotkeysButton_Click(object sender, RoutedEventArgs e)
    {
        var currentViewModel = viewModel ?? (sender as FrameworkElement)?.DataContext as MainViewModel;
        currentViewModel?.Settings.Hotkeys.ResetToDefaults();
    }

    private void MainWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        ReleaseNativeOverlayChatInputFocusForWpfTextInput(e.OriginalSource);

        if (TryReturnToBrowseCategoriesFromMouseButton(e.ChangedButton))
        {
            e.Handled = true;
            return;
        }

        if (viewModel?.IsStreamSearchPanelVisible != true)
        {
            return;
        }

        if (IsPointInsideElement(HomeSearchAnchor, e.GetPosition(HomeSearchAnchor)))
        {
            return;
        }

        viewModel.DismissStreamSearchDropdown();
    }

    private void MainWindowPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        ReleaseNativeOverlayChatInputFocusForWpfTextInput(e.NewFocus);
    }

    private void ReleaseNativeOverlayChatInputFocusForWpfTextInput(object? candidate)
    {
        if (!IsWpfTextInput(candidate))
        {
            return;
        }

        viewModel?.ReleaseNativeOverlayChatInputFocus();
    }

    private static bool IsWpfTextInput(object? candidate)
    {
        if (candidate is TextBoxBase or PasswordBox)
        {
            return true;
        }

        return candidate is DependencyObject dependencyObject &&
            (dependencyObject is Visual ||
                dependencyObject is System.Windows.Media.Media3D.Visual3D) &&
            (FindVisualParent<TextBoxBase>(dependencyObject) is not null ||
                FindVisualParent<PasswordBox>(dependencyObject) is not null);
    }

    internal static bool IsBrowseBackMouseButton(MouseButton changedButton)
    {
        return changedButton == MouseButton.XButton1;
    }

    private bool TryReturnToBrowseCategoriesFromMouseButton(MouseButton changedButton)
    {
        if (!IsBrowseBackMouseButton(changedButton))
        {
            return false;
        }

        var command = viewModel?.ReturnToBrowseCategoriesCommand;
        if (command?.CanExecute(null) != true)
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    private void MainWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (tabDetachDragTab is null && videoReorderDragTab is null)
        {
            return;
        }

        var screenPoint = PointToNativeScreenPoint(e.GetPosition(this));
        if (TryContinueVideoReorderDrag(screenPoint))
        {
            e.Handled = true;
            return;
        }

        if (tabDetachDragTab is null)
        {
            return;
        }

        if (TryContinueTabDetachDrag(screenPoint, continueDrag: true))
        {
            e.Handled = true;
        }
    }

    private void MainWindowPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var screenPoint = PointToNativeScreenPoint(e.GetPosition(this));
        if (TryCompleteVideoReorderDrag(screenPoint))
        {
            e.Handled = true;
        }

        if (TryCompleteTabDetachDrag(screenPoint))
        {
            e.Handled = true;
        }

        ClearVideoReorderDrag();
        ClearTabDetachDrag();
    }

    private void HomeContentScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (homeAutoScrollViewer is not null)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                ClearHomeAutoScroll();
                e.Handled = true;
            }

            return;
        }

        if (e.ChangedButton == MouseButton.Middle &&
            e.OriginalSource is DependencyObject streamSource &&
            TryHandleHomeStreamOpenAndStayOnHomeCommand(streamSource))
        {
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Middle ||
            sender is not ScrollViewer scrollViewer ||
            scrollViewer.ScrollableHeight <= 0 ||
            tabDetachDragTab is not null ||
            videoReorderDragTab is not null)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source && FindVisualParent<ScrollBar>(source) is not null)
        {
            return;
        }

        BeginHomeAutoScroll(scrollViewer, e.GetPosition(scrollViewer));
        e.Handled = homeAutoScrollViewer is not null;
    }

    private void HomeStreamItemButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject ?? sender as DependencyObject;
        if (TryHandleHomeStreamOpenAndStayOnHomeCommand(source))
        {
            e.Handled = true;
        }
    }

    private void HomeContentScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (homeAutoScrollViewer is null)
        {
            return;
        }

        if (!ShouldContinueHomeAutoScroll(e.MiddleButton))
        {
            ClearHomeAutoScroll();
            e.Handled = true;
            return;
        }

        homeAutoScrollCursorPoint = e.GetPosition(homeAutoScrollViewer);
        e.Handled = true;
    }

    private void HomeContentScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (homeAutoScrollViewer is null || e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        ClearHomeAutoScroll();
        e.Handled = true;
    }

    private void HomeContentScrollViewer_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, homeAutoScrollViewer))
        {
            ClearHomeAutoScroll(releaseCapture: false);
        }
    }

    private void HomeContentScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer ||
            viewModel?.IsBrowseCategoriesPageVisible != true ||
            viewModel.LoadMoreBrowseCategoriesCommand.CanExecute(null) != true ||
            !IsHomeContentScrollNearBottom(
                scrollViewer.VerticalOffset,
                scrollViewer.ScrollableHeight,
                BrowseCategoryLoadMoreBottomThreshold))
        {
            return;
        }

        viewModel.LoadMoreBrowseCategoriesCommand.Execute(null);
    }

    private void BrowseCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        // Button commands run after Click, so reset after the stream page replaces the category grid.
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(HomeContentScrollViewer.ScrollToTop));
    }

    private void BeginHomeAutoScroll(ScrollViewer scrollViewer, Point anchorPoint)
    {
        if (!Mouse.Capture(scrollViewer, CaptureMode.SubTree))
        {
            return;
        }

        homeAutoScrollViewer = scrollViewer;
        homeAutoScrollAnchorPoint = anchorPoint;
        homeAutoScrollCursorPoint = anchorPoint;
        homeAutoScrollPreviousCursor = Cursor;
        homeAutoScrollLastTickTimestamp = Stopwatch.GetTimestamp();
        Cursor = Cursors.ScrollNS;
        EnsureHomeAutoScrollTimer().Start();
    }

    private DispatcherTimer EnsureHomeAutoScrollTimer()
    {
        if (homeAutoScrollTimer is not null)
        {
            return homeAutoScrollTimer;
        }

        homeAutoScrollTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        {
            Interval = HomeAutoScrollInterval
        };
        homeAutoScrollTimer.Tick += HomeAutoScrollTimerOnTick;
        return homeAutoScrollTimer;
    }

    private void HomeAutoScrollTimerOnTick(object? sender, EventArgs e)
    {
        var scrollViewer = homeAutoScrollViewer;
        if (scrollViewer is null)
        {
            homeAutoScrollTimer?.Stop();
            return;
        }

        if (!ShouldContinueHomeAutoScroll(Mouse.MiddleButton))
        {
            ClearHomeAutoScroll();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = homeAutoScrollLastTickTimestamp == 0
            ? HomeAutoScrollInterval.TotalSeconds
            : (now - homeAutoScrollLastTickTimestamp) / (double)Stopwatch.Frequency;
        homeAutoScrollLastTickTimestamp = now;

        var targetOffset = GetHomeAutoScrollVerticalOffset(
            scrollViewer.VerticalOffset,
            homeAutoScrollAnchorPoint.Y,
            homeAutoScrollCursorPoint.Y,
            scrollViewer.ScrollableHeight,
            elapsedSeconds);
        if (Math.Abs(targetOffset - scrollViewer.VerticalOffset) > double.Epsilon)
        {
            scrollViewer.ScrollToVerticalOffset(targetOffset);
        }
    }

    private void ClearHomeAutoScroll(bool releaseCapture = true)
    {
        var scrollViewer = homeAutoScrollViewer;
        if (scrollViewer is null)
        {
            return;
        }

        homeAutoScrollTimer?.Stop();
        homeAutoScrollViewer = null;
        homeAutoScrollAnchorPoint = default;
        homeAutoScrollCursorPoint = default;
        homeAutoScrollLastTickTimestamp = 0;
        Cursor = homeAutoScrollPreviousCursor;
        homeAutoScrollPreviousCursor = null;

        if (releaseCapture && ReferenceEquals(Mouse.Captured, scrollViewer))
        {
            Mouse.Capture(null);
        }
    }

    internal static bool ShouldContinueHomeAutoScroll(MouseButtonState middleButtonState)
        => HomeAutoScrollController.ShouldContinue(middleButtonState);

    internal static double GetHomeAutoScrollVelocity(double anchorY, double currentY)
        => HomeAutoScrollController.GetVelocity(anchorY, currentY);

    internal static double GetHomeAutoScrollVerticalOffset(
        double currentVerticalOffset,
        double anchorY,
        double currentY,
        double scrollableHeight,
        double elapsedSeconds)
        => HomeAutoScrollController.GetVerticalOffset(
            currentVerticalOffset,
            anchorY,
            currentY,
            scrollableHeight,
            elapsedSeconds);

    internal static bool IsHomeContentScrollNearBottom(
        double verticalOffset,
        double scrollableHeight,
        double bottomThreshold)
        => HomeAutoScrollController.IsNearBottom(verticalOffset, scrollableHeight, bottomThreshold);

    internal static bool TryHandleHomeStreamOpenAndStayOnHomeCommand(DependencyObject? source)
    {
        if (!TryResolveHomeStreamOpenAndStayOnHomeCommand(source, out var command))
        {
            return false;
        }

        if (command.CanExecute(null))
        {
            command.Execute(null);
        }

        return true;
    }

    internal static bool TryResolveHomeStreamOpenAndStayOnHomeCommand(
        DependencyObject? source,
        out AsyncRelayCommand command)
    {
        command = null!;
        var button = source as Button ?? (source is null ? null : FindVisualParent<Button>(source));
        if (button?.DataContext is not IHomeStreamOpenItemViewModel item ||
            !IsHomeStreamOpenButton(button))
        {
            return false;
        }

        command = item.OpenAndStayOnHomeCommand;
        return true;
    }

    private static bool IsHomeStreamOpenButton(Button button)
    {
        var binding = BindingOperations.GetBindingExpression(button, ButtonBase.CommandProperty);
        return string.Equals(binding?.ParentBinding.Path?.Path, "OpenCommand", StringComparison.Ordinal);
    }

    private async void MainWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindowLoaded;

        var logger = new FileAppLogger();
        appLogger = logger;
        try
        {
            await InitializeMainWindowAsync(logger);
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Error, "Startup", "Application startup failed.", ex);
            if (!shutdownStarted)
            {
                exitRequested = true;
                Close();
            }
        }
    }

    private async Task InitializeMainWindowAsync(IAppLogger logger)
    {
        var jsonSettingsService = new JsonSettingsService();
        settingsService = jsonSettingsService;
        var settings = await jsonSettingsService.LoadAsync();
        var settingsLoadWarning = jsonSettingsService.LastLoadWarning;
        if (!string.IsNullOrWhiteSpace(settingsLoadWarning))
        {
            logger.Write(AppLogLevel.Warning, "Settings", settingsLoadWarning);
        }
        if (shutdownStarted)
        {
            return;
        }

        ThemeManager.ApplyTheme(settings.Theme);

        settings.StreamlinkPath ??= ExecutableResolver.FindStreamlink();
        settings.VlcDirectory ??= ExecutableResolver.FindVlcDirectory();

        var streamlinkService = new StreamlinkService(logger);
        var playbackFactory = new LibVlcPlaybackEngineFactory(logger, settings.Chat);
        var chatFactory = new ChatClientFactory(settings, logger);
        var viewerCountService = new ViewerCountService(logger);
        var replayResolver = new ReplayResolver(logger, streamlinkService);
        var kickOfficialChatReplayStore = new KickOfficialChatReplayStore(logger);
        this.kickOfficialChatReplayStore = kickOfficialChatReplayStore;
        var replayChatProvider = new ReplayChatProvider(kickOfficialChatReplayStore, logger);
        this.replayChatProvider = replayChatProvider;
        var kickChatHistoryProvider = new KickChatHistoryProvider(logger);
        this.kickChatHistoryProvider = kickChatHistoryProvider;
        var followedStreamsService = new FollowedStreamsService(logger);
        var streamMetadataService = new StreamMetadataService(logger);
        var streamSearchService = new StreamSearchService(logger, streamlinkService);
        var twitchVodService = new TwitchVodService(logger);
        var twitchSubOnlyVodResolver = new TwitchSubOnlyVodResolver(logger);
        var twitchClipService = new TwitchClipService();
        var kickVodService = new KickVodService(logger);
        kickEventSubscriptionService = new KickEventSubscriptionService(
            logger,
            settingsPersister: (_, cancellationToken) => settingsService.SaveAsync(settings, cancellationToken));
        var browseService = new BrowseService(logger);
        var liveNotificationService = new ToastLiveNotificationService(logger);
        var appUpdateService = new GitHubReleaseAppUpdateService(logger);
        this.liveNotificationService = liveNotificationService;
        liveNotificationService.Activated += OnLiveNotificationActivated;

        viewModel = new MainViewModel(new MainViewModelDependencies
        {
            Settings = settings,
            SettingsService = settingsService,
            StreamlinkService = streamlinkService,
            PlaybackFactory = playbackFactory,
            ChatFactory = chatFactory,
            Logger = logger,
            Dispatch = DispatchToUi,
            ViewerCountService = viewerCountService,
            FollowedStreamsService = followedStreamsService,
            StreamMetadataService = streamMetadataService,
            ReplayResolver = replayResolver,
            ReplayChatProvider = replayChatProvider,
            TwitchVodService = twitchVodService,
            BrowseService = browseService,
            KickChatHistoryProvider = kickChatHistoryProvider,
            StreamSearchService = streamSearchService,
            KickVodService = kickVodService,
            KickEventSubscriptionService = kickEventSubscriptionService,
            LiveNotificationService = liveNotificationService,
            TwitchSubOnlyVodResolver = twitchSubOnlyVodResolver,
            TwitchClipService = twitchClipService,
            AppUpdateService = appUpdateService,
            RequestShutdown = RequestApplicationExit,
            TryDispatch = TryDispatchToUi
        });

        viewModel.Initialize();
        if (!string.IsNullOrWhiteSpace(settingsLoadWarning))
        {
            viewModel.SetStartupWarning(settingsLoadWarning);
        }
        viewModel.Tabs.CollectionChanged += ViewModelTabsCollectionChanged;
        settings.Chat.PropertyChanged += ChatSettingsOnPropertyChanged;
        DataContext = viewModel;

        if (setupRequested || !settings.SetupCompleted)
        {
            var setupWizard = new SetupWizardWindow(settings, settingsService, logger)
            {
                Owner = this
            };
            setupWizard.ShowDialog();
        }

        browserCaptureServer = new BrowserCaptureServer(HandleBrowserCaptureUrlAsync, logger);
        if (!browserCaptureServer.Start())
        {
            browserClickFallbackEnabled = true;
            viewModel.SetBrowserClickStatus("Browser capture extension listener could not start");
        }
        else
        {
            browserClickFallbackEnabled = false;
        }

        await ReconcileKickWebhookListenerAsync(settings.Chat);
    }

    private void ChatSettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (viewModel is null ||
            sender is not ChatSettings settings ||
            (e.PropertyName != nameof(ChatSettings.KickWebhookListenerEnabled) &&
                e.PropertyName != nameof(ChatSettings.KickWebhookListenerPort)))
        {
            return;
        }

        _ = ReconcileKickWebhookListenerAsync(settings);
    }

    private async Task ReconcileKickWebhookListenerAsync(ChatSettings settings)
    {
        await kickWebhookLifecycleGate.WaitAsync();
        try
        {
            if (viewModel is null ||
                appLogger is null ||
                kickOfficialChatReplayStore is null ||
                shutdownStarted)
            {
                return;
            }

            var requestedPort = settings.KickWebhookListenerPort;
            if (!settings.KickWebhookListenerEnabled)
            {
                await StopKickWebhookListenerAsync().ConfigureAwait(true);
                viewModel.SetKickWebhookListenerStatus(
                    $"Official Kick webhook listener is stopped. Local forwarding target: {viewModel.KickWebhookLocalUrl}");
                return;
            }

            if (kickWebhookChatServer is not null &&
                kickWebhookActiveSettingsPort == requestedPort)
            {
                viewModel.SetKickWebhookListenerStatus(
                    $"Official Kick webhook listener is running at {kickWebhookChatServer.LocalWebhookUrl}");
                return;
            }

            await StopKickWebhookListenerAsync().ConfigureAwait(true);
            var webhookServer = new KickWebhookChatServer(
                kickOfficialChatReplayStore,
                appLogger,
                requestedPort);
            if (webhookServer.Start())
            {
                kickWebhookChatServer = webhookServer;
                kickWebhookActiveSettingsPort = requestedPort;
                viewModel.SetKickWebhookListenerStatus(
                    $"Official Kick webhook listener is running at {webhookServer.LocalWebhookUrl}");
                return;
            }

            await webhookServer.DisposeAsync();
            viewModel.SetKickWebhookListenerStatus(
                $"Official Kick webhook listener could not start. Check whether port {requestedPort} is already in use.");
        }
        catch (Exception ex)
        {
            appLogger?.Write(AppLogLevel.Warning, "Chat", "Official Kick webhook listener update failed.", ex);
            if (!shutdownStarted)
            {
                viewModel?.SetKickWebhookListenerStatus(
                    $"Official Kick webhook listener failed: {ex.Message}");
            }
        }
        finally
        {
            kickWebhookLifecycleGate.Release();
        }
    }

    private async Task StopKickWebhookListenerAsync()
    {
        if (kickWebhookChatServer is null)
        {
            kickWebhookActiveSettingsPort = -1;
            return;
        }

        var webhookServer = kickWebhookChatServer;
        kickWebhookChatServer = null;
        kickWebhookActiveSettingsPort = -1;
        await webhookServer.DisposeAsync();
    }

    private async Task StopKickWebhookListenerSerializedAsync()
    {
        await kickWebhookLifecycleGate.WaitAsync();
        try
        {
            await StopKickWebhookListenerAsync();
        }
        finally
        {
            kickWebhookLifecycleGate.Release();
        }
    }

    private async void MainWindowClosing(object? sender, CancelEventArgs e)
    {
        // Clear this while the HWND is still valid. The close can be deferred for asynchronous
        // shutdown or converted into a tray hide, and either path must release shell fullscreen.
        ClearTaskbarFullscreen();

        if (closeConfirmed)
        {
            return;
        }

        e.Cancel = true;
        if (!exitRequested)
        {
            if (viewModel?.Settings.CloseBehavior == WindowCloseBehavior.MinimizeToTray)
            {
                HideToTray();
                return;
            }

            exitRequested = true;
        }

        if (shutdownStarted)
        {
            Environment.Exit(0);
            return;
        }

        shutdownStarted = true;
        Hide();
        DisposeTrayIcon();
        var forceExit = false;

        try
        {
            if (viewModel is not null)
            {
                CloseAllDetachedWindows(reattach: false);
                if (browserCaptureServer is not null)
                {
                    await browserCaptureServer.DisposeAsync().AsTask().WaitAsync(ShutdownTimeout);
                    browserCaptureServer = null;
                }

                await StopKickWebhookListenerSerializedAsync().WaitAsync(ShutdownTimeout);

                viewModel.Settings.Chat.PropertyChanged -= ChatSettingsOnPropertyChanged;
                viewModel.Tabs.CollectionChanged -= ViewModelTabsCollectionChanged;

                await viewModel.DisposeAsync().AsTask().WaitAsync(ShutdownTimeout);
                await DisposeKickEventSubscriptionServiceAsync().WaitAsync(ShutdownTimeout);
                if (settingsService is not null)
                {
                    await settingsService.SaveAsync(viewModel.Settings).WaitAsync(ShutdownTimeout);
                }
            }
        }
        catch (Exception)
        {
            // Shutdown must still complete even if a stream/chat backend refuses to stop.
            forceExit = true;
        }
        finally
        {
            DisposeReplayProviders();
            await DisposeAppLoggerAsync();
            closeConfirmed = true;
            // Null-conditional: test hosts close this window on a bare STA dispatcher
            // without a WPF Application instance.
            Application.Current?.Shutdown();
            if (forceExit)
            {
                Environment.Exit(0);
            }
        }
    }

    private void MainWindowClosed(object? sender, EventArgs e)
    {
        ClearTaskbarFullscreen();

        if (viewModel is not null)
        {
            viewModel.Settings.Chat.PropertyChanged -= ChatSettingsOnPropertyChanged;
            viewModel.Tabs.CollectionChanged -= ViewModelTabsCollectionChanged;
        }

        CloseAllDetachedWindows(reattach: false);
        ClearHomeAutoScroll();
        UninstallMouseWheelHook();
        StopVideoReorderPolling();
        var subscriptionService = kickEventSubscriptionService;
        kickEventSubscriptionService = null;
        if (subscriptionService is not null)
        {
            _ = subscriptionService.DisposeAsync();
        }
        if (liveNotificationService is not null)
        {
            liveNotificationService.Activated -= OnLiveNotificationActivated;
            liveNotificationService.Dispose();
            liveNotificationService = null;
        }

        DisposeReplayProviders();

        DisposeTrayIcon();
    }

    private async Task DisposeAppLoggerAsync()
    {
        var logger = appLogger;
        appLogger = null;
        try
        {
            if (logger is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().AsTask().WaitAsync(ShutdownTimeout);
            }
            else if (logger is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // Shutdown must remain bounded even if the filesystem is no longer writable.
        }
    }

    private async Task DisposeKickEventSubscriptionServiceAsync()
    {
        var service = kickEventSubscriptionService;
        kickEventSubscriptionService = null;
        if (service is not null)
        {
            await service.DisposeAsync();
        }
    }

    private void DisposeReplayProviders()
    {
        replayChatProvider = null;

        var historyProvider = kickChatHistoryProvider;
        kickChatHistoryProvider = null;
        if (historyProvider is not null)
        {
            try
            {
                historyProvider.Dispose();
            }
            catch (Exception ex)
            {
                appLogger?.Write(AppLogLevel.Warning, "Shutdown", "Failed to dispose Kick chat history provider.", ex);
            }
        }
    }

    private void OnLiveNotificationActivated(NotificationActivation activation)
    {
        DispatchToUi(() =>
        {
            RestoreFromNotification();
            viewModel?.OpenChannelFromNotification(activation.Platform, activation.Channel);
        });
    }

    private void RestoreFromNotification()
    {
        ShowInTaskbar = true;
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void DispatchToUi(Action action)
    {
        _ = TryDispatchToUi(action);
    }

    private bool TryDispatchToUi(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return false;
        }

        try
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, action);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private Task HandleBrowserCaptureUrlAsync(string url)
    {
        if (!StreamInputParser.TryParsePlatformUrl(url, out var target) || target is null)
        {
            DispatchToUi(() => viewModel?.SetBrowserClickStatus("Ignored browser capture that was not a stream channel"));
            return Task.CompletedTask;
        }

        DispatchToUi(() =>
        {
            if (viewModel is not null)
            {
                ShowMainWindow();
                _ = viewModel.OpenDetectedStreamAsync(target);
            }
        });
        return Task.CompletedTask;
    }

    private void VideoSurface_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is VideoSurface surface && surface.Tag is StreamTabViewModel tab)
        {
            videoSurfaces[tab] = surface;
            surface.NativeMouseLeftButtonDown += VideoSurface_NativeMouseLeftButtonDown;
            surface.NativeMouseMoved += VideoSurface_NativeMouseMoved;
            surface.NativeMouseLeftButtonUp += VideoSurface_NativeMouseLeftButtonUp;
            tab.SetVideoHandle(surface.Handle);
        }
    }

    private void VideoSurface_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is VideoSurface surface && surface.Tag is StreamTabViewModel tab &&
            videoSurfaces.TryGetValue(tab, out var trackedSurface) &&
            ReferenceEquals(surface, trackedSurface))
        {
            surface.NativeMouseLeftButtonDown -= VideoSurface_NativeMouseLeftButtonDown;
            surface.NativeMouseMoved -= VideoSurface_NativeMouseMoved;
            surface.NativeMouseLeftButtonUp -= VideoSurface_NativeMouseLeftButtonUp;
            videoSurfaces.Remove(tab);
            tab.ClearVideoHandle(surface.Handle);
        }
    }

    private void VideoSurface_MouseLeftButtonPressed(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement { Tag: StreamTabViewModel tab } && viewModel is not null)
        {
            viewModel.SelectedTab = tab;
        }
    }

    private void VideoSurface_NativeMouseLeftButtonDown(object? sender, VideoSurfaceNativeMouseEventArgs e)
    {
        if (sender is FrameworkElement { Tag: StreamTabViewModel tab } && viewModel is not null)
        {
            viewModel.SelectedTab = tab;
        }

        _ = BeginVideoReorderDragCandidate(new NativePoint(e.ScreenX, e.ScreenY));
    }

    private void VideoSurface_NativeMouseMoved(object? sender, VideoSurfaceNativeMouseEventArgs e)
    {
        if (TryContinueVideoReorderDrag(new NativePoint(e.ScreenX, e.ScreenY)) && videoReorderDragStarted)
        {
            e.Handled = true;
        }
    }

    private void VideoSurface_NativeMouseLeftButtonUp(object? sender, VideoSurfaceNativeMouseEventArgs e)
    {
        if (TryCompleteVideoReorderDrag(new NativePoint(e.ScreenX, e.ScreenY)))
        {
            e.Handled = true;
            return;
        }

        ClearVideoReorderDrag();
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel is null)
        {
            return;
        }

        if ((sender as FrameworkElement)?.Tag is TabStripItemViewModel item)
        {
            viewModel.CloseTabStripItem(item);
        }
        else if ((sender as FrameworkElement)?.Tag is StreamTabViewModel tab)
        {
            viewModel.CloseTab(tab);
        }
    }

    private void TabContent_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindVisualParent<Button>(source) is not null)
        {
            return;
        }

        var startedWithControlModifier = isControlModifierPressed();
        var sourceElement = sender as FrameworkElement;
        if (sourceElement?.DataContext is TabStripItemViewModel item)
        {
            if (viewModel is not null && TabsContainActiveTab(item))
            {
                viewModel.SelectedTab = item.ActiveTab;
                TabListBox.SelectedItem = viewModel.SelectedTabStripItem;
            }
            else
            {
                TabListBox.SelectedItem = item;
            }

            BeginTabDetachDrag(
                sourceElement,
                item.ActiveTab,
                item.Tabs,
                e.GetPosition(this),
                startedWithControlModifier);
            e.Handled = true;
        }
        else if (sourceElement?.DataContext is StreamTabViewModel tab)
        {
            if (viewModel is not null)
            {
                viewModel.SelectedTab = tab;
                TabListBox.SelectedItem = viewModel.SelectedTabStripItem;
            }
            else
            {
                TabListBox.SelectedItem = tab;
            }

            BeginTabDetachDrag(
                sourceElement,
                tab,
                [tab],
                e.GetPosition(this),
                startedWithControlModifier);
            e.Handled = true;
        }
    }

    private bool TabsContainActiveTab(TabStripItemViewModel item)
    {
        return viewModel?.Tabs.Contains(item.ActiveTab) == true;
    }

    private void BeginTabDetachDrag(
        FrameworkElement? source,
        StreamTabViewModel tab,
        IReadOnlyList<StreamTabViewModel> draggedTabs,
        Point startPoint,
        bool startedWithControlModifier)
    {
        if (source is null)
        {
            return;
        }

        if (PresentationSource.FromVisual(this) is not null)
        {
            Mouse.Capture(this, CaptureMode.SubTree);
        }

        tabDetachDragSource = source;
        tabDetachDragTab = tab;
        tabDetachDragMergeTarget = null;
        tabDetachDragTabs = draggedTabs
            .Where(candidate => viewModel?.Tabs.Contains(candidate) == true)
            .Distinct()
            .ToArray();
        tabDetachDragStartPoint = startPoint;
        tabDetachDragStartScreenPoint = PointToNativeScreenPoint(startPoint);
        tabStripReorderDragReordered = false;
        tabDetachDragStartedWithControlModifier = startedWithControlModifier;
        UpdateLowLevelMouseMoveRouteState();
    }

    private void ClearTabDetachDrag()
    {
        if (ReferenceEquals(Mouse.Captured, this) && videoReorderDragTab is null)
        {
            Mouse.Capture(null);
        }

        tabDetachDragSource = null;
        tabDetachDragTab = null;
        tabDetachDragMergeTarget = null;
        tabDetachDragTabs = [];
        tabDetachDragStartPoint = default;
        tabDetachDragStartScreenPoint = default;
        tabStripReorderDragReordered = false;
        tabDetachDragStartedWithControlModifier = false;
        if (videoReorderDragTab is null)
        {
            Cursor = null;
        }

        UpdateLowLevelMouseMoveRouteState();
    }

    private bool BeginVideoReorderDragCandidate(NativePoint screenPoint)
    {
        ClearVideoReorderDrag();
        if (viewModel is null ||
            GetVideoTabAtScreenPoint(screenPoint) is not { } tab ||
            !viewModel.CanReorderVisibleVideoTab(tab) ||
            IsScreenPointOverNativeOverlay(tab, screenPoint))
        {
            return false;
        }

        videoReorderDragTab = tab;
        videoReorderDragStartScreenPoint = screenPoint;
        videoReorderDragStarted = false;
        videoReorderDragReordered = false;
        Mouse.Capture(this, CaptureMode.SubTree);
        StartVideoReorderPolling();
        UpdateLowLevelMouseMoveRouteState();
        return true;
    }

    private void ClearVideoReorderDrag()
    {
        if (ReferenceEquals(Mouse.Captured, this) && tabDetachDragTab is null)
        {
            Mouse.Capture(null);
        }

        videoReorderDragTab = null;
        videoReorderDragStartScreenPoint = default;
        videoReorderDragStarted = false;
        videoReorderDragReordered = false;
        Cursor = null;
        UpdateLowLevelMouseMoveRouteState();
    }

    private void UpdateLowLevelMouseMoveRouteState()
    {
        hasActiveLowLevelMouseMoveRoute =
            tabDetachDragTab is not null ||
            videoReorderDragTab is not null ||
            detachedWindows.Values.Distinct().Any(window => window.HasVideoMoveCandidate);
    }

    private void StartVideoReorderPolling()
    {
        if (videoReorderPollTimer is not null)
        {
            if (!videoReorderPollTimer.IsEnabled)
            {
                videoReorderPollTimer.Start();
            }

            return;
        }

        videoReorderPollTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(VideoReorderPollIntervalMilliseconds)
        };
        videoReorderPollTimer.Tick += VideoReorderPollTimerOnTick;
        videoReorderPollTimer.Start();
    }

    private void StopVideoReorderPolling()
    {
        if (videoReorderPollTimer is null)
        {
            return;
        }

        videoReorderPollTimer.Stop();
        videoReorderPollTimer.Tick -= VideoReorderPollTimerOnTick;
        videoReorderPollTimer = null;
    }

    private void VideoReorderPollTimerOnTick(object? sender, EventArgs e)
    {
        var leftButtonPressed = isLeftMouseButtonPressed();
        if (getVideoReorderCursorScreenPoint() is not { } screenPoint)
        {
            videoReorderPollLeftButtonWasPressed = leftButtonPressed;
            if (!leftButtonPressed && videoReorderDragTab is not null)
            {
                ClearVideoReorderDrag();
            }

            return;
        }

        PollVideoReorderDrag(screenPoint, leftButtonPressed);
    }

    private void PollVideoReorderDrag(NativePoint screenPoint, bool leftButtonPressed)
    {
        var leftButtonWasPressed = videoReorderPollLeftButtonWasPressed;
        videoReorderPollLeftButtonWasPressed = leftButtonPressed;

        if (viewModel is null)
        {
            ClearVideoReorderDrag();
            return;
        }

        if (videoReorderDragTab is null)
        {
            if (leftButtonPressed && !leftButtonWasPressed && IsActive)
            {
                _ = BeginVideoReorderDragCandidate(screenPoint);
            }

            return;
        }

        if (leftButtonPressed)
        {
            _ = TryContinueVideoReorderDrag(screenPoint, leftButtonPressed: true);
            return;
        }

        if (!TryCompleteVideoReorderDrag(screenPoint))
        {
            ClearVideoReorderDrag();
        }
    }

    private bool TryContinueVideoReorderDrag(NativePoint screenPoint)
    {
        return TryContinueVideoReorderDrag(screenPoint, isLeftMouseButtonPressed());
    }

    private bool TryContinueVideoReorderDrag(NativePoint screenPoint, bool leftButtonPressed)
    {
        if (viewModel is null ||
            videoReorderDragTab is not { } draggedTab)
        {
            ClearVideoReorderDrag();
            return false;
        }

        if (!leftButtonPressed)
        {
            ClearVideoReorderDrag();
            return false;
        }

        if (!viewModel.CanReorderVisibleVideoTab(draggedTab))
        {
            ClearVideoReorderDrag();
            return false;
        }

        if (!videoReorderDragStarted)
        {
            if (!HasExceededDragDistance(videoReorderDragStartScreenPoint, screenPoint))
            {
                return false;
            }

            videoReorderDragStarted = true;
            viewModel.SelectedTab = draggedTab;
            ResetVideoDoubleClickTracking();
            Cursor = Cursors.SizeAll;
        }

        if (GetVideoReorderDropTarget(screenPoint, draggedTab) is { } dropTab)
        {
            _ = TryReorderDraggedVideoTab(draggedTab, dropTab);
        }

        return true;
    }

    private bool TryCompleteVideoReorderDrag(NativePoint screenPoint)
    {
        if (viewModel is null ||
            videoReorderDragTab is not { } draggedTab ||
            !videoReorderDragStarted)
        {
            return false;
        }

        if (!videoReorderDragReordered &&
            GetVideoReorderDropTarget(screenPoint, draggedTab) is { } dropTab)
        {
            _ = TryReorderDraggedVideoTab(draggedTab, dropTab);
        }

        ClearVideoReorderDrag();
        return true;
    }

    private bool TryReorderDraggedVideoTab(StreamTabViewModel draggedTab, StreamTabViewModel dropTab)
    {
        if (viewModel?.TryReorderVisibleVideoTab(draggedTab, dropTab) != true)
        {
            return false;
        }

        videoReorderDragReordered = true;
        VideoViewport.UpdateLayout();
        return true;
    }

    private StreamTabViewModel? GetVideoReorderDropTarget(NativePoint screenPoint, StreamTabViewModel draggedTab)
    {
        if (viewModel is null ||
            GetVideoTabAtScreenPoint(screenPoint) is not { } targetTab ||
            ReferenceEquals(targetTab, draggedTab) ||
            !viewModel.CanReorderVisibleVideoTab(targetTab))
        {
            return null;
        }

        return targetTab;
    }

    private bool IsScreenPointOverNativeOverlay(StreamTabViewModel tab, NativePoint screenPoint)
    {
        return videoSurfaces.TryGetValue(tab, out var surface) &&
            IsScreenPointOverElement(surface, screenPoint) &&
            IsPointerOverNativeOverlay(tab);
    }

    private bool IsPointerOverNativeOverlay(StreamTabViewModel tab)
    {
        return viewModel?.Settings.Chat.Layout == ChatLayout.Overlay &&
            tab.IsChatVisible &&
            !tab.IsDockedChatOverrideActive &&
            tab.UsesNativeOverlay &&
            TryGetVideoCursorPoint(tab, out var videoPoint, out _, out var videoHeight) &&
            TryGetNativeOverlayBounds(tab, videoHeight, out var overlayBounds) &&
            overlayBounds.Contains(videoPoint);
    }

    private bool TryContinueTabDetachDrag(NativePoint screenPoint, bool continueDrag)
    {
        if (viewModel is null ||
            tabDetachDragTab is not { } tab ||
            tabDetachDragSource is null)
        {
            ClearTabDetachDrag();
            return false;
        }

        if (!isLeftMouseButtonPressed())
        {
            ClearTabDetachDrag();
            return false;
        }

        if (!HasExceededTabDetachDragDistance(screenPoint))
        {
            return false;
        }

        var isControlDrag = tabDetachDragStartedWithControlModifier || isControlModifierPressed();
        var isOutsideTabStrip = IsScreenPointOutsideTabStrip(screenPoint);
        if (!isControlDrag && !isOutsideTabStrip)
        {
            tabDetachDragMergeTarget = null;
            Cursor = Cursors.SizeAll;
            _ = TryReorderDraggedTabStripTabs(screenPoint, tab);
            return true;
        }

        if (TryAttachDraggedTabToPictureInPictureTarget(screenPoint, tab))
        {
            ClearTabDetachDrag();
            return true;
        }

        if (!isOutsideTabStrip)
        {
            if (isControlDrag)
            {
                UpdateTabDetachDragMergeTarget(screenPoint, tab);
            }

            return true;
        }

        ClearTabDetachDrag();
        DetachTabToPictureInPicture(tab, new Point(screenPoint.X, screenPoint.Y), continueDrag);
        return true;
    }

    private bool TryCompleteTabDetachDrag(NativePoint screenPoint)
    {
        if (viewModel is null ||
            tabDetachDragTab is not { } tab ||
            !HasExceededTabDetachDragDistance(screenPoint))
        {
            return false;
        }

        var isControlDrag = tabDetachDragStartedWithControlModifier || isControlModifierPressed();
        var isOutsideTabStrip = IsScreenPointOutsideTabStrip(screenPoint);
        if (!isControlDrag && !isOutsideTabStrip)
        {
            Cursor = Cursors.SizeAll;
            if (!tabStripReorderDragReordered)
            {
                _ = TryReorderDraggedTabStripTabs(screenPoint, tab);
            }

            return true;
        }

        if (TryAttachDraggedTabToPictureInPictureTarget(screenPoint, tab))
        {
            return true;
        }

        var hasTargetItem = TryGetTabStripItemAtScreenPoint(screenPoint, out var targetItem);
        if (isControlDrag &&
            hasTargetItem &&
            targetItem is not null)
        {
            if (TryMergeDraggedTabIntoMultiViewTarget(tab, targetItem))
            {
                return true;
            }

            tabDetachDragMergeTarget = null;
        }
        else if (isControlDrag && TryMergeDraggedTabIntoRememberedMultiViewTarget(tab))
        {
            return true;
        }

        var draggedTabs = GetTabDetachDragTabs(tab);
        if (isOutsideTabStrip &&
            HasPotentialPictureInPictureDropTarget(draggedTabs))
        {
            DetachTabToPictureInPicture(tab, new Point(screenPoint.X, screenPoint.Y), continueDrag: false);
            return true;
        }

        return false;
    }

    private bool TryReorderDraggedTabStripTabs(NativePoint screenPoint, StreamTabViewModel activeDraggedTab)
    {
        if (viewModel is null)
        {
            return false;
        }

        var draggedTabs = GetTabDetachDragTabs(activeDraggedTab);
        if (draggedTabs.Length == 0 ||
            !TryGetTabStripReorderTarget(
                screenPoint,
                out var targetTab,
                out var insertAfterTarget))
        {
            return false;
        }

        if (!viewModel.TryReorderTabStripTabs(
            draggedTabs,
            targetTab,
            insertAfterTarget,
            activeDraggedTab))
        {
            return false;
        }

        tabStripReorderDragReordered = true;
        TabListBox.UpdateLayout();
        VideoViewport.UpdateLayout();
        return true;
    }

    private bool TryAttachDraggedTabToPictureInPictureTarget(NativePoint screenPoint, StreamTabViewModel tab)
    {
        var draggedTabs = GetTabDetachDragTabs(tab);
        if (draggedTabs.Length == 0)
        {
            return false;
        }

        var targetWindow = GetPictureInPictureDropTarget(screenPoint, draggedTabs);
        return targetWindow is not null &&
            AddTabsToPictureInPictureWindow(targetWindow, draggedTabs, tab);
    }

    private void UpdateTabDetachDragMergeTarget(NativePoint screenPoint, StreamTabViewModel tab)
    {
        tabDetachDragMergeTarget = TryGetValidTabStripMergeTarget(screenPoint, tab, out var targetTab)
            ? targetTab
            : null;
    }

    private bool TryGetValidTabStripMergeTarget(
        NativePoint screenPoint,
        StreamTabViewModel tab,
        out StreamTabViewModel targetTab)
    {
        targetTab = null!;
        var draggedTabs = GetTabDetachDragTabs(tab);
        if (viewModel is null ||
            draggedTabs.Length == 0 ||
            !TryGetTabStripItemAtScreenPoint(screenPoint, out var targetItem) ||
            targetItem is null)
        {
            return false;
        }

        var draggedSet = draggedTabs.ToHashSet();
        if (targetItem.Tabs.Any(draggedSet.Contains) ||
            !viewModel.Tabs.Contains(targetItem.ActiveTab) ||
            targetItem.ActiveTab.IsDetached ||
            draggedTabs.Any(candidate => !viewModel.Tabs.Contains(candidate) || candidate.IsDetached) ||
            (draggedTabs.Length == 1 && draggedTabs[0].IsMergedTabGroupMember))
        {
            return false;
        }

        targetTab = targetItem.ActiveTab;
        return true;
    }

    private bool TryMergeDraggedTabIntoMultiViewTarget(
        StreamTabViewModel tab,
        TabStripItemViewModel targetItem)
    {
        var draggedTabs = GetTabDetachDragTabs(tab);
        return viewModel is not null &&
            draggedTabs.Length > 0 &&
            TryMergeTabsIntoMultiViewTabs(draggedTabs, tab, targetItem.ActiveTab);
    }

    private bool TryMergeDraggedTabIntoRememberedMultiViewTarget(StreamTabViewModel tab)
    {
        if (tabDetachDragMergeTarget is not { } targetTab)
        {
            return false;
        }

        var draggedTabs = GetTabDetachDragTabs(tab);
        if (viewModel is null ||
            draggedTabs.Length == 0 ||
            !viewModel.Tabs.Contains(targetTab) ||
            !TryMergeTabsIntoMultiViewTabs(draggedTabs, tab, targetTab))
        {
            tabDetachDragMergeTarget = null;
            return false;
        }

        return true;
    }

    internal bool TryMergeTabsIntoMultiViewTabs(
        IReadOnlyList<StreamTabViewModel> draggedTabs,
        StreamTabViewModel activeDraggedTab,
        StreamTabViewModel targetTab)
    {
        if (viewModel?.TryMergeTabsIntoMultiView(draggedTabs, targetTab, activeDraggedTab) != true)
        {
            return false;
        }

        VideoViewport.UpdateLayout();
        return true;
    }

    internal bool AddTabsToPictureInPictureWindow(
        DetachedVideoWindow targetWindow,
        IReadOnlyList<StreamTabViewModel> tabs,
        StreamTabViewModel? activeTab)
    {
        if (viewModel is null ||
            targetWindow.IsClosing ||
            !detachedWindows.ContainsValue(targetWindow))
        {
            return false;
        }

        var tabsToAdd = tabs
            .Where(viewModel.Tabs.Contains)
            .Distinct()
            .Where(tab => !targetWindow.Tabs.Contains(tab))
            .ToArray();
        if (tabsToAdd.Length == 0)
        {
            return false;
        }

        if (fullscreen)
        {
            ExitFullscreenMode();
        }

        foreach (var tab in tabsToAdd)
        {
            if (!detachedWindows.TryGetValue(tab, out var sourceWindow) ||
                ReferenceEquals(sourceWindow, targetWindow))
            {
                continue;
            }

            detachedWindows.Remove(tab);
            sourceWindow.RemoveTabForTransfer(tab);
            viewModel.ClearPictureInPictureVisibleTabGroup([tab]);
            if (sourceWindow.TabCount == 0)
            {
                RemoveDetachedWindowMappings(sourceWindow);
                sourceWindow.CloseForTabDisposal();
            }
        }

        if (!targetWindow.TryAddTabs(tabsToAdd, activeTab))
        {
            return false;
        }

        viewModel.SetPictureInPictureTabGroup(targetWindow.Tabs);
        foreach (var tab in tabsToAdd)
        {
            detachedWindows[tab] = targetWindow;
        }

        var newlyDetachedTabs = tabsToAdd
            .Where(tab => !tab.IsDetached)
            .ToArray();
        if (newlyDetachedTabs.Length > 0)
        {
            viewModel.SetTabsDetached(newlyDetachedTabs, detached: true);
        }

        viewModel.SelectedTab = activeTab is not null && viewModel.Tabs.Contains(activeTab)
            ? activeTab
            : tabsToAdd[0];
        VideoViewport.UpdateLayout();
        BringDetachedWindowForward(targetWindow);
        targetWindow.UpdateLayout();
        targetWindow.AttachVideoSurface();
        SyncPictureInPictureVisibleTabGroup(targetWindow);
        return true;
    }

    private StreamTabViewModel[] GetPictureInPictureDragTabs(StreamTabViewModel tab)
    {
        return viewModel is null
            ? []
            : viewModel.GetPictureInPictureDragTabs(tab)
                .Where(viewModel.Tabs.Contains)
                .Distinct()
                .ToArray();
    }

    private StreamTabViewModel[] GetTabDetachDragTabs(StreamTabViewModel tab)
    {
        if (viewModel is null)
        {
            return [];
        }

        var capturedTabs = tabDetachDragTabs
            .Where(viewModel.Tabs.Contains)
            .Distinct()
            .ToArray();
        return capturedTabs.Length > 0
            ? capturedTabs
            : GetPictureInPictureDragTabs(tab);
    }

    private DetachedVideoWindow? GetPictureInPictureDropTarget(
        NativePoint screenPoint,
        IReadOnlyCollection<StreamTabViewModel> draggedTabs)
    {
        var draggedSet = draggedTabs.ToHashSet();
        if (TryGetTabAtTabStripScreenPoint(screenPoint, out var targetTab) &&
            targetTab is not null &&
            !draggedSet.Contains(targetTab) &&
            detachedWindows.TryGetValue(targetTab, out var tabWindow) &&
            !tabWindow.IsClosing)
        {
            return tabWindow;
        }

        foreach (var window in detachedWindows.Values.Distinct().ToArray())
        {
            if (window.IsClosing ||
                !window.Tabs.Any(tab => !draggedSet.Contains(tab)) ||
                !window.ContainsScreenPoint(screenPoint.X, screenPoint.Y))
            {
                continue;
            }

            return window;
        }

        return null;
    }

    private bool TryGetTabAtTabStripScreenPoint(NativePoint screenPoint, out StreamTabViewModel? tab)
    {
        if (TryGetTabStripItemAtScreenPoint(screenPoint, out var item) && item is not null)
        {
            tab = item.ActiveTab;
            return true;
        }

        tab = null;
        return false;
    }

    private bool TryGetTabStripReorderTarget(
        NativePoint screenPoint,
        out StreamTabViewModel targetTab,
        out bool insertAfterTarget)
    {
        targetTab = null!;
        insertAfterTarget = false;

        if (!IsScreenPointOverElement(TabListBox, screenPoint))
        {
            return false;
        }

        var candidates = GetVisibleTabStripItemBounds().ToArray();
        if (candidates.Length == 0)
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (screenPoint.X < candidate.Left ||
                screenPoint.X >= candidate.Right ||
                screenPoint.Y < candidate.Top ||
                screenPoint.Y >= candidate.Bottom)
            {
                continue;
            }

            targetTab = candidate.Item.ActiveTab;
            insertAfterTarget = screenPoint.X >= candidate.Left + ((candidate.Right - candidate.Left) / 2);
            return true;
        }

        var first = candidates[0];
        if (screenPoint.X < first.Left)
        {
            targetTab = first.Item.ActiveTab;
            insertAfterTarget = false;
            return true;
        }

        var last = candidates[^1];
        if (screenPoint.X >= last.Right)
        {
            targetTab = last.Item.ActiveTab;
            insertAfterTarget = true;
            return true;
        }

        double? nearestDistance = null;
        TabStripItemViewModel? nearestItem = null;
        var nearestInsertAfter = false;
        foreach (var candidate in candidates)
        {
            var distanceToLeft = Math.Abs(screenPoint.X - candidate.Left);
            if (nearestDistance is null || distanceToLeft < nearestDistance)
            {
                nearestDistance = distanceToLeft;
                nearestItem = candidate.Item;
                nearestInsertAfter = false;
            }

            var distanceToRight = Math.Abs(screenPoint.X - candidate.Right);
            if (distanceToRight < nearestDistance)
            {
                nearestDistance = distanceToRight;
                nearestItem = candidate.Item;
                nearestInsertAfter = true;
            }
        }

        if (nearestItem is null)
        {
            return false;
        }

        targetTab = nearestItem.ActiveTab;
        insertAfterTarget = nearestInsertAfter;
        return true;
    }

    private IEnumerable<TabStripItemBounds> GetVisibleTabStripItemBounds()
    {
        foreach (var item in TabListBox.Items)
        {
            if (TabListBox.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem listBoxItem ||
                listBoxItem.DataContext is not TabStripItemViewModel targetItem ||
                !listBoxItem.IsVisible ||
                listBoxItem.ActualWidth <= 0 ||
                listBoxItem.ActualHeight <= 0)
            {
                continue;
            }

            var topLeft = listBoxItem.PointToScreen(new Point(0, 0));
            var bottomRight = listBoxItem.PointToScreen(new Point(listBoxItem.ActualWidth, listBoxItem.ActualHeight));
            yield return new TabStripItemBounds(
                targetItem,
                Math.Min(topLeft.X, bottomRight.X),
                Math.Max(topLeft.X, bottomRight.X),
                Math.Min(topLeft.Y, bottomRight.Y),
                Math.Max(topLeft.Y, bottomRight.Y));
        }
    }

    private bool TryGetTabStripItemAtScreenPoint(NativePoint screenPoint, out TabStripItemViewModel? tabStripItem)
    {
        tabStripItem = null;
        if (!TabListBox.IsVisible ||
            TabListBox.ActualWidth <= 0 ||
            TabListBox.ActualHeight <= 0)
        {
            return false;
        }

        foreach (var item in TabListBox.Items)
        {
            if (TabListBox.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem listBoxItem ||
                listBoxItem.DataContext is not TabStripItemViewModel targetItem ||
                !listBoxItem.IsVisible ||
                listBoxItem.ActualWidth <= 0 ||
                listBoxItem.ActualHeight <= 0 ||
                !IsScreenPointOverElement(listBoxItem, screenPoint))
            {
                continue;
            }

            tabStripItem = targetItem;
            return true;
        }

        return false;
    }

    private bool HasPotentialPictureInPictureDropTarget(IReadOnlyCollection<StreamTabViewModel> draggedTabs)
    {
        if (draggedTabs.Count == 0 ||
            detachedWindows.Count == 0)
        {
            return false;
        }

        var draggedSet = draggedTabs.ToHashSet();
        return detachedWindows
            .Where(pair => !draggedSet.Contains(pair.Key))
            .Select(pair => pair.Value)
            .Distinct()
            .Any(window => !window.IsClosing);
    }

    private bool HasExceededTabDetachDragDistance(NativePoint screenPoint)
    {
        return HasExceededDragDistance(tabDetachDragStartScreenPoint, screenPoint);
    }

    private static bool HasExceededDragDistance(NativePoint startPoint, NativePoint screenPoint)
    {
        return Math.Abs(screenPoint.X - startPoint.X) >= GetSystemMetrics(SmCxDrag) ||
            Math.Abs(screenPoint.Y - startPoint.Y) >= GetSystemMetrics(SmCyDrag);
    }

    private bool IsScreenPointOutsideTabStrip(NativePoint screenPoint)
    {
        if (!TabListBox.IsVisible || TabListBox.ActualWidth <= 0 || TabListBox.ActualHeight <= 0)
        {
            return false;
        }

        var topLeft = TabListBox.PointToScreen(new Point(0, 0));
        var bottomRight = TabListBox.PointToScreen(new Point(TabListBox.ActualWidth, TabListBox.ActualHeight));
        var transformToDevice = PresentationSource.FromVisual(TabListBox)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var marginX = TabDetachOuterMargin * transformToDevice.M11;
        var marginY = TabDetachOuterMargin * transformToDevice.M22;
        var left = Math.Min(topLeft.X, bottomRight.X) - marginX;
        var right = Math.Max(topLeft.X, bottomRight.X) + marginX;
        var top = Math.Min(topLeft.Y, bottomRight.Y) - marginY;
        var bottom = Math.Max(topLeft.Y, bottomRight.Y) + marginY;

        return screenPoint.X < left ||
            screenPoint.X > right ||
            screenPoint.Y < top ||
            screenPoint.Y > bottom;
    }

    private NativePoint PointToNativeScreenPoint(Point windowPoint)
    {
        var screenPoint = PointToScreen(windowPoint);
        return new NativePoint((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));
    }

    private NativePoint ClientMessagePointToNativeScreenPoint(IntPtr lParam)
    {
        var value = lParam.ToInt32();
        var clientX = (short)(value & 0xFFFF);
        var clientY = (short)((value >> 16) & 0xFFFF);
        var hwnd = windowHandle != IntPtr.Zero
            ? windowHandle
            : new WindowInteropHelper(this).Handle;
        var point = new WindowPoint
        {
            X = clientX,
            Y = clientY
        };
        return hwnd != IntPtr.Zero && ClientToScreen(hwnd, ref point)
            ? new NativePoint(point.X, point.Y)
            : PointToNativeScreenPoint(new Point(clientX, clientY));
    }

    private static bool IsLeftMouseButtonPressed()
    {
        return IsAsyncKeyPressed(VkLeftButton);
    }

    private static bool IsControlModifierPressed()
    {
        return Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
            IsAsyncKeyPressed(VkControl) ||
            IsAsyncKeyPressed(VkLeftControl) ||
            IsAsyncKeyPressed(VkRightControl);
    }

    private static bool IsAsyncKeyPressed(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private static NativePoint? GetCursorScreenPoint()
    {
        return GetCursorPos(out var point)
            ? new NativePoint(point.X, point.Y)
            : null;
    }

    private void DetachTabToPictureInPicture(StreamTabViewModel tab, Point screenPoint, bool continueDrag)
    {
        if (viewModel is null || !viewModel.Tabs.Contains(tab))
        {
            return;
        }

        var detachedTabs = viewModel.GetPictureInPictureDragTabs(tab)
            .Where(viewModel.Tabs.Contains)
            .Distinct()
            .ToArray();
        if (detachedTabs.Length == 0)
        {
            return;
        }

        var existingWindow = detachedTabs
            .Select(candidate => detachedWindows.TryGetValue(candidate, out var window) ? window : null)
            .FirstOrDefault(window => window is not null);
        if (existingWindow is not null)
        {
            BringDetachedWindowForward(existingWindow);
            if (continueDrag)
            {
                existingWindow.BeginInteractiveMove();
            }

            return;
        }

        if (fullscreen)
        {
            ExitFullscreenMode();
        }

        viewModel.SelectedTab = tab;
        // Do not assign Owner here. Owned WPF windows minimize with the owner.
        var detachedWindow = new DetachedVideoWindow(
            detachedTabs,
            tab,
            GetSavedPictureInPictureTopBarVisibility(tab));
        detachedWindow.IsPointerOverOverlayChat = (candidate, _, _) =>
            IsPointerOverNativeOverlay(candidate);

        var hasExistingDetachedWindow = detachedWindows.Values.Any(window => !window.IsClosing);
        var usedSavedLocation = PositionDetachedWindow(
            detachedWindow,
            screenPoint,
            useSavedLocation: !hasExistingDetachedWindow);
        var restoreFullscreenMode = usedSavedLocation
            ? GetSavedDetachedWindowFullscreenMode()
            : null;
        foreach (var detachedTab in detachedTabs)
        {
            detachedWindows[detachedTab] = detachedWindow;
        }

        detachedWindow.RestorableBoundsChanged += (_, _) => RememberPictureInPictureWindowBounds(detachedWindow);
        detachedWindow.StateChanged += (_, _) => RememberPictureInPictureWindowBounds(detachedWindow);
        detachedWindow.Closing += async (_, _) => await RememberPictureInPictureWindowBoundsAsync(detachedWindow);
        detachedWindow.ReattachRequested += (_, _) => ReattachDetachedWindow(detachedWindow);
        detachedWindow.VisibleTabsChanged += (_, _) => SyncPictureInPictureVisibleTabGroup(detachedWindow);
        detachedWindow.TopBarVisibilityChanged += DetachedWindowOnTopBarVisibilityChanged;
        detachedWindow.VideoMoveCandidateChanged += DetachedWindowOnVideoMoveCandidateChanged;
        detachedWindow.TabActivated += activatedTab =>
        {
            if (viewModel?.Tabs.Contains(activatedTab) == true)
            {
                viewModel.SelectedTab = activatedTab;
            }
        };

        detachedWindow.Show();
        detachedWindow.UpdateLayout();
        if (restoreFullscreenMode is { } fullscreenModeToRestore)
        {
            RestoreDetachedWindowFullscreen(detachedWindow, fullscreenModeToRestore);
        }

        detachedWindow.AttachVideoSurface();

        viewModel.SetPictureInPictureTabGroup(detachedWindow.Tabs);
        SyncPictureInPictureVisibleTabGroup(detachedWindow);
        if (!viewModel.SetTabsDetached(detachedTabs, detached: true))
        {
            RemoveDetachedWindowMappings(detachedWindow);
            detachedWindow.CloseForTabDisposal();
            return;
        }

        VideoViewport.UpdateLayout();
        BringDetachedWindowForward(detachedWindow);
        if (continueDrag && !usedSavedLocation)
        {
            detachedWindow.BeginInteractiveMove();
        }
    }

    private void ReattachDetachedWindow(DetachedVideoWindow detachedWindow)
    {
        if (viewModel is null)
        {
            return;
        }

        var detachedTabs = detachedWindow.Tabs
            .Where(viewModel.Tabs.Contains)
            .Distinct()
            .ToArray();
        RemoveDetachedWindowMappings(detachedWindow);
        if (detachedTabs.Length == 0)
        {
            return;
        }

        ShowMainWindow();
        viewModel.SetTabsDetached(detachedTabs, detached: false);
        viewModel.SelectedTab = detachedWindow.ActiveTab is { } activeTab && viewModel.Tabs.Contains(activeTab)
            ? activeTab
            : detachedTabs[0];
        VideoViewport.UpdateLayout();
    }

    private bool PositionDetachedWindow(DetachedVideoWindow window, Point screenPoint, bool useSavedLocation)
    {
        if (useSavedLocation && viewModel?.Settings.PictureInPictureWindowLocation is { } savedLocation)
        {
            if (savedLocation.IsFullscreen)
            {
                var fullscreenWorkingArea = TryGetSavedPictureInPictureFullscreenWorkingArea(
                    savedLocation,
                    out var savedFullscreenWorkingArea)
                    ? savedFullscreenWorkingArea
                    : GetMonitorWorkingAreaAtDeviceIndependentPoint(new Point(savedLocation.Left, savedLocation.Top));
                var fullscreenRestoreBounds = GetSavedDetachedFullscreenRestoreBounds(
                    window,
                    savedLocation,
                    fullscreenWorkingArea);
                ApplyDetachedWindowBounds(window, fullscreenRestoreBounds, fullscreenWorkingArea);
                return true;
            }

            var savedPoint = new Point(savedLocation.Left, savedLocation.Top);
            var savedWorkingArea = GetMonitorWorkingAreaAtDeviceIndependentPoint(savedPoint);
            var savedSize = TryGetSavedDetachedWindowSize(window, savedLocation, savedWorkingArea, out var restoredSize)
                ? restoredSize
                : GetDetachedWindowSize(window, savedWorkingArea);
            ApplyDetachedWindowBounds(window, savedLocation.Left, savedLocation.Top, savedSize, savedWorkingArea);
            return true;
        }

        var workingArea = GetMonitorWorkingAreaAtScreenPoint(screenPoint);
        var size = GetDetachedWindowSize(window, workingArea);
        var dipPoint = this.ToDeviceIndependentPoint(screenPoint);
        var bounds = GetAvailableDetachedWindowBounds(new Rect(
            dipPoint.X - size.Width / 2,
            dipPoint.Y - DetachedWindowTitleBarHeight / 2,
            size.Width,
            size.Height), workingArea);
        ApplyDetachedWindowBounds(window, bounds, workingArea);
        return false;
    }

    private PictureInPictureFullscreenMode? GetSavedDetachedWindowFullscreenMode()
    {
        var savedLocation = viewModel?.Settings.PictureInPictureWindowLocation;
        return savedLocation?.IsFullscreen == true
            ? savedLocation.FullscreenMode
            : null;
    }

    private bool GetSavedPictureInPictureTopBarVisibility(StreamTabViewModel tab)
    {
        return viewModel?.Settings.StreamPictureInPictureTopBarVisibility.TryGetValue(
            tab.Target.StateKey,
            out var showTopBar) == true && showTopBar;
    }

    private void DetachedWindowOnTopBarVisibilityChanged(StreamTabViewModel tab, bool showTopBar)
    {
        if (viewModel?.Tabs.Contains(tab) == true)
        {
            _ = viewModel.RememberStreamPictureInPictureTopBarVisibilityAsync(tab.Target, showTopBar);
        }
    }

    private void DetachedWindowOnVideoMoveCandidateChanged(object? sender, EventArgs e)
    {
        UpdateLowLevelMouseMoveRouteState();
    }

    private static void RestoreDetachedWindowFullscreen(
        DetachedVideoWindow window,
        PictureInPictureFullscreenMode fullscreenMode)
    {
        if (fullscreenMode == PictureInPictureFullscreenMode.MultiView)
        {
            window.EnterMultiViewFullscreen();
        }
        else
        {
            window.EnterStreamFullscreen();
        }

        window.UpdateLayout();
    }

    private static Size GetDetachedWindowSize(DetachedVideoWindow window, Rect workingArea)
    {
        var aspectRatio = double.IsFinite(window.ContentAspectRatio) && window.ContentAspectRatio > 0.2
            ? window.ContentAspectRatio
            : 16.0 / 9.0;
        var titleBarHeight = window.IsTopBarShown ? DetachedWindowTitleBarHeight : 0;
        var width = Math.Min(DetachedWindowDefaultWidth, Math.Max(window.MinWidth, workingArea.Width));
        var height = Math.Max(
            window.MinHeight,
            titleBarHeight + width / aspectRatio);
        if (height > workingArea.Height)
        {
            height = workingArea.Height;
            width = Math.Max(
                window.MinWidth,
                (height - titleBarHeight) * aspectRatio);
        }

        return PictureInPictureWindowSizing.FitWindowSize(
            new Size(width, height),
            aspectRatio,
            leftInset: 0,
            topInset: titleBarHeight,
            rightInset: 0,
            bottomInset: 0,
            window.MinWidth,
            window.MinHeight);
    }

    private static bool TryGetSavedDetachedWindowSize(
        DetachedVideoWindow window,
        PictureInPictureWindowLocation savedLocation,
        Rect workingArea,
        out Size size)
    {
        size = default;
        if (!IsUsableWindowLength(savedLocation.Width) ||
            !IsUsableWindowLength(savedLocation.Height))
        {
            return false;
        }

        var maxWidth = Math.Max(window.MinWidth, workingArea.Width);
        var maxHeight = Math.Max(window.MinHeight, workingArea.Height);
        var requestedSize = new Size(
            ClampWindowCoordinate(savedLocation.Width, window.MinWidth, maxWidth),
            ClampWindowCoordinate(savedLocation.Height, window.MinHeight, maxHeight));
        var aspectRatio = double.IsFinite(window.ContentAspectRatio) && window.ContentAspectRatio > 0.2
            ? window.ContentAspectRatio
            : 16.0 / 9.0;
        var titleBarHeight = window.IsTopBarShown ? DetachedWindowTitleBarHeight : 0;
        size = PictureInPictureWindowSizing.FitWindowSize(
            requestedSize,
            aspectRatio,
            leftInset: 0,
            topInset: titleBarHeight,
            rightInset: 0,
            bottomInset: 0,
            window.MinWidth,
            window.MinHeight);
        return true;
    }

    private static void ApplyDetachedWindowBounds(DetachedVideoWindow window, double left, double top, Size size, Rect workingArea)
    {
        var width = size.Width;
        var height = size.Height;
        window.Width = width;
        window.Height = height;
        window.Left = ClampWindowCoordinate(left, workingArea.Left, workingArea.Right - width);
        window.Top = ClampWindowCoordinate(top, workingArea.Top, workingArea.Bottom - height);
    }

    private static void ApplyDetachedWindowBounds(DetachedVideoWindow window, Rect bounds, Rect workingArea)
    {
        ApplyDetachedWindowBounds(window, bounds.Left, bounds.Top, bounds.Size, workingArea);
    }

    private Rect GetAvailableDetachedWindowBounds(Rect preferredBounds, Rect workingArea)
    {
        var existingBounds = detachedWindows.Values
            .Distinct()
            .Where(window => !window.IsClosing)
            .Select(window => window.GetRestorableBounds())
            .Where(IsUsableWindowBounds)
            .ToArray();
        var bounds = ClampDetachedWindowBounds(preferredBounds, workingArea);
        if (existingBounds.Length == 0 || !HasDuplicateDetachedWindowPosition(bounds, existingBounds))
        {
            return bounds;
        }

        for (var attempt = 1; attempt <= DetachedWindowCascadeAttempts; attempt++)
        {
            var offset = DetachedWindowCascadeOffset * attempt;
            var candidate = ClampDetachedWindowBounds(
                new Rect(
                    preferredBounds.Left + offset,
                    preferredBounds.Top + offset,
                    preferredBounds.Width,
                    preferredBounds.Height),
                workingArea);
            if (!HasDuplicateDetachedWindowPosition(candidate, existingBounds))
            {
                return candidate;
            }
        }

        return bounds;
    }

    private static Rect ClampDetachedWindowBounds(Rect bounds, Rect workingArea)
    {
        return new Rect(
            ClampWindowCoordinate(bounds.Left, workingArea.Left, workingArea.Right - bounds.Width),
            ClampWindowCoordinate(bounds.Top, workingArea.Top, workingArea.Bottom - bounds.Height),
            bounds.Width,
            bounds.Height);
    }

    private static bool HasDuplicateDetachedWindowPosition(Rect bounds, IReadOnlyList<Rect> existingBounds)
    {
        return existingBounds.Any(existing =>
            Math.Abs(existing.Left - bounds.Left) < DetachedWindowCascadeDuplicateTolerance &&
            Math.Abs(existing.Top - bounds.Top) < DetachedWindowCascadeDuplicateTolerance);
    }

    private static Rect GetSavedDetachedFullscreenRestoreBounds(
        DetachedVideoWindow window,
        PictureInPictureWindowLocation savedLocation,
        Rect workingArea)
    {
        var size = TryGetSavedDetachedWindowSize(window, savedLocation, workingArea, out var restoredSize)
            ? restoredSize
            : GetDetachedWindowSize(window, workingArea);
        var savedBounds = new Rect(new Point(savedLocation.Left, savedLocation.Top), size);
        if (ContainsWindowCenter(workingArea, savedBounds))
        {
            return savedBounds;
        }

        return new Rect(
            workingArea.Left + (workingArea.Width - size.Width) / 2,
            workingArea.Top + (workingArea.Height - size.Height) / 2,
            size.Width,
            size.Height);
    }

    private static bool ContainsWindowCenter(Rect area, Rect bounds)
    {
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        return center.X >= area.Left &&
            center.X <= area.Right &&
            center.Y >= area.Top &&
            center.Y <= area.Bottom;
    }

    private void RememberPictureInPictureWindowBounds(DetachedVideoWindow window)
    {
        if (viewModel is null ||
            !TryGetPictureInPictureWindowLocation(window, out var location))
        {
            return;
        }

        viewModel.RememberPictureInPictureWindowBounds(location);
    }

    private async Task RememberPictureInPictureWindowBoundsAsync(DetachedVideoWindow window)
    {
        if (viewModel is null ||
            !TryGetPictureInPictureWindowLocation(window, out var location))
        {
            return;
        }

        await viewModel.RememberPictureInPictureWindowBoundsAsync(location);
    }

    private bool TryGetPictureInPictureWindowLocation(
        DetachedVideoWindow window,
        out PictureInPictureWindowLocation location)
    {
        location = new PictureInPictureWindowLocation();
        var bounds = window.GetRestorableBounds();
        if (!IsUsableWindowBounds(bounds))
        {
            return false;
        }

        var previousLocation = viewModel?.Settings.PictureInPictureWindowLocation;
        var previousFullscreenScreen = previousLocation?.FullscreenScreen;
        var isFullscreen = window.IsStreamFullscreen ||
            window.WindowState == WindowState.Maximized ||
            (window.IsClosing && previousLocation?.IsFullscreen == true);
        location = new PictureInPictureWindowLocation(
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height)
        {
            IsFullscreen = isFullscreen,
            FullscreenMode = isFullscreen
                ? window.GetRestorableFullscreenMode()
                : PictureInPictureFullscreenMode.StreamOnly,
            FullscreenScreen = isFullscreen && TryGetPictureInPictureFullscreenScreen(window, out var fullscreenScreen)
                ? fullscreenScreen
                : previousFullscreenScreen
        };
        return true;
    }

    private static bool TryGetPictureInPictureFullscreenScreen(
        DetachedVideoWindow window,
        out PictureInPictureFullscreenScreen fullscreenScreen)
    {
        fullscreenScreen = new PictureInPictureFullscreenScreen();
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var screen = System.Windows.Forms.Screen.FromHandle(handle);
        if (screen is null)
        {
            return false;
        }

        fullscreenScreen = new PictureInPictureFullscreenScreen(
            screen.DeviceName,
            screen.Bounds.Left,
            screen.Bounds.Top,
            screen.Bounds.Width,
            screen.Bounds.Height);
        return true;
    }

    private bool TryGetSavedPictureInPictureFullscreenWorkingArea(
        PictureInPictureWindowLocation savedLocation,
        out Rect workingArea)
    {
        workingArea = default;
        if (savedLocation.FullscreenScreen is not { } savedScreen ||
            !TryFindSavedPictureInPictureFullscreenScreen(savedScreen, out var screen))
        {
            return false;
        }

        workingArea = this.ToDeviceIndependentRect(new NativeRectangle
        {
            Left = screen.WorkingArea.Left,
            Top = screen.WorkingArea.Top,
            Right = screen.WorkingArea.Right,
            Bottom = screen.WorkingArea.Bottom
        });
        return true;
    }

    private static bool TryFindSavedPictureInPictureFullscreenScreen(
        PictureInPictureFullscreenScreen savedScreen,
        out System.Windows.Forms.Screen screen)
    {
        screen = System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0];
        if (!string.IsNullOrWhiteSpace(savedScreen.DeviceName))
        {
            var matchingScreen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(candidate =>
                string.Equals(candidate.DeviceName, savedScreen.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (matchingScreen is not null)
            {
                screen = matchingScreen;
                return true;
            }
        }

        if (!IsUsableWindowLength(savedScreen.Width) || !IsUsableWindowLength(savedScreen.Height))
        {
            return false;
        }

        var center = new System.Drawing.Point(
            (int)Math.Round(savedScreen.Left + savedScreen.Width / 2),
            (int)Math.Round(savedScreen.Top + savedScreen.Height / 2));
        screen = System.Windows.Forms.Screen.FromPoint(center);
        return true;
    }

    private static double ClampWindowCoordinate(double value, double min, double max)
    {
        return max < min ? min : Math.Clamp(value, min, max);
    }

    private static bool IsUsableWindowLength(double value)
    {
        return double.IsFinite(value) && value > 0;
    }

    private void BringDetachedWindowForward(DetachedVideoWindow window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
        var wasTopmost = window.Topmost;
        window.Topmost = true;
        window.Topmost = wasTopmost;
    }

    private void ViewModelTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            CloseAllDetachedWindows(reattach: false);
            return;
        }

        if (e.Action is not (NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace) ||
            e.OldItems is null)
        {
            return;
        }

        foreach (StreamTabViewModel tab in e.OldItems)
        {
            CloseDetachedWindowForTab(tab, reattach: false);
        }
    }

    private void CloseAllDetachedWindows(bool reattach)
    {
        foreach (var window in detachedWindows.Values.Distinct().ToArray())
        {
            CloseDetachedWindow(window, reattach);
        }
    }

    private void CloseDetachedWindowForTab(StreamTabViewModel tab, bool reattach)
    {
        if (!detachedWindows.TryGetValue(tab, out var window))
        {
            return;
        }

        if (reattach)
        {
            window.Close();
            return;
        }

        detachedWindows.Remove(tab);
        window.RemoveTabForDisposal(tab);
        viewModel?.ClearPictureInPictureVisibleTabGroup([tab]);
        viewModel?.ClearPictureInPictureTabGroup([tab]);
        if (window.TabCount == 0)
        {
            RemoveDetachedWindowMappings(window);
            window.CloseForTabDisposal();
        }
    }

    private void CloseDetachedWindow(DetachedVideoWindow window, bool reattach)
    {
        if (reattach)
        {
            window.Close();
            return;
        }

        RemoveDetachedWindowMappings(window);
        window.CloseForTabDisposal();
    }

    private void RemoveDetachedWindowMappings(DetachedVideoWindow window)
    {
        window.TopBarVisibilityChanged -= DetachedWindowOnTopBarVisibilityChanged;
        window.VideoMoveCandidateChanged -= DetachedWindowOnVideoMoveCandidateChanged;
        viewModel?.ClearPictureInPictureVisibleTabGroup(window.Tabs);
        viewModel?.ClearPictureInPictureTabGroup(window.Tabs);
        foreach (var tab in detachedWindows
            .Where(pair => ReferenceEquals(pair.Value, window))
            .Select(pair => pair.Key)
            .ToArray())
        {
            detachedWindows.Remove(tab);
        }

        UpdateLowLevelMouseMoveRouteState();
    }

    private void SyncPictureInPictureVisibleTabGroup(DetachedVideoWindow window)
    {
        if (viewModel is null || window.IsClosing)
        {
            return;
        }

        viewModel.SetPictureInPictureVisibleTabGroup(window.VisibleTabs);
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel?.SelectedTab is not null)
        {
            viewModel.SelectedTab.IsMuted = !viewModel.SelectedTab.IsMuted;
        }
    }

    private void ReplaySeekSlider_BeginPreview(object sender, MouseButtonEventArgs e)
    {
        if (TryGetReplaySeekTab(sender, out var tab, out var slider))
        {
            replaySeekPointerCommitPending = true;
            tab.BeginReplaySeekPreview(slider.Value);
        }
    }

    private void ReplaySeekThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (TryGetReplaySeekTab(sender, out var tab, out var slider))
        {
            replaySeekPointerCommitPending = true;
            tab.BeginReplaySeekPreview(slider.Value);
        }
    }

    private async void ReplaySeekThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        await CommitReplaySeekAsync(sender, requirePointerPreview: true);
    }

    private async void ReplaySeekSlider_Commit(object sender, MouseButtonEventArgs e)
    {
        await CommitReplaySeekAsync(sender, requirePointerPreview: true);
    }

    private async void ReplaySeekSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown))
        {
            return;
        }

        await CommitReplaySeekAsync(sender);
    }

    private async Task CommitReplaySeekAsync(object sender, bool requirePointerPreview = false)
    {
        if (requirePointerPreview)
        {
            if (!replaySeekPointerCommitPending)
            {
                return;
            }

            replaySeekPointerCommitPending = false;
        }

        if (!TryGetReplaySeekTab(sender, out var tab, out var slider))
        {
            return;
        }

        await tab.CommitReplaySeekPreviewAsync(slider.Value);
    }

    private bool TryGetReplaySeekTab(object sender, out StreamTabViewModel tab, out Slider slider)
    {
        tab = null!;
        slider = null!;
        var candidateSlider = sender as Slider;
        if (candidateSlider is null && sender is DependencyObject dependencyObject)
        {
            candidateSlider = FindVisualParent<Slider>(dependencyObject);
        }

        if (candidateSlider is not { IsEnabled: true } ||
            viewModel?.SelectedTab is not { } selectedTab)
        {
            return false;
        }

        tab = selectedTab;
        slider = candidateSlider;
        return true;
    }

    private void VideoSurface_MouseWheelScrolled(object sender, VideoSurfaceMouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: StreamTabViewModel tab } || e.Delta == 0)
        {
            return;
        }

        AdjustVolume(tab, e.Delta);
    }

    private void VideoSurface_MouseLeftButtonDoubleClicked(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement { Tag: StreamTabViewModel tab } && viewModel is not null)
        {
            viewModel.SelectedTab = tab;
        }

        ToggleStreamFullscreenFromVideoDoubleClick();
    }

    private void VideoViewport_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && ToggleStreamFullscreenFromVideoDoubleClick())
        {
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            _ = BeginVideoReorderDragCandidate(PointToNativeScreenPoint(e.GetPosition(this)));
        }
    }

    private void DockedChatPanel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
        {
            return;
        }

        if (!IsPointOverElement(DockedChatListBox, e.GetPosition(DockedChatListBox)))
        {
            return;
        }

        ScrollDockedChat(e.Delta);
        e.Handled = true;
    }

    private void DockedChatResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (viewModel is not null)
        {
            var currentWidth = viewModel.Settings.Chat.DockWidth;
            viewModel.Settings.Chat.DockWidth = ChatSettings.NormalizeDockWidth(currentWidth - e.HorizontalChange);
        }

        e.Handled = true;
    }

    private void ChatListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        DockedChatPanel_PreviewMouseWheel(sender, e);
    }

    private void DockedChatScrollThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        dockedChatScrollThumbDragging = true;
        dockedChatForceScrollPending = false;
        dockedChatManualScrollOverride = true;
        dockedChatShouldFollowBottom = false;
        CaptureDockedChatScrollAnchor();
    }

    private void DockedChatScrollThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        dockedChatScrollThumbDragging = false;
        EnsureDockedChatScrollViewer();
        var scrollViewer = dockedChatScrollViewer;
        if (scrollViewer is null)
        {
            return;
        }

        UpdateDockedChatManualScrollState(scrollViewer, scrollViewer.VerticalOffset);
        if (dockedChatShouldFollowBottom)
        {
            QueueDockedChatScrollToBottom(force: true);
        }
    }

    private void EnsureDockedChatScrollViewer()
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(DockedChatListBox);
        if (scrollViewer is null || ReferenceEquals(scrollViewer, dockedChatScrollViewer))
        {
            return;
        }

        if (dockedChatScrollViewer is not null)
        {
            dockedChatScrollViewer.ScrollChanged -= DockedChatScrollViewerOnScrollChanged;
        }

        dockedChatScrollViewer = scrollViewer;
        dockedChatScrollViewer.ScrollChanged += DockedChatScrollViewerOnScrollChanged;
    }

    private void DockedChatScrollViewerOnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        if (dockedChatManualScrollOverride || dockedChatScrollThumbDragging)
        {
            UpdateDockedChatManualScrollState(scrollViewer, scrollViewer.VerticalOffset);
            return;
        }

        dockedChatShouldFollowBottom = true;
        dockedChatAnchorItem = null;
        if (!IsDockedChatAtBottom(scrollViewer, scrollViewer.VerticalOffset))
        {
            QueueDockedChatScrollToBottom(force: true);
        }
    }

    private void DockedChatItemsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or
            NotifyCollectionChangedAction.Remove or
            NotifyCollectionChangedAction.Reset or
            NotifyCollectionChangedAction.Replace)
        {
            if (dockedChatManualScrollOverride || !dockedChatShouldFollowBottom)
            {
                QueueDockedChatAnchorRestore();
                return;
            }

            QueueDockedChatScrollToBottom(force: false);
        }
    }

    private void LockDockedChatToBottom()
    {
        dockedChatManualScrollOverride = false;
        dockedChatShouldFollowBottom = true;
        dockedChatAnchorItem = null;
    }

    private void UpdateDockedChatManualScrollState(ScrollViewer scrollViewer, double verticalOffset)
    {
        if (IsDockedChatAtBottom(scrollViewer, verticalOffset) && !dockedChatScrollThumbDragging)
        {
            LockDockedChatToBottom();
            return;
        }

        dockedChatManualScrollOverride = true;
        dockedChatShouldFollowBottom = false;
        if (IsDockedChatAtBottom(scrollViewer, verticalOffset))
        {
            dockedChatAnchorItem = null;
        }
        else
        {
            CaptureDockedChatScrollAnchor();
        }
    }

    private void QueueDockedChatScrollToBottom(bool force)
    {
        dockedChatForceScrollPending |= force;
        if (dockedChatScrollPending)
        {
            return;
        }

        dockedChatScrollPending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            var shouldScroll = dockedChatForceScrollPending || dockedChatShouldFollowBottom;
            dockedChatScrollPending = false;
            dockedChatForceScrollPending = false;

            if (shouldScroll)
            {
                ScrollDockedChatToBottom();
            }
        }));
    }

    private void QueueDockedChatAnchorRestore()
    {
        if (dockedChatAnchorItem is null && !CaptureDockedChatScrollAnchor())
        {
            return;
        }

        if (dockedChatAnchorRestorePending)
        {
            return;
        }

        dockedChatAnchorRestorePending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            dockedChatAnchorRestorePending = false;
            RestoreDockedChatScrollAnchor();
        }));
    }

    private bool CaptureDockedChatScrollAnchor()
    {
        EnsureDockedChatScrollViewer();
        var scrollViewer = dockedChatScrollViewer;
        if (scrollViewer is null)
        {
            return false;
        }

        foreach (var item in DockedChatListBox.Items)
        {
            if (DockedChatListBox.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container ||
                container.RenderSize.Height <= 0)
            {
                continue;
            }

            var top = container.TransformToAncestor(scrollViewer).Transform(new Point(0, 0)).Y;
            var bottom = top + container.RenderSize.Height;
            if (bottom <= 0 || top >= scrollViewer.ViewportHeight)
            {
                continue;
            }

            dockedChatAnchorItem = item;
            dockedChatAnchorTop = top;
            return true;
        }

        dockedChatAnchorItem = null;
        return false;
    }

    private void RestoreDockedChatScrollAnchor()
    {
        EnsureDockedChatScrollViewer();
        var scrollViewer = dockedChatScrollViewer;
        if (scrollViewer is null || dockedChatAnchorItem is null)
        {
            return;
        }

        DockedChatListBox.UpdateLayout();
        if (DockedChatListBox.ItemContainerGenerator.ContainerFromItem(dockedChatAnchorItem) is not FrameworkElement container)
        {
            dockedChatAnchorItem = null;
            return;
        }

        var currentTop = container.TransformToAncestor(scrollViewer).Transform(new Point(0, 0)).Y;
        var targetOffset = Math.Clamp(
            scrollViewer.VerticalOffset + currentTop - dockedChatAnchorTop,
            0,
            scrollViewer.ScrollableHeight);

        if (Math.Abs(targetOffset - scrollViewer.VerticalOffset) > double.Epsilon)
        {
            scrollViewer.ScrollToVerticalOffset(targetOffset);
        }

        dockedChatShouldFollowBottom = IsDockedChatAtBottom(scrollViewer, targetOffset);
        if (dockedChatShouldFollowBottom)
        {
            LockDockedChatToBottom();
        }
        else
        {
            dockedChatManualScrollOverride = true;
            DockedChatListBox.UpdateLayout();
            CaptureDockedChatScrollAnchor();
        }
    }

    private void TabListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
        {
            return;
        }

        ScrollTabs(e.Delta);
        e.Handled = true;
    }

    internal bool TryRouteMouseWheel(int screenX, int screenY, int delta)
    {
        return TryRouteMouseWheel(new NativePoint(screenX, screenY), delta);
    }

    private bool TryRouteMouseWheel(NativePoint screenPoint, int delta)
    {
        if (delta == 0 || !IsActive || windowHandle == IntPtr.Zero || GetForegroundWindow() != windowHandle)
        {
            return false;
        }

        if (IsScreenPointOverElement(TabListBox, screenPoint))
        {
            ScrollTabs(delta);
            return true;
        }

        if (IsScreenPointOverElement(DockedChatListBox, screenPoint))
        {
            ScrollDockedChat(delta);
            return true;
        }

        var videoTab = GetVideoTabAtScreenPoint(screenPoint);
        if (videoTab is not null && TryRouteNativeOverlayWheel(videoTab, screenPoint, delta))
        {
            return true;
        }

        if (videoTab is not null && IsScreenPointOverVideoContent(videoTab, screenPoint))
        {
            AdjustVolume(videoTab, delta);
            return true;
        }

        return false;
    }

    private bool TryRouteDetachedMouseWheel(NativePoint screenPoint, int delta)
    {
        if (delta == 0)
        {
            return false;
        }

        foreach (var window in detachedWindows.Values.Distinct().ToArray())
        {
            if (window.TryRouteMouseWheel(screenPoint.X, screenPoint.Y, delta))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryBeginDetachedBottomResizeFromScreenClick(NativePoint screenPoint)
    {
        foreach (var window in detachedWindows.Values.Distinct().ToArray())
        {
            if (window.TryBeginBottomResizeFromScreenClick(screenPoint.X, screenPoint.Y))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryBeginDetachedVideoMoveFromScreenClick(NativePoint screenPoint)
    {
        var started = false;
        foreach (var window in detachedWindows.Values.Distinct().ToArray())
        {
            started |= window.TryBeginVideoMoveFromScreenClick(screenPoint.X, screenPoint.Y);
        }

        return started;
    }

    private bool TryContinueDetachedVideoMove(NativePoint screenPoint)
    {
        foreach (var window in detachedWindows.Values.Distinct().ToArray())
        {
            if (window.HasVideoMoveCandidate &&
                window.TryContinueVideoMove(screenPoint.X, screenPoint.Y))
            {
                return true;
            }
        }

        return false;
    }

    private void CancelDetachedVideoMoveCandidates()
    {
        foreach (var window in detachedWindows.Values.Distinct().ToArray())
        {
            window.CancelVideoMoveCandidate();
        }
    }

    private bool TryOpenDetachedVideoContextMenu(NativePoint screenPoint)
    {
        foreach (var window in detachedWindows.Values.Distinct().ToArray())
        {
            if (window.TryOpenVideoContextMenu(screenPoint.X, screenPoint.Y))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryCaptureBrowserStreamClick(NativePoint screenPoint)
    {
        if (!browserClickFallbackEnabled)
        {
            return false;
        }

        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero ||
            foregroundWindow == windowHandle ||
            !IsSupportedBrowserWindow(foregroundWindow) ||
            !TryGetBrowserAddressUrl(foregroundWindow, out var currentUrl) ||
            !IsSupportedPlatformUrl(currentUrl))
        {
            return false;
        }

        if (!TryGetStreamTargetFromAutomationPoint(screenPoint, currentUrl, out var target) ||
            target is null)
        {
            return false;
        }

        suppressNextBrowserMouseUp = true;
        if (!IsDuplicateDetectedStream(target.Url))
        {
            DispatchToUi(() =>
            {
                if (viewModel is not null)
                {
                    ShowMainWindow();
                    _ = viewModel.OpenDetectedStreamAsync(target);
                }
            });
        }

        return true;
    }

    private bool IsDuplicateDetectedStream(string streamUrl)
    {
        var now = DateTimeOffset.UtcNow;
        if (string.Equals(lastDetectedStreamUrl, streamUrl, StringComparison.OrdinalIgnoreCase) &&
            now - lastDetectedStreamAt < BrowserClickDuplicateWindow)
        {
            return true;
        }

        lastDetectedStreamUrl = streamUrl;
        lastDetectedStreamAt = now;
        return false;
    }

    private static bool TryGetStreamTargetFromAutomationPoint(
        NativePoint screenPoint,
        string currentUrl,
        out StreamTarget? target)
    {
        target = null;

        try
        {
            var element = AutomationElement.FromPoint(new Point(screenPoint.X, screenPoint.Y));
            for (var depth = 0; element is not null && depth < 12; depth++)
            {
                if (TryGetStreamTargetFromAutomationElement(element, currentUrl, out target))
                {
                    return true;
                }

                element = TreeWalker.RawViewWalker.GetParent(element);
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
        }

        return false;
    }

    private static bool TryGetStreamTargetFromAutomationElement(
        AutomationElement element,
        string currentUrl,
        out StreamTarget? target)
    {
        target = null;
        foreach (var text in GetAutomationElementTextCandidates(element))
        {
            foreach (var candidate in ExtractUrlCandidates(text))
            {
                if (TryNormalizeSupportedBrowserUrl(candidate, currentUrl, out var normalizedUrl) &&
                    StreamInputParser.TryParsePlatformUrl(normalizedUrl, out target) &&
                    target is not null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryGetBrowserAddressUrl(IntPtr browserWindow, out string normalizedUrl)
    {
        normalizedUrl = "";
        return TryFindBrowserAddressBar(browserWindow, out _, out _, out normalizedUrl);
    }

    private static bool TryFindBrowserAddressBar(
        IntPtr browserWindow,
        out AutomationElement? addressBar,
        out ValuePattern? valuePattern,
        out string normalizedUrl)
    {
        addressBar = null;
        valuePattern = null;
        normalizedUrl = "";
        if (!IsSupportedBrowserWindow(browserWindow))
        {
            return false;
        }

        try
        {
            var root = AutomationElement.FromHandle(browserWindow);
            if (root is null)
            {
                return false;
            }

            var windowBounds = root.Current.BoundingRectangle;
            var editCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
            var editElements = root.FindAll(TreeScope.Descendants, editCondition);
            for (var index = 0; index < editElements.Count; index++)
            {
                var element = editElements[index];
                if (!IsLikelyBrowserAddressBar(element, windowBounds) ||
                    !TryGetValuePattern(element, out var candidatePattern) ||
                    candidatePattern is null ||
                    !TryGetAutomationValue(candidatePattern, out var value) ||
                    !TryNormalizeSupportedBrowserUrl(value, out var candidateUrl))
                {
                    continue;
                }

                addressBar = element;
                valuePattern = candidatePattern;
                normalizedUrl = candidateUrl;
                return true;
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
        }

        return false;
    }

    private static bool IsSupportedBrowserWindow(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return SupportedBrowserProcessNames.Contains(process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static bool IsLikelyBrowserAddressBar(AutomationElement element, Rect windowBounds)
    {
        try
        {
            var bounds = element.Current.BoundingRectangle;
            if (bounds.IsEmpty || windowBounds.IsEmpty || bounds.Top > windowBounds.Top + 180 || bounds.Width < 180)
            {
                return false;
            }

            var name = element.Current.Name ?? "";
            var automationId = element.Current.AutomationId ?? "";
            if (ContainsIgnoreCase(name, "address") ||
                ContainsIgnoreCase(name, "location") ||
                ContainsIgnoreCase(name, "search") ||
                ContainsIgnoreCase(automationId, "address") ||
                ContainsIgnoreCase(automationId, "url") ||
                ContainsIgnoreCase(automationId, "urlbar"))
            {
                return true;
            }

            return bounds.Top <= windowBounds.Top + 120;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return false;
        }
    }

    private static bool TryGetValuePattern(AutomationElement element, out ValuePattern? valuePattern)
    {
        valuePattern = null;
        try
        {
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ||
                pattern is not ValuePattern candidatePattern)
            {
                return false;
            }

            valuePattern = candidatePattern;
            return true;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return false;
        }
    }

    private static bool TryGetAutomationValue(ValuePattern valuePattern, out string value)
    {
        value = "";
        try
        {
            value = valuePattern.Current.Value?.Trim() ?? "";
            return value.Length > 0;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return false;
        }
    }

    private static IEnumerable<string> GetAutomationElementTextCandidates(AutomationElement element)
    {
        string[] currentValues;
        try
        {
            currentValues =
            [
                element.Current.Name,
                element.Current.HelpText,
                element.Current.AutomationId,
                element.Current.ItemStatus,
                element.Current.ItemType,
                element.Current.ClassName
            ];
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            yield break;
        }

        foreach (var value in currentValues)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }

        if (TryGetValuePattern(element, out var valuePattern) &&
            valuePattern is not null &&
            TryGetAutomationValue(valuePattern, out var patternValue))
        {
            yield return patternValue;
        }

    }

    private static IEnumerable<string> ExtractUrlCandidates(string text)
    {
        var trimmed = text.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            yield return trimmed;
        }

        foreach (Match match in BrowserUrlCandidatePattern().Matches(text))
        {
            yield return match.Value.TrimEnd('.', ',', ';', ')', ']', '}', '"', '\'');
        }
    }

    private static bool TryNormalizeSupportedBrowserUrl(string value, out string normalizedUrl)
    {
        return TryNormalizeSupportedBrowserUrl(value, baseUrl: null, out normalizedUrl);
    }

    private static bool TryNormalizeSupportedBrowserUrl(string value, string? baseUrl, out string normalizedUrl)
    {
        normalizedUrl = "";
        var candidate = value.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            if (candidate.StartsWith("/", StringComparison.Ordinal) &&
                !candidate.StartsWith("//", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(baseUrl) &&
                Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            {
                candidate = new Uri(baseUri, candidate).ToString();
            }
            else if (StartsWithSupportedHost(candidate))
            {
                candidate = $"https://{candidate}";
            }
            else
            {
                return false;
            }
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = NormalizeSupportedBrowserHost(uri.Host);
        if (host is null)
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        normalizedUrl = $"{uri.Scheme.ToLowerInvariant()}://{host}{path}";
        return true;
    }

    private static bool IsSupportedPlatformUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            NormalizeSupportedBrowserHost(uri.Host) is not null;
    }

    private static bool StartsWithSupportedHost(string value)
    {
        return value.StartsWith("twitch.tv/", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("www.twitch.tv/", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("www.twitch.tv", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("m.twitch.tv/", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("m.twitch.tv", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("kick.com/", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("kick.com", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("www.kick.com/", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("www.kick.com", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("m.kick.com/", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("m.kick.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeSupportedBrowserHost(string host)
    {
        var normalized = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? host[4..]
            : host;

        if (normalized.Equals("m.twitch.tv", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("m.kick.com", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        if (normalized.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase))
        {
            return "www.twitch.tv";
        }

        if (normalized.Equals("kick.com", StringComparison.OrdinalIgnoreCase))
        {
            return "kick.com";
        }

        return null;
    }

    private static bool ContainsIgnoreCase(string value, string expected)
    {
        return value.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryRouteNativeOverlayWheel(StreamTabViewModel tab, NativePoint screenPoint, int delta)
    {
        if (viewModel?.Settings.Chat.Layout != ChatLayout.Overlay ||
            !tab.IsChatVisible ||
            !tab.UsesNativeOverlay)
        {
            return false;
        }

        if (!IsScreenPointOverElement(VideoViewport, screenPoint) ||
            !TryGetVideoCursorPoint(tab, out var videoPoint, out var videoWidth, out var videoHeight) ||
            !IsVideoPointInside(videoPoint, videoWidth, videoHeight) ||
            !TryGetNativeOverlayBounds(tab, videoHeight, out var overlayBounds) ||
            !overlayBounds.Contains(videoPoint))
        {
            return false;
        }

        var localPoint = new Point(videoPoint.X - overlayBounds.Left, videoPoint.Y - overlayBounds.Top);
        if (IsNativeOverlayScrollableMessagePoint(localPoint, overlayBounds.Width, overlayBounds.Height, videoHeight))
        {
            SendNativeOverlayScroll(tab, delta);
        }

        return true;
    }

    private void ScrollDockedChat(int delta)
    {
        EnsureDockedChatScrollViewer();
        var scrollViewer = dockedChatScrollViewer;
        if (scrollViewer is null)
        {
            return;
        }

        var notches = delta / (double)Mouse.MouseWheelDeltaForOneLine;
        var targetOffset = Math.Clamp(
            scrollViewer.VerticalOffset - notches * ChatPixelsPerWheelNotch,
            0,
            scrollViewer.ScrollableHeight);

        if (IsDockedChatAtBottom(scrollViewer, targetOffset) && !dockedChatScrollThumbDragging)
        {
            LockDockedChatToBottom();
        }
        else
        {
            dockedChatForceScrollPending = false;
            dockedChatManualScrollOverride = true;
            dockedChatShouldFollowBottom = false;
        }

        scrollViewer.ScrollToVerticalOffset(targetOffset);
        UpdateDockedChatManualScrollState(scrollViewer, targetOffset);
    }

    private void ScrollDockedChatToBottom()
    {
        if (!DockedChatListBox.IsLoaded)
        {
            return;
        }

        DockedChatListBox.UpdateLayout();
        EnsureDockedChatScrollViewer();
        var scrollViewer = dockedChatScrollViewer;
        if (scrollViewer is null)
        {
            return;
        }

        scrollViewer.ScrollToVerticalOffset(scrollViewer.ScrollableHeight);
        LockDockedChatToBottom();
    }

    private static bool IsDockedChatAtBottom(ScrollViewer scrollViewer, double verticalOffset)
    {
        return scrollViewer.ScrollableHeight - verticalOffset <= ChatBottomFollowTolerance;
    }

    private void ScrollTabs(int delta)
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(TabListBox);
        if (scrollViewer is null)
        {
            return;
        }

        var notches = delta / (double)Mouse.MouseWheelDeltaForOneLine;
        var targetOffset = Math.Clamp(
            scrollViewer.HorizontalOffset - notches * TabPixelsPerWheelNotch,
            0,
            scrollViewer.ScrollableWidth);

        scrollViewer.ScrollToHorizontalOffset(targetOffset);
    }

    private void AdjustVolume(StreamTabViewModel tab, int delta)
    {
        VolumeOverlay.AdjustVolume(tab, delta, VolumeOsd, ResolveVolumeOsdTarget(tab));
    }

    private UIElement ResolveVolumeOsdTarget(StreamTabViewModel tab)
    {
        return videoSurfaces.TryGetValue(tab, out var surface) && surface.IsVisible
            ? surface
            : VideoViewport;
    }

    private StreamTabViewModel? GetVideoTabAtScreenPoint(NativePoint screenPoint)
    {
        foreach (var (tab, surface) in videoSurfaces)
        {
            if (tab.IsVideoVisible && IsScreenPointOverElement(surface, screenPoint))
            {
                return tab;
            }
        }

        return null;
    }

    private bool IsScreenPointOverVideoContent(StreamTabViewModel tab, NativePoint screenPoint)
    {
        return videoSurfaces.TryGetValue(tab, out var surface) &&
            IsScreenPointOverElement(surface, screenPoint) &&
            TryGetVideoCursorPoint(tab, out var videoPoint, out var videoWidth, out var videoHeight) &&
            IsVideoPointInside(videoPoint, videoWidth, videoHeight);
    }

    private bool TryGetNativeOverlayBounds(StreamTabViewModel tab, int videoHeight, out VideoRect bounds)
    {
        bounds = default;
        if (viewModel is null)
        {
            return false;
        }

        var x = DefaultOverlayChatX;
        var y = DefaultOverlayChatY;
        var hidden = false;
        if (!string.IsNullOrWhiteSpace(tab.NativeOverlayPositionStatePath) &&
            TryReadIntFile(tab.NativeOverlayPositionStatePath, out var positionValues))
        {
            if (positionValues.Length >= 2)
            {
                x = positionValues[0];
                y = positionValues[1];
            }

            if (positionValues.Length >= 3)
            {
                hidden = positionValues[2] != 0;
            }
        }

        if (hidden)
        {
            return false;
        }

        var referenceWidth = NativeOverlaySizing.ClampReferenceWidth((int)Math.Round(viewModel.Settings.Chat.DockWidth));
        var referenceHeight = DefaultOverlayChatHeight;
        var width = ScaleOverlayReferencePixels(videoHeight, referenceWidth);
        var height = ScaleOverlayReferencePixels(videoHeight, referenceHeight);
        var sizePath = string.IsNullOrWhiteSpace(tab.NativeOverlayPositionStatePath)
            ? null
            : $"{tab.NativeOverlayPositionStatePath}.size";
        if (!string.IsNullOrWhiteSpace(sizePath) &&
            TryReadNativeOverlaySizeFile(sizePath, out var sizeWidth, out var sizeHeight, out var referenceSize))
        {
            if (referenceSize)
            {
                width = ScaleOverlayReferencePixels(videoHeight, NativeOverlaySizing.ClampReferenceWidth(sizeWidth));
                height = ScaleOverlayReferencePixels(videoHeight, NativeOverlaySizing.ClampReferenceHeight(sizeHeight));
            }
            else
            {
                width = Math.Clamp(
                    sizeWidth,
                    ScaleOverlayReferencePixels(videoHeight, NativeOverlaySizing.MinWidth),
                    ScaleOverlayReferencePixels(videoHeight, NativeOverlaySizing.MaxWidth));
                height = Math.Clamp(
                    sizeHeight,
                    ScaleOverlayReferencePixels(videoHeight, NativeOverlaySizing.MinHeight),
                    ScaleOverlayReferencePixels(videoHeight, NativeOverlaySizing.MaxHeight));
            }
        }

        bounds = new VideoRect(x, y, width, height);
        return true;
    }

    private static bool TryGetVideoCursorPoint(
        StreamTabViewModel tab,
        out Point videoPoint,
        out int videoWidth,
        out int videoHeight)
    {
        videoPoint = default;
        videoWidth = 0;
        videoHeight = 0;
        if (!tab.TryGetVideoSize(out videoWidth, out videoHeight) ||
            !tab.TryGetVideoCursor(out var x, out var y))
        {
            return false;
        }

        videoPoint = new Point(x, y);
        return true;
    }

    private static bool IsVideoPointInside(Point videoPoint, int videoWidth, int videoHeight)
    {
        return videoWidth > 0 &&
            videoHeight > 0 &&
            videoPoint.X >= 0 &&
            videoPoint.X < videoWidth &&
            videoPoint.Y >= 0 &&
            videoPoint.Y < videoHeight;
    }

    private static bool IsNativeOverlayScrollableMessagePoint(Point localPoint, int overlayWidth, int overlayHeight, int videoHeight)
    {
        var messagePadding = ScaleOverlayReferencePixels(videoHeight, OverlayMessagePadding);
        var chatInputHeight = ScaleOverlayReferencePixels(videoHeight, OverlayChatInputHeight);
        var chatInputGap = ScaleOverlayReferencePixels(videoHeight, OverlayChatInputGap);
        var buttonMargin = ScaleOverlayReferencePixels(videoHeight, OverlayButtonMargin);
        var hideButtonWidth = ScaleOverlayReferencePixels(videoHeight, OverlayHideButtonWidth);
        var hideButtonHeight = ScaleOverlayReferencePixels(videoHeight, OverlayHideButtonHeight);
        var hitInset = ScaleOverlayReferencePixels(videoHeight, (int)Math.Round(OverlayChatHitInsetPixels));

        var messageLeft = messagePadding + hitInset;
        var messageTop = messagePadding + hitInset;
        var messageRight = overlayWidth - messagePadding - hitInset;
        var messageBottom = overlayHeight - messagePadding - chatInputHeight - chatInputGap - hitInset;
        if (messageBottom <= messageTop || messageRight <= messageLeft)
        {
            return false;
        }

        if (localPoint.X < messageLeft ||
            localPoint.X >= messageRight ||
            localPoint.Y < messageTop ||
            localPoint.Y >= messageBottom)
        {
            return false;
        }

        var buttonLeft = overlayWidth - hideButtonWidth - buttonMargin;
        var buttonRight = buttonLeft + hideButtonWidth;
        var buttonTop = buttonMargin;
        var buttonBottom = buttonTop + hideButtonHeight;

        return localPoint.X < buttonLeft ||
            localPoint.X >= buttonRight ||
            localPoint.Y < buttonTop ||
            localPoint.Y >= buttonBottom;
    }

    private static int ScaleOverlayReferencePixels(int videoHeight, int value)
    {
        return NativeOverlaySizing.ScaleReferencePixels(videoHeight, value);
    }

    private static int[] ParseIntsFromText(string text)
    {
        return text
            .Split([' ', '\t', '\r', '\n', ':', ',', '{', '}'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => int.TryParse(token, out var value) ? value : (int?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
    }

    private static bool TryReadNativeOverlaySizeFile(
        string path,
        out int width,
        out int height,
        out bool referenceSize)
    {
        width = 0;
        height = 0;
        referenceSize = false;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var text = File.ReadAllText(path);
            var values = ParseIntsFromText(text);
            if (values.Length < 2)
            {
                return false;
            }

            width = values[0];
            height = values[1];
            referenceSize =
                text.Contains("reference", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("normalized", StringComparison.OrdinalIgnoreCase);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryReadIntFile(string path, out int[] values)
    {
        values = [];
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            values = ParseIntsFromText(File.ReadAllText(path));
            return values.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void SendNativeOverlayScroll(StreamTabViewModel tab, int delta)
    {
        if (string.IsNullOrWhiteSpace(tab.NativeOverlayPipeName))
        {
            return;
        }

        var notches = Math.Sign(delta) * Math.Max(1, Math.Abs(delta) / Mouse.MouseWheelDeltaForOneLine);
        try
        {
            using var pipe = new NamedPipeClientStream(".", $"{tab.NativeOverlayPipeName}_events", PipeDirection.Out);
            pipe.Connect(10);

            var buffer = NativeOverlayProtocolCodec.BuildEventMessage(
                NativeOverlayProtocolCodec.ScrollEventType,
                notches);
            pipe.Write(buffer);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsScreenPointOverElement(FrameworkElement element, NativePoint screenPoint)
    {
        return WindowInteropHelpers.IsScreenPointOverElement(element, screenPoint.X, screenPoint.Y);
    }

    private static bool IsPointOverElement(FrameworkElement element, Point point)
    {
        return element.IsVisible &&
            element.ActualWidth > 0 &&
            element.ActualHeight > 0 &&
            point.X >= 0 &&
            point.X < element.ActualWidth &&
            point.Y >= 0 &&
            point.Y < element.ActualHeight;
    }

    private Rect GetCurrentMonitorBounds(bool useWorkingArea)
    {
        var hwnd = windowHandle != IntPtr.Zero
            ? windowHandle
            : new WindowInteropHelper(this).Handle;

        if (!TryGetMonitorInfo(hwnd, out var monitorInfo))
        {
            return useWorkingArea
                ? SystemParameters.WorkArea
                : new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        }

        return this.ToDeviceIndependentRect(useWorkingArea ? monitorInfo.WorkArea : monitorInfo.Monitor);
    }

    private Rect GetMonitorWorkingAreaAtScreenPoint(Point screenPoint)
    {
        var nativePoint = new NativePoint(
            (int)Math.Round(screenPoint.X),
            (int)Math.Round(screenPoint.Y));
        var monitor = MonitorFromPoint(nativePoint, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return SystemParameters.WorkArea;
        }

        return TryGetMonitorInfoForMonitor(monitor, out var monitorInfo)
            ? this.ToDeviceIndependentRect(monitorInfo.WorkArea)
            : SystemParameters.WorkArea;
    }

    private Rect GetMonitorWorkingAreaAtDeviceIndependentPoint(Point point)
    {
        return GetMonitorWorkingAreaAtScreenPoint(this.ToDevicePoint(point));
    }

    private Rect GetRestorableWindowBounds()
    {
        if (WindowState == WindowState.Normal)
        {
            return new Rect(Left, Top, Width, Height);
        }

        return IsUsableWindowBounds(RestoreBounds)
            ? RestoreBounds
            : new Rect(Left, Top, Width, Height);
    }

    private void ApplyWindowBounds(Rect bounds)
    {
        if (!IsUsableWindowBounds(bounds))
        {
            return;
        }

        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    private void InstallMouseWheelHook()
    {
        if (mouseHookPump is not null)
        {
            return;
        }

        try
        {
            var hookDispatcher = new LowLevelMouseHookDispatcher(
                Dispatcher,
                RouteLowLevelMouseHookEvent,
                HasActiveLowLevelMouseMoveRoute);
            mouseHookPump = new LowLevelMouseHookPump(hookDispatcher);
            mouseHookPump.Start();
        }
        catch (Exception)
        {
            mouseHookPump?.Dispose();
            mouseHookPump = null;
        }
    }

    private void UninstallMouseWheelHook()
    {
        mouseHookPump?.Dispose();
        mouseHookPump = null;
    }

    internal bool HasActiveLowLevelMouseMoveRoute()
    {
        return hasActiveLowLevelMouseMoveRoute;
    }

    internal bool RouteLowLevelMouseHookEvent(LowLevelMouseHookEvent hookEvent)
    {
        var screenPoint = new NativePoint(hookEvent.ScreenX, hookEvent.ScreenY);
        if (hookEvent.Message == LowLevelMouseHookEvent.WmMouseWheel)
        {
            var delta = hookEvent.WheelDelta;
            return TryRouteDetachedMouseWheel(screenPoint, delta) ||
                TryRouteMouseWheel(screenPoint, delta);
        }

        if (hookEvent.Message == LowLevelMouseHookEvent.WmMouseMove)
        {
            _ = TryContinueDetachedVideoMove(screenPoint);

            if (videoReorderDragTab is not null)
            {
                _ = TryContinueVideoReorderDrag(screenPoint);
            }

            if (tabDetachDragTab is not null &&
                !IsScreenPointInMainWindow(screenPoint))
            {
                _ = TryContinueTabDetachDrag(screenPoint, continueDrag: true);
            }

            return false;
        }

        if (hookEvent.Message == LowLevelMouseHookEvent.WmLeftButtonDown)
        {
            if (TryBeginDetachedBottomResizeFromScreenClick(screenPoint))
            {
                return true;
            }

            if (TryToggleDetachedStreamFullscreenFromVideoDoubleClick(screenPoint))
            {
                return true;
            }

            _ = TryActivateDetachedVideoTabFromScreenClick(screenPoint);
            _ = TryBeginDetachedVideoMoveFromScreenClick(screenPoint);

            if (TryToggleStreamFullscreenFromVideoDoubleClick(screenPoint))
            {
                return true;
            }

            _ = TryActivateVideoTabFromScreenClick(screenPoint);
            _ = BeginVideoReorderDragCandidate(screenPoint);

            return TryCaptureBrowserStreamClick(screenPoint);
        }

        if (hookEvent.Message == LowLevelMouseHookEvent.WmLeftButtonUp)
        {
            CancelDetachedVideoMoveCandidates();

            if (!TryCompleteVideoReorderDrag(screenPoint))
            {
                ClearVideoReorderDrag();
            }

            if (!IsScreenPointInMainWindow(screenPoint))
            {
                _ = TryCompleteTabDetachDrag(screenPoint);
                ClearTabDetachDrag();
            }

            if (suppressNextBrowserMouseUp)
            {
                suppressNextBrowserMouseUp = false;
                return true;
            }
        }

        if (hookEvent.Message == LowLevelMouseHookEvent.WmRightButtonDown)
        {
            return TryOpenDetachedVideoContextMenu(screenPoint);
        }

        return false;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject child)
        where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent is not null)
        {
            if (parent is T match)
            {
                return match;
            }

            parent = VisualTreeHelper.GetParent(parent);
        }

        return null;
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullscreenMode(GetFullscreenButtonMode());
    }

    private void TheatreButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullscreenMode(FullscreenMode.Theatre);
    }

    private FullscreenMode GetFullscreenButtonMode()
    {
        return viewModel?.IsCurrentVideoViewMultiStream() == true
            ? FullscreenMode.MultiView
            : FullscreenMode.StreamOnly;
    }

    private void ToggleFullscreenMode(FullscreenMode requestedMode)
    {
        if (fullscreen && fullscreenMode == requestedMode)
        {
            ExitFullscreenMode();
            return;
        }

        if (!fullscreen)
        {
            EnterFullscreenWindow();
        }

        ApplyFullscreenMode(requestedMode);
    }

    private void EnterFullscreenWindow()
    {
        previousWindowState = WindowState;
        previousWindowStyle = WindowStyle;
        previousResizeMode = ResizeMode;
        previousWindowBounds = GetRestorableWindowBounds();
        previousTopmost = Topmost;
        previousTitleRowHeight = TitleRow.Height;
        previousTopControlsRowHeight = TopControlsRow.Height;

        CaptureFullscreenChatState();

        if (viewModel is not null)
        {
            viewModel.IsSettingsOpen = false;
        }

        TitleBar.Visibility = Visibility.Collapsed;
        TopControlsBar.Visibility = Visibility.Collapsed;
        TitleRow.Height = new GridLength(0);
        TopControlsRow.Height = new GridLength(0);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        fullscreen = true;
        ApplyWindowChromeHitTestState();
        Topmost = false;
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ApplyFullscreenMode(FullscreenMode mode)
    {
        fullscreenMode = mode;
        ApplyFullscreenWindowBounds(mode);
        if (viewModel is not null)
        {
            var isVideoFullscreen = mode is FullscreenMode.StreamOnly or FullscreenMode.MultiView;
            viewModel.IsVideoFullscreenActive = isVideoFullscreen;
            viewModel.IsStreamOnlyFullscreenActive = mode == FullscreenMode.StreamOnly;
        }

        if (mode == FullscreenMode.Theatre)
        {
            ApplyTheatreModeChatToSelectedTab();
            return;
        }
    }

    private void ApplyFullscreenSelectedTabState()
    {
        if (fullscreenMode == FullscreenMode.Theatre)
        {
            ApplyTheatreModeChatToSelectedTab();
        }
    }

    private void ApplyTheatreModeChatToSelectedTab()
    {
        if (viewModel is null)
        {
            return;
        }

        var theatreChatTabs = viewModel.GetTheatreModeChatTargetTabs();
        foreach (var tab in theatreChatTabs)
        {
            CaptureFullscreenChatVisibility(tab);
        }

        viewModel.ApplyTheatreModeDockedChat(theatreChatTabs);
        LockDockedChatToBottom();
        QueueDockedChatScrollToBottom(force: true);
    }

    private void ApplyFullscreenWindowBounds(FullscreenMode mode)
    {
        if (!fullscreen)
        {
            return;
        }

        ApplyWindowBounds(GetCurrentMonitorBounds(useWorkingArea: mode == FullscreenMode.Theatre));
        MarkTaskbarFullscreen();
    }

    private void ExitFullscreenMode()
    {
        ClearTaskbarFullscreen();

        if (viewModel is not null)
        {
            viewModel.IsStreamOnlyFullscreenActive = false;
            viewModel.IsVideoFullscreenActive = false;
        }

        RestoreFullscreenChatState();
        ResetVideoDoubleClickTracking();

        TitleRow.Height = previousTitleRowHeight;
        TopControlsRow.Height = previousTopControlsRowHeight;
        TitleBar.Visibility = Visibility.Visible;
        TopControlsBar.Visibility = Visibility.Visible;
        WindowStyle = previousWindowStyle;
        ResizeMode = previousResizeMode;
        fullscreenMode = FullscreenMode.None;
        fullscreen = false;
        ApplyWindowChromeHitTestState();
        Topmost = previousTopmost;
        WindowState = WindowState.Normal;
        if (previousWindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
        else
        {
            ApplyWindowBounds(previousWindowBounds);
            WindowState = previousWindowState == WindowState.Minimized
                ? WindowState.Normal
                : previousWindowState;
        }

        // Retry once after the placement transition if the shell was temporarily unavailable
        // for the first unregistration request.
        ClearTaskbarFullscreen();
    }

    private void MarkTaskbarFullscreen(bool force = false)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (force && taskbarFullscreenWindowHandle == handle)
        {
            // Explorer was recreated, so its prior registration no longer exists. Drop the
            // local cache before retrying and leave it clear if the new shell is not ready yet.
            taskbarFullscreenWindowHandle = IntPtr.Zero;
        }

        if (handle == IntPtr.Zero || (!force && taskbarFullscreenWindowHandle == handle))
        {
            return;
        }

        if (taskbarFullscreenWindowHandle != IntPtr.Zero &&
            taskbarFullscreenWindowHandle != handle)
        {
            ClearTaskbarFullscreen();
            if (taskbarFullscreenWindowHandle != IntPtr.Zero)
            {
                return;
            }
        }

        if (TaskbarFullscreenController.TrySetFullscreen(handle, fullscreen: true))
        {
            taskbarFullscreenWindowHandle = handle;
        }
    }

    private bool ShouldMarkTaskbarFullscreen()
    {
        return fullscreen;
    }

    private void ClearTaskbarFullscreen()
    {
        var handle = taskbarFullscreenWindowHandle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (TaskbarFullscreenController.TrySetFullscreen(handle, fullscreen: false))
        {
            taskbarFullscreenWindowHandle = IntPtr.Zero;
        }
    }

    private bool ToggleStreamFullscreenFromVideoDoubleClick()
    {
        if (viewModel?.SelectedTab is null)
        {
            return false;
        }

        if (fullscreen)
        {
            ExitFullscreenMode();
        }
        else
        {
            ToggleFullscreenMode(FullscreenMode.StreamOnly);
        }

        return true;
    }

    private bool TryToggleDetachedStreamFullscreenFromVideoDoubleClick(NativePoint screenPoint)
    {
        foreach (var window in detachedWindows.Values.Distinct().ToArray())
        {
            if (!window.TryToggleStreamFullscreenFromScreenClick(screenPoint.X, screenPoint.Y))
            {
                continue;
            }

            if (window.ActiveTab is { } activeTab && viewModel?.Tabs.Contains(activeTab) == true)
            {
                viewModel.SelectedTab = activeTab;
            }

            return true;
        }

        return false;
    }

    private bool TryActivateDetachedVideoTabFromScreenClick(NativePoint screenPoint)
    {
        foreach (var window in detachedWindows.Values.Distinct().ToArray())
        {
            if (window.TryActivateTabFromScreenClick(screenPoint.X, screenPoint.Y))
            {
                return true;
            }
        }

        return false;
    }

    internal bool TryActivateVideoTabFromScreenClick(int screenX, int screenY)
    {
        return TryActivateVideoTabFromScreenClick(new NativePoint(screenX, screenY));
    }

    private bool TryActivateVideoTabFromScreenClick(NativePoint screenPoint)
    {
        if (viewModel is null ||
            !IsScreenPointInMainWindow(screenPoint) ||
            GetVideoTabAtScreenPoint(screenPoint) is not { } tab ||
            !viewModel.Tabs.Contains(tab))
        {
            return false;
        }

        viewModel.SelectedTab = tab;
        return true;
    }

    private bool TryToggleStreamFullscreenFromVideoDoubleClick(NativePoint screenPoint)
    {
        if (!IsScreenPointInMainWindow(screenPoint) ||
            viewModel is null ||
            GetVideoTabAtScreenPoint(screenPoint) is not { } tab)
        {
            ResetVideoDoubleClickTracking();
            return false;
        }

        var now = Environment.TickCount64;
        var isDoubleClick = IsTrackedVideoDoubleClick(now, screenPoint);
        CaptureVideoLeftButtonDown(now, screenPoint);

        if (!isDoubleClick)
        {
            return false;
        }

        ResetVideoDoubleClickTracking();
        viewModel.SelectedTab = tab;
        if (fullscreen)
        {
            ExitFullscreenMode();
        }
        else
        {
            ToggleFullscreenMode(FullscreenMode.StreamOnly);
        }

        return true;
    }

    private bool IsTrackedVideoDoubleClick(long now, NativePoint screenPoint)
    {
        if (lastVideoLeftButtonDownAt == long.MinValue)
        {
            return false;
        }

        var elapsed = now - lastVideoLeftButtonDownAt;
        return elapsed >= 0 &&
            elapsed <= GetDoubleClickTime() &&
            Math.Abs(screenPoint.X - lastVideoLeftButtonDownX) <= GetSystemMetrics(SmCxDoubleClick) &&
            Math.Abs(screenPoint.Y - lastVideoLeftButtonDownY) <= GetSystemMetrics(SmCyDoubleClick);
    }

    private void CaptureVideoLeftButtonDown(long now, NativePoint screenPoint)
    {
        lastVideoLeftButtonDownAt = now;
        lastVideoLeftButtonDownX = screenPoint.X;
        lastVideoLeftButtonDownY = screenPoint.Y;
    }

    private void ResetVideoDoubleClickTracking()
    {
        lastVideoLeftButtonDownAt = long.MinValue;
        lastVideoLeftButtonDownX = 0;
        lastVideoLeftButtonDownY = 0;
    }

    private bool IsScreenPointInMainWindow(NativePoint screenPoint)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        return WindowHitTestPolicy.IsPointInWindow(
            windowHitTester,
            windowHandle,
            screenPoint.X,
            screenPoint.Y,
            includeOwnedPopups: false);
    }

    private void CaptureFullscreenChatState()
    {
        if (fullscreenChatStateCaptured)
        {
            return;
        }

        fullscreenChatStateCaptured = true;
        fullscreenChatVisibility.Clear();
        fullscreenDockedChatPanelVisibility.Clear();
        previousChatLayout = viewModel?.Settings.Chat.Layout;
        if (viewModel?.SelectedTab is { } tab)
        {
            CaptureFullscreenChatVisibility(tab);
        }
    }

    private void CaptureFullscreenChatVisibility(StreamTabViewModel tab)
    {
        if (!fullscreenChatVisibility.ContainsKey(tab))
        {
            fullscreenChatVisibility[tab] = tab.IsChatVisible;
        }

        if (!fullscreenDockedChatPanelVisibility.ContainsKey(tab))
        {
            fullscreenDockedChatPanelVisibility[tab] = tab.IsDockedChatPanelVisible;
        }
    }

    private void RestoreFullscreenChatState()
    {
        if (!fullscreenChatStateCaptured)
        {
            return;
        }

        if (viewModel is not null && previousChatLayout is { } chatLayout)
        {
            viewModel.Settings.Chat.Layout = chatLayout;
        }

        foreach (var (tab, chatVisible) in fullscreenChatVisibility)
        {
            tab.IsChatVisible = chatVisible;
        }

        foreach (var (tab, chatPanelVisible) in fullscreenDockedChatPanelVisibility)
        {
            tab.IsDockedChatPanelVisible = chatPanelVisible;
        }

        viewModel?.ClearTheatreModeDockedChatOverrides();

        fullscreenChatVisibility.Clear();
        fullscreenDockedChatPanelVisibility.Clear();
        previousChatLayout = null;
        fullscreenChatStateCaptured = false;
    }

    private void ApplyWindowChromeHitTestState()
    {
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            chrome.CaptionHeight = fullscreen ? 0 : TitleBarChromeCaptionHeight;
            chrome.ResizeBorderThickness = fullscreen || WindowState == WindowState.Maximized
                ? new Thickness(0)
                : WindowChromeResizeBorderThickness;
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximizeRestore();
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
    {
        viewModel?.CloseAllTabs();
        Close();
    }

    private void InitializeTrayIcon()
    {
        if (trayIconVisible || windowHandle == IntPtr.Zero)
        {
            return;
        }

        trayIconHandle = CreateTrayIconHandle(out destroyTrayIconHandle);
        var data = CreateNotifyIconData(NifMessage | NifIcon | NifTip);
        trayIconVisible = Shell_NotifyIcon(NimAdd, ref data);
    }

    private static IntPtr CreateTrayIconHandle(out bool destroyIcon)
    {
        destroyIcon = false;
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            File.Exists(processPath) &&
            ExtractIconEx(processPath, 0, out var largeIcon, out var smallIcon, 1) > 0)
        {
            if (smallIcon != IntPtr.Zero)
            {
                if (largeIcon != IntPtr.Zero)
                {
                    DestroyIcon(largeIcon);
                }

                destroyIcon = true;
                return smallIcon;
            }

            if (largeIcon != IntPtr.Zero)
            {
                destroyIcon = true;
                return largeIcon;
            }
        }

        return LoadIcon(IntPtr.Zero, new IntPtr(IdiApplication));
    }

    private NotifyIconData CreateNotifyIconData(uint flags)
    {
        return new NotifyIconData
        {
            Size = Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = windowHandle,
            Id = TrayIconId,
            Flags = flags,
            CallbackMessage = WmAppTrayIcon,
            IconHandle = trayIconHandle,
            Tip = "Twitch & Kick player",
            State = 0,
            StateMask = 0,
            Info = "",
            TimeoutOrVersion = 0,
            InfoTitle = "",
            InfoFlags = 0,
            Guid = Guid.Empty,
            BalloonIconHandle = IntPtr.Zero
        };
    }

    private void HandleTrayIconMessage(IntPtr lParam)
    {
        var mouseMessage = lParam.ToInt32();
        if (mouseMessage is WmLeftButtonUp or WmLeftButtonDoubleClick)
        {
            ShowMainWindow();
            return;
        }

        if (mouseMessage == WmRightButtonUp)
        {
            ShowTrayMenu();
        }
    }

    private void ShowTrayMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MfString, new UIntPtr((uint)TrayCommandOpen), "Open");
            AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
            AppendMenu(menu, MfString, new UIntPtr((uint)TrayCommandExit), "Exit");

            if (!GetCursorPos(out var cursorPoint))
            {
                return;
            }

            SetForegroundWindow(windowHandle);
            var command = TrackPopupMenuEx(
                menu,
                TpmRightButton | TpmReturnCommand,
                cursorPoint.X,
                cursorPoint.Y,
                windowHandle,
                IntPtr.Zero);

            if (command == TrayCommandOpen)
            {
                ShowMainWindow();
            }
            else if (command == TrayCommandExit)
            {
                RequestApplicationExit();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void HideToTray()
    {
        if (fullscreen)
        {
            ExitFullscreenMode();
        }

        ShowInTaskbar = false;
        Hide();
    }

    private void ShowMainWindow()
    {
        if (!Dispatcher.CheckAccess())
        {
            DispatchToUi(ShowMainWindow);
            return;
        }

        if (shutdownStarted)
        {
            return;
        }

        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        var wasTopmost = Topmost;
        Topmost = true;
        Topmost = wasTopmost;
        Activate();
        Focus();
    }

    private void RequestApplicationExit()
    {
        if (shutdownStarted)
        {
            return;
        }

        exitRequested = true;
        Close();
    }

    private void DisposeTrayIcon()
    {
        if (trayIconVisible)
        {
            var data = CreateNotifyIconData(0);
            Shell_NotifyIcon(NimDelete, ref data);
            trayIconVisible = false;
        }

        if (trayIconHandle != IntPtr.Zero && destroyTrayIconHandle)
        {
            DestroyIcon(trayIconHandle);
        }

        trayIconHandle = IntPtr.Zero;
        destroyTrayIconHandle = false;
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateMaximizeRestoreButton();
    }

    private void UpdateMaximizeRestoreButton()
    {
        MaximizeRestoreButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeRestoreButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    private void ChatInputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        var command = viewModel?.SelectedTab?.SendChatMessageCommand;
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }

        e.Handled = true;
    }

    private void StreamSearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        var command = viewModel?.AddAndPlayCommand;
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }

        e.Handled = true;
    }

    private void HomeSearchAnchor_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        Dispatcher.BeginInvoke(
            () => viewModel?.ShowStreamSearchDropdown(),
            DispatcherPriority.Input);
    }

    private void HomeStreamSearchTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        viewModel?.ShowStreamSearchDropdown();
    }

    private static bool IsPointInsideElement(FrameworkElement element, Point point)
    {
        return point.X >= 0 &&
            point.Y >= 0 &&
            point.X <= element.ActualWidth &&
            point.Y <= element.ActualHeight;
    }

    private void BrowseStreamlinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select streamlink.exe",
            Filter = "Streamlink executable|streamlink.exe|Executable files|*.exe|All files|*.*",
            FileName = "streamlink.exe"
        };

        if (!string.IsNullOrWhiteSpace(viewModel.Settings.StreamlinkPath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(viewModel.Settings.StreamlinkPath);
        }

        if (dialog.ShowDialog(this) == true)
        {
            viewModel.Settings.StreamlinkPath = dialog.FileName;
            viewModel.RefreshSettingsBindings();
        }
    }

    private void BrowseVlcButton_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select libvlc.dll",
            Filter = "libVLC|libvlc.dll|Dynamic libraries|*.dll|All files|*.*",
            FileName = "libvlc.dll"
        };

        if (!string.IsNullOrWhiteSpace(viewModel.Settings.VlcDirectory))
        {
            dialog.InitialDirectory = viewModel.Settings.VlcDirectory;
        }

        if (dialog.ShowDialog(this) == true)
        {
            viewModel.Settings.VlcDirectory = Path.GetDirectoryName(dialog.FileName);
            viewModel.RefreshSettingsBindings();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public readonly int X;
        public readonly int Y;
    }

    private readonly struct TabStripItemBounds
    {
        public TabStripItemBounds(TabStripItemViewModel item, double left, double right, double top, double bottom)
        {
            Item = item;
            Left = left;
            Right = right;
            Top = top;
            Bottom = bottom;
        }

        public readonly TabStripItemViewModel Item;
        public readonly double Left;
        public readonly double Right;
        public readonly double Top;
        public readonly double Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public int CallbackMessage;
        public IntPtr IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIconHandle;
    }

    private readonly struct VideoRect
    {
        public VideoRect(int left, int top, int width, int height)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            Right = left + width;
            Bottom = top + height;
        }

        public int Left { get; }
        public int Top { get; }
        public int Width { get; }
        public int Height { get; }
        public int Right { get; }
        public int Bottom { get; }

        public bool Contains(Point point)
        {
            return point.X >= Left &&
                point.X < Right &&
                point.Y >= Top &&
                point.Y < Bottom;
        }
    }

    [LibraryImport("user32")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterWindowMessage(string message);

    [LibraryImport("user32")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32")]
    private static partial uint GetDoubleClickTime();

    [LibraryImport("user32")]
    private static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32")]
    private static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out WindowPoint point);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(IntPtr hWnd, ref WindowPoint lpPoint);

    [LibraryImport("user32")]
    private static partial IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

    [GeneratedRegex("""https?://[^\s"'<>]+|(?:www\.)?(?:twitch\.tv|kick\.com)/[^\s"'<>]+|/(?:[A-Za-z0-9_.-]{1,80})(?:/[^\s"'<>]*)?""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BrowserUrlCandidatePattern();

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [LibraryImport("shell32", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint ExtractIconEx(string fileName, int iconIndex, out IntPtr largeIcon, out IntPtr smallIcon, uint icons);

    [LibraryImport("user32", EntryPoint = "LoadIconW", SetLastError = true)]
    private static partial IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);

    [LibraryImport("user32", SetLastError = true)]
    private static partial IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr newItemId, string? newItem);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(IntPtr menu);

    [LibraryImport("user32", SetLastError = true)]
    private static partial int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);

}

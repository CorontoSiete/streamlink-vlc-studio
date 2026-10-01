using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using StreamlinkVlcStudio.App.Wpf.ViewModels;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf;

public partial class MainWindow
{
    private sealed class WindowModeController : IDisposable
    {
        private readonly MainWindow window;
        private bool disposed;
        internal WindowModeController(MainWindow window) => this.window = window;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ClearTaskbarFullscreen();
            DisposeTrayIcon();
        }
        private MainViewModel? viewModel => window.viewModel;
        private IntPtr windowHandle => window.windowHandle;
        private bool shutdownStarted => window.shutdownStarted;

        private void DispatchToUi(Action action) => window.DispatchToUi(action);

        private void LockDockedChatToBottom() => window.LockDockedChatToBottom();

        private void QueueDockedChatScrollToBottom(bool force) => window.QueueDockedChatScrollToBottom(force);

        private Rect GetCurrentMonitorBounds(bool useWorkingArea) => window.GetCurrentMonitorBounds(useWorkingArea);

        private Rect GetRestorableWindowBounds() => window.GetRestorableWindowBounds();

        private void ApplyWindowBounds(Rect bounds) => window.ApplyWindowBounds(bounds);

        private void ResetVideoDoubleClickTracking() => window.ResetVideoDoubleClickTracking();
        private ScaleTransform chromeScale => window.chromeScale;

        private void UpdateResponsiveLayout() => window.UpdateResponsiveLayout();
        private RowDefinition TitleRow => window.TitleRow;
        private RowDefinition TopControlsRow => window.TopControlsRow;
        private Border TitleBar => window.TitleBar;
        private Button MaximizeRestoreButton => window.MaximizeRestoreButton;
        private Border TopControlsBar => window.TopControlsBar;
        private Dispatcher Dispatcher { get => window.Dispatcher; }
        private WindowState WindowState { get => window.WindowState; set => window.WindowState = value; }
        private WindowStyle WindowStyle { get => window.WindowStyle; set => window.WindowStyle = value; }
        private ResizeMode ResizeMode { get => window.ResizeMode; set => window.ResizeMode = value; }
        private bool Topmost { get => window.Topmost; set => window.Topmost = value; }

        internal readonly Dictionary<StreamTabViewModel, bool> fullscreenChatVisibility = [];
        internal readonly Dictionary<StreamTabViewModel, bool> fullscreenDockedChatPanelVisibility = [];
        internal IntPtr taskbarFullscreenWindowHandle;
        internal IntPtr trayIconHandle;
        internal bool exitRequested;
        internal bool trayIconVisible;
        internal bool destroyTrayIconHandle;
        internal bool fullscreen;
        internal bool fullscreenChatStateCaptured;
        internal FullscreenMode fullscreenMode = FullscreenMode.None;
        internal WindowState previousWindowState;
        internal WindowStyle previousWindowStyle;
        internal ResizeMode previousResizeMode;
        internal WindowChrome? previousWindowChrome;
        internal Rect previousWindowBounds;
        internal bool previousTopmost;
        internal GridLength previousTitleRowHeight;
        internal GridLength previousTopControlsRowHeight;
        internal ChatLayout? previousChatLayout;

        internal void FullscreenButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullscreenMode(GetFullscreenButtonMode());
        }

        internal void TheatreButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullscreenMode(FullscreenMode.Theatre);
        }

        internal FullscreenMode GetFullscreenButtonMode()
        {
            return viewModel?.IsCurrentVideoViewMultiStream() == true
                ? FullscreenMode.MultiView
                : FullscreenMode.StreamOnly;
        }

        internal void ToggleFullscreenMode(FullscreenMode requestedMode)
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

        internal void EnterFullscreenWindow()
        {
            previousWindowState = WindowState;
            previousWindowStyle = WindowStyle;
            previousResizeMode = ResizeMode;
            previousWindowChrome = WindowChrome.GetWindowChrome(window);
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
            fullscreen = true;

            // Remove WindowChrome while fullscreen so its native border behavior cannot
            // reserve monitor-edge pixels outside the client/video surface.
            if (previousWindowChrome is not null)
            {
                WindowChrome.SetWindowChrome(window, null);
            }

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = false;
            WindowState = WindowState.Normal;
            window.Activate();
        }

        internal void ApplyFullscreenMode(FullscreenMode mode)
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

        internal void ApplyFullscreenSelectedTabState()
        {
            if (fullscreenMode == FullscreenMode.Theatre)
            {
                ApplyTheatreModeChatToSelectedTab();
            }
        }

        internal void ApplyTheatreModeChatToSelectedTab()
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

        internal void ApplyFullscreenWindowBounds(FullscreenMode mode)
        {
            if (!fullscreen)
            {
                return;
            }

            ApplyWindowBounds(GetCurrentMonitorBounds(useWorkingArea: mode == FullscreenMode.Theatre));
            MarkTaskbarFullscreen();
        }

        internal void ExitFullscreenMode()
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
            if (previousWindowChrome is { } chrome)
            {
                ApplyWindowChromeHitTestState(chrome);
                WindowChrome.SetWindowChrome(window, chrome);
                previousWindowChrome = null;
            }
            else
            {
                ApplyWindowChromeHitTestState();
            }
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
            UpdateResponsiveLayout();
            window.QueueRemoveDwmClientFrame();
        }

        internal void MarkTaskbarFullscreen(bool force = false)
        {
            var handle = new WindowInteropHelper(window).Handle;
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

        internal bool ShouldMarkTaskbarFullscreen()
        {
            return fullscreen;
        }

        internal void ClearTaskbarFullscreen()
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

        internal bool ToggleStreamFullscreenFromVideoDoubleClick()
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

        internal void CaptureFullscreenChatState()
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

        internal void CaptureFullscreenChatVisibility(StreamTabViewModel tab)
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

        internal void RestoreFullscreenChatState()
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

        internal void ApplyWindowChromeHitTestState()
        {
            if (WindowChrome.GetWindowChrome(window) is { } chrome)
            {
                ApplyWindowChromeHitTestState(chrome);
            }
        }

        private void ApplyWindowChromeHitTestState(WindowChrome chrome)
        {
            chrome.CaptionHeight = fullscreen ? 0 : TitleBarChromeCaptionHeight * chromeScale.ScaleY;
            chrome.ResizeBorderThickness = fullscreen || WindowState == WindowState.Maximized
                ? new Thickness(0)
                : WindowChromeResizeBorderThickness;
        }

        internal void InitializeTrayIcon()
        {
            if (trayIconVisible || windowHandle == IntPtr.Zero)
            {
                return;
            }

            trayIconHandle = CreateTrayIconHandle(out destroyTrayIconHandle);
            var data = CreateNotifyIconData(NifMessage | NifIcon | NifTip);
            trayIconVisible = Shell_NotifyIcon(NimAdd, ref data);
        }

        internal static IntPtr CreateTrayIconHandle(out bool destroyIcon)
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

        internal NotifyIconData CreateNotifyIconData(uint flags)
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

        internal void HandleTrayIconMessage(IntPtr lParam)
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

        internal void ShowTrayMenu()
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

        internal void HideToTray()
        {
            if (fullscreen)
            {
                ExitFullscreenMode();
            }

            window.ShowInTaskbar = false;
            window.Hide();
        }

        internal void ShowMainWindow()
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

            window.ShowInTaskbar = true;
            window.Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            var wasTopmost = Topmost;
            Topmost = true;
            Topmost = wasTopmost;
            window.Activate();
            window.Focus();
        }

        internal void RequestApplicationExit()
        {
            if (shutdownStarted)
            {
                return;
            }

            exitRequested = true;
            window.Close();
        }

        internal void DisposeTrayIcon()
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

        internal void ToggleMaximizeRestore()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            UpdateMaximizeRestoreButton();
        }

        internal void UpdateMaximizeRestoreButton()
        {
            MaximizeRestoreButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            MaximizeRestoreButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        }
    }
}

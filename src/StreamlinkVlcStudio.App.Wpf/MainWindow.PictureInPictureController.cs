using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using StreamlinkVlcStudio.App.Wpf.ViewModels;
using StreamlinkVlcStudio.Core.Settings;
using static StreamlinkVlcStudio.App.Wpf.WindowInteropHelpers;

namespace StreamlinkVlcStudio.App.Wpf;

public partial class MainWindow
{
    private sealed class PictureInPictureController : IDisposable
    {
        private readonly MainWindow window;
        private bool disposed;
        internal PictureInPictureController(MainWindow window) => this.window = window;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            CloseAllDetachedWindows(reattach: false);
        }
        private MainViewModel? viewModel => window.viewModel;
        private bool fullscreen => window.fullscreen;

        private void UpdateLowLevelMouseMoveRouteState() => window.UpdateLowLevelMouseMoveRouteState();

        private bool IsPointerOverNativeOverlay(StreamTabViewModel tab) => window.IsPointerOverNativeOverlay(tab);

        private bool TryGetTabAtTabStripScreenPoint(NativePoint screenPoint, out StreamTabViewModel? tab) => window.TryGetTabAtTabStripScreenPoint(screenPoint, out tab);



        private Rect GetMonitorWorkingAreaAtScreenPoint(Point screenPoint) => window.GetMonitorWorkingAreaAtScreenPoint(screenPoint);

        private Rect GetMonitorWorkingAreaAtDeviceIndependentPoint(Point point) => window.GetMonitorWorkingAreaAtDeviceIndependentPoint(point);

        private void ExitFullscreenMode() => window.ExitFullscreenMode();

        private void ShowMainWindow() => window.ShowMainWindow();
        private Grid VideoViewport => window.VideoViewport;
        internal readonly Dictionary<StreamTabViewModel, DetachedVideoWindow> detachedWindows = [];

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

        internal StreamTabViewModel[] GetPictureInPictureDragTabs(StreamTabViewModel tab)
        {
            return viewModel is null
                ? []
                : viewModel.GetPictureInPictureDragTabs(tab)
                    .Where(viewModel.Tabs.Contains)
                    .Distinct()
                    .ToArray();
        }

        internal DetachedVideoWindow? GetPictureInPictureDropTarget(
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

        internal bool HasPotentialPictureInPictureDropTarget(IReadOnlyCollection<StreamTabViewModel> draggedTabs)
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

        internal void DetachTabToPictureInPicture(StreamTabViewModel tab, Point screenPoint, bool continueDrag)
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
            detachedWindow.CloseTabsRequested += (_, _) => CloseDetachedWindowTabs(detachedWindow);
            detachedWindow.ReattachRequested += (_, _) => ReattachDetachedWindow(detachedWindow);
            detachedWindow.VisibleTabsChanged += (_, _) => SyncPictureInPictureVisibleTabGroup(detachedWindow);
            detachedWindow.TopBarVisibilityChanged += DetachedWindowOnTopBarVisibilityChanged;
            detachedWindow.VideoMoveCandidateChanged += DetachedWindowOnVideoMoveCandidateChanged;
            detachedWindow.TabActivated += activatedTab =>
            {
                if (viewModel?.Tabs.Contains(activatedTab) == true)
                {
                    viewModel.ActivatePictureInPictureTab(activatedTab);
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

        internal void ReattachDetachedWindow(DetachedVideoWindow detachedWindow)
        {
            if (viewModel is null)
            {
                return;
            }

            var detachedTabs = detachedWindow.Tabs
                .Where(viewModel.Tabs.Contains)
                .Distinct()
                .ToArray();
            foreach (var tab in detachedTabs)
            {
                // The main window may not realize its presenter until after Closing
                // disconnects the PiP tree. Mark the current host transfer explicitly
                // so that PiP teardown cannot bind VLC to a parking HWND in between.
                tab.VideoSurfacePresenterOwner?.DetachSurfaceForTransfer();
            }

            RemoveDetachedWindowMappings(detachedWindow);
            if (detachedTabs.Length == 0)
            {
                return;
            }

            ShowMainWindow();
            viewModel.SelectedTab = detachedWindow.ActiveTab is { } activeTab && viewModel.Tabs.Contains(activeTab)
                ? activeTab
                : detachedTabs[0];
            viewModel.SetTabsDetached(detachedTabs, detached: false);
            VideoViewport.UpdateLayout();
        }

        internal bool PositionDetachedWindow(DetachedVideoWindow window, Point screenPoint, bool useSavedLocation)
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
            var dipPoint = this.window.ToDeviceIndependentPoint(screenPoint);
            var bounds = GetAvailableDetachedWindowBounds(new Rect(
                dipPoint.X - size.Width / 2,
                dipPoint.Y - DetachedWindowTitleBarHeight / 2,
                size.Width,
                size.Height), workingArea);
            ApplyDetachedWindowBounds(window, bounds, workingArea);
            return false;
        }

        internal PictureInPictureFullscreenMode? GetSavedDetachedWindowFullscreenMode()
        {
            var savedLocation = viewModel?.Settings.PictureInPictureWindowLocation;
            return savedLocation?.IsFullscreen == true
                ? savedLocation.FullscreenMode
                : null;
        }

        internal bool GetSavedPictureInPictureTopBarVisibility(StreamTabViewModel tab)
        {
            return viewModel?.Settings.StreamPictureInPictureTopBarVisibility.TryGetValue(
                tab.Target.StateKey,
                out var showTopBar) == true && showTopBar;
        }

        internal void DetachedWindowOnTopBarVisibilityChanged(StreamTabViewModel tab, bool showTopBar)
        {
            if (viewModel?.Tabs.Contains(tab) == true)
            {
                _ = viewModel.RememberStreamPictureInPictureTopBarVisibilityAsync(tab.Target, showTopBar);
            }
        }

        internal void DetachedWindowOnVideoMoveCandidateChanged(object? sender, EventArgs e)
        {
            UpdateLowLevelMouseMoveRouteState();
        }

        internal static void RestoreDetachedWindowFullscreen(
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

        internal static Size GetDetachedWindowSize(DetachedVideoWindow window, Rect workingArea)
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

        internal static bool TryGetSavedDetachedWindowSize(
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

        internal static void ApplyDetachedWindowBounds(DetachedVideoWindow window, double left, double top, Size size, Rect workingArea)
        {
            var width = size.Width;
            var height = size.Height;
            window.Width = width;
            window.Height = height;
            window.Left = ClampWindowCoordinate(left, workingArea.Left, workingArea.Right - width);
            window.Top = ClampWindowCoordinate(top, workingArea.Top, workingArea.Bottom - height);
        }

        internal static void ApplyDetachedWindowBounds(DetachedVideoWindow window, Rect bounds, Rect workingArea)
        {
            ApplyDetachedWindowBounds(window, bounds.Left, bounds.Top, bounds.Size, workingArea);
        }

        internal Rect GetAvailableDetachedWindowBounds(Rect preferredBounds, Rect workingArea)
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

        internal static Rect ClampDetachedWindowBounds(Rect bounds, Rect workingArea)
        {
            return new Rect(
                ClampWindowCoordinate(bounds.Left, workingArea.Left, workingArea.Right - bounds.Width),
                ClampWindowCoordinate(bounds.Top, workingArea.Top, workingArea.Bottom - bounds.Height),
                bounds.Width,
                bounds.Height);
        }

        internal static bool HasDuplicateDetachedWindowPosition(Rect bounds, IReadOnlyList<Rect> existingBounds)
        {
            return existingBounds.Any(existing =>
                Math.Abs(existing.Left - bounds.Left) < DetachedWindowCascadeDuplicateTolerance &&
                Math.Abs(existing.Top - bounds.Top) < DetachedWindowCascadeDuplicateTolerance);
        }

        internal static Rect GetSavedDetachedFullscreenRestoreBounds(
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

        internal static bool ContainsWindowCenter(Rect area, Rect bounds)
        {
            var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            return center.X >= area.Left &&
                center.X <= area.Right &&
                center.Y >= area.Top &&
                center.Y <= area.Bottom;
        }

        internal void RememberPictureInPictureWindowBounds(DetachedVideoWindow window)
        {
            if (viewModel is null ||
                !TryGetPictureInPictureWindowLocation(window, out var location))
            {
                return;
            }

            viewModel.RememberPictureInPictureWindowBounds(location);
        }

        internal async Task RememberPictureInPictureWindowBoundsAsync(DetachedVideoWindow window)
        {
            if (viewModel is null ||
                !TryGetPictureInPictureWindowLocation(window, out var location))
            {
                return;
            }

            await viewModel.RememberPictureInPictureWindowBoundsAsync(location);
        }

        internal bool TryGetPictureInPictureWindowLocation(
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

        internal static bool TryGetPictureInPictureFullscreenScreen(
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

        internal bool TryGetSavedPictureInPictureFullscreenWorkingArea(
            PictureInPictureWindowLocation savedLocation,
            out Rect workingArea)
        {
            workingArea = default;
            if (savedLocation.FullscreenScreen is not { } savedScreen ||
                !TryFindSavedPictureInPictureFullscreenScreen(savedScreen, out var screen))
            {
                return false;
            }

            workingArea = window.ToDeviceIndependentRect(new NativeRectangle
            {
                Left = screen.WorkingArea.Left,
                Top = screen.WorkingArea.Top,
                Right = screen.WorkingArea.Right,
                Bottom = screen.WorkingArea.Bottom
            });
            return true;
        }

        internal static bool TryFindSavedPictureInPictureFullscreenScreen(
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

        internal static double ClampWindowCoordinate(double value, double min, double max)
        {
            return max < min ? min : Math.Clamp(value, min, max);
        }

        internal static bool IsUsableWindowLength(double value)
        {
            return double.IsFinite(value) && value > 0;
        }

        internal void BringDetachedWindowForward(DetachedVideoWindow window)
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

        internal void ViewModelTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
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

        internal void CloseAllDetachedWindows(bool reattach)
        {
            foreach (var window in detachedWindows.Values.Distinct().ToArray())
            {
                CloseDetachedWindow(window, reattach);
            }
        }

        internal void CloseDetachedWindowTabs(DetachedVideoWindow window)
        {
            if (viewModel is null)
            {
                CloseDetachedWindow(window, reattach: false);
                return;
            }

            var tabs = window.Tabs
                .Where(viewModel.Tabs.Contains)
                .Distinct()
                .ToArray();
            if (tabs.Length == 0)
            {
                CloseDetachedWindow(window, reattach: false);
                return;
            }

            viewModel.CloseTabs(tabs);
        }

        internal void CloseDetachedWindowForTab(StreamTabViewModel tab, bool reattach)
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

        internal void CloseDetachedWindow(DetachedVideoWindow window, bool reattach)
        {
            if (reattach)
            {
                window.Close();
                return;
            }

            RemoveDetachedWindowMappings(window);
            window.CloseForTabDisposal();
        }

        internal void RemoveDetachedWindowMappings(DetachedVideoWindow window)
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

        internal void SyncPictureInPictureVisibleTabGroup(DetachedVideoWindow window)
        {
            if (viewModel is null || window.IsClosing)
            {
                return;
            }

            viewModel.SetPictureInPictureVisibleTabGroup(window.VisibleTabs);
        }

        internal bool TryRouteDetachedMouseWheel(NativePoint screenPoint, int delta)
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

        internal bool TryBeginDetachedResizeFromScreenClick(NativePoint screenPoint)
        {
            foreach (var window in detachedWindows.Values.Distinct().ToArray())
            {
                if (window.TryBeginResizeFromScreenClick(screenPoint.X, screenPoint.Y))
                {
                    return true;
                }
            }

            return false;
        }

        internal bool TryBeginDetachedVideoMoveFromScreenClick(NativePoint screenPoint)
        {
            var started = false;
            foreach (var window in detachedWindows.Values.Distinct().ToArray())
            {
                started |= window.TryBeginVideoMoveFromScreenClick(screenPoint.X, screenPoint.Y);
            }

            return started;
        }

        internal bool TryContinueDetachedVideoMove(NativePoint screenPoint)
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

        internal void CancelDetachedVideoMoveCandidates()
        {
            foreach (var window in detachedWindows.Values.Distinct().ToArray())
            {
                window.CancelVideoMoveCandidate();
            }
        }

        internal bool TryOpenDetachedVideoContextMenu(NativePoint screenPoint)
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

        internal bool TryToggleDetachedStreamFullscreenFromVideoDoubleClick(NativePoint screenPoint)
        {
            foreach (var window in detachedWindows.Values.Distinct().ToArray())
            {
                if (!window.TryToggleStreamFullscreenFromScreenClick(screenPoint.X, screenPoint.Y))
                {
                    continue;
                }

                if (window.ActiveTab is { } activeTab && viewModel?.Tabs.Contains(activeTab) == true)
                {
                    viewModel.ActivatePictureInPictureTab(activeTab);
                }

                return true;
            }

            return false;
        }

        internal bool TryActivateDetachedVideoTabFromScreenClick(NativePoint screenPoint)
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
    }
}

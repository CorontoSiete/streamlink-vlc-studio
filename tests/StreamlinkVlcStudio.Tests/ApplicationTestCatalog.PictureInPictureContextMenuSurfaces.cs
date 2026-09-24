using System.Windows.Interop;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> PictureInPictureContextMenuSurfaceTests =>
    [
        ("picture-in-picture context menu restores the top bar from the visible native replay seek overlay",
            () => PictureInPictureContextMenuSurfaceAsync("replay overlay")),
        ("picture-in-picture context menu toggles the top bar from the bottom resize border",
            () => PictureInPictureContextMenuSurfaceAsync("bottom border")),
        ("picture-in-picture context menu restores the top bar from an empty multiview cell",
            () => PictureInPictureContextMenuSurfaceAsync("empty multiview cell")),
        ("picture-in-picture context menu restores the top bar through the visible volume indicator",
            () => PictureInPictureContextMenuSurfaceAsync("volume indicator")),
        ("picture-in-picture context menu does not reopen its owner when right clicking inside its own popup",
            PictureInPictureContextMenuPopupIsolationAsync)
    ];

    private static Task PictureInPictureContextMenuSurfaceAsync(string targetKind) =>
        WithPictureInPictureContextMenuSurfaceAsync(targetKind == "bottom border", async (session, main, window, surface) =>
        {
            foreach (var existingOverlay in FindVisualDescendants<ReplaySeekOverlay>(window))
            {
                existingOverlay.IsOverlayEnabled = false;
                StopReplayOverlayPointerSampling(existingOverlay);
            }
            var overlay = FindVisualDescendants<ReplaySeekOverlay>(window).Single(value => ReferenceEquals(value.DataContext, session.Tab));
            overlay.IsOverlayEnabled = targetKind == "replay overlay";
            StopReplayOverlayPointerSampling(overlay);
            var owner = new WindowInteropHelper(window).Handle;
            Popup? expiringPopup = null;
            Point point;
            if (targetKind == "replay overlay")
            {
                var sample = surface.PointToScreen(new Point(surface.ActualWidth / 2, surface.ActualHeight / 2));
                overlay.ProcessPointerSample(sample, true, Environment.TickCount64);
                await Task.Delay(150);
                PumpPictureInPictureResize(window);
                StopReplayOverlayPointerSampling(overlay);
                Assert.True(overlay.IsOverlayOpen);
                var chrome = (FrameworkElement)overlay.FindName("OverlayChrome");
                point = chrome.PointToScreen(new Point(chrome.ActualWidth / 2, chrome.ActualHeight / 2));
                Assert.True(ReplaySeekOverlay.IsReplayOverlayWindow(
                    NativeWindowHitTester.Instance.WindowFromPoint((int)Math.Round(point.X), (int)Math.Round(point.Y))),
                    "Replay context-menu input must hit the visible native seek overlay.");
            }
            else if (targetKind == "bottom border")
            {
                var grip = (FrameworkElement)window.FindName("BottomResizeGrip");
                Assert.True(grip.IsVisible && grip.ActualHeight > 0);
                point = grip.PointToScreen(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2));
                var borderTarget = NativeWindowHitTester.Instance.WindowFromPoint((int)Math.Round(point.X), (int)Math.Round(point.Y));
                Assert.True(borderTarget == owner || NativeWindowHitTester.Instance.IsChild(owner, borderTarget),
                    "The bottom resize border must belong to the PiP window.");
            }
            else if (targetKind == "empty multiview cell")
            {
                Assert.Equal(3, window.Tabs.Count);
                var host = (FrameworkElement)window.FindName("VideoHost");
                point = host.PointToScreen(new Point(host.ActualWidth * 0.75, host.ActualHeight * 0.75));
                foreach (var mountedSurface in FindVisualDescendants<VideoSurface>(window))
                {
                    var inSurface = mountedSurface.PointFromScreen(point);
                    Assert.True(inSurface.Y >= mountedSurface.ActualHeight || inSurface.Y < 0 ||
                        inSurface.X < 0 || inSurface.X >= mountedSurface.ActualWidth,
                        "The empty-cell regression must use a point outside every video surface.");
                }
                Assert.Equal(owner, NativeWindowHitTester.Instance.WindowFromPoint((int)Math.Round(point.X), (int)Math.Round(point.Y)));
            }
            else
            {
                var osd = (VolumeOverlay)window.FindName("VolumeOsd");
                osd.Show(surface, session.Tab.Volume, muted: false);
                await Task.Delay(180);
                PumpPictureInPictureResize(window);
                var popup = (Popup)osd.FindName("Popup");
                expiringPopup = popup;
                Assert.True(popup.IsOpen);
                var chrome = (FrameworkElement)popup.Child;
                var source = (HwndSource)PresentationSource.FromVisual(chrome)!;
                point = chrome.PointToScreen(new Point(chrome.ActualWidth / 2, chrome.ActualHeight / 2));
                var target = NativeWindowHitTester.Instance.WindowFromPoint((int)Math.Round(point.X), (int)Math.Round(point.Y));
                Assert.True(target == source.Handle || target == surface.Handle ||
                    NativeWindowHitTester.Instance.IsChild(surface.Handle, target),
                    "The visible volume indicator must occupy its popup or its hit-test-transparent video target.");
                Console.WriteLine($"PiP volume indicator physical target: {(target == source.Handle ? "popup HWND" : "video behind transparent popup")}.");
            }

            var menu = (ContextMenu)window.FindName("VideoContextMenu");
            var item = (MenuItem)window.FindName("ShowTopBarMenuItem");
            var initialTopBar = window.IsTopBarShown;
            var initialSeekCount = session.SeekCount;
            var opened = 0;
            var closed = 0;
            var popupClosedBeforeMenuOpened = false;
            var changes = new List<bool>();
            menu.Opened += (_, _) =>
            {
                opened++;
                popupClosedBeforeMenuOpened = expiringPopup is { IsOpen: false };
            };
            menu.Closed += (_, _) => closed++;
            window.TopBarVisibilityChanged += (_, shown) => changes.Add(shown);
            NativeWindowTest.SetCursorPosition((int)Math.Round(point.X), (int)Math.Round(point.Y));
            await Task.Delay(80);
            var outside = window.PointToScreen(new Point(-12, -12));
            Action movePointer = () => NativeWindowTest.SetCursorPosition((int)Math.Round(outside.X), (int)Math.Round(outside.Y));
            if (expiringPopup is not null)
                await SendPictureInPictureContextMenuExpiredOsdClickAsync(movePointer);
            else
                await SendPictureInPictureContextMenuRightClickAsync(busy: true, movePointer);
            await TestWait.UntilAsync(() => menu.IsOpen, TimeSpan.FromSeconds(2),
                $"A physical right click on the {targetKind} must open the PiP menu after the UI resumes.");
            await Task.Delay(200);
            Assert.True(menu.IsOpen);
            Assert.Equal(1, opened);
            Assert.Equal(0, closed);
            if (expiringPopup is not null)
                Assert.True(popupClosedBeforeMenuOpened,
                    "The accepted OSD click must survive the normal-priority hide timer closing and unregistering its popup before input dispatch.");
            Assert.Equal(initialTopBar, item.IsChecked);
            Assert.Equal(initialSeekCount, session.SeekCount);
            Assert.Equal(false, window.HasActiveWindowMove);
            Assert.Equal(false, window.HasVideoMoveCandidate);
            await ClickPictureInPictureTopBarMenuItemAsync(menu, item);
            await TestWait.UntilAsync(() => window.IsTopBarShown != initialTopBar && !menu.IsOpen,
                TimeSpan.FromSeconds(2), $"Show top bar must physically toggle from the {targetKind} menu.");
            await Task.Delay(150);
            Assert.Equal(1, opened);
            Assert.Equal(1, closed);
            Assert.SequenceEqual(new[] { !initialTopBar }, changes);
            Assert.Equal(initialSeekCount, session.SeekCount);
        }, includeEmptyCell: targetKind == "empty multiview cell");

    private static async Task SendPictureInPictureContextMenuExpiredOsdClickAsync(Action afterInjection)
    {
        using var senderReady = new ManualResetEventSlim();
        using var dispatcherBlocked = new ManualResetEventSlim();
        using var inputSent = new ManualResetEventSlim();
        var sender = Task.Run(() =>
        {
            senderReady.Set();
            Assert.True(dispatcherBlocked.Wait(TimeSpan.FromSeconds(2)), "The expiring OSD click sender did not observe its start gate.");
            try
            {
                PictureInPictureContextMenuMouseEvent(0x0008, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(45);
            }
            finally
            {
                PictureInPictureContextMenuMouseEvent(0x0010, 0, 0, 0, UIntPtr.Zero);
            }
            afterInjection();
            inputSent.Set();
        });
        Assert.True(senderReady.Wait(TimeSpan.FromSeconds(2)), "The expiring OSD click sender did not start.");
        dispatcherBlocked.Set();
        // VolumeOverlay's real hide timer expires at 1000 ms and runs at Normal,
        // ahead of the accepted right click queued at Input. Retaining the popup
        // registration itself would therefore drop this otherwise valid gesture.
        Thread.Sleep(1200);
        var completedDuringStall = inputSent.IsSet;
        await sender.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(completedDuringStall, "The physical OSD click and pointer movement must finish before the dispatcher resumes.");
    }

    private static Task PictureInPictureContextMenuPopupIsolationAsync() =>
        WithPictureInPictureContextMenuSurfaceAsync(showTopBar: false, async (_, _, window, surface) =>
        {
            var overlay = FindVisualDescendants<ReplaySeekOverlay>(window).Single();
            overlay.IsOverlayEnabled = false;
            StopReplayOverlayPointerSampling(overlay);
            var point = VolumeWheelPoint(surface);
            NativeWindowTest.SetCursorPosition(point.X, point.Y);
            await Task.Delay(80);
            var menu = (ContextMenu)window.FindName("VideoContextMenu");
            var item = (MenuItem)window.FindName("ShowTopBarMenuItem");
            var opened = 0;
            menu.Opened += (_, _) => opened++;
            await SendPictureInPictureContextMenuRightClickAsync(busy: false, afterInjection: null);
            await TestWait.UntilAsync(() => menu.IsOpen, TimeSpan.FromSeconds(2));
            await Task.Delay(180);
            menu.UpdateLayout();
            Assert.Equal(1, opened);
            var source = (HwndSource)PresentationSource.FromVisual(menu)!;
            // Use menu background above the item to avoid invoking any command via
            // WPF's own right-button handling. The popup is a separate owned HWND.
            var popupPoint = menu.PointToScreen(new Point(menu.ActualWidth / 2, 1));
            var x = (int)Math.Round(popupPoint.X);
            var y = (int)Math.Round(popupPoint.Y);
            var target = NativeWindowHitTester.Instance.WindowFromPoint(x, y);
            Assert.True(target == source.Handle || NativeWindowHitTester.Instance.IsChild(source.Handle, target),
                "Popup-isolation input must land on the context menu's own HWND.");
            var initialHorizontal = menu.HorizontalOffset;
            var initialVertical = menu.VerticalOffset;
            NativeWindowTest.SetCursorPosition(x, y);
            await Task.Delay(80);
            await SendPictureInPictureContextMenuRightClickAsync(busy: true, afterInjection: null);
            await Task.Delay(250);
            Assert.Equal(1, opened);
            AssertNear(initialHorizontal, menu.HorizontalOffset, 0.5);
            AssertNear(initialVertical, menu.VerticalOffset, 0.5);
            Assert.Equal(false, window.IsTopBarShown);

            // If WPF dismissed its own menu, reopen normally; either native menu
            // behavior is allowed, but the owner must never close/reopen it itself.
            if (!menu.IsOpen)
            {
                NativeWindowTest.SetCursorPosition(point.X, point.Y);
                await SendPictureInPictureContextMenuRightClickAsync(busy: false, afterInjection: null);
                await TestWait.UntilAsync(() => menu.IsOpen, TimeSpan.FromSeconds(2));
            }
            await ClickPictureInPictureTopBarMenuItemAsync(menu, item);
            await TestWait.UntilAsync(() => window.IsTopBarShown && !menu.IsOpen, TimeSpan.FromSeconds(2));
        });

    private static Task WithPictureInPictureContextMenuSurfaceAsync(bool showTopBar,
        Func<ReplayOverlayTestSession, MainWindow, DetachedVideoWindow, VideoSurface, Task> run,
        bool includeEmptyCell = false) => TestSta.RunAsync(async () =>
    {
        if (!NativeWindowTest.TryGetCursorPosition(out var originalCursor))
            throw new InteractiveDesktopTestSkippedException("Cannot preserve the cursor for PiP context-menu surface input.");
        var previousForeground = NativeWindowTest.GetForegroundWindow();
        await using var session = await ReplayOverlayTestSession.CreateAsync();
        await using var second = includeEmptyCell ? await ReplayOverlayTestSession.CreateAsync() : null;
        await using var third = includeEmptyCell ? await ReplayOverlayTestSession.CreateAsync() : null;
        StreamTabViewModel[] tabs = includeEmptyCell ? [session.Tab, second!.Tab, third!.Tab] : [session.Tab];
        var window = new DetachedVideoWindow(tabs, session.Tab, showTopBar)
        {
            Width = 640,
            Height = showTopBar ? 404 : 360,
            Left = SystemParameters.WorkArea.Left + 80,
            Top = SystemParameters.WorkArea.Top + 80,
            Topmost = true,
            ShowInTaskbar = false
        };
        var main = new MainWindow(false);
        RemoveMainWindowAutomaticStartup(main);
        var detachedWindows = VolumeWheelDetachedWindows(main);
        detachedWindows.Add(session.Tab, window);
        try
        {
            ShowPictureInPictureResizeWindow(window);
            await NativeWindowTest.RequireForegroundAsync(new WindowInteropHelper(window).Handle,
                TimeSpan.FromSeconds(2), "PiP context-menu surface input");
            var surface = FindVisualDescendants<VideoSurface>(window).Single(value => ReferenceEquals(value.Tag, session.Tab));
            await StartVolumeWheelTestHookAsync(main);
            await run(session, main, window, surface);
        }
        finally
        {
            PictureInPictureContextMenuMouseEvent(0x0010 | 0x0004, 0, 0, 0, UIntPtr.Zero);
            ((ContextMenu)window.FindName("VideoContextMenu")).IsOpen = false;
            StopVolumeWheelTestHook(main);
            NativeWindowTest.ReleaseCapture();
            detachedWindows.Clear();
            window.CloseForTabDisposal();
            main.Close();
            NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
            NativeWindowTest.ActivateWindow(previousForeground);
        }
    });
}

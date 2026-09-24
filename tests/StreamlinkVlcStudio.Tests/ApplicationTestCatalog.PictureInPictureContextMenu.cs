using System.Windows.Interop;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> PictureInPictureContextMenuTests =>
    [
        ("picture-in-picture context menu preserves physical right clicks over a native child while the dispatcher is busy",
            () => PictureInPictureContextMenuPhysicalAsync(null)),
        .. PictureInPictureContextMenuVlcTests
    ];

    private static IReadOnlyList<(string Name, Func<Task> Run)> PictureInPictureContextMenuVlcTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA"))
            ? []
            :
            [
                ("picture-in-picture context menu toggles the top bar once over real Direct3D11 VLC during busy active and inactive input",
                    () => PictureInPictureContextMenuPhysicalAsync(VideoRendererMode.Direct3D11)),
                ("picture-in-picture context menu toggles the top bar once over real GDI VLC during busy active and inactive input",
                    () => PictureInPictureContextMenuPhysicalAsync(VideoRendererMode.Gdi))
            ];

    private static Task PictureInPictureContextMenuPhysicalAsync(VideoRendererMode? rendererMode) => TestSta.RunAsync(async () =>
    {
        if (!NativeWindowTest.TryGetCursorPosition(out var originalCursor))
            throw new InteractiveDesktopTestSkippedException("Cannot preserve the cursor for physical PiP context-menu input.");
        var previousForeground = NativeWindowTest.GetForegroundWindow();
        var workArea = SystemParameters.WorkArea;
        if (workArea.Width < 880 || workArea.Height < 440)
            throw new InteractiveDesktopTestSkippedException("PiP context-menu input requires room for two nonoverlapping windows.");

        await using var session = await ReplayOverlayTestSession.CreateAsync();
        var window = new DetachedVideoWindow([session.Tab], session.Tab, showTopBar: false)
        {
            Left = workArea.Left + 20,
            Top = workArea.Top + 50,
            Width = 640,
            Height = 360,
            Topmost = true,
            ShowInTaskbar = false
        };
        var other = new Window
        {
            Title = "PiP inactive input test",
            Left = workArea.Right - 180,
            Top = workArea.Top + 50,
            Width = 160,
            Height = 120,
            Topmost = true,
            ShowInTaskbar = false,
            Content = new Border { Background = Brushes.DimGray }
        };
        var main = new MainWindow(false);
        RemoveMainWindowAutomaticStartup(main);
        var detachedWindows = VolumeWheelDetachedWindows(main);
        detachedWindows.Add(session.Tab, window);
        IPlaybackEngine? engine = null;
        var child = IntPtr.Zero;
        PictureInPictureContextMenuChildProcedure? childProcedure = null;
        var menu = (ContextMenu)window.FindName("VideoContextMenu");
        var topBarItem = (MenuItem)window.FindName("ShowTopBarMenuItem");
        var opened = 0;
        var closed = 0;
        var changed = new List<bool>();
        menu.Opened += (_, _) => opened++;
        menu.Closed += (_, _) => closed++;
        window.TopBarVisibilityChanged += (_, shown) => changed.Add(shown);
        var context = rendererMode?.ToString() ?? "native child";
        try
        {
            other.Show();
            ShowPictureInPictureResizeWindow(window);
            var handle = new WindowInteropHelper(window).Handle;
            var otherHandle = new WindowInteropHelper(other).Handle;
            await NativeWindowTest.RequireForegroundAsync(handle, TimeSpan.FromSeconds(2), $"{context} PiP context menu");
            var surface = FindVisualDescendants<VideoSurface>(window).Single();
            var overlay = FindVisualDescendants<ReplaySeekOverlay>(window).Single();
            overlay.IsOverlayEnabled = false;
            StopReplayOverlayPointerSampling(overlay);
            if (rendererMode is { } mode)
            {
                var vlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
                var mediaPath = Path.GetFullPath(Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA")!);
                Assert.True(File.Exists(Path.Combine(vlcDirectory, "libvlc.dll")), "Configured libVLC is missing.");
                Assert.True(File.Exists(mediaPath), "Configured deterministic video fixture is missing.");
                var factory = new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings());
                engine = await factory.CreateAsync(vlcDirectory, enableNativeOverlay: false, rendererMode: mode);
                engine.SetVideoHandle(surface.Handle);
                await engine.PlayAsync(new Uri(mediaPath), 0, PlaybackAudioState.HardMuted);
                await TestWait.UntilAsync(() => engine.TryGetVideoSize(out var width, out var height) &&
                    width > 0 && height > 0 && engine.TryGetPlaybackClock(out var clock) &&
                    clock.Position > TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(8));
                Assert.Equal(mode, ((LibVlcPlaybackEngine)engine).RendererMode);
                PumpPictureInPictureResize(window);
                using var initialVideo = CaptureReplayVlcSurface(surface);
                AssertReplayVlcVideoPixels(initialVideo);
            }
            else
            {
                // Like VLC's renderer, this real child HWND consumes its own right
                // clicks. A managed routed event cannot stand in for this input path.
                child = NativeWindowTest.CreateVisibleChildWindow(surface.Handle, "button");
                childProcedure = (hwnd, message, wParam, lParam, _, _) =>
                    message is 0x0204 or 0x0205 or 0x007B
                        ? IntPtr.Zero
                        : PictureInPictureContextMenuDefSubclassProc(hwnd, message, wParam, lParam);
                Assert.True(PictureInPictureContextMenuSetWindowSubclass(child, childProcedure, new UIntPtr(1), UIntPtr.Zero),
                    "Could not install the native child's input consumer.");
            }

            // InstallMouseWheelHook constructs the actual production dispatcher,
            // with its default deadline and native capture policy.
            await StartVolumeWheelTestHookAsync(main);
            (string Name, bool Active, bool Busy, bool MovePointer)[] scenarios =
            [
                ("active stalled", true, true, true),
                ("inactive stalled", false, true, true),
                ("active immediate", true, false, false),
                ("inactive immediate", false, false, false),
                ("active repeated stall", true, true, true),
                ("inactive repeated stall", false, true, true)
            ];
            foreach (var scenario in scenarios)
            {
                Assert.Equal(false, window.IsTopBarShown);
                foreach (var showTopBar in new[] { true, false })
                {
                    PumpPictureInPictureResize(window);
                    surface.SyncNativeBounds();
                    if (child != IntPtr.Zero)
                    {
                        var bounds = NativeWindowTest.GetWindowBounds(surface.Handle);
                        NativeWindowTest.SetWindowBounds(child, 0, 0, bounds.Width, bounds.Height);
                    }
                    await NativeWindowTest.RequireForegroundAsync(scenario.Active ? handle : otherHandle,
                        TimeSpan.FromSeconds(2), $"{context} {scenario.Name}");
                    Assert.Equal(scenario.Active, NativeWindowTest.GetForegroundWindow() == handle);
                    var point = VolumeWheelPoint(surface);
                    var target = NativeWindowHitTester.Instance.WindowFromPoint(point.X, point.Y);
                    Assert.True(target != IntPtr.Zero && target != surface.Handle &&
                        NativeWindowHitTester.Instance.IsChild(surface.Handle, target),
                        $"{context} {scenario.Name}: physical input must hit a native video descendant: " +
                        NativeWindowTest.DescribeWindowAtPoint(point.X, point.Y));
                    if (child != IntPtr.Zero) Assert.Equal(child, target);
                    NativeWindowTest.SetCursorPosition(point.X, point.Y);
                    await Task.Delay(80);
                    var opensBefore = opened;
                    var closesBefore = closed;
                    var changesBefore = changed.Count;
                    var movedPoint = window.PointToScreen(new Point(-12, -12));
                    Action? movePointer = scenario.MovePointer
                        ? () => NativeWindowTest.SetCursorPosition((int)Math.Round(movedPoint.X), (int)Math.Round(movedPoint.Y))
                        : null;
                    await SendPictureInPictureContextMenuRightClickAsync(scenario.Busy, movePointer);
                    await TestWait.UntilAsync(() => menu.IsOpen, TimeSpan.FromSeconds(2),
                        $"{context} {scenario.Name}: a physical right click must open the PiP menu after the UI resumes.");
                    await Task.Delay(250); // Include the release and any late native/default routing.
                    Assert.True(menu.IsOpen, $"{context} {scenario.Name}: the right-button release must leave the menu open.");
                    Assert.Equal(opensBefore + 1, opened);
                    Assert.Equal(closesBefore, closed);
                    Assert.Equal(!showTopBar, topBarItem.IsChecked);
                    var placement = window.ToDeviceIndependentPoint(new Point(point.X, point.Y));
                    AssertNear(placement.X, menu.HorizontalOffset, 0.5);
                    AssertNear(placement.Y, menu.VerticalOffset, 0.5);

                    await ClickPictureInPictureTopBarMenuItemAsync(menu, topBarItem);
                    await TestWait.UntilAsync(() => window.IsTopBarShown == showTopBar && !menu.IsOpen,
                        TimeSpan.FromSeconds(2), $"{context}: physically selecting Show top bar must toggle the chrome.");
                    await Task.Delay(150);
                    Assert.Equal(opensBefore + 1, opened);
                    Assert.Equal(closesBefore + 1, closed);
                    Assert.Equal(changesBefore + 1, changed.Count);
                    Assert.Equal(showTopBar, changed[^1]);
                    Assert.Equal(showTopBar, window.IsTopBarShown);
                }
            }
            Console.WriteLine($"PiP {context}: 12 physical right clicks each opened one persistent menu and physically toggled the top bar; " +
                "active/inactive input, 300 ms dispatcher stalls, and pointer movement before dispatch all passed.");
        }
        finally
        {
            PictureInPictureContextMenuMouseEvent(0x0010 | 0x0004, 0, 0, 0, UIntPtr.Zero);
            menu.IsOpen = false;
            StopVolumeWheelTestHook(main);
            NativeWindowTest.ReleaseCapture();
            if (child != IntPtr.Zero)
            {
                if (childProcedure is not null)
                    PictureInPictureContextMenuRemoveWindowSubclass(child, childProcedure, new UIntPtr(1));
                NativeWindowTest.DestroyWindow(child);
            }
            GC.KeepAlive(childProcedure);
            if (engine is not null)
            {
                await engine.StopAsync();
                engine.Dispose();
            }
            detachedWindows.Clear();
            window.CloseForTabDisposal();
            other.Close();
            main.Close();
            NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
            NativeWindowTest.ActivateWindow(previousForeground);
        }
    });

    private static async Task SendPictureInPictureContextMenuRightClickAsync(bool busy, Action? afterInjection)
    {
        using var senderReady = new ManualResetEventSlim();
        using var dispatcherBlocked = new ManualResetEventSlim();
        using var inputSent = new ManualResetEventSlim();
        var sender = Task.Run(() =>
        {
            senderReady.Set();
            Assert.True(dispatcherBlocked.Wait(TimeSpan.FromSeconds(2)), "The PiP right-click sender did not observe its start gate.");
            try
            {
                PictureInPictureContextMenuMouseEvent(0x0008, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(45);
            }
            finally
            {
                PictureInPictureContextMenuMouseEvent(0x0010, 0, 0, 0, UIntPtr.Zero);
            }
            afterInjection?.Invoke();
            inputSent.Set();
        });
        Assert.True(senderReady.Wait(TimeSpan.FromSeconds(2)), "The PiP right-click sender did not start.");
        dispatcherBlocked.Set();
        var completedDuringStall = true;
        if (busy)
        {
            Thread.Sleep(300);
            completedDuringStall = inputSent.IsSet;
        }
        await sender.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(completedDuringStall, "Physical right-button down/up and pointer movement must finish while WPF is blocked.");
    }

    private static async Task ClickPictureInPictureTopBarMenuItemAsync(ContextMenu menu, MenuItem item)
    {
        menu.UpdateLayout();
        item.UpdateLayout();
        var source = (HwndSource)PresentationSource.FromVisual(menu)!;
        var point = item.PointToScreen(new Point(item.ActualWidth / 2, item.ActualHeight / 2));
        var x = (int)Math.Round(point.X);
        var y = (int)Math.Round(point.Y);
        await TestWait.UntilAsync(() =>
        {
            var target = NativeWindowHitTester.Instance.WindowFromPoint(x, y);
            return target == source.Handle || NativeWindowHitTester.Instance.IsChild(source.Handle, target);
        }, TimeSpan.FromSeconds(1), "The physical Show top bar click must land in the menu popup HWND.");
        NativeWindowTest.SetCursorPosition(x, y);
        await Task.Delay(80);
        await Task.Run(() =>
        {
            try
            {
                PictureInPictureContextMenuMouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(45);
            }
            finally
            {
                PictureInPictureContextMenuMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
            }
        });
    }

    private delegate IntPtr PictureInPictureContextMenuChildProcedure(
        IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("comctl32", EntryPoint = "SetWindowSubclass")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PictureInPictureContextMenuSetWindowSubclass(
        IntPtr hwnd, PictureInPictureContextMenuChildProcedure procedure, UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("comctl32", EntryPoint = "RemoveWindowSubclass")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PictureInPictureContextMenuRemoveWindowSubclass(
        IntPtr hwnd, PictureInPictureContextMenuChildProcedure procedure, UIntPtr subclassId);

    [DllImport("comctl32", EntryPoint = "DefSubclassProc")]
    private static extern IntPtr PictureInPictureContextMenuDefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32", EntryPoint = "mouse_event")]
    private static extern void PictureInPictureContextMenuMouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}

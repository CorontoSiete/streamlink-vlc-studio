using System.Windows.Interop;
using System.Windows.Threading;

internal static class NativePictureInPictureContextMenuTargetTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("PiP context-menu capture retains its native descendant owner and original screen point", NativeDescendantRetainsOwnerAsync),
        ("PiP context-menu capture excludes popups and retains opted-in aliases across OSD expiry", OwnedPopupRegistrationAsync),
        ("PiP context-menu capture invalidates callbacks when an HWND registration changes", StaleRegistrationAsync),
        ("PiP context-menu capture ignores unregistered and disabled roots", UnregisteredAndDisabledRootsAsync),
        ("PiP context-menu capture never claims a foreign process HWND", ForeignProcessPassesThroughAsync)
    ];

    private static Task NativeDescendantRetainsOwnerAsync() => TestSta.RunAsync(async () =>
    {
        using var surface = new VideoSurface();
        var owner = CreateWindow(surface, 20);
        var other = CreateWindow(new Border { Background = Brushes.White }, 340);
        var previousForeground = NativeWindowTest.GetForegroundWindow();
        if (!NativeWindowTest.TryGetCursorPosition(out var originalCursor))
            throw new InteractiveDesktopTestSkippedException("Cannot preserve the cursor for PiP context-menu ownership tests.");
        var ownerHandle = IntPtr.Zero;
        var otherHandle = IntPtr.Zero;
        var child = IntPtr.Zero;
        var received = new List<(string Owner, LowLevelMouseHookEvent Event)>();
        try
        {
            owner.Show();
            other.Show();
            owner.UpdateLayout();
            other.UpdateLayout();
            ownerHandle = new WindowInteropHelper(owner).Handle;
            otherHandle = new WindowInteropHelper(other).Handle;
            await NativeWindowTest.RequireForegroundAsync(otherHandle, TimeSpan.FromSeconds(2),
                "PiP context-menu inactive descendant capture");
            child = NativeWindowTest.CreateVisibleChildWindow(surface.Handle, "button");
            var click = RightDownAt(surface.PointToScreen(new Point(10, 10)));
            Assert.Equal(child, NativeWindowHitTester.Instance.WindowFromPoint(click.ScreenX, click.ScreenY));
            NativePictureInPictureContextMenuTarget.RegisterWindow(ownerHandle, e => received.Add(("owner", e)));
            NativePictureInPictureContextMenuTarget.RegisterWindow(otherHandle, e => received.Add(("other", e)));

            // Capture takes place off the dispatcher, just like the production hook.
            // Moving the pointer cannot replace the event's original screen coordinates.
            var otherPoint = other.PointToScreen(new Point(100, 100));
            NativeWindowTest.SetCursorPosition((int)Math.Round(otherPoint.X), (int)Math.Round(otherPoint.Y));
            var retained = await Task.Run(() => NativePictureInPictureContextMenuTarget.CaptureRoute(click));
            Assert.NotNull(retained);
            Assert.Equal(0, received.Count);

            // The UI can remain busy long enough for another HWND to cover the click.
            // Already-owned input must still go to its original PiP, not the new hit.
            var bounds = NativeWindowTest.GetWindowBounds(ownerHandle);
            NativeWindowTest.SetWindowBounds(otherHandle, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            await NativeWindowTest.RequireForegroundAsync(otherHandle, TimeSpan.FromSeconds(2),
                "PiP context-menu replacement root");
            Assert.Equal(otherHandle, NativeWindowHitTester.Instance.GetRootWindow(
                NativeWindowHitTester.Instance.WindowFromPoint(click.ScreenX, click.ScreenY)));
            var replacement = await Task.Run(() => NativePictureInPictureContextMenuTarget.CaptureRoute(click));
            Assert.NotNull(replacement);

            retained!();
            replacement!();
            Assert.SequenceEqual(new[] { ("owner", click), ("other", click) }, received);
        }
        finally
        {
            NativePictureInPictureContextMenuTarget.UnregisterWindow(ownerHandle);
            NativePictureInPictureContextMenuTarget.UnregisterWindow(otherHandle);
            if (child != IntPtr.Zero) NativeWindowTest.DestroyWindow(child);
            other.Close();
            owner.Close();
            NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
            NativeWindowTest.ActivateWindow(previousForeground);
        }
    });

    private static Task OwnedPopupRegistrationAsync() => TestSta.RunAsync(async () =>
    {
        var owner = CreateWindow(new Border { Background = Brushes.White }, 20);
        var popupContent = new Border { Width = 140, Height = 80, Background = Brushes.Gold };
        var popup = new Popup
        {
            Child = popupContent,
            PlacementTarget = owner,
            Placement = PlacementMode.RelativePoint,
            HorizontalOffset = 30,
            VerticalOffset = 30,
            StaysOpen = true,
            AllowsTransparency = true
        };
        var previousForeground = NativeWindowTest.GetForegroundWindow();
        var ownerHandle = IntPtr.Zero;
        var popupHandle = IntPtr.Zero;
        var received = new List<LowLevelMouseHookEvent>();
        try
        {
            owner.Show();
            owner.UpdateLayout();
            ownerHandle = new WindowInteropHelper(owner).Handle;
            await NativeWindowTest.RequireForegroundAsync(ownerHandle, TimeSpan.FromSeconds(2),
                "PiP context-menu popup exclusion");
            NativePictureInPictureContextMenuTarget.RegisterWindow(ownerHandle, received.Add);
            popup.IsOpen = true;
            await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            popupContent.UpdateLayout();
            var click = RightDownAt(popupContent.PointToScreen(new Point(20, 20)));
            popupHandle = NativeWindowHitTester.Instance.GetRootWindow(
                NativeWindowHitTester.Instance.WindowFromPoint(click.ScreenX, click.ScreenY));
            Assert.True(popupHandle != IntPtr.Zero && popupHandle != ownerHandle,
                "The test must hit a separate owned popup HWND.");
            Assert.Equal(ownerHandle, NativeWindowHitTester.Instance.GetRootOwnerWindow(popupHandle));
            Assert.True(NativePictureInPictureContextMenuTarget.CaptureRoute(click) is null,
                "An existing context menu must retain its own right-click input.");

            // Noninteractive volume OSDs can explicitly opt in without admitting all
            // owned popup windows (especially the context menu itself).
            NativePictureInPictureContextMenuTarget.RegisterAlias(popupHandle, ownerHandle);
            var retainedAlias = NativePictureInPictureContextMenuTarget.CaptureRoute(click);
            Assert.NotNull(retainedAlias);
            retainedAlias!();
            Assert.SequenceEqual(new[] { click }, received);
            NativePictureInPictureContextMenuTarget.UnregisterWindow(popupHandle);
            Assert.True(NativePictureInPictureContextMenuTarget.CaptureRoute(click) is null);

            // The OSD auto-hide timer has higher dispatcher priority than queued input.
            // Expiring this transient HWND must not erase a click its living PiP owns.
            retainedAlias!();
            Assert.SequenceEqual(new[] { click, click }, received);

            NativePictureInPictureContextMenuTarget.RegisterAlias(popupHandle, ownerHandle);
            var retainedForClosingOwner = NativePictureInPictureContextMenuTarget.CaptureRoute(click);
            Assert.NotNull(retainedForClosingOwner);
            NativePictureInPictureContextMenuTarget.UnregisterWindow(ownerHandle);
            Assert.True(NativePictureInPictureContextMenuTarget.CaptureRoute(click) is null,
                "An alias cannot claim new input after its owner registration closes.");
            retainedForClosingOwner!();
            Assert.Equal(2, received.Count);

            var replacementEvents = new List<LowLevelMouseHookEvent>();
            NativePictureInPictureContextMenuTarget.RegisterWindow(ownerHandle, replacementEvents.Add);
            Assert.True(NativePictureInPictureContextMenuTarget.CaptureRoute(click) is null,
                "A stale alias must not attach itself to a replacement owner with the same HWND.");
            retainedForClosingOwner!();
            Assert.Equal(2, received.Count);
            Assert.Equal(0, replacementEvents.Count);

            NativePictureInPictureContextMenuTarget.RegisterAlias(popupHandle, ownerHandle);
            var currentAlias = NativePictureInPictureContextMenuTarget.CaptureRoute(click);
            Assert.NotNull(currentAlias);
            currentAlias!();
            Assert.SequenceEqual(new[] { click }, replacementEvents);
        }
        finally
        {
            NativePictureInPictureContextMenuTarget.UnregisterWindow(popupHandle);
            NativePictureInPictureContextMenuTarget.UnregisterWindow(ownerHandle);
            popup.IsOpen = false;
            owner.Close();
            NativeWindowTest.ActivateWindow(previousForeground);
        }
    });

    private static Task StaleRegistrationAsync() => TestSta.RunAsync(async () =>
    {
        var window = CreateWindow(new Border { Background = Brushes.White }, 20);
        var previousForeground = NativeWindowTest.GetForegroundWindow();
        var handle = IntPtr.Zero;
        var received = new List<string>();
        try
        {
            window.Show();
            window.UpdateLayout();
            handle = new WindowInteropHelper(window).Handle;
            await NativeWindowTest.RequireForegroundAsync(handle, TimeSpan.FromSeconds(2),
                "PiP context-menu registration lifetime");
            var click = RightDownAt(window.PointToScreen(new Point(30, 40)));
            NativePictureInPictureContextMenuTarget.RegisterWindow(handle, _ => received.Add("original"));
            var removed = NativePictureInPictureContextMenuTarget.CaptureRoute(click);
            Assert.NotNull(removed);
            NativePictureInPictureContextMenuTarget.UnregisterWindow(handle);
            Assert.True(NativePictureInPictureContextMenuTarget.CaptureRoute(click) is null);
            removed!();
            Assert.Equal(0, received.Count);

            // Reusing an HWND for a new registration cannot revive queued callbacks
            // from the closed window, even when the numeric handle stays identical.
            NativePictureInPictureContextMenuTarget.RegisterWindow(handle, _ => received.Add("replacement"));
            removed!();
            Assert.Equal(0, received.Count);
            var replaced = NativePictureInPictureContextMenuTarget.CaptureRoute(click);
            Assert.NotNull(replaced);
            NativePictureInPictureContextMenuTarget.RegisterWindow(handle, _ => received.Add("current"));
            replaced!();
            Assert.Equal(0, received.Count);
            var current = NativePictureInPictureContextMenuTarget.CaptureRoute(click);
            Assert.NotNull(current);
            current!();
            Assert.SequenceEqual(new[] { "current" }, received);
        }
        finally
        {
            NativePictureInPictureContextMenuTarget.UnregisterWindow(handle);
            window.Close();
            NativeWindowTest.ActivateWindow(previousForeground);
        }
    });

    private static Task UnregisteredAndDisabledRootsAsync() => TestSta.RunAsync(async () =>
    {
        var registered = CreateWindow(new Border { Background = Brushes.White }, 20);
        var ordinary = CreateWindow(new Border { Background = Brushes.White }, 340);
        var previousForeground = NativeWindowTest.GetForegroundWindow();
        var handle = IntPtr.Zero;
        try
        {
            registered.Show();
            ordinary.Show();
            registered.UpdateLayout();
            ordinary.UpdateLayout();
            handle = new WindowInteropHelper(registered).Handle;
            await NativeWindowTest.RequireForegroundAsync(handle, TimeSpan.FromSeconds(2),
                "PiP context-menu native eligibility");
            NativePictureInPictureContextMenuTarget.RegisterWindow(handle, _ =>
                throw new InvalidOperationException("An ineligible root must not invoke its context-menu callback."));
            var ordinaryClick = RightDownAt(ordinary.PointToScreen(new Point(30, 40)));
            Assert.True(NativePictureInPictureContextMenuTarget.CaptureRoute(ordinaryClick) is null,
                "Unregistered roots in the same process must retain ordinary mouse delivery.");
            var disabledClick = RightDownAt(registered.PointToScreen(new Point(30, 40)));
            Assert.NotNull(NativePictureInPictureContextMenuTarget.CaptureRoute(disabledClick));
            _ = EnableWindow(handle, false);
            Assert.True(!NativeWindowTest.IsWindowEnabled(handle));
            Assert.True(NativePictureInPictureContextMenuTarget.CaptureRoute(disabledClick) is null,
                "A PiP disabled by a modal dialog must not claim new context-menu input.");
        }
        finally
        {
            if (handle != IntPtr.Zero) _ = EnableWindow(handle, true);
            NativePictureInPictureContextMenuTarget.UnregisterWindow(handle);
            ordinary.Close();
            registered.Close();
            NativeWindowTest.ActivateWindow(previousForeground);
        }
    });

    private static Task ForeignProcessPassesThroughAsync() => TestSta.RunAsync(() =>
    {
        var area = SystemParameters.WorkArea;
        foreach (var point in new[]
        {
            new Point(area.Left + 2, area.Top + 2),
            new Point(area.Right - 2, area.Bottom - 2),
            new Point(area.Left + area.Width / 2, area.Top + area.Height / 2)
        })
        {
            var click = RightDownAt(point);
            var target = NativeWindowHitTester.Instance.WindowFromPoint(click.ScreenX, click.ScreenY);
            var root = NativeWindowHitTester.Instance.GetRootWindow(target);
            if (root == IntPtr.Zero || GetWindowThreadProcessId(root, out var processId) == 0 ||
                processId == (uint)Environment.ProcessId)
                continue;

            // Even an incorrect or stale registration must never claim another process.
            NativePictureInPictureContextMenuTarget.RegisterWindow(root, _ =>
                throw new InvalidOperationException("Foreign input reached a PiP context-menu callback."));
            try
            {
                Assert.True(NativePictureInPictureContextMenuTarget.CaptureRoute(click) is null);
            }
            finally
            {
                NativePictureInPictureContextMenuTarget.UnregisterWindow(root);
            }
            return;
        }
        throw new InteractiveDesktopTestSkippedException("No foreign desktop HWND is exposed for PiP context-menu ownership checks.");
    });

    private static Window CreateWindow(UIElement content, double horizontalOffset) => new()
    {
        Width = 280,
        Height = 180,
        Left = SystemParameters.WorkArea.Left + horizontalOffset,
        Top = SystemParameters.WorkArea.Top + 40,
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize,
        Topmost = true,
        ShowInTaskbar = false,
        Content = content
    };

    private static LowLevelMouseHookEvent RightDownAt(Point point) => new(
        LowLevelMouseHookEvent.WmRightButtonDown, (int)Math.Round(point.X), (int)Math.Round(point.Y), 0);

    [DllImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool enabled);

    [DllImport("user32")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}

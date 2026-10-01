internal static partial class ApplicationTestCatalog
{
    private static IReadOnlyList<(string Name, Func<Task> Run)> ReplaySeekOverlayZOrderTests =>
    [
        ("replay seek overlay repairs existing renderer z-order while the pointer is stationary",
            () => ReplaySeekOverlayRepairsExistingRendererAsync(ReplayOverlayRepairTrigger.PointerPoll)),
        ("replay seek overlay repairs existing renderer z-order on repeated reveal activity",
            () => ReplaySeekOverlayRepairsExistingRendererAsync(ReplayOverlayRepairTrigger.Reveal)),
        ("replay seek overlay repairs native renderer reorder without pointer polling or activity",
            () => ReplaySeekOverlayRepairsExistingRendererAsync(ReplayOverlayRepairTrigger.NativeReorder))
    ];

    private enum ReplayOverlayRepairTrigger { PointerPoll, Reveal, NativeReorder }

    private static Task ReplaySeekOverlayRepairsExistingRendererAsync(ReplayOverlayRepairTrigger trigger) => TestSta.RunAsync(async () =>
    {
        if (!NativeWindowTest.TryGetCursorPosition(out var originalCursor))
            throw new InteractiveDesktopTestSkippedException("Cannot preserve the cursor for seekbar z-order input.");
        await using var session = await ReplayOverlayTestSession.CreateAsync();
        await session.Tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        using var fixture = new ReplayOverlayTestHost(session.Tab);
        var window = Window.GetWindow(fixture.Overlay)!;
        window.Topmost = true;
        var renderer = IntPtr.Zero;
        try
        {
            var activationPoint = fixture.Target.PointToScreen(new Point(20, 20));
            NativeWindowTest.SendLeftClick((int)Math.Round(activationPoint.X), (int)Math.Round(activationPoint.Y));
            await NativeWindowTest.RequireForegroundAsync(fixture.OwnerHandle, TimeSpan.FromSeconds(1),
                "Seekbar z-order input requires the test owner to be active");
            renderer = NativeWindowTest.CreateVisibleChildWindow(fixture.Target.Handle, "StreamStudioVideoSurface");
            fixture.FlushBindings();
            await TestWait.UntilAsync(() => typeof(VideoSurface)
                .GetField("rendererWindowRepairTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(fixture.Target) is null, TimeSpan.FromSeconds(10),
                "The bounded startup renderer repair must finish before exercising a later z-order change.");
            fixture.Overlay.ProcessPointerSample(new Point(100, 100), true, Environment.TickCount64);
            fixture.FlushBindings();
            await Task.Delay(180);
            fixture.StopPointerSampling();
            var combo = (ComboBox)fixture.Overlay.FindName("PlaybackRateComboBox");
            var center = combo.PointToScreen(new Point(combo.ActualWidth / 2, combo.ActualHeight / 2));
            var centerX = (int)Math.Round(center.X);
            var centerY = (int)Math.Round(center.Y);
            NativeWindowTest.SetCursorPosition(centerX, centerY);
            fixture.Overlay.ProcessPointerSample(new Point(centerX, centerY), true, true, Environment.TickCount64);
            var overlay = fixture.NativeOverlayHandle;
            var rendererBounds = NativeWindowTest.GetWindowBounds(renderer);
            var overlayBounds = NativeWindowTest.GetWindowBounds(overlay);
            Assert.Equal(overlay, NativeWindowHitTester.Instance.WindowFromPoint(centerX, centerY));
            Assert.True(ReplayOverlaySetWindowPos(renderer, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010));
            Assert.True(!fixture.Target.IsOverlayAboveRenderer(overlay));
            Assert.Equal(renderer, NativeWindowHitTester.Instance.WindowFromPoint(centerX, centerY));
            Assert.Equal(rendererBounds, NativeWindowTest.GetWindowBounds(renderer));
            Assert.Equal(overlayBounds, NativeWindowTest.GetWindowBounds(overlay));
            Assert.True(fixture.Overlay.IsOverlayOpen && fixture.Chrome.Opacity > 0.99);
            if (trigger == ReplayOverlayRepairTrigger.PointerPoll)
            {
                var pointerTimer = (System.Windows.Threading.DispatcherTimer)typeof(ReplaySeekOverlay)
                    .GetField("pointerTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Overlay)!;
                pointerTimer.Start();
                await TestWait.UntilAsync(() => fixture.Target.IsOverlayAboveRenderer(overlay),
                    TimeSpan.FromSeconds(1), "Pointer polling must repair z-order before classifying the stationary pointer.");
                await Task.Delay(ReplaySeekOverlay.IdleDelay + TimeSpan.FromMilliseconds(300));
            }
            else if (trigger == ReplayOverlayRepairTrigger.Reveal)
            {
                fixture.Overlay.ProcessPointerSample(new Point(centerX + 1, centerY), true, true, Environment.TickCount64);
            }
            else
            {
                await TestWait.UntilAsync(() => fixture.Target.IsOverlayAboveRenderer(overlay),
                    TimeSpan.FromMilliseconds(250),
                    "A native renderer reorder must repair controls without pointer polling, activity, or changed bounds.");
            }
            Assert.True(fixture.Target.IsOverlayAboveRenderer(overlay),
                "Already-open controls must be raised above a renderer that changed only sibling z-order.");
            Assert.True(fixture.Overlay.IsOverlayOpen);
            Assert.Equal(overlay, fixture.NativeOverlayHandle);
            Assert.Equal(overlay, NativeWindowHitTester.Instance.WindowFromPoint(centerX, centerY));
            Assert.Equal(rendererBounds, NativeWindowTest.GetWindowBounds(renderer));
            Assert.Equal(overlayBounds, NativeWindowTest.GetWindowBounds(overlay));
            if (trigger == ReplayOverlayRepairTrigger.NativeReorder)
            {
                var overlayHost = (VideoOverlayHost)fixture.Overlay.FindName("OverlayHost");
                var hookField = typeof(VideoOverlayHost)
                    .GetField("rendererReorderHook", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.True((IntPtr)hookField.GetValue(overlayHost)! != IntPtr.Zero);
                fixture.Overlay.IsOverlayEnabled = false;
                fixture.FlushBindings();
                await fixture.AssertClosedAsync();
                Assert.Equal(IntPtr.Zero, (IntPtr)hookField.GetValue(overlayHost)!);
            }
        }
        finally
        {
            fixture.StopPointerSampling();
            if (renderer != IntPtr.Zero) NativeWindowTest.DestroyWindow(renderer);
            NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
        }
    });

    [DllImport("user32", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReplayOverlaySetWindowPos(IntPtr window, IntPtr insertAfter,
        int left, int top, int width, int height, int flags);
}

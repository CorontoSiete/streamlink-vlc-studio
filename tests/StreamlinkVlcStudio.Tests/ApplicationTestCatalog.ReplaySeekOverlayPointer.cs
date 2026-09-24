internal static partial class ApplicationTestCatalog
{
    private static IReadOnlyList<(string Name, Func<Task> Run)> ReplaySeekOverlayPointerTests =>
    [
        ("replay seek overlay physical clicks land exactly under the pointer across layouts", ReplaySeekOverlayExactPointerClicksAsync),
        ("replay seek overlay physical track and thumb drags preserve exact pointer position", ReplaySeekOverlayExactPointerDragsAsync),
        ("replay seek overlay pointer moves stay exact before pending layout runs", ReplaySeekOverlayPointerBeforeLayoutAsync)
    ];

    private static Task ReplaySeekOverlayExactPointerClicksAsync() => WithReplayOverlayPointerAsync(async (session, fixture) =>
    {
        // Alternate in both directions. A broad assertion such as "past halfway" misses
        // the original failure, where a click can double the distance from the old thumb.
        foreach (var fraction in new[] { 0.73, 0.24, 0.61, 0.13, 0.87, 0.46 })
            await ClickReplayTrackAndAssertAsync(session, fixture, fraction);

        fixture.Target.Width = 196;
        fixture.Target.Height = 200;
        fixture.FlushBindings();
        Assert.True(fixture.Overlay.IsCompactLayout);
        foreach (var fraction in new[] { 0.22, 0.78, 0.38 })
            await ClickReplayTrackAndAssertAsync(session, fixture, fraction);

        fixture.Target.Width = double.NaN;
        fixture.Target.Height = double.NaN;
        fixture.FlushBindings();
        Assert.Equal(false, fixture.Overlay.IsCompactLayout);

        // Exercise physical pixels versus WPF units even on a 100% DPI test desktop.
        // Asymmetric track margins also ensure slider bounds cannot stand in for the
        // actual track. Pointer coordinates still come from the rendered visual tree.
        fixture.Slider.LayoutTransform = new ScaleTransform(1.25, 1);
        ReplayPointerTrack(fixture).Margin = new Thickness(17, 0, 31, 0);
        fixture.FlushBindings();
        foreach (var fraction in new[] { 0.81, 0.19, 0.57 })
            await ClickReplayTrackAndAssertAsync(session, fixture, fraction);
    });

    private static Task ReplaySeekOverlayExactPointerDragsAsync() => WithReplayOverlayPointerAsync(async (session, fixture) =>
    {
        var track = ReplayPointerTrack(fixture);
        var initialCount = session.SeekCount;
        var start = ReplayTrackScreenPoint(track, 0.28);
        await BeginReplayPhysicalPointerAsync(session, fixture, start);
        AssertReplayPointerPreview(session, fixture, start, "track press");
        foreach (var fraction in new[] { 0.74, 0.16, -0.20, 1.20, 0.58 })
        {
            var point = ReplayTrackScreenPoint(track, fraction);
            await MoveReplayPointerAndWaitAsync(track, point);
            fixture.FlushBindings();
            AssertReplayPointerPreview(session, fixture, point, $"track drag to {fraction:P0}");
            Assert.Equal(initialCount, session.SeekCount);
        }
        var release = ReplayTrackScreenPoint(track, 0.58);
        await ReleaseReplayPhysicalPointerAsync(session, fixture, release, initialCount);

        // Press off-center inside the real Thumb so its grab offset must survive the
        // drag. Raising DragStarted directly would bypass both native capture and
        // Slider's class handlers, so all down/move/up input here is physical.
        var thumb = track.Thumb!;
        var thumbCenter = thumb.PointToScreen(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2));
        var press = RoundedReplayPointer(new Point(thumbCenter.X + 3, thumbCenter.Y));
        var grabOffset = press.X - thumbCenter.X;
        initialCount = session.SeekCount;
        await BeginReplayPhysicalPointerAsync(session, fixture, press);
        Assert.True(thumb.IsDragging, "Pressing the visible thumb must use the WPF Thumb drag route.");
        Assert.True(ReferenceEquals(Mouse.Captured, thumb), "The Thumb must retain native mouse capture while dragging.");
        foreach (var fraction in new[] { 0.82, 0.21, 0.67 })
        {
            var desiredCenter = ReplayTrackScreenPoint(track, fraction);
            var point = RoundedReplayPointer(new Point(desiredCenter.X + grabOffset, desiredCenter.Y));
            await MoveReplayPointerAndWaitAsync(track, point);
            fixture.FlushBindings();
            var expectedCenter = new Point(point.X - grabOffset, point.Y);
            AssertReplayPointerPreview(session, fixture, expectedCenter, $"thumb drag to {fraction:P0}");
            Assert.Equal(initialCount, session.SeekCount);
        }
        var finalCenter = thumb.PointToScreen(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2));
        await ReleaseReplayPhysicalPointerAsync(session, fixture, finalCenter, initialCount);
        Assert.Equal(false, thumb.IsDragging);
    });

    private static Task ReplaySeekOverlayPointerBeforeLayoutAsync() => WithReplayOverlayPointerAsync(async (session, fixture) =>
    {
        var track = ReplayPointerTrack(fixture);
        var initialCount = session.SeekCount;
        await BeginReplayPhysicalPointerAsync(session, fixture, ReplayTrackScreenPoint(track, 0.31));
        fixture.FlushBindings();

        // Send each WM_MOUSEMOVE synchronously while the physical button is held.
        // No dispatcher pump or UpdateLayout is allowed between samples: Track's
        // cached thumb center still belongs to the previous arrange. These are real
        // HwndSource input reports, not Slider.Value assignments or handler calls.
        foreach (var fraction in new[] { 0.76, 0.23, 0.23, 0.62, -0.1, 1.1, 0.42 })
        {
            var point = ReplayTrackScreenPoint(track, fraction);
            MoveReplayPointer(point);
            NativeWindowTest.SendMessage(fixture.NativeOverlayHandle, 0x0200, new IntPtr(1),
                NativeWindowTest.MakeMouseLParamFromScreenPoint(fixture.NativeOverlayHandle, point));
            var reported = Mouse.GetPosition(track);
            var expectedPoint = track.PointFromScreen(point);
            Assert.True(Math.Abs(reported.X - expectedPoint.X) < 0.01,
                $"Native input must report the injected point before layout; expected {expectedPoint}, got {reported}.");
            AssertReplayPointerValue(session, fixture, point, $"unarranged move to {fraction:P0}");
            Assert.Equal(initialCount, session.SeekCount);
        }
        fixture.FlushBindings();
        var release = ReplayTrackScreenPoint(track, 0.42);
        AssertReplayPointerPreview(session, fixture, release, "after the pending arrange");
        await ReleaseReplayPhysicalPointerAsync(session, fixture, release, initialCount);
    });

    private static Task WithReplayOverlayPointerAsync(Func<ReplayOverlayTestSession, ReplayOverlayTestHost, Task> run) =>
        TestSta.RunAsync(async () =>
        {
            if (!NativeWindowTest.TryGetCursorPosition(out var originalCursor))
                throw new InteractiveDesktopTestSkippedException("Cannot preserve the cursor for exact replay pointer tests.");

            await using var session = await ReplayOverlayTestSession.CreateAsync();
            using var fixture = new ReplayOverlayTestHost(session.Tab);
            var window = Window.GetWindow(fixture.Overlay)!;
            window.Topmost = true;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = SystemParameters.WorkArea.Left + 24;
            window.Top = SystemParameters.WorkArea.Top + 24;
            window.Width = Math.Min(820, SystemParameters.WorkArea.Width - 48);
            window.Height = Math.Min(520, SystemParameters.WorkArea.Height - 48);
            try
            {
                await NativeWindowTest.RequireForegroundAsync(fixture.OwnerHandle, TimeSpan.FromSeconds(1),
                    "Exact replay pointer input requires the test owner to be active");
                fixture.Overlay.ProcessPointerSample(new Point(100, 100), true, Environment.TickCount64);
                await Task.Delay(150); // Finish the native overlay's reveal animation.
                fixture.FlushBindings();
                fixture.StopPointerSampling();
                Assert.True(fixture.Overlay.IsOverlayOpen);
                Assert.True(fixture.Slider.IsEnabled);
                Assert.True(ReplayPointerTrack(fixture).ActualWidth > 0);
                await run(session, fixture);
            }
            finally
            {
                ReplayVlcMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                Mouse.Capture(null);
                NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
            }
        });

    private static Track ReplayPointerTrack(ReplayOverlayTestHost fixture) =>
        (Track)fixture.Slider.Template.FindName("PART_Track", fixture.Slider);

    private static Point RoundedReplayPointer(Point point) => new(Math.Round(point.X), Math.Round(point.Y));

    private static Point ReplayTrackScreenPoint(Track track, double fraction)
    {
        var thumbWidth = track.Thumb!.ActualWidth;
        return RoundedReplayPointer(track.PointToScreen(new Point(
            thumbWidth / 2 + (track.ActualWidth - thumbWidth) * fraction, track.ActualHeight / 2)));
    }

    private static void MoveReplayPointer(Point point) =>
        NativeWindowTest.SetCursorPosition((int)Math.Round(point.X), (int)Math.Round(point.Y));

    private static async Task MoveReplayPointerAndWaitAsync(Track track, Point point)
    {
        MoveReplayPointer(point);
        Assert.True(NativeWindowTest.TryGetCursorPosition(out var actualScreenPoint));
        // SetCursorPos queues a native packet; an ApplicationIdle callback can still
        // run before that packet reaches WPF. Wait for input delivery, not for the
        // slider result we are testing. Windows may clip an outside drag to a monitor
        // edge, so use the actual screen position when checking delivery.
        var expected = track.PointFromScreen(new Point(actualScreenPoint.X, actualScreenPoint.Y));
        await TestWait.UntilAsync(() =>
        {
            var actual = Mouse.GetPosition(track);
            return Math.Abs(actual.X - expected.X) < 0.01 && Math.Abs(actual.Y - expected.Y) < 0.01;
        }, TimeSpan.FromSeconds(1));
    }

    private static double ExpectedReplayPointerValue(ReplayOverlayTestHost fixture, Point screenPoint)
    {
        var track = ReplayPointerTrack(fixture);
        var point = track.PointFromScreen(screenPoint);
        var thumbWidth = track.Thumb!.ActualWidth;
        var fraction = Math.Clamp((point.X - thumbWidth / 2) / (track.ActualWidth - thumbWidth), 0, 1);
        return fixture.Slider.Minimum + fraction * (fixture.Slider.Maximum - fixture.Slider.Minimum);
    }

    private static async Task BeginReplayPhysicalPointerAsync(
        ReplayOverlayTestSession session, ReplayOverlayTestHost fixture, Point point)
    {
        var hit = NativeWindowHitTester.Instance.WindowFromPoint((int)point.X, (int)point.Y);
        Assert.Equal(fixture.NativeOverlayHandle, hit);
        MoveReplayPointer(point);
        ReplayVlcMouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
        await TestWait.UntilAsync(() => session.Tab.IsReplaySeekPreviewActive,
            TimeSpan.FromSeconds(1));
        fixture.FlushBindings();
    }

    private static async Task ClickReplayTrackAndAssertAsync(
        ReplayOverlayTestSession session, ReplayOverlayTestHost fixture, double fraction)
    {
        var point = ReplayTrackScreenPoint(ReplayPointerTrack(fixture), fraction);
        var initialCount = session.SeekCount;
        await MoveReplayPointerAndWaitAsync(ReplayPointerTrack(fixture), point);
        // Queue down/up together, as a fast click does, with no layout pass inserted
        // between them by this test. The committed engine position is the assertion.
        NativeWindowTest.SendLeftClick((int)point.X, (int)point.Y);
        await AssertReplayPointerCommittedAsync(session, fixture, point, initialCount, $"click at {fraction:P0}");
    }

    private static async Task ReleaseReplayPhysicalPointerAsync(
        ReplayOverlayTestSession session, ReplayOverlayTestHost fixture, Point expectedCenter, int initialCount)
    {
        ReplayVlcMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
        await AssertReplayPointerCommittedAsync(session, fixture, expectedCenter, initialCount, "pointer release");
    }

    private static async Task AssertReplayPointerCommittedAsync(
        ReplayOverlayTestSession session, ReplayOverlayTestHost fixture, Point point, int initialCount, string scenario)
    {
        await TestWait.UntilAsync(() => session.SeekCount == initialCount + 1 &&
            !session.Tab.IsReplaySeekPreviewActive && session.Tab.CanSeekReplay, TimeSpan.FromSeconds(2));
        fixture.FlushBindings();
        var expected = ExpectedReplayPointerValue(fixture, point);
        Assert.True(Math.Abs(session.PlaybackFactory.Engine!.Position.TotalSeconds - expected) < 0.001,
            $"{scenario}: playback must seek to {expected:0.000000}s under the pointer, " +
            $"not {session.PlaybackFactory.Engine.Position.TotalSeconds:0.000000}s.");
        AssertReplayPointerValue(session, fixture, point, scenario);
        AssertReplayThumbCenter(fixture, point, scenario);
        Assert.Equal(initialCount + 1, session.SeekCount);
    }

    private static void AssertReplayPointerPreview(
        ReplayOverlayTestSession session, ReplayOverlayTestHost fixture, Point point, string scenario)
    {
        Assert.True(session.Tab.IsReplaySeekPreviewActive, $"{scenario}: preview must remain active until release.");
        AssertReplayPointerValue(session, fixture, point, scenario);
        AssertReplayThumbCenter(fixture, point, scenario);
    }

    private static void AssertReplayPointerValue(
        ReplayOverlayTestSession session, ReplayOverlayTestHost fixture, Point point, string scenario)
    {
        var expected = ExpectedReplayPointerValue(fixture, point);
        Assert.True(Math.Abs(fixture.Slider.Value - expected) < 0.001,
            $"{scenario}: slider must select {expected:0.000000}s under the pointer, not {fixture.Slider.Value:0.000000}s.");
        Assert.True(Math.Abs(session.Tab.ReplaySeekSliderValue - expected) < 0.001,
            $"{scenario}: the two-way preview must match the pointer's exact position.");
    }

    private static void AssertReplayThumbCenter(ReplayOverlayTestHost fixture, Point point, string scenario)
    {
        var track = ReplayPointerTrack(fixture);
        var thumb = track.Thumb!;
        var center = thumb.PointToScreen(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2));
        var start = track.PointToScreen(new Point(thumb.ActualWidth / 2, track.ActualHeight / 2));
        var end = track.PointToScreen(new Point(track.ActualWidth - thumb.ActualWidth / 2, track.ActualHeight / 2));
        var expectedX = Math.Clamp(point.X, start.X, end.X);
        Assert.True(Math.Abs(center.X - expectedX) <= 1,
            $"{scenario}: rendered thumb center {center.X:0.###}px must match pointer {expectedX:0.###}px.");
    }
}

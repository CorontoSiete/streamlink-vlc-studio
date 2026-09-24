internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> ReplaySeekOverlayTests { get; } =
    [
        ("replay seek overlay stays visible over controls and hides on stationary video pointer", ReplaySeekOverlayFollowsPointerActivityAsync),
        ("replay seek overlay stays visible while scrubbing and commits exactly once", ReplaySeekOverlayScrubCommitAsync),
        ("replay seek overlay cancels a drag when the selected stream changes", ReplaySeekOverlayTabSwitchCancelsScrubAsync),
        ("replay seek overlay cancels an interrupted thumb drag without seeking", ReplaySeekOverlayCancelledDragAsync),
        ("replay seek overlay closes and cancels preview when disabled or unloaded", ReplaySeekOverlayLifecycleAsync),
        ("replay seek overlay retargets between video surfaces and releases the previous host", ReplaySeekOverlayRetargetAsync),
        ("replay seek overlay protects keyboard seeking and mouse capture from idle dismissal", ReplaySeekOverlayKeyboardAndCaptureAsync),
        ("replay seek overlay is a transparent video child that moves synchronously and stays above renderer children", ReplaySeekOverlayNativeEmbeddingAsync),
        ("replay seek overlay owns native input while volume popup remains routable", ReplaySeekOverlayNativeRoutingAsync),
        ("replay seek overlay polls real movement over native renderer children", ReplaySeekOverlayNativePointerIntegrationAsync),
        .. ReplaySeekOverlayPointerTests,
        .. ReplaySeekTrackTests,
        .. ReplaySeekHoverTests,
        .. ReplaySeekOverlayVlcTests
    ];

    private static Task ReplaySeekOverlayFollowsPointerActivityAsync() => TestSta.RunAsync(async () =>
    {
        await using var session = await ReplayOverlayTestSession.CreateAsync();
        using var fixture = new ReplayOverlayTestHost(session.Tab);
        var overlay = fixture.Overlay;
        var start = Environment.TickCount64;
        var idleDelay = (long)ReplaySeekOverlay.IdleDelay.TotalMilliseconds;
        var position = new Point(100, 100);

        overlay.ProcessPointerSample(position, true, start);
        fixture.FlushBindings();
        Assert.True(overlay.IsOverlayOpen);
        Assert.NotNull(PresentationSource.FromVisual(fixture.Chrome));
        Assert.True(fixture.Target.RenderSize.Height > fixture.Chrome.ActualHeight,
            "The overlay must float over the video target rather than replace it.");
        Assert.Equal(false, overlay.IsCompactLayout);
        Assert.True(Math.Abs(fixture.Chrome.ActualHeight - 74) <= 1,
            $"Expected the normal seekbar to remain one compact row; height was {fixture.Chrome.ActualHeight}.");

        // Resting over the controls keeps them available without requiring constant movement.
        overlay.ProcessPointerSample(position, true, false, start + idleDelay - 1);
        Assert.True(overlay.IsOverlayOpen);
        overlay.ProcessPointerSample(position, true, true, start + idleDelay);
        await Task.Delay(250);
        Assert.True(overlay.IsOverlayOpen, "A pointer parked on the replay controls must keep them visible.");

        position = new Point(100, 101);
        overlay.ProcessPointerSample(position, true, false, start + idleDelay + 251);
        start += idleDelay + 251;
        overlay.ProcessPointerSample(position, true, false, start + idleDelay);
        overlay.ProcessPointerSample(new Point(100, 102), true, false, start + idleDelay + 1);
        await Task.Delay(250);
        Assert.True(overlay.IsOverlayOpen, "Movement must cancel an in-progress fade without a stale completion closing the controls.");
        position = new Point(100, 102);
        start += idleDelay + 1;
        overlay.ProcessPointerSample(position, true, false, start + idleDelay);
        await fixture.AssertClosedAsync();

        // Continuing to sample the stationary pointer must not immediately reopen the controls.
        overlay.ProcessPointerSample(position, true, start + idleDelay + 200);
        Assert.Equal(false, overlay.IsOverlayOpen);
        overlay.ProcessPointerSample(new Point(101, 100), true, start + idleDelay + 201);
        Assert.True(overlay.IsOverlayOpen);

        // Activity elsewhere in the application does not reveal video controls.
        overlay.ProcessPointerSample(new Point(700, 500), false, start + 2 * idleDelay + 201);
        await fixture.AssertClosedAsync();
        overlay.ProcessPointerSample(new Point(701, 500), false, start + 2 * idleDelay + 202);
        Assert.Equal(false, overlay.IsOverlayOpen);

        // Exercise the compiled control namescope binding at the smallest supported video
        // width. The timestamp and action buttons must move onto separate rows to fit.
        overlay.IsOverlayEnabled = false;
        fixture.Target.Width = 196;
        fixture.Target.Height = 200;
        fixture.FlushBindings();
        overlay.IsOverlayEnabled = true;
        fixture.StopPointerSampling();
        overlay.ProcessPointerSample(new Point(702, 500), true, start + 2 * idleDelay + 203);
        fixture.FlushBindings();
        Assert.True(overlay.IsOverlayOpen);
        Assert.True(overlay.IsCompactLayout);
        Assert.Equal(180d, fixture.Chrome.ActualWidth);
        Assert.True(Math.Abs(fixture.Chrome.ActualHeight - 93) <= 1,
            $"Expected a compact two-row seekbar at 196px target width; height was {fixture.Chrome.ActualHeight}.");
        var transportGrid = (Grid)overlay.FindName("ReplayTransportGrid");
        var actions = transportGrid.Children.OfType<StackPanel>().Single();
        Assert.Equal(1, Grid.GetRow(actions));
        Assert.Equal(2, Grid.GetColumnSpan(actions));
        var compactSource = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(fixture.Chrome)!;
        var nativeBounds = NativeWindowTest.GetWindowBounds(compactSource.Handle);
        var chromeSize = fixture.Chrome.PointToScreen(new Point(fixture.Chrome.ActualWidth, fixture.Chrome.ActualHeight))
            - fixture.Chrome.PointToScreen(new Point());
        Assert.True(Math.Abs(nativeBounds.Height - chromeSize.Y) <= 1 &&
            Math.Abs(nativeBounds.Width - chromeSize.X) <= 1,
            "Reopening after a hidden resize must measure the resumed WPF root before sizing its HWND.");
    });

    private static Task ReplaySeekOverlayScrubCommitAsync() => TestSta.RunAsync(async () =>
    {
        await using var session = await ReplayOverlayTestSession.CreateAsync();
        using var fixture = new ReplayOverlayTestHost(session.Tab);
        var start = Environment.TickCount64;
        fixture.Overlay.ProcessPointerSample(new Point(100, 100), true, start);
        var seekCount = session.SeekCount;
        var committedPosition = session.Tab.ReplaySeekValue;

        fixture.BeginDrag();
        fixture.Slider.Value = 600;
        fixture.FlushBindings();
        Assert.True(session.Tab.IsReplaySeekPreviewActive);
        Assert.Equal(600d, session.Tab.ReplaySeekSliderValue);
        Assert.Equal(committedPosition, session.Tab.ReplaySeekValue);
        fixture.Overlay.ProcessPointerSample(null, false, start + 10_000);
        Assert.True(fixture.Overlay.IsOverlayOpen,
            "A stationary drag outside the video must retain the controls until release.");
        Assert.Equal(seekCount, session.SeekCount);

        fixture.EndDrag(cancelled: false);
        await TestWait.UntilAsync(
            () => !session.Tab.IsReplaySeekPreviewActive && session.SeekCount == seekCount + 1,
            TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromMinutes(10), session.PlaybackFactory.Engine!.Position);

        // A thumb release is followed by mouse-up in real WPF routing; that second event
        // must not issue another seek after DragCompleted has consumed the preview.
        fixture.Slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent
        });
        fixture.FlushBindings();
        Assert.Equal(seekCount + 1, session.SeekCount);
    });

    private static Task ReplaySeekOverlayTabSwitchCancelsScrubAsync() => TestSta.RunAsync(async () =>
    {
        await using var first = await ReplayOverlayTestSession.CreateAsync();
        await using var second = await ReplayOverlayTestSession.CreateAsync();
        using var fixture = new ReplayOverlayTestHost(first.Tab);
        var start = Environment.TickCount64;
        fixture.Overlay.ProcessPointerSample(new Point(100, 100), true, start);
        var firstSeekCount = first.SeekCount;
        var secondSeekCount = second.SeekCount;
        var secondPosition = second.Tab.ReplaySeekValue;

        fixture.BeginDrag();
        fixture.Slider.Value = 420;
        fixture.FlushBindings();
        Assert.True(first.Tab.IsReplaySeekPreviewActive);
        var previousOverlayHandle = fixture.NativeOverlayHandle;
        fixture.Overlay.DataContext = second.Tab;
        fixture.FlushBindings();
        Assert.Equal(false, fixture.Overlay.IsOverlayOpen);
        Assert.Equal(false, NativeWindowTest.IsWindowVisible(previousOverlayHandle));
        Assert.Equal(false, first.Tab.IsReplaySeekPreviewActive);
        Assert.Equal(first.Tab.ReplaySeekValue, first.Tab.ReplaySeekSliderValue);

        // Routed events can arrive after a tab selection change. They belong to the old
        // interaction and must not commit the rebound slider into the newly selected tab.
        fixture.EndDrag(cancelled: false);
        fixture.FlushBindings();
        Assert.Equal(firstSeekCount, first.SeekCount);
        Assert.Equal(secondSeekCount, second.SeekCount);
        Assert.Equal(secondPosition, second.Tab.ReplaySeekValue);
        Assert.Equal(false, second.Tab.IsReplaySeekPreviewActive);
    });

    private static Task ReplaySeekOverlayCancelledDragAsync() => TestSta.RunAsync(async () =>
    {
        await using var session = await ReplayOverlayTestSession.CreateAsync();
        using var fixture = new ReplayOverlayTestHost(session.Tab);
        fixture.Overlay.ProcessPointerSample(new Point(100, 100), true, Environment.TickCount64);
        var seekCount = session.SeekCount;
        var originalPosition = session.Tab.ReplaySeekValue;

        fixture.BeginDrag();
        fixture.Slider.Value = 300;
        fixture.FlushBindings();
        Assert.True(session.Tab.IsReplaySeekPreviewActive);
        fixture.EndDrag(cancelled: true);
        fixture.FlushBindings();
        Assert.Equal(false, session.Tab.IsReplaySeekPreviewActive);
        Assert.Equal(originalPosition, session.Tab.ReplaySeekSliderValue);
        Assert.Equal(seekCount, session.SeekCount);
    });

    private static Task ReplaySeekOverlayLifecycleAsync() => TestSta.RunAsync(async () =>
    {
        await using var session = await ReplayOverlayTestSession.CreateAsync();
        using var fixture = new ReplayOverlayTestHost(session.Tab);
        var start = Environment.TickCount64;
        fixture.Overlay.ProcessPointerSample(new Point(100, 100), true, start);
        fixture.FlushBindings();
        var firstHandle = fixture.NativeOverlayHandle;
        fixture.BeginDrag();
        fixture.Slider.Value = 120;
        fixture.FlushBindings();
        var seekCount = session.SeekCount;

        fixture.Overlay.IsOverlayEnabled = false;
        await fixture.AssertClosedAsync();
        Assert.Equal(false, NativeWindowTest.IsWindowVisible(firstHandle));
        Assert.Equal(false, session.Tab.IsReplaySeekPreviewActive);
        fixture.Overlay.ProcessPointerSample(new Point(101, 100), true, start + 1);
        Assert.Equal(false, fixture.Overlay.IsOverlayOpen);
        fixture.EndDrag(cancelled: false);
        Assert.Equal(seekCount, session.SeekCount);

        fixture.Overlay.IsOverlayEnabled = true;
        fixture.StopPointerSampling();
        fixture.Overlay.ProcessPointerSample(new Point(102, 100), true, start + 2);
        fixture.FlushBindings();
        Assert.True(fixture.Overlay.IsOverlayOpen);
        var unloadedHandle = fixture.NativeOverlayHandle;
        fixture.BeginDrag();
        fixture.Slider.Value = 240;
        fixture.FlushBindings();
        Assert.True(session.Tab.IsReplaySeekPreviewActive);
        fixture.Root.Children.Remove(fixture.Overlay);
        fixture.FlushBindings();
        await fixture.AssertClosedAsync();
        Assert.Equal(false, session.Tab.IsReplaySeekPreviewActive);
        Assert.Equal(false, NativeWindowTest.IsWindow(unloadedHandle));
        Assert.Equal(false, ReplaySeekOverlay.IsReplayOverlayWindow(unloadedHandle));
        fixture.Overlay.ProcessPointerSample(new Point(103, 100), true, start + 3);
        Assert.Equal(false, fixture.Overlay.IsOverlayOpen);
        fixture.EndDrag(cancelled: false);
        Assert.Equal(seekCount, session.SeekCount);

        // Reloading the same control must create a child of the current surface, with
        // no dependency on a stale HWND or a pointer-timer tick from the previous load.
        fixture.Root.Children.Add(fixture.Overlay);
        fixture.FlushBindings();
        fixture.StopPointerSampling();
        fixture.Overlay.ProcessPointerSample(new Point(104, 100), true, start + 4);
        fixture.FlushBindings();
        Assert.True(fixture.Overlay.IsOverlayOpen);
        Assert.Equal(fixture.Target.Handle, NativeWindowTest.GetParent(fixture.NativeOverlayHandle));
    });

    private static Task ReplaySeekOverlayKeyboardAndCaptureAsync() => TestSta.RunAsync(async () =>
    {
        await using var session = await ReplayOverlayTestSession.CreateAsync();
        using var fixture = new ReplayOverlayTestHost(session.Tab);
        var start = Environment.TickCount64;
        fixture.Overlay.ProcessPointerSample(new Point(100, 100), true, start);
        fixture.FlushBindings();
        var source = PresentationSource.FromVisual(fixture.Slider);
        Assert.NotNull(source);
        var seekCount = session.SeekCount;
        fixture.Slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source!, 0, Key.Right)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
        fixture.Slider.Value = 900;
        fixture.FlushBindings();
        Assert.True(session.Tab.IsReplaySeekPreviewActive);
        fixture.Overlay.ProcessPointerSample(null, false, start + 10_000);
        Assert.True(fixture.Overlay.IsOverlayOpen);
        Assert.Equal(seekCount, session.SeekCount);
        fixture.Slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source!, 0, Key.Right)
        {
            RoutedEvent = Keyboard.KeyUpEvent
        });
        await TestWait.UntilAsync(() => session.SeekCount == seekCount + 1, TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromMinutes(15), session.PlaybackFactory.Engine!.Position);

        foreach (var (key, expectedPosition) in new[] { (Key.Up, 905d), (Key.Down, 900d) })
        {
            await TestWait.UntilAsync(() => session.Tab.CanSeekReplay, TimeSpan.FromSeconds(2));
            var beforeSeekCount = session.SeekCount;
            fixture.Slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source!, 0, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            });
            // Exercise the same built-in slider commands used by Up and Down, then the
            // overlay's key-release commit. Value changes alone must remain a preview.
            (key == Key.Up ? Slider.IncreaseSmall : Slider.DecreaseSmall).Execute(null, fixture.Slider);
            fixture.FlushBindings();
            Assert.True(session.Tab.IsReplaySeekPreviewActive);
            Assert.Equal(expectedPosition, fixture.Slider.Value);
            Assert.Equal(beforeSeekCount, session.SeekCount);
            fixture.Slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source!, 0, key)
            {
                RoutedEvent = Keyboard.KeyUpEvent
            });
            await TestWait.UntilAsync(
                () => session.SeekCount == beforeSeekCount + 1 && session.Tab.CanSeekReplay,
                TimeSpan.FromSeconds(2));
            Assert.Equal(TimeSpan.FromSeconds(expectedPosition), session.PlaybackFactory.Engine!.Position);
        }

        Assert.True(fixture.Chrome.CaptureMouse());
        try
        {
            fixture.Overlay.ProcessPointerSample(null, false, start + 20_000);
            Assert.True(fixture.Overlay.IsOverlayOpen, "Captured controls must retain their mouse-up target even when the pointer is idle.");
        }
        finally
        {
            fixture.Chrome.ReleaseMouseCapture();
        }

        fixture.Overlay.ProcessPointerSample(null, false, start + 20_001);
        await fixture.AssertClosedAsync();
    });

    private static Task ReplaySeekOverlayNativeEmbeddingAsync() => TestSta.RunAsync(async () =>
    {
        await using var session = await ReplayOverlayTestSession.CreateAsync();
        using var fixture = new ReplayOverlayTestHost(session.Tab);
        fixture.Overlay.ProcessPointerSample(new Point(100, 100), true, Environment.TickCount64);
        fixture.FlushBindings();
        var overlaySource = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(fixture.Chrome)!;
        Assert.NotNull(overlaySource);
        var overlayHandle = overlaySource.Handle;
        const int wsChild = 0x40000000;
        const int wsPopup = unchecked((int)0x80000000);
        var style = NativeWindowTest.GetWindowStyle(overlayHandle);
        Assert.True((style & wsChild) != 0, "The seekbar must be a native child window.");
        Assert.True((style & wsPopup) == 0, "A floating popup cannot move atomically with the video.");
        Assert.Equal(false, NativeWindowTest.IsTopmost(overlayHandle));
        Assert.Equal(fixture.Target.Handle, NativeWindowTest.GetParent(overlayHandle));
        Assert.Equal(fixture.OwnerHandle, NativeWindowHitTester.Instance.GetRootWindow(overlayHandle));
        Assert.True(overlaySource.UsesPerPixelOpacity,
            "The child HWND must preserve the seekbar's translucent background and rounded corners.");

        var ownerBefore = NativeWindowTest.GetWindowBounds(fixture.OwnerHandle);
        var surfaceBefore = NativeWindowTest.GetWindowBounds(fixture.Target.Handle);
        var overlayBefore = NativeWindowTest.GetWindowBounds(overlayHandle);
        Assert.True(surfaceBefore.Contains(overlayBefore));
        Assert.True(overlayBefore.Top > surfaceBefore.Top + surfaceBefore.Height / 2);

        // Suppress all overlay DispatcherTimers. Inspect native rectangles immediately
        // after the move, before a dispatcher pump, layout pass, await, or pointer sample.
        // A timer-followed Popup fails even if it would catch up 100 ms later.
        fixture.StopPointerSampling();
        const int dx = 37;
        const int dy = 29;
        NativeWindowTest.SetWindowBounds(fixture.OwnerHandle,
            ownerBefore.Left + dx, ownerBefore.Top + dy, ownerBefore.Width, ownerBefore.Height);
        var surfaceAfterMove = NativeWindowTest.GetWindowBounds(fixture.Target.Handle);
        var overlayAfterMove = NativeWindowTest.GetWindowBounds(overlayHandle);
        Assert.Equal(surfaceBefore.Left + dx, surfaceAfterMove.Left);
        Assert.Equal(surfaceBefore.Top + dy, surfaceAfterMove.Top);
        Assert.Equal(overlayBefore.Left + dx, overlayAfterMove.Left);
        Assert.Equal(overlayBefore.Top + dy, overlayAfterMove.Top);
        Assert.Equal(overlayBefore.Size, overlayAfterMove.Size);

        var renderer = IntPtr.Zero;
        try
        {
            // VLC creates its renderer after playback starts. That renderer must fill
            // the surface while the existing controls retain their smaller bounds.
            renderer = NativeWindowTest.CreateVisibleChildWindow(
                fixture.Target.Handle, "StreamlinkVlcStudioVideoSurface");
            fixture.FlushBindings();
            Assert.Equal(NativeWindowTest.GetWindowBounds(fixture.Target.Handle),
                NativeWindowTest.GetWindowBounds(renderer));
            Assert.Equal(overlayAfterMove, NativeWindowTest.GetWindowBounds(overlayHandle));
            Assert.Equal(overlayHandle, NativeWindowTest.GetTopChildWindow(fixture.Target.Handle));

            fixture.ResizeWindowBy(-120, -60);
            fixture.FlushBindings();
            Assert.True(fixture.Overlay.IsOverlayOpen,
                "Resizing must retain the open overlay while pointer polling is stopped.");
            var surfaceAfterResize = NativeWindowTest.GetWindowBounds(fixture.Target.Handle);
            var overlayAfterResize = NativeWindowTest.GetWindowBounds(overlayHandle);
            Assert.True(overlayAfterResize.Width < overlayAfterMove.Width,
                "The controls must be laid out immediately when the video width changes.");
            Assert.True(surfaceAfterResize.Contains(overlayAfterResize),
                $"The resized seekbar {overlayAfterResize} must stay inside video {surfaceAfterResize}.");
            Assert.True(overlayAfterResize.Top > surfaceAfterResize.Top + surfaceAfterResize.Height / 2);
            Assert.True(overlayAfterResize.Height < surfaceAfterResize.Height / 2,
                "Renderer child resizing must not stretch the seekbar over the video.");
            Assert.Equal(surfaceAfterResize, NativeWindowTest.GetWindowBounds(renderer));
            Assert.Equal(overlayHandle, NativeWindowTest.GetTopChildWindow(fixture.Target.Handle));

            // Exercise the periodic native bounds repair with no size change as well.
            // A fresh renderer forces the direct-child discovery path to run again.
            NativeWindowTest.DestroyWindow(renderer);
            renderer = NativeWindowTest.CreateVisibleChildWindow(
                fixture.Target.Handle, "StreamlinkVlcStudioVideoSurface");
            fixture.Target.SyncNativeBounds();
            fixture.FlushBindings();
            Assert.Equal(surfaceAfterResize, NativeWindowTest.GetWindowBounds(renderer));
            Assert.Equal(overlayAfterResize, NativeWindowTest.GetWindowBounds(overlayHandle));
            Assert.Equal(overlayHandle, NativeWindowTest.GetTopChildWindow(fixture.Target.Handle));
        }
        finally
        {
            if (renderer != IntPtr.Zero) NativeWindowTest.DestroyWindow(renderer);
        }
    });

    private static Task ReplaySeekOverlayNativeRoutingAsync() => TestSta.RunAsync(async () =>
    {
        await using var session = await ReplayOverlayTestSession.CreateAsync();
        var hitTester = new FakeWindowHitTester();
        var detached = new DetachedVideoWindow([session.Tab], session.Tab, true, hitTester)
        {
            Width = 820,
            Height = 520,
            ShowInTaskbar = false
        };
        var main = new MainWindow(false, hitTester);
        RemoveMainWindowAutomaticStartup(main);
        try
        {
            detached.Show();
            detached.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            detached.UpdateLayout();
            var overlay = FindVisualDescendants<ReplaySeekOverlay>(detached).Single();
            var surface = FindVisualDescendants<VideoSurface>(detached).Single();
            StopReplayOverlayPointerSampling(overlay);
            overlay.ProcessPointerSample(new Point(100, 100), true, Environment.TickCount64);
            detached.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var chrome = (Border)overlay.FindName("OverlayChrome");
            var overlaySource = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(chrome)!;
            Assert.NotNull(overlaySource);
            Assert.True(ReplaySeekOverlay.IsReplayOverlayWindow(overlaySource.Handle));
            var timestampRun = FindVisualDescendants<TextBlock>(chrome)
                .SelectMany(textBlock => textBlock.Inlines.OfType<Run>())
                .First(run => run.Text.Contains(':'));
            Assert.True(ReplaySeekOverlay.IsReplayOverlayInput(timestampRun),
                "Timestamp Run input must remain in the overlay even though Run is not a Visual.");
            Assert.True(ReplaySeekOverlay.IsReplayOverlayInput(overlay.FindName("ReplaySeekSlider")));
            Assert.Equal(false, ReplaySeekOverlay.IsReplayOverlayInput(surface));
            Assert.Equal(false, ReplaySeekOverlay.IsReplayOverlayInput(new Run("outside overlay")));
            Assert.Equal(false, ReplaySeekOverlay.IsReplayOverlayWindow(surface.Handle));
            Assert.True(overlaySource.Handle != surface.Handle,
                "Replay controls require a child HWND above the native VLC renderer.");
            Assert.Equal(surface.Handle, NativeWindowTest.GetParent(overlaySource.Handle));

            var videoBounds = new Rect(surface.PointToScreen(new Point()),
                surface.PointToScreen(new Point(surface.ActualWidth, surface.ActualHeight)));
            var chromeBounds = new Rect(chrome.PointToScreen(new Point()),
                chrome.PointToScreen(new Point(chrome.ActualWidth, chrome.ActualHeight)));
            Assert.True(videoBounds.Contains(chromeBounds),
                $"Expected seekbar {chromeBounds} inside video {videoBounds}.");
            Assert.True(chromeBounds.Top > videoBounds.Top + videoBounds.Height / 2);
            var x = (int)(chromeBounds.Left + chromeBounds.Width / 2);
            var y = (int)(chromeBounds.Top + chromeBounds.Height / 2);
            var windowHandle = new System.Windows.Interop.WindowInteropHelper(detached).Handle;
            hitTester.PointWindow = overlaySource.Handle;
            hitTester.RootOwnerWindow = windowHandle;

            Assert.Equal(false, detached.ContainsScreenPoint(x, y));
            Assert.Equal(false, detached.TryBeginVideoMoveFromScreenClick(x, y));
            Assert.Equal(false, detached.TryBeginResizeFromScreenClick(x, y));
            Assert.Equal(false, detached.TryToggleStreamFullscreenFromScreenClick(x, y));
            Assert.Equal(false, detached.TryToggleStreamFullscreenFromScreenClick(x, y));
            Assert.Equal(false, detached.HasVideoMoveCandidate);

            session.Tab.SetVideoPlacement(true, 0, 0, 1, 1);
            var surfaces = (Dictionary<StreamTabViewModel, VideoSurface>)typeof(MainWindow)
                .GetField("videoSurfaces", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            surfaces.Add(session.Tab, surface);
            var nativePointType = typeof(MainWindow).GetNestedType("NativePoint", BindingFlags.NonPublic)!;
            var nativePoint = Activator.CreateInstance(nativePointType, [x, y]);
            var getVideoTab = typeof(MainWindow).GetMethod("GetVideoTabAtScreenPoint",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.True(getVideoTab.Invoke(main, [nativePoint]) is null,
                "The main window's polling reorder route must leave overlay input to the seekbar.");

            var volumeOsd = (VolumeOverlay)detached.FindName("VolumeOsd");
            volumeOsd.Show(surface, session.Tab.Volume, session.Tab.IsMuted);
            detached.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var volumePopup = (Popup)volumeOsd.FindName("Popup");
            var volumeChrome = (FrameworkElement)volumePopup.Child;
            var volumeSource = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(volumeChrome)!;
            Assert.Equal(false, ReplaySeekOverlay.IsReplayOverlayWindow(volumeSource.Handle));
            hitTester.PointWindow = volumeSource.Handle;
            Assert.True(detached.ContainsScreenPoint(x, y), "Ordinary owned popups must retain native window routing.");
            Assert.True(ReferenceEquals(session.Tab, getVideoTab.Invoke(main, [nativePoint])));
            var previousVolume = session.Tab.Volume;
            Assert.True(detached.TryRouteMouseWheel(x, y, -Mouse.MouseWheelDeltaForOneLine));
            Assert.Equal(previousVolume - VolumeOverlay.WheelStep, session.Tab.Volume);

            var volumeBounds = new Rect(volumeChrome.PointToScreen(new Point()),
                volumeChrome.PointToScreen(new Point(volumeChrome.ActualWidth, volumeChrome.ActualHeight)));
            Assert.True(volumeBounds.Bottom <= chromeBounds.Top,
                $"Volume OSD {volumeBounds} must sit above the seek controls {chromeBounds}.");
        }
        finally
        {
            main.Close();
            detached.CloseForTabDisposal();
        }
    });

    private static Task ReplaySeekOverlayNativePointerIntegrationAsync() => TestSta.RunAsync(async () =>
    {
        if (!NativeWindowTest.TryGetCursorPosition(out var originalCursor))
            throw new InteractiveDesktopTestSkippedException("Cannot preserve the desktop cursor for native pointer polling.");

        await using var tab = CreateTestStreamTab();
        var surface = new VideoSurface();
        var overlay = new ReplaySeekOverlay { PlacementTarget = surface, DataContext = tab };
        var root = new Grid();
        root.Children.Add(surface);
        root.Children.Add(overlay);
        var workArea = SystemParameters.WorkArea;
        var window = new Window
        {
            Title = "Replay seek overlay native polling regression test",
            Content = root,
            WindowStyle = WindowStyle.None,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = workArea.Left + 60,
            Top = workArea.Top + 80,
            Width = Math.Min(720, workArea.Width - 140),
            Height = Math.Min(420, workArea.Height - 160),
            ShowInTaskbar = false,
            Topmost = true
        };
        var renderer = IntPtr.Zero;
        try
        {
            window.Show();
            window.UpdateLayout();
            var ownerHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            await NativeWindowTest.RequireForegroundAsync(
                ownerHandle,
                TimeSpan.FromSeconds(1),
                "Native replay pointer polling requires the test owner to be active");
            await TestWait.UntilAsync(() => window.IsActive && surface.Handle != IntPtr.Zero, TimeSpan.FromSeconds(1));

            // Emulate VLC's nested renderer HWND. No WPF mouse event is sent to the
            // overlay: its production DispatcherTimer must discover these native hits.
            // WindowFromPoint intentionally skips Win32 static text controls. Use the
            // registered opaque video class, as the native double-click fixtures do.
            renderer = NativeWindowTest.CreateVisibleChildWindow(surface.Handle, "StreamlinkVlcStudioVideoSurface");
            var surfaceBounds = NativeWindowTest.GetWindowBounds(surface.Handle);
            NativeWindowTest.SetWindowBounds(renderer, 0, 0, surfaceBounds.Width, surfaceBounds.Height);
            NativeWindowTest.SetCursorPosition(surfaceBounds.Left - 12, surfaceBounds.Top - 12);
            await Task.Delay(150);
            var x = surfaceBounds.Left + surfaceBounds.Width / 2;
            var y = surfaceBounds.Top + surfaceBounds.Height / 4;
            var rendererBounds = NativeWindowTest.GetWindowBounds(renderer);
            Assert.True(NativeWindowTest.IsWindowVisible(surface.Handle) && NativeWindowTest.IsWindowVisible(renderer),
                $"Native surface and renderer must be visible; surface={surfaceBounds}, renderer={rendererBounds}.");
            Assert.True(NativeWindowTest.IsWindowEnabled(surface.Handle) && NativeWindowTest.IsWindowEnabled(renderer),
                "Native surface and renderer must be enabled for WindowFromPoint to hit them.");
            Assert.True(rendererBounds.Contains(x, y),
                $"Native renderer {rendererBounds} must cover test point ({x},{y}).");
            var actualHit = NativeWindowHitTester.Instance.WindowFromPoint(x, y);
            if (actualHit != renderer && actualHit != IntPtr.Zero &&
                NativeWindowHitTester.Instance.GetRootWindow(actualHit) != ownerHandle &&
                NativeWindowHitTester.Instance.GetRootOwnerWindow(actualHit) != ownerHandle)
                throw new InteractiveDesktopTestSkippedException(
                    "Native renderer test point is covered by another desktop window: " +
                    NativeWindowTest.DescribeWindowAtPoint(x, y));
            Assert.True(actualHit == renderer,
                $"Expected native renderer HWND {renderer} at ({x},{y}); surface={surfaceBounds}, renderer={rendererBounds}. " +
                NativeWindowTest.DescribeWindowAtPoint(x, y));

            var parked = Stopwatch.StartNew();
            NativeWindowTest.SetCursorPosition(x, y);
            await TestWait.UntilAsync(() => overlay.IsOverlayOpen, TimeSpan.FromSeconds(1));
            var chrome = (Border)overlay.FindName("OverlayChrome");
            Assert.NotNull(PresentationSource.FromVisual(chrome));
            await Task.Delay(1_700);
            Assert.True(overlay.IsOverlayOpen, "The native polling overlay hid before its inactivity delay elapsed.");
            try
            {
                await TestWait.UntilAsync(() => !overlay.IsOverlayOpen, TimeSpan.FromSeconds(2));
            }
            catch (InvalidOperationException error)
            {
                NativeWindowTest.TryGetCursorPosition(out var currentCursor);
                var state = string.Join(", ", typeof(ReplaySeekOverlay)
                    .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Where(field => field.Name is "lastPointer" or "lastActivity" or "fading" or "keyboardSeeking" or "seekTab")
                    .Select(field => $"{field.Name}={field.GetValue(overlay)}"));
                throw new InvalidOperationException(
                    $"Native idle close failed at tick {Environment.TickCount64}; parked=({x},{y}), cursor={currentCursor}, " +
                    $"capture={chrome.IsMouseCaptureWithin}, opacity={chrome.Opacity}, ownerActive={window.IsActive}; {state}.", error);
            }
            Assert.True(parked.Elapsed >= TimeSpan.FromMilliseconds(2_180),
                $"Idle dismissal must include the two-second delay and fade; observed {parked.Elapsed.TotalMilliseconds:0} ms.");

            NativeWindowTest.SetCursorPosition(x + 4, y + 2);
            await TestWait.UntilAsync(() => overlay.IsOverlayOpen, TimeSpan.FromSeconds(1));
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var overlayHandle = ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(chrome)!).Handle;
            Assert.Equal(surface.Handle, NativeWindowTest.GetParent(overlayHandle));
            Assert.Equal(overlayHandle, NativeWindowTest.GetTopChildWindow(surface.Handle));
            Assert.True(overlay.IsOverlayOpen);
        }
        finally
        {
            if (renderer != IntPtr.Zero) NativeWindowTest.DestroyWindow(renderer);
            window.Close();
            NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
        }
    });

    private static void StopReplayOverlayPointerSampling(ReplaySeekOverlay overlay)
    {
        // Use the production state machine with deterministic pointer samples. Suppress
        // unrelated movement of the real desktop mouse while these tests are running.
        foreach (var field in typeof(ReplaySeekOverlay).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (field.GetValue(overlay) is System.Windows.Threading.DispatcherTimer timer)
            {
                timer.Stop();
            }
        }
    }

    private sealed class ReplayOverlayTestHost : IDisposable
    {
        private readonly Window window;

        public ReplayOverlayTestHost(StreamTabViewModel tab)
        {
            Target = new VideoSurface();
            Overlay = new ReplaySeekOverlay
            {
                DataContext = tab,
                PlacementTarget = Target,
                IsOverlayEnabled = true,
                PreviewImageLoader = static (_, _, _) => Task.FromResult<BitmapSource?>(null)
            };
            Root = new Grid();
            Root.Children.Add(Target);
            Root.Children.Add(Overlay);
            window = new Window
            {
                Title = "Replay seek overlay regression test",
                Width = 820,
                Height = 520,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                Content = Root
            };
            window.Show();
            window.Activate();
            FlushBindings();
            StopPointerSampling();
            Slider = (Slider)Overlay.FindName("ReplaySeekSlider");
            Chrome = (Border)Overlay.FindName("OverlayChrome");
            Assert.True(Overlay.IsLoaded);
            Assert.True(Target.IsVisible);
        }

        public Grid Root { get; }
        public VideoSurface Target { get; }
        public ReplaySeekOverlay Overlay { get; }
        public Slider Slider { get; }
        public Border Chrome { get; }
        public IntPtr OwnerHandle => new System.Windows.Interop.WindowInteropHelper(window).Handle;
        public IntPtr NativeOverlayHandle =>
            (PresentationSource.FromVisual(Chrome) as System.Windows.Interop.HwndSource)?.Handle ?? IntPtr.Zero;

        public void StopPointerSampling() => StopReplayOverlayPointerSampling(Overlay);

        public void ResizeWindowBy(double widthChange, double heightChange)
        {
            window.Width += widthChange;
            window.Height += heightChange;
        }

        public void BeginDrag() => Slider.RaiseEvent(new DragStartedEventArgs(0, 0)
        {
            RoutedEvent = Thumb.DragStartedEvent
        });

        public void EndDrag(bool cancelled) => Slider.RaiseEvent(new DragCompletedEventArgs(0, 0, cancelled)
        {
            RoutedEvent = Thumb.DragCompletedEvent
        });

        public void FlushBindings()
        {
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }

        public Task AssertClosedAsync() => TestWait.UntilAsync(
            () => !Overlay.IsOverlayOpen,
            TimeSpan.FromSeconds(1));

        public void Dispose() => window.Close();
    }

    private sealed class ReplayOverlayTestSession(StreamTabViewModel tab, FakePlaybackEngineFactory playbackFactory) : IAsyncDisposable
    {
        public StreamTabViewModel Tab { get; } = tab;
        public FakePlaybackEngineFactory PlaybackFactory { get; } = playbackFactory;
        public int SeekCount => PlaybackFactory.Engines.Sum(engine => engine.SeekCount);

        public static async Task<ReplayOverlayTestSession> CreateAsync(ReplaySessionInfo? replayOverride = null)
        {
            // Keep the native clock consistent with replay metadata when the first
            // seek switches from live playback to the replay media.
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                Duration = TimeSpan.FromHours(1)
            });
            var replay = replayOverride ?? new ReplaySessionInfo(
                PlatformKind.Twitch,
                "streamer",
                "https://www.twitch.tv/videos/123",
                "123",
                null,
                TimeSpan.FromHours(1),
                true,
                "");
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("streamer", PlatformKind.Twitch),
                "best",
                new FakeStreamlinkService(),
                playbackFactory,
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action(),
                replayResolver: new FakeReplayResolver(replay),
                vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = false;
            tab.SetVideoHandle(new IntPtr(42));
            try
            {
                await tab.StartAsync(settings);
                await TestWait.UntilAsync(() => tab.CanSeekReplay, TimeSpan.FromSeconds(2));
                await StopReplayClockPollingAsync(tab);
                return new ReplayOverlayTestSession(tab, playbackFactory);
            }
            catch
            {
                await tab.DisposeAsync();
                throw;
            }
        }

        public ValueTask DisposeAsync() => Tab.DisposeAsync();
    }
}

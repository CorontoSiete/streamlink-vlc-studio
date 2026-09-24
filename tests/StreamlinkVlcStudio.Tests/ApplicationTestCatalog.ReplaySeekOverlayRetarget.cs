internal static partial class ApplicationTestCatalog
{
    private static Task ReplaySeekOverlayRetargetAsync() => TestSta.RunAsync(async () =>
    {
        await using var session = await ReplayOverlayTestSession.CreateAsync();
        using var fixture = new ReplayOverlayTestHost(session.Tab);
        var secondTarget = new VideoSurface();
        fixture.Root.ColumnDefinitions.Add(new ColumnDefinition());
        fixture.Root.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(secondTarget, 1);
        fixture.Root.Children.Insert(1, secondTarget);
        fixture.FlushBindings();
        Assert.True(secondTarget.IsVisible && secondTarget.Handle != IntPtr.Zero);
        Assert.True(fixture.Target.Handle != secondTarget.Handle);

        var start = Environment.TickCount64;
        fixture.Overlay.ProcessPointerSample(new Point(100, 100), true, start);
        fixture.FlushBindings();
        var originalSource = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(fixture.Chrome)!;
        Assert.NotNull(originalSource);
        var originalOverlayHandle = originalSource.Handle;
        var originalTargetHandle = fixture.Target.Handle;
        var seekCount = session.SeekCount;
        var originalPosition = session.Tab.ReplaySeekValue;
        Assert.Equal(originalTargetHandle, NativeWindowTest.GetParent(originalOverlayHandle));

        fixture.BeginDrag();
        Assert.True(fixture.Slider.CaptureMouse());
        fixture.Slider.Value = 180;
        fixture.FlushBindings();
        Assert.True(session.Tab.IsReplaySeekPreviewActive);
        Assert.Equal(180d, session.Tab.ReplaySeekSliderValue);

        // Switching the native parent must abandon the gesture and destroy its old
        // presentation source before the same logical controls can be opened on B.
        fixture.Overlay.PlacementTarget = secondTarget;
        fixture.StopPointerSampling();
        fixture.FlushBindings();
        Assert.Equal(false, fixture.Overlay.IsOverlayOpen);
        Assert.True(originalSource.IsDisposed);
        Assert.Equal(false, NativeWindowTest.IsWindow(originalOverlayHandle));
        Assert.Equal(false, fixture.Slider.IsMouseCaptureWithin);
        Assert.Equal(false, session.Tab.IsReplaySeekPreviewActive);
        Assert.Equal(originalPosition, session.Tab.ReplaySeekSliderValue);
        Assert.Equal(seekCount, session.SeekCount);

        // A delayed mouse-up from A cannot commit its preview after the retarget.
        fixture.EndDrag(cancelled: false);
        fixture.FlushBindings();
        Assert.Equal(seekCount, session.SeekCount);
        fixture.Overlay.ProcessPointerSample(new Point(101, 100), true, start + 1);
        fixture.FlushBindings();
        Assert.True(fixture.Overlay.IsOverlayOpen);
        var replacementSource = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(fixture.Chrome)!;
        Assert.NotNull(replacementSource);
        Assert.Equal(false, ReferenceEquals(originalSource, replacementSource));
        Assert.Equal(secondTarget.Handle, NativeWindowTest.GetParent(replacementSource.Handle));
        Assert.True(NativeWindowTest.GetWindowBounds(secondTarget.Handle)
            .Contains(NativeWindowTest.GetWindowBounds(replacementSource.Handle)));
        Assert.True(ReferenceEquals(session.Tab, fixture.Chrome.DataContext));
        Assert.Equal(session.Tab.ReplaySeekMaximum, fixture.Slider.Maximum);
        Assert.Equal(session.Tab.ReplaySeekSliderValue, fixture.Slider.Value);

        // Exercise the inherited two-way binding and command path in the new HWND.
        // Replacing the native source must not leave the slider bound to a dead tree.
        fixture.BeginDrag();
        fixture.Slider.Value = 360;
        fixture.FlushBindings();
        Assert.True(session.Tab.IsReplaySeekPreviewActive);
        Assert.Equal(360d, session.Tab.ReplaySeekSliderValue);
        Assert.Equal(originalPosition, session.Tab.ReplaySeekValue);
        fixture.EndDrag(cancelled: false);
        await TestWait.UntilAsync(
            () => session.SeekCount == seekCount + 1 && !session.Tab.IsReplaySeekPreviewActive,
            TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromMinutes(6), session.PlaybackFactory.Engine!.Position);

        // A must no longer own the new host's destruction callback. Dispose it while
        // the overlay stays open on B, without another pointer sample to reopen it.
        fixture.Root.Children.Remove(fixture.Target);
        fixture.Target.Dispose();
        fixture.FlushBindings();
        Assert.Equal(false, NativeWindowTest.IsWindow(originalTargetHandle));
        Assert.True(fixture.Overlay.IsOverlayOpen);
        Assert.Equal(false, replacementSource.IsDisposed);
        Assert.True(ReferenceEquals(replacementSource, PresentationSource.FromVisual(fixture.Chrome)));
        Assert.True(NativeWindowTest.IsWindowVisible(replacementSource.Handle));
        Assert.Equal(secondTarget.Handle, NativeWindowTest.GetParent(replacementSource.Handle));
        Assert.Equal(replacementSource.Handle, NativeWindowTest.GetTopChildWindow(secondTarget.Handle));
        Assert.Equal(seekCount + 1, session.SeekCount);
    });
}

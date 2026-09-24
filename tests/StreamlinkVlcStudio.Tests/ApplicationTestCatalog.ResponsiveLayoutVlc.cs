internal static partial class ApplicationTestCatalog
{
    private static IReadOnlyList<(string Name, Func<Task> Run)> ResponsiveVlcResizeTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA"))
            ? []
            :
            [
                ("responsive main window keeps real Direct3D11 VLC video and replay controls inside resized client",
                    () => ResponsiveVlcResizeAsync(VideoRendererMode.Direct3D11)),
                ("responsive main window keeps real GDI VLC video and replay controls inside resized client",
                    () => ResponsiveVlcResizeAsync(VideoRendererMode.Gdi))
            ];

    private static Task ResponsiveVlcResizeAsync(VideoRendererMode rendererMode) =>
        WithResponsiveWindowAsync(withVideo: false, async (window, viewModel) =>
        {
            var vlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
            var mediaPath = Path.GetFullPath(Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA")!);
            Assert.True(File.Exists(Path.Combine(vlcDirectory, "libvlc.dll")));
            Assert.True(File.Exists(mediaPath));
            await using var session = await ReplayOverlayTestSession.CreateAsync();
            viewModel.Tabs.Add(session.Tab);
            viewModel.SelectedTab = session.Tab;
            // The replay session uses a fake overlay engine and can fall back to
            // docked chat when attached to a main view model. This matrix exercises
            // the full player; the separate chat matrix covers that layout.
            session.Tab.IsChatVisible = false;
            session.Tab.IsDockedChatPanelVisible = false;
            if (!viewModel.IsReplaySeekBarUiVisible)
            {
                viewModel.ToggleReplaySeekBarCommand.Execute(null);
            }
            window.Topmost = true;
            PumpResponsiveLayout(window);
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            await NativeWindowTest.RequireForegroundAsync(handle, TimeSpan.FromSeconds(1),
                "Real VLC resize rendering requires an interactive desktop");
            var surface = FindVisualDescendants<VideoSurface>(window).Single();
            var overlay = FindVisualDescendants<ReplaySeekOverlay>(window).Single();
            var factory = new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings());
            using var engine = await factory.CreateAsync(vlcDirectory,
                enableNativeOverlay: false, rendererMode: rendererMode);
            try
            {
                engine.SetVideoHandle(surface.Handle);
                await engine.PlayAsync(new Uri(mediaPath), 0, PlaybackAudioState.HardMuted);
                await TestWait.UntilAsync(() => engine.TryGetVideoSize(out var width, out var height) &&
                    width > 0 && height > 0 && engine.TryGetPlaybackClock(out var clock) &&
                    clock.Position > TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(8));
                Assert.Equal(rendererMode, ((LibVlcPlaybackEngine)engine).RendererMode);
                foreach (var size in ResponsiveWindowSizes)
                {
                    overlay.IsOverlayEnabled = false;
                    var client = ResizeResponsiveWindow(window, size.Width, size.Height);
                    await Task.Delay(180); // Wait for the next native VLC frame at the new size.
                    PumpResponsiveLayout(window);
                    AssertResponsiveElementInside((FrameworkElement)window.Content, client, "real VLC workspace");
                    var nativeVideo = NativeWindowTest.GetWindowBounds(surface.Handle);
                    var videoBounds = new Rect(nativeVideo.X, nativeVideo.Y, nativeVideo.Width, nativeVideo.Height);
                    AssertResponsiveBoundsInside(videoBounds, client, "real VLC video HWND");
                    using (var video = CaptureReplayVlcSurface(surface))
                    {
                        AssertReplayVlcVideoPixels(video);
                        SaveReplayVlcArtifact(video, rendererMode, $"resize-{size.Width}x{size.Height}-video");
                    }
                    // Pointer reveal is intentionally gated on an active owner;
                    // establish the same precondition as an interactive resize.
                    if (!window.IsActive)
                    {
                        Console.WriteLine($"VLC resize {size.Width}x{size.Height}: reacquiring the active owner before pointer input.");
                        await NativeWindowTest.RequireForegroundAsync(handle, TimeSpan.FromSeconds(1),
                            "Replay pointer input requires the resized main window to be active");
                    }
                    overlay.IsOverlayEnabled = true;
                    StopReplayOverlayPointerSampling(overlay);
                    var pointer = surface.PointToScreen(new Point(surface.ActualWidth / 2, surface.ActualHeight / 2));
                    overlay.ProcessPointerSample(pointer,
                        true, Environment.TickCount64);
                    await Task.Delay(160);
                    PumpResponsiveLayout(window);
                    StopReplayOverlayPointerSampling(overlay);
                    if (size is (623, 800) or (480, 320) or (320, 240) or (1100, 760))
                    {
                        Assert.True(overlay.IsOverlayOpen, $"Replay controls did not return at {size.Width}x{size.Height}; " +
                            $"ownerActive={window.IsActive}, loaded={overlay.IsLoaded}, enabled={overlay.IsOverlayEnabled}, " +
                            $"targetVisible={surface.IsVisible}, targetSize={surface.ActualWidth}x{surface.ActualHeight}, " +
                            $"targetCurrent={ReferenceEquals(overlay.PlacementTarget, surface)}, " +
                            $"targetMounted={FindVisualDescendants<VideoSurface>(window).Contains(surface)}, " +
                            $"canDisplay={typeof(ReplaySeekOverlay).GetProperty("CanDisplay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overlay)}.");
                    }
                    if (overlay.IsOverlayOpen)
                    {
                        var chrome = (FrameworkElement)overlay.FindName("OverlayChrome");
                        AssertResponsiveElementInside(chrome, videoBounds, "replay seek chrome");
                        var source = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(chrome)!;
                        var nativeControls = NativeWindowTest.GetWindowBounds(source.Handle);
                        AssertResponsiveBoundsInside(new Rect(nativeControls.X, nativeControls.Y,
                            nativeControls.Width, nativeControls.Height), videoBounds, "native replay controls");
                        using var composed = CaptureReplayVlcSurface(surface);
                        SaveReplayVlcArtifact(composed, rendererMode, $"resize-{size.Width}x{size.Height}-controls");
                    }
                    else
                    {
                        Assert.True(size.Height <= 180 || size.Width < 200,
                            "Replay controls disappeared while the player still had usable room.");
                    }
                }
            }
            finally
            {
                await engine.StopAsync();
                viewModel.SelectedTab = null;
                viewModel.Tabs.Remove(session.Tab);
                viewModel.VideoTabs.Remove(session.Tab);
            }
        });
}

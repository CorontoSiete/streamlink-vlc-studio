internal static partial class ApplicationTestCatalog
{
    private static IReadOnlyList<(string Name, Func<Task> Run)> TabSwitchVlcTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA"))
            ? []
            :
            [
                ("tab switching keeps real GDI VLC video at its final bounds", () => TabSwitchVlcAsync(VideoRendererMode.Gdi)),
                ("tab switching keeps real Direct3D11 VLC video at its final bounds", () => TabSwitchVlcAsync(VideoRendererMode.Direct3D11))
            ];

    private static Task TabSwitchVlcAsync(VideoRendererMode mode) =>
        WithResponsiveWindowAsync(withVideo: true, async (window, viewModel) =>
        {
            var first = viewModel.SelectedTab!;
            var second = TestViewModels.CreateTab(
                StreamInputParser.Parse("vlcswitchfixture", PlatformKind.Twitch), "best",
                new FakeStreamlinkService(), new FakePlaybackEngineFactory(),
                new FakeChatClientFactory(), new MemoryLogger(), action => action());
            viewModel.Tabs.Add(second);
            viewModel.SelectedTab = second;
            PumpResponsiveLayout(window);
            window.Topmost = true;
            await NativeWindowTest.RequireForegroundAsync(
                new System.Windows.Interop.WindowInteropHelper(window).Handle, TimeSpan.FromSeconds(1),
                "Real VLC tab switching requires an interactive desktop");
            var engines = new Dictionary<StreamTabViewModel, IPlaybackEngine>();
            var surfaces = FindVisualDescendants<VideoSurface>(window).ToDictionary(surface => (StreamTabViewModel)surface.Tag);
            var handles = surfaces.ToDictionary(pair => pair.Key, pair => pair.Value.Handle);
            try
            {
                var factory = new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings());
                foreach (var tab in new[] { second, first })
                {
                    viewModel.SelectedTab = tab;
                    PumpResponsiveLayout(window);
                    var engine = await factory.CreateAsync(
                        Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!,
                        enableNativeOverlay: false, rendererMode: mode);
                    engines.Add(tab, engine);
                    engine.SetVideoHandle(surfaces[tab].Handle);
                    await engine.PlayAsync(new Uri(Path.GetFullPath(
                        Environment.GetEnvironmentVariable("SVS_TEST_VLC_MEDIA")!)), 0, PlaybackAudioState.HardMuted);
                    await TestWait.UntilAsync(() => engine.TryGetVideoSize(out var width, out var height) &&
                        width > 0 && height > 0 && engine.TryGetPlaybackClock(out var clock) &&
                        clock.Position > TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(8));
                    AssertActualVideoRenderer(surfaces[tab], mode == VideoRendererMode.Gdi ? "WinGDI output" : "Direct3D11 output");
                    await Task.Delay(150);
                    using var frame = CaptureReplayVlcSurface(surfaces[tab]);
                    AssertReplayVlcVideoPixels(frame);
                }

                using var firstBounds = new TabSwitchBoundsRecorder(handles[first]);
                using var secondBounds = new TabSwitchBoundsRecorder(handles[second]);
                foreach (var size in new[] { (Width: 1100, Height: 760), (Width: 720, Height: 900), (Width: 901, Height: 620) })
                {
                    ResizeResponsiveWindow(window, size.Width, size.Height);
                    for (var index = 0; index < 6; index++)
                    {
                        var target = index % 2 == 0 ? second : first;
                        var recorder = index % 2 == 0 ? secondBounds : firstBounds;
                        recorder.VisibleBounds.Clear();
                        await engines[viewModel.SelectedTab!].PauseAsync();
                        viewModel.SelectedTab = target;
                        PumpResponsiveLayout(window);
                        await engines[target].ResumeAsync();
                        await Task.Delay(80);
                        using var frame = CaptureReplayVlcSurface(surfaces[target]);
                        AssertReplayVlcVideoPixels(frame);
                        if (index == 5)
                        {
                            SaveReplayVlcArtifact(frame, mode, $"tab-switch-{size.Width}x{size.Height}");
                        }
                        Assert.True(recorder.VisibleBounds.Count > 0, "No native visibility/bounds events were recorded.");
                        var expected = NativeWindowTest.GetWindowBounds(surfaces[target].Handle);
                        Assert.True(recorder.VisibleBounds.All(bounds => bounds == expected),
                            $"Real VLC tab switch exposed intermediate bounds: {string.Join(" -> ", recorder.VisibleBounds.Distinct())}; expected {expected}.");
                        Assert.Equal(handles[first], surfaces[first].Handle);
                        Assert.Equal(handles[second], surfaces[second].Handle);
                    }
                }
            }
            finally
            {
                foreach (var engine in engines.Values)
                {
                    try { await engine.StopAsync(); }
                    finally { engine.Dispose(); }
                }
            }
        });
}

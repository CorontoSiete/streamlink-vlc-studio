internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> TabSwitchVideoBoundsTests { get; } =
    [
        ("tab switching never exposes a video HWND at intermediate bounds", () => TabSwitchVideoBoundsAsync()),
        ("tab switching preserves video bounds for different aspect ratios", () => TabSwitchVideoBoundsAsync(4.0 / 3.0)),
        .. TabSwitchVlcTests
    ];

    private static Task TabSwitchVideoBoundsAsync(double secondAspectRatio = 16.0 / 9.0) =>
            WithResponsiveWindowAsync(withVideo: true, (window, viewModel) =>
            {
                var first = viewModel.SelectedTab!;
                var second = TestViewModels.CreateTab(
                    StreamInputParser.Parse("switchfixture", PlatformKind.Twitch), "best",
                    new FakeStreamlinkService(), new FakePlaybackEngineFactory(),
                    new FakeChatClientFactory(), new MemoryLogger(), action => action());
                viewModel.Tabs.Add(second);
                typeof(StreamTabViewModel).GetProperty(nameof(StreamTabViewModel.VideoAspectRatio))!
                    .SetValue(second, secondAspectRatio);
                // Mount both HWNDs, exactly as visiting two streamer tabs does.
                viewModel.SelectedTab = second;
                PumpResponsiveLayout(window);
                viewModel.SelectedTab = first;
                PumpResponsiveLayout(window);
                var surfaces = FindVisualDescendants<VideoSurface>(window).ToArray();
                var firstSurface = surfaces.Single(surface => ReferenceEquals(surface.Tag, first));
                var secondSurface = surfaces.Single(surface => ReferenceEquals(surface.Tag, second));
                using var firstBounds = new TabSwitchBoundsRecorder(firstSurface.Handle);
                using var secondBounds = new TabSwitchBoundsRecorder(secondSurface.Handle);
                foreach (var size in new[] { (Width: 1100, Height: 760), (Width: 720, Height: 900), (Width: 901, Height: 620) })
                {
                    ResizeResponsiveWindow(window, size.Width, size.Height);
                    for (var index = 0; index < 6; index++)
                    {
                        var target = index % 2 == 0 ? second : first;
                        var recorder = index % 2 == 0 ? secondBounds : firstBounds;
                        recorder.VisibleBounds.Clear();
                        viewModel.SelectedTab = target;
                        PumpResponsiveLayout(window);
                        var surface = index % 2 == 0 ? secondSurface : firstSurface;
                        var expected = NativeWindowTest.GetWindowBounds(surface.Handle);
                        Assert.True(recorder.VisibleBounds.Count > 0, "No native visibility/bounds events were recorded.");
                        var observed = recorder.VisibleBounds.Distinct().ToArray();
                        Assert.True(observed.All(bounds => bounds == expected),
                            $"Tab switch exposed intermediate native bounds: {string.Join(" -> ", observed)}; expected {expected}.");
                    }
                    viewModel.SelectHomeCommand.Execute(null);
                    PumpResponsiveLayout(window);
                    firstBounds.VisibleBounds.Clear();
                    viewModel.SelectedTab = first;
                    PumpResponsiveLayout(window);
                    var restored = NativeWindowTest.GetWindowBounds(firstSurface.Handle);
                    Assert.True(firstBounds.VisibleBounds.Count > 0 && firstBounds.VisibleBounds.All(bounds => bounds == restored),
                        $"Returning from Home exposed intermediate native bounds: {string.Join(" -> ", firstBounds.VisibleBounds.Distinct())}.");
                }
                Assert.Equal(secondAspectRatio, second.VideoAspectRatio);
                return Task.CompletedTask;
            });

    // Observe synchronous Win32 changes, including those before WPF's next render.
    // Checking ActualWidth after UpdateLayout alone misses the visible flash.
    private sealed class TabSwitchBoundsRecorder : IDisposable
    {
        private readonly IntPtr handle;
        private readonly BoundsWindowProcedure procedure;
        internal List<System.Drawing.Rectangle> VisibleBounds { get; } = [];

        internal TabSwitchBoundsRecorder(IntPtr handle)
        {
            this.handle = handle;
            procedure = Observe;
            Assert.True(SetWindowSubclass(handle, procedure, new UIntPtr(1), UIntPtr.Zero));
        }

        private IntPtr Observe(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam,
            UIntPtr subclassId, UIntPtr referenceData)
        {
            var result = DefSubclassProc(hwnd, message, wParam, lParam);
            if (message is 0x0047 or 0x0018 && NativeWindowTest.IsWindowVisible(hwnd))
            {
                VisibleBounds.Add(NativeWindowTest.GetWindowBounds(hwnd));
            }
            return result;
        }

        public void Dispose()
        {
            RemoveWindowSubclass(handle, procedure, new UIntPtr(1));
            GC.KeepAlive(procedure);
        }

        private delegate IntPtr BoundsWindowProcedure(
            IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData);

        [DllImport("comctl32")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowSubclass(IntPtr hwnd, BoundsWindowProcedure procedure, UIntPtr subclassId, UIntPtr referenceData);

        [DllImport("comctl32")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveWindowSubclass(IntPtr hwnd, BoundsWindowProcedure procedure, UIntPtr subclassId);

        [DllImport("comctl32")]
        private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    }
}

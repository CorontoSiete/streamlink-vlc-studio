internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> VolumeOverlaySizingTests { get; } =
    [
        ("volume popup remains inside a tiny video while its window resizes", VolumePopupFitsResizedTargetAsync),
        ("volume popup follows seekbar visibility and compact sizing on a presented video", () => VolumePopupFollowsSeekbarAsync(true)),
        ("volume popup follows seekbar visibility and compact sizing on a finished screen", () => VolumePopupFollowsSeekbarAsync(false)),
        ("volume popup releases seekbar space when the seekbar retargets or unloads", VolumePopupSeekbarRetargetAsync),
        ("volume popup clears the seekbar in the main player and fullscreen", VolumePopupMainPlayerAsync),
        ("volume popup targets the finished VOD screen in main and detached players", VolumePopupFinishedPlayerAsync)
    ];

    private static Task VolumePopupFitsResizedTargetAsync() => TestSta.RunAsync(() =>
    {
        var target = new Border();
        var overlay = new VolumeOverlay();
        var content = new Grid();
        content.Children.Add(target);
        content.Children.Add(overlay);
        var window = new Window
        {
            Width = 640,
            Height = 360,
            Left = 160,
            Top = 160,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Content = content
        };
        var popup = (System.Windows.Controls.Primitives.Popup)overlay.FindName("Popup");
        var scale = (Viewbox)overlay.FindName("OverlayScale");
        var chrome = (Border)overlay.FindName("OverlayChrome");

        void FlushLayout()
        {
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        void AssertContained()
        {
            FlushLayout();
            Assert.True(popup.IsOpen, "Resizing a visible video must keep the volume indicator available.");
            var source = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(scale)!;
            Assert.NotNull(source);
            var popupBounds = NativeWindowTest.GetWindowBounds(source.Handle);
            var topLeft = target.PointToScreen(new Point());
            var bottomRight = target.PointToScreen(new Point(target.ActualWidth, target.ActualHeight));
            Assert.True(popupBounds.Left >= topLeft.X - 1 && popupBounds.Top >= topLeft.Y - 1 &&
                popupBounds.Right <= bottomRight.X + 1 && popupBounds.Bottom <= bottomRight.Y + 1,
                $"Volume popup {popupBounds} escaped target {topLeft} to {bottomRight}.");
        }

        try
        {
            window.Show();
            FlushLayout();
            overlay.Show(target, 75, muted: false);
            AssertContained();
            Assert.True(Math.Abs(scale.ActualWidth - chrome.ActualWidth) <= 1,
                "The normal-size volume indicator must retain its unscaled appearance.");

            // Keep the popup open throughout both resizes; a second Show call would mask
            // stale native desktop placement and target event subscription failures.
            window.Width = 96;
            window.Height = 52;
            AssertContained();
            Assert.True(scale.ActualWidth < chrome.ActualWidth,
                "The popup chrome must shrink when the video is narrower than the pill.");

            window.Width = 56;
            window.Height = 12;
            AssertContained();
            Assert.True(scale.ActualHeight <= target.ActualHeight);

            target.Visibility = Visibility.Collapsed;
            FlushLayout();
            Assert.Equal(false, popup.IsOpen);

            target.Visibility = Visibility.Visible;
            window.Width = 320;
            window.Height = 180;
            FlushLayout();
            overlay.Show(target, 50, muted: true);
            AssertContained();
            window.Left += 25;
            FlushLayout();
            Assert.True(!popup.IsOpen,
                "A desktop popup must not remain at the old position when its owner moves.");
        }
        finally
        {
            window.Close();
        }
    });

    private static Task VolumePopupFollowsSeekbarAsync(bool nativeVideo) => TestSta.RunAsync(async () =>
    {
        await using var tab = CreateTestStreamTab();
        var presenter = new VideoSurfacePresenter { Tab = tab };
        var finishedScreen = new Border { Background = Brushes.Black };
        var seekbar = new ReplaySeekOverlay { DataContext = tab };
        var volume = new VolumeOverlay();
        var root = new Grid();
        root.Children.Add(nativeVideo ? presenter : finishedScreen);
        root.Children.Add(seekbar);
        root.Children.Add(volume);
        var window = new Window
        {
            Width = 640,
            Height = 360,
            Left = SystemParameters.WorkArea.Left + 80,
            Top = SystemParameters.WorkArea.Top + 80,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Content = root
        };
        try
        {
            window.Show();
            window.Activate();
            FlushVolumeSeekLayout(window);
            Assert.True(window.IsActive, "The volume and seekbar fixture must have an active owner.");
            FlushVolumeSeekLayout(window);
            FrameworkElement target = nativeVideo ? presenter.Surface! : finishedScreen;
            seekbar.PlacementTarget = target;
            seekbar.IsOverlayEnabled = false;
            StopReplayOverlayPointerSampling(seekbar);
            FlushVolumeSeekLayout(window);

            // Keep this one popup open through reveal, resizes and dismissal. Repeating
            // Show would hide stale placement and missing seekbar notifications.
            volume.Show(target, 75, muted: false);
            FlushVolumeSeekLayout(window);
            var hiddenBounds = VolumePopupBounds(volume);
            seekbar.IsOverlayEnabled = true;
            StopReplayOverlayPointerSampling(seekbar);
            seekbar.ProcessPointerSample(new Point(100, 100), true, Environment.TickCount64);
            await Task.Delay(180);
            FlushVolumeSeekLayout(window);
            SaveVolumeSeekArtifact(target, nativeVideo ? "presented-reveal" : "finished-reveal");
            AssertVolumeClearsSeekbar(volume, seekbar, target);
            Assert.True(VolumePopupBounds(volume).Bottom < hiddenBounds.Bottom,
                "Opening the seekbar must move an already visible volume popup up.");

            foreach (var size in new[] { new Size(320, 180), new Size(236, 150), new Size(196, 126), new Size(320, 100) })
            {
                window.Width = size.Width;
                window.Height = size.Height;
                FlushVolumeSeekLayout(window);
                AssertVolumeClearsSeekbar(volume, seekbar, target);
                await Task.Delay(30);
                SaveVolumeSeekArtifact(target, $"{(nativeVideo ? "presented" : "finished")}-{size.Width}x{size.Height}");
            }

            window.Width = 640;
            window.Height = 360;
            FlushVolumeSeekLayout(window);
            AssertVolumeClearsSeekbar(volume, seekbar, target);
            var shownBounds = VolumePopupBounds(volume);
            seekbar.IsOverlayEnabled = false;
            FlushVolumeSeekLayout(window);
            Assert.True(!seekbar.IsOverlayOpen);
            Assert.True(VolumePopupBounds(volume).Bottom > shownBounds.Bottom,
                "Hiding the seekbar must release its space while the volume popup is still open.");
        }
        finally
        {
            window.Close();
        }
    });

    private static Task VolumePopupSeekbarRetargetAsync() => TestSta.RunAsync(async () =>
    {
        await using var tab = CreateTestStreamTab();
        var first = new Border { Background = Brushes.Black };
        var second = new Border { Background = Brushes.Black };
        var seekbar = new ReplaySeekOverlay { DataContext = tab, PlacementTarget = first };
        var volume = new VolumeOverlay();
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(second, 1);
        root.Children.Add(first);
        root.Children.Add(second);
        root.Children.Add(seekbar);
        root.Children.Add(volume);
        var window = new Window
        {
            Width = 820,
            Height = 360,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Content = root
        };
        try
        {
            window.Show();
            window.Activate();
            FlushVolumeSeekLayout(window);
            Assert.True(window.IsActive, "The volume and seekbar retargeting fixture must have an active owner.");
            FlushVolumeSeekLayout(window);
            StopReplayOverlayPointerSampling(seekbar);
            seekbar.ProcessPointerSample(new Point(100, 100), true, Environment.TickCount64);
            FlushVolumeSeekLayout(window);
            volume.Show(first, 75, muted: false);
            FlushVolumeSeekLayout(window);
            AssertVolumeClearsSeekbar(volume, seekbar, first);
            var reservedBottom = VolumePopupBounds(volume).Bottom;

            seekbar.PlacementTarget = second;
            StopReplayOverlayPointerSampling(seekbar);
            seekbar.ProcessPointerSample(new Point(101, 100), true, Environment.TickCount64);
            FlushVolumeSeekLayout(window);
            Assert.True(seekbar.IsOverlayOpen);
            Assert.True(VolumePopupBounds(volume).Bottom > reservedBottom,
                "A seekbar on another video must not reserve space on the old volume target.");

            volume.Show(second, 50, muted: true);
            FlushVolumeSeekLayout(window);
            AssertVolumeClearsSeekbar(volume, seekbar, second);
            reservedBottom = VolumePopupBounds(volume).Bottom;
            root.Children.Remove(seekbar);
            FlushVolumeSeekLayout(window);
            Assert.True(VolumePopupBounds(volume).Bottom > reservedBottom,
                "Unloading the seekbar must release its target's reserved space.");
        }
        finally
        {
            window.Close();
        }
    });

    private static Task VolumePopupMainPlayerAsync() => WithResponsiveWindowAsync(withVideo: true, async (window, _) =>
    {
        window.Topmost = true;
        window.Activate();
        PumpResponsiveLayout(window);
        var volume = (VolumeOverlay)window.FindName("VolumeOsd");
        try
        {
            foreach (var fullscreen in new[] { false, true })
            {
                if (fullscreen) ToggleMainWindowFullscreen(window, "StreamOnly");
                PumpResponsiveLayout(window);
                var seekbar = FindVisualDescendants<ReplaySeekOverlay>(window).Single();
                seekbar.IsOverlayEnabled = false;
                Assert.True(window.TryExecutePlaybackHotkey(Key.Up, ModifierKeys.None, null));
                seekbar.IsOverlayEnabled = true;
                StopReplayOverlayPointerSampling(seekbar);
                seekbar.ProcessPointerSample(new Point(fullscreen ? 101 : 100, 100), true, Environment.TickCount64);
                await Task.Delay(180);
                PumpResponsiveLayout(window);
                var target = seekbar.PlacementTarget!;
                AssertVolumeClearsSeekbar(volume, seekbar, target);
                SaveVolumeSeekArtifact(target, fullscreen ? "main-fullscreen" : "main-player");
            }
        }
        finally
        {
            ExitMainWindowFullscreenIfActive(window);
        }
    });

    private static Task VolumePopupFinishedPlayerAsync() => WithResponsiveWindowAsync(withVideo: false, async (window, viewModel) =>
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var factory = new FakePlaybackEngineFactory();
        await using var tab = VodResumeTestCatalog.Tab(VodResumeTestCatalog.Target(), files.Create(), factory);
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(VodResumeTestCatalog.Settings());
        factory.Engine!.PlaybackHealthOverride = () => new(1, PlaybackEngineState.Ended, 0, 0, 0, 0);
        tab.CheckVodPlaybackCompletion();
        Assert.True(tab.IsVodFinished);
        viewModel.Tabs.Add(tab);
        viewModel.SelectedTab = tab;
        window.Topmost = true;
        window.Activate();
        PumpResponsiveLayout(window);
        await VerifyAsync(window, "finished-main");

        var detached = new DetachedVideoWindow(tab) { Topmost = true, ShowInTaskbar = false, Width = 640, Height = 360 };
        try
        {
            detached.Show();
            FlushVolumeSeekLayout(detached);
            await VerifyAsync(detached, "finished-detached");
        }
        finally
        {
            detached.CloseForTabDisposal();
        }

        async Task VerifyAsync(Window owner, string phase)
        {
            var seekbar = FindVisualDescendants<ReplaySeekOverlay>(owner).Single();
            StopReplayOverlayPointerSampling(seekbar);
            seekbar.ProcessPointerSample(new Point(100, 100), true, Environment.TickCount64);
            var target = seekbar.PlacementTarget!;
            Assert.True(target is Grid && target.IsVisible, "The completed VOD must use its visible WPF screen.");
            var volume = (VolumeOverlay)owner.FindName("VolumeOsd");
            if (owner is MainWindow main)
                Assert.True(main.TryExecutePlaybackHotkey(Key.Up, ModifierKeys.None, null));
            else
                typeof(DetachedVideoWindow).GetMethod("AdjustVolume", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(owner, [tab, Mouse.MouseWheelDeltaForOneLine]);
            await Task.Delay(180);
            FlushVolumeSeekLayout(owner);
            Assert.True(ReferenceEquals(target, ((Popup)volume.FindName("Popup")).PlacementTarget),
                "Volume input must use the same per-tab finished screen as the seekbar.");
            AssertVolumeClearsSeekbar(volume, seekbar, target);
            SaveVolumeSeekArtifact(target, phase);
        }
    });

    private static void FlushVolumeSeekLayout(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static Rect VolumePopupBounds(VolumeOverlay volume)
    {
        var popup = (Popup)volume.FindName("Popup");
        Assert.True(popup.IsOpen, "The original volume popup must remain open during placement changes.");
        var handle = ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(popup.Child)!).Handle;
        var bounds = NativeWindowTest.GetWindowBounds(handle);
        return new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    private static void AssertVolumeClearsSeekbar(VolumeOverlay volume, ReplaySeekOverlay seekbar, FrameworkElement target)
    {
        Assert.True(seekbar.IsOverlayOpen, "The seekbar must be visible for the overlap assertion.");
        var chrome = (FrameworkElement)seekbar.FindName("OverlayChrome");
        var seekBounds = GetResponsiveScreenBounds(chrome);
        var volumeBounds = VolumePopupBounds(volume);
        var targetBounds = GetResponsiveScreenBounds(target);
        Assert.True(volumeBounds.Bottom <= seekBounds.Top,
            $"Volume popup {volumeBounds} overlaps seekbar {seekBounds} in target {targetBounds}.");
        AssertResponsiveBoundsInside(volumeBounds, targetBounds, "volume popup");
        Console.WriteLine($"Volume/seek bounds: target={targetBounds}, volume={volumeBounds}, seek={seekBounds}.");
    }

    private static void SaveVolumeSeekArtifact(FrameworkElement target, string phase)
    {
        var directory = Environment.GetEnvironmentVariable("SVS_TEST_ARTIFACT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bounds = GetResponsiveScreenBounds(target);
        using var bitmap = new System.Drawing.Bitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height));
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        var screenDc = ReplayVlcGetDc(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Failed to acquire the desktop DC.");
        try
        {
            var bitmapDc = graphics.GetHdc();
            try
            {
                const uint sourceCopyWithLayeredWindows = 0x00CC0020 | 0x40000000;
                if (!ReplayVlcBitBlt(bitmapDc, 0, 0, bitmap.Width, bitmap.Height,
                    screenDc, (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top), sourceCopyWithLayeredWindows))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Failed to capture the volume and seekbar.");
            }
            finally
            {
                graphics.ReleaseHdc(bitmapDc);
            }
        }
        finally
        {
            _ = ReplayVlcReleaseDc(IntPtr.Zero, screenDc);
        }
        bitmap.Save(Path.Combine(directory, $"volume-seek-{phase}.png"), System.Drawing.Imaging.ImageFormat.Png);
    }
}

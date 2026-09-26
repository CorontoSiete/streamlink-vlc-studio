internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> VodCompletionTests =>
    [
        ("VOD finished: main and detached video templates show completion and restore video", VodFinishedTemplatesAsync),
        ("VOD finished: seek controls remain usable over the finished screen", VodFinishedSeekControlsAsync),
        .. string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY"))
            ? Array.Empty<(string, Func<Task>)>()
            : [
                ("VOD finished: real VLC end of media and seek back", () => NativeVodFinishedAsync(false, 57)),
                ("VOD finished: real VLC MP4 skip to final frame", () => NativeVodFinishedAsync(false, 59.75)),
                ("VOD finished: real VLC MP4 skip to exact end", () => NativeVodFinishedAsync(false, 60)),
                ("VOD finished: real VLC HLS skip to final frame", () => NativeVodFinishedAsync(true, 59.75)),
                ("VOD finished: real VLC HLS skip to exact end", () => NativeVodFinishedAsync(true, 60)),
                ("VOD finished: real VLC MP4 reopen at final frame", () => NativeVodFinishedAsync(false, 59.75, fromFinished: true)),
                ("VOD finished: real VLC MP4 reopen at exact end", () => NativeVodFinishedAsync(false, 60, fromFinished: true)),
                ("VOD finished: real VLC HLS reopen at final frame", () => NativeVodFinishedAsync(true, 59.75, fromFinished: true)),
                ("VOD finished: real VLC HLS reopen at exact end", () => NativeVodFinishedAsync(true, 60, fromFinished: true))
            ]
    ];

    private static Task VodFinishedSeekControlsAsync() => TestSta.RunAsync(async () =>
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var factory = new FakePlaybackEngineFactory();
        await using var tab = VodResumeTestCatalog.Tab(VodResumeTestCatalog.Target(), files.Create(), factory);
        tab.SetVideoHandle(new IntPtr(123));
        await tab.StartAsync(VodResumeTestCatalog.Settings());
        using var fixture = new ReplayOverlayTestHost(tab);
        var screen = new Grid { Background = Brushes.Black, Margin = new Thickness(60, 30, 40, 20) };
        fixture.Root.Children.Insert(0, screen);
        factory.Engine!.PlaybackHealthOverride = () => new(1,
            factory.Engine.PlayCount == 1 ? PlaybackEngineState.Ended : PlaybackEngineState.Playing, 0, 0, 0, 0);
        tab.CheckVodPlaybackCompletion();
        fixture.Target.Visibility = Visibility.Hidden;
        fixture.Overlay.PlacementTarget = screen;
        fixture.FlushBindings();
        fixture.StopPointerSampling();
        fixture.Overlay.ProcessPointerSample(new Point(100, 100), true, Environment.TickCount64);
        fixture.FlushBindings();
        Assert.True(fixture.Overlay.IsOverlayOpen);
        Assert.Equal(fixture.OwnerHandle, NativeWindowTest.GetParent(fixture.NativeOverlayHandle));
        AssertPlacement();
        screen.Margin = new Thickness(80, 50, 20, 0);
        fixture.FlushBindings();
        AssertPlacement();

        fixture.BeginDrag();
        fixture.Slider.Value = 120;
        fixture.EndDrag(cancelled: false);
        await TestWait.UntilAsync(() => tab.Status == PlaybackStatus.Playing && !tab.IsReplaySeekInProgress,
            TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(120), factory.Engine.LastStartPosition!.Value);
        fixture.Overlay.PlacementTarget = fixture.Target;
        fixture.Target.Visibility = Visibility.Visible;
        fixture.FlushBindings();
        Assert.Equal(false, fixture.Overlay.IsOverlayOpen);
        fixture.Overlay.ProcessPointerSample(new Point(101, 100), true, Environment.TickCount64);
        fixture.FlushBindings();
        Assert.True(fixture.Overlay.IsOverlayOpen);
        Assert.Equal(fixture.Target.Handle, NativeWindowTest.GetParent(fixture.NativeOverlayHandle));

        void AssertPlacement()
        {
            var bounds = NativeWindowTest.GetWindowBounds(fixture.NativeOverlayHandle);
            var origin = screen.PointToScreen(new Point());
            var end = screen.PointToScreen(new Point(screen.ActualWidth, screen.ActualHeight));
            Assert.True(bounds.Left >= origin.X && bounds.Right <= end.X + 1);
            Assert.True(bounds.Top >= origin.Y && bounds.Bottom <= end.Y + 1);
            Assert.True(Math.Abs(bounds.Bottom - (end.Y - 16)) < 2);
        }
    });

    private static Task VodFinishedTemplatesAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var factory = new FakePlaybackEngineFactory();
        await using var tab = VodResumeTestCatalog.Tab(VodResumeTestCatalog.Target(), files.Create(), factory);
        tab.SetVideoPlacement(true, 0, 0, 1, 1);
        tab.SetVideoHandle(new IntPtr(123));
        await tab.StartAsync(VodResumeTestCatalog.Settings());
        var main = new MainWindow();
        RemoveMainWindowAutomaticStartup(main);
        var detached = new DetachedVideoWindow(tab);
        try
        {
            var mainItems = ((Grid)main.FindName("VideoViewport")).Children.OfType<ItemsControl>().Single();
            var detachedItems = ((Grid)detached.FindName("VideoHost")).Children.OfType<ItemsControl>().Single();
            var mainHost = (Grid)mainItems.ItemTemplate.LoadContent();
            mainHost.DataContext = tab;
            var detachedHost = (Grid)detachedItems.ItemTemplate.LoadContent();
            detachedHost.DataContext = detached.MountedVideoItems.Single();
            var hosts = new[] { mainHost, detachedHost };

            void Verify(bool finished)
            {
                main.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                foreach (var host in hosts)
                {
                    host.Measure(new Size(640, 360));
                    host.Arrange(new Rect(0, 0, 640, 360));
                    host.UpdateLayout();
                    var message = host.Children.OfType<TextBlock>().Single(text => text.Text == "VOD finished");
                    Assert.Equal(finished ? Visibility.Visible : Visibility.Collapsed, message.Visibility);
                    Assert.Equal(finished ? Visibility.Hidden : Visibility.Visible,
                        host.Children.OfType<VideoSurface>().Single().Visibility);
                    Assert.Equal<FrameworkElement?>(finished ? host : host.Children.OfType<VideoSurface>().Single(),
                        host.Children.OfType<ReplaySeekOverlay>().Single().PlacementTarget);
                    if (finished)
                    {
                        Assert.True(message.ActualWidth > 100 && message.ActualHeight > 20);
                        message.InvalidateVisual();
                        host.UpdateLayout();
                        main.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        var bitmap = WpfVisualTest.Render(host);
                        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                        var brightPixels = 0;
                        for (var index = 0; index < pixels.Length; index += 4)
                            if (pixels[index] > 200 && pixels[index + 1] > 200 && pixels[index + 2] > 200) brightPixels++;
                        Assert.True(brightPixels > 500, $"Finished text did not render: visible={message.IsVisible}, brightPixels={brightPixels}.");
                        var directory = Environment.GetEnvironmentVariable("SVS_TEST_ARTIFACT_DIR");
                        if (!string.IsNullOrWhiteSpace(directory))
                        {
                            Directory.CreateDirectory(directory);
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var output = File.Create(Path.Combine(directory,
                                ReferenceEquals(host, mainHost) ? "vod-finished-main.png" : "vod-finished-detached.png"));
                            encoder.Save(output);
                        }
                    }
                }
            }

            Verify(false);
            factory.Engine!.PlaybackHealthOverride = () => new(1, PlaybackEngineState.Ended, 0, 0, 0, 0);
            tab.CheckVodPlaybackCompletion();
            Verify(true);
            factory.Engine.PlaybackHealthOverride = null;
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(1));
            Verify(false);
        }
        finally
        {
            main.Close();
            detached.Close();
        }
    });

    private static Task NativeVodFinishedAsync(bool hls, double seekSeconds, bool fromFinished = false,
        Uri? sourceOverride = null, double durationSeconds = 60,
        IPlaybackMediaSourceGateway? gatewayOverride = null, bool nativeOverlay = false) => TestSta.RunOffscreenAsync(async () =>
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var settings = VodResumeTestCatalog.Settings();
        settings.VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
        settings.VideoRendererMode = VideoRendererMode.Gdi;
        if (nativeOverlay) settings.Chat.Layout = ChatLayout.Overlay;
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures",
            hls ? "replay-position-event/index.m3u8" : "replay-position-colors.mp4");
        await using var server = hls ? new ReplayFixtureServer(Path.GetDirectoryName(fixture)!,
            File.ReadAllText(fixture).Replace("PLAYLIST-TYPE:EVENT", "PLAYLIST-TYPE:VOD") + "\n#EXT-X-ENDLIST\n") : null;
        var source = sourceOverride ?? server?.Uri ?? new Uri(fixture);
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(source, "Fixture"))
        };
        var logger = new MemoryLogger();
        var history = files.Create();
        await using var gateway = new TwitchMutedVodPlaybackGateway(logger);
        var factory = new VodResumeNativeFactory(settings.Chat, gatewayOverride ?? gateway, nativeOverlay);
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            await using var tab = new StreamTabViewModel(new StreamTabViewModelDependencies
            {
                Target = VodResumeTestCatalog.Target() with { MediaDuration = TimeSpan.FromSeconds(durationSeconds) },
                Quality = "best",
                StreamlinkService = streamlink,
                PlaybackFactory = factory,
                ChatFactory = new FakeChatClientFactory(),
                Logger = logger,
                Dispatch = action => action(),
                VodPlaybackHistory = history
            });
            tab.SetVideoHandle(handle);
            tab.IsMuted = true;
            await tab.StartAsync(settings);
            await TestWait.UntilAsync(() => factory.Engine!.TryGetPlaybackHealth(out var health) &&
                health.State == PlaybackEngineState.Playing && health.DisplayedPictures > 0, TimeSpan.FromSeconds(8));
            if (fromFinished)
            {
                await tab.SeekReplayAsync(TimeSpan.FromSeconds(durationSeconds));
                await TestWait.UntilAsync(() => tab.IsVodFinished, TimeSpan.FromSeconds(10));
            }
            await tab.SeekReplayAsync(TimeSpan.FromSeconds(seekSeconds));
            var hasHealth = factory.Engine!.TryGetPlaybackHealth(out var afterSeek);
            Console.WriteLine($"VOD completion {(hls ? "HLS" : "MP4")} seek={seekSeconds}, reload={fromFinished}: tab={tab.Status}, healthAvailable={hasHealth}, decoder={afterSeek}");
            try
            {
                await TestWait.UntilAsync(() => tab.IsVodFinished, TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                factory.Engine.TryGetPlaybackHealth(out var failed);
                throw new InvalidOperationException($"VOD did not finish: tab={tab.Status}, decoder={failed}, error={tab.ErrorMessage}", ex);
            }
            Assert.Equal("VOD finished", tab.StatusText);
            Assert.Equal(tab.ReplaySeekMaximum, tab.ReplaySeekValue);
            Assert.Equal(false, tab.IsBusy);
            Assert.Equal(false, tab.IsReplaySeekInProgress);
            Assert.Equal(false, logger.Entries.Any(entry => entry.Message.StartsWith("Replay seek failed", StringComparison.Ordinal)));
            var completed = await history.GetAsync(tab.Target);
            Console.WriteLine($"VOD completion history: {completed}");
            Assert.True(completed is { Completed: true, HasBeenWatched: true },
                "Confirmed decoder EOF must also mark the VOD watched, including a seek that ends before another clock sample.");
            await VodWatchedCompletionTestCatalog.AssertPersistedAsync(files, history, tab.Target);
            await tab.SeekReplayAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(false, tab.IsVodFinished);
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            await TestWait.UntilAsync(() => factory.Engine!.TryGetPlaybackClock(out var clock) &&
                clock.Position >= TimeSpan.FromSeconds(4) && clock.Position < TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(3));
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });
}

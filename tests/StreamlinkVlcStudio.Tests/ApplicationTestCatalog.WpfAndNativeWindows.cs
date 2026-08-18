internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> WpfAndNativeWindows { get; } =
    [
    ("live browse and VOD cards use rounded thumbnail clips and compact side gutters", () =>
    {
        return TestSta.RunAsync(async () =>
        {
            const long liveThumbnailCacheVersion = 24601;
            var settings = new AppSettings
            {
                KeepHomeCardRightGap = false
            };
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                new FakeStreamlinkService(),
                new FakePlaybackEngineFactory(),
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action());
            var liveItem = new LiveStreamCardViewModel(
                LiveStreamCardData.FromFollowedStream(
                    CreateTestFollowedStream(PlatformKind.Twitch, "live-card")),
                (_, _) => Task.CompletedTask,
                liveThumbnailCacheVersion);
            var vodItem = new VodViewModel(
                new TwitchVodItem(
                    "vod-card",
                    "stream-card",
                    "broadcaster-card",
                    "vod-channel",
                    "VOD Channel",
                    "Recorded stream",
                    "",
                    "https://www.twitch.tv/videos/123",
                    "https://example.invalid/vod-card.jpg",
                    DateTimeOffset.UtcNow.AddHours(-2),
                    DateTimeOffset.UtcNow.AddHours(-2),
                    TimeSpan.FromHours(1),
                    100,
                    TwitchVodTypeFilter.Archive),
                (_, _) => Task.CompletedTask);
            var browseItem = new LiveStreamCardViewModel(
                LiveStreamCardData.FromBrowseStream(new BrowseLiveStream(
                    PlatformKind.Kick,
                    "browse-card",
                    "Browse Card",
                    "Live now",
                    "42",
                    "Just Chatting",
                    100,
                    "https://example.invalid/browse-card.jpg",
                    DateTimeOffset.UtcNow,
                    false,
                    "en",
                    "https://kick.com/browse-card")),
                (_, _) => Task.CompletedTask);
            AnimatedEmoteImage.SetCachedSolidColorImageForTest(
                liveItem.ThumbnailUrl,
                AnimatedEmoteImage.DefaultMaxImageBytes,
                [Colors.MediumPurple],
                [TimeSpan.FromSeconds(1)],
                width: 16,
                height: 9,
                cacheVersion: liveItem.ThumbnailCacheVersion);
            var unversionedThumbnailUrls = new[] { vodItem.ThumbnailUrl, browseItem.ThumbnailUrl };
            foreach (var thumbnailUrl in unversionedThumbnailUrls)
            {
                AnimatedEmoteImage.SetCachedSolidColorImageForTest(
                    thumbnailUrl,
                    AnimatedEmoteImage.DefaultMaxImageBytes,
                    [Colors.MediumPurple],
                    [TimeSpan.FromSeconds(1)],
                    width: 16,
                    height: 9);
            }

            viewModel.LiveFollowedChannels.Add(liveItem);
            viewModel.TwitchVods.Add(vodItem);

            var window = new MainWindow
            {
                Width = 1100,
                Height = 760,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);

            try
            {
                window.Show();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var liveClip = FindVisualDescendants<RoundedClipBorder>(window)
                    .Single(border => ReferenceEquals(border.DataContext, liveItem));
                AssertHomeMediaThumbnailClip(liveClip);
                AssertHomeCardCompactHorizontalGutter(window, liveClip);
                var liveImage = FindVisualDescendants<AnimatedEmoteImage>(liveClip).Single();
                Assert.NotNull(liveImage.ImageRequest);
                Assert.Equal(liveItem.ThumbnailCacheVersion, liveImage.ImageRequest!.CacheVersion);
                Assert.True(liveImage.CurrentImageCacheKey.HasValue);
                Assert.Equal(
                    liveItem.ThumbnailCacheVersion,
                    liveImage.CurrentImageCacheKey.GetValueOrDefault().CacheVersion);
                Assert.Equal(
                    false,
                    AnimatedEmoteImage.ContainsCachedImageForTest(
                        liveItem.ThumbnailUrl,
                        AnimatedEmoteImage.DefaultMaxImageBytes));

                viewModel.ShowTwitchVodsHomePageCommand.Execute(null);
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var vodClip = FindVisualDescendants<RoundedClipBorder>(window)
                    .Single(border => ReferenceEquals(border.DataContext, vodItem));
                AssertHomeMediaThumbnailClip(vodClip);
                AssertHomeCardCompactHorizontalGutter(window, vodClip);

                viewModel.ShowBrowseHomePageCommand.Execute(null);
                var selectBrowseStreamsPage = typeof(MainViewModel).GetMethod(
                    "SetBrowseStreamsPageSelected",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(selectBrowseStreamsPage);
                selectBrowseStreamsPage!.Invoke(viewModel, [true]);
                viewModel.BrowseStreams.Add(browseItem);
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var browseClip = FindVisualDescendants<RoundedClipBorder>(window)
                    .Single(border => ReferenceEquals(border.DataContext, browseItem));
                AssertHomeMediaThumbnailClip(browseClip);
                AssertHomeCardCompactHorizontalGutter(window, browseClip);
                var browseImage = FindVisualDescendants<AnimatedEmoteImage>(browseClip).Single();
                Assert.NotNull(browseImage.ImageRequest);
                Assert.Equal(0L, browseImage.ImageRequest!.CacheVersion);
            }
            finally
            {
                window.Close();
                await viewModel.DisposeAsync();
                AnimatedEmoteImage.RemoveCachedImageForTest(
                    liveItem.ThumbnailUrl,
                    AnimatedEmoteImage.DefaultMaxImageBytes,
                    liveItem.ThumbnailCacheVersion);
                foreach (var thumbnailUrl in unversionedThumbnailUrls)
                {
                    AnimatedEmoteImage.RemoveCachedImageForTest(
                        thumbnailUrl,
                        AnimatedEmoteImage.DefaultMaxImageBytes);
                }
            }
        });
    }),
    ("Twitch VOD cards show subscriber-only and unknown access tags", () =>
    {
        return TestSta.RunAsync(async () =>
        {
            var settings = new AppSettings();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                new FakeStreamlinkService(),
                new FakePlaybackEngineFactory(),
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action());

            static TwitchVodItem CreateVod(string id, string title, TwitchVodAccessKind accessKind) => new(
                id,
                $"stream-{id}",
                "broadcaster-card",
                "vod-channel",
                "VOD Channel",
                title,
                "",
                $"https://www.twitch.tv/videos/{id}",
                "",
                DateTimeOffset.UtcNow.AddHours(-2),
                DateTimeOffset.UtcNow.AddHours(-2),
                TimeSpan.FromHours(1),
                100,
                TwitchVodTypeFilter.Archive,
                accessKind);

            var subscriberCard = new VodViewModel(
                CreateVod("2091984624", "Subscriber VOD", TwitchVodAccessKind.SubscriberOnly),
                (_, _) => Task.CompletedTask);
            var publicCard = new VodViewModel(
                CreateVod("2838068542", "Public VOD", TwitchVodAccessKind.Public),
                (_, _) => Task.CompletedTask);
            var unknownCard = new VodViewModel(
                CreateVod("2838068543", "Unknown VOD", TwitchVodAccessKind.Unknown),
                (_, _) => Task.CompletedTask);
            viewModel.TwitchVods.Add(subscriberCard);
            viewModel.TwitchVods.Add(publicCard);
            viewModel.TwitchVods.Add(unknownCard);

            var window = new MainWindow
            {
                Width = 1100,
                Height = 760,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);

            try
            {
                window.Show();
                viewModel.ShowTwitchVodsHomePageCommand.Execute(null);
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                static Border FindAccessTag(MainWindow owner, VodViewModel card, string automationName) =>
                    FindVisualDescendants<Border>(owner).Single(border =>
                        ReferenceEquals(border.DataContext, card) &&
                        string.Equals(
                            System.Windows.Automation.AutomationProperties.GetName(border),
                            automationName,
                            StringComparison.Ordinal));

                Assert.Equal(
                    Visibility.Visible,
                    FindAccessTag(window, subscriberCard, "Subscriber-only Twitch VOD").Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    FindAccessTag(window, subscriberCard, "Twitch VOD access unknown").Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    FindAccessTag(window, publicCard, "Subscriber-only Twitch VOD").Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    FindAccessTag(window, publicCard, "Twitch VOD access unknown").Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    FindAccessTag(window, unknownCard, "Subscriber-only Twitch VOD").Visibility);
                Assert.Equal(
                    Visibility.Visible,
                    FindAccessTag(window, unknownCard, "Twitch VOD access unknown").Visibility);
            }
            finally
            {
                window.Close();
                await viewModel.DisposeAsync();
            }
        });
    }),
    ("home card wrap panel preserves fixed right gap mode", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var panel = new HomeCardWrapPanel
            {
                ItemWidth = 100,
                HorizontalGap = 10,
                VerticalGap = 12,
                KeepRightGap = true
            };
            var cards = AddHomeCardPanelChildren(panel, 4);

            panel.Measure(new System.Windows.Size(350, double.PositiveInfinity));
            panel.Arrange(new System.Windows.Rect(0, 0, 350, panel.DesiredSize.Height));

            Assert.Equal(350d, panel.DesiredSize.Width);
            Assert.Equal(124d, panel.DesiredSize.Height);
            Assert.Equal(100d, cards[0].RenderSize.Width);
            Assert.Equal(100d, cards[1].RenderSize.Width);
            Assert.Equal(0d, cards[0].TranslatePoint(new System.Windows.Point(0, 0), panel).X);
            Assert.Equal(110d, cards[1].TranslatePoint(new System.Windows.Point(0, 0), panel).X);
            Assert.Equal(220d, cards[2].TranslatePoint(new System.Windows.Point(0, 0), panel).X);
            Assert.Equal(30d, 350 - cards[2].TranslatePoint(new System.Windows.Point(100, 0), panel).X);
        });
    }),
    ("home card wrap panel fills rows when right gap mode is off", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var panel = new HomeCardWrapPanel
            {
                ItemWidth = 100,
                HorizontalGap = 10,
                VerticalGap = 12,
                KeepRightGap = false
            };
            var cards = AddHomeCardPanelChildren(panel, 4);

            panel.Measure(new System.Windows.Size(350, double.PositiveInfinity));
            panel.Arrange(new System.Windows.Rect(0, 0, 350, panel.DesiredSize.Height));

            Assert.Equal(350d, panel.DesiredSize.Width);
            Assert.Equal(124d, panel.DesiredSize.Height);
            AssertNear(110, cards[0].RenderSize.Width);
            AssertNear(110, cards[1].RenderSize.Width);
            AssertNear(0, cards[0].TranslatePoint(new System.Windows.Point(0, 0), panel).X);
            AssertNear(120, cards[1].TranslatePoint(new System.Windows.Point(0, 0), panel).X);
            AssertNear(240, cards[2].TranslatePoint(new System.Windows.Point(0, 0), panel).X);
            AssertNear(0, 350 - cards[2].TranslatePoint(new System.Windows.Point(cards[2].RenderSize.Width, 0), panel).X);
        });
    }),
    ("video viewport chrome uses true black behind VLC", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var window = new MainWindow();
            var playbackHost = (System.Windows.Controls.DockPanel)window.FindName("PlaybackHost");
            var videoViewport = (System.Windows.Controls.Grid)window.FindName("VideoViewport");

            Assert.NotNull(playbackHost);
            Assert.NotNull(videoViewport);
            WpfVisualTest.AssertSolidBrushColor("#FF000000", playbackHost.Background);
            WpfVisualTest.AssertSolidBrushColor("#FF000000", videoViewport.Background);
            Assert.Equal(true, videoViewport.ClipToBounds);
            Assert.Equal(true, videoViewport.SnapsToDevicePixels);
            Assert.Equal(true, videoViewport.UseLayoutRounding);
        });
    }),
    ("picture-in-picture resizing keeps the video area aspect-correct", () =>
    {
        const double aspectRatio = 16.0 / 9.0;
        var insets = new PictureInPictureWindowInsets(0, 34, 0, 0);
        var resizeEdges = new[]
        {
            PictureInPictureWindowSizing.WmszLeft,
            PictureInPictureWindowSizing.WmszRight,
            PictureInPictureWindowSizing.WmszTop,
            PictureInPictureWindowSizing.WmszTopLeft,
            PictureInPictureWindowSizing.WmszTopRight,
            PictureInPictureWindowSizing.WmszBottom,
            PictureInPictureWindowSizing.WmszBottomLeft,
            PictureInPictureWindowSizing.WmszBottomRight
        };

        foreach (var resizeEdge in resizeEdges)
        {
            var proposed = new NativeRectangle
            {
                Left = 100,
                Top = 120,
                Right = 900,
                Bottom = 620
            };

            Assert.True(PictureInPictureWindowSizing.TryConstrainRect(
                proposed,
                resizeEdge,
                aspectRatio,
                insets,
                minimumWidth: 320,
                minimumHeight: 220,
                out var constrained));

            var contentWidth = constrained.Right - constrained.Left - insets.Horizontal;
            var contentHeight = constrained.Bottom - constrained.Top - insets.Vertical;
            AssertNear(aspectRatio, contentWidth / (double)contentHeight);
            Assert.True(constrained.Right > constrained.Left);
            Assert.True(constrained.Bottom > constrained.Top);
        }

        var fittedDefaultSize = PictureInPictureWindowSizing.FitWindowSize(
            new Size(520, 326.5),
            aspectRatio,
            leftInset: 0,
            topInset: 34,
            rightInset: 0,
            bottomInset: 0,
            minimumWidth: 320,
            minimumHeight: 220);
        AssertNear(520, fittedDefaultSize.Width);
        AssertNear(326.5, fittedDefaultSize.Height);
        return Task.CompletedTask;
    }),
    ("picture-in-picture movement stays inside the monitor work area", () =>
    {
        var workArea = new NativeRectangle
        {
            Left = 100,
            Top = 80,
            Right = 900,
            Bottom = 680
        };
        var proposals = new[]
        {
            new NativeRectangle { Left = 240, Top = 200, Right = 560, Bottom = 380 },
            new NativeRectangle { Left = -400, Top = 200, Right = -80, Bottom = 380 },
            new NativeRectangle { Left = 800, Top = 200, Right = 1120, Bottom = 380 },
            new NativeRectangle { Left = 240, Top = -300, Right = 560, Bottom = -120 },
            new NativeRectangle { Left = 240, Top = 620, Right = 560, Bottom = 800 }
        };

        foreach (var proposed in proposals)
        {
            Assert.True(PictureInPictureWindowSizing.TryConstrainMoveRect(
                proposed,
                workArea,
                out var constrained));
            Assert.True(constrained.Left >= workArea.Left);
            Assert.True(constrained.Top >= workArea.Top);
            Assert.True(constrained.Right <= workArea.Right);
            Assert.True(constrained.Bottom <= workArea.Bottom);
            Assert.Equal(proposed.Right - proposed.Left, constrained.Right - constrained.Left);
            Assert.Equal(proposed.Bottom - proposed.Top, constrained.Bottom - constrained.Top);
        }

        var negativeWorkArea = new NativeRectangle
        {
            Left = -1920,
            Top = -200,
            Right = 0,
            Bottom = 880
        };
        Assert.True(PictureInPictureWindowSizing.TryConstrainMoveRect(
            new NativeRectangle { Left = -2400, Top = -500, Right = -1760, Bottom = -140 },
            negativeWorkArea,
            out var negativeConstrained));
        Assert.Equal(-1920, negativeConstrained.Left);
        Assert.Equal(-200, negativeConstrained.Top);
        Assert.Equal(-1280, negativeConstrained.Right);
        Assert.Equal(160, negativeConstrained.Bottom);

        Assert.True(PictureInPictureWindowSizing.TryConstrainMoveRect(
            new NativeRectangle { Left = -500, Top = -300, Right = 1100, Bottom = 600 },
            workArea,
            out var oversizedConstrained));
        Assert.True(oversizedConstrained.Left >= workArea.Left);
        Assert.True(oversizedConstrained.Top >= workArea.Top);
        Assert.True(oversizedConstrained.Right <= workArea.Right);
        Assert.True(oversizedConstrained.Bottom <= workArea.Bottom);
        AssertNear(
            1600.0 / 900.0,
            (oversizedConstrained.Right - oversizedConstrained.Left) /
            (double)(oversizedConstrained.Bottom - oversizedConstrained.Top),
            0.01);
        return Task.CompletedTask;
    }),
    ("picture-in-picture movement remains responsive after pushing against a screen edge", () =>
    {
        var workArea = new NativeRectangle
        {
            Left = 100,
            Top = 80,
            Right = 900,
            Bottom = 680
        };
        var currentBounds = new NativeRectangle
        {
            Left = 240,
            Top = 200,
            Right = 560,
            Bottom = 380
        };
        var moveSession = new PictureInPictureWindowMoveSession();

        Assert.Equal(false, moveSession.TryGetNextBounds(
            currentBounds,
            2000,
            300,
            workArea,
            out _));

        moveSession.Begin(300, 300);
        Assert.True(moveSession.TryGetNextBounds(
            currentBounds,
            2000,
            300,
            workArea,
            out var edgeBounds));
        Assert.Equal(580, edgeBounds.Left);
        Assert.Equal(900, edgeBounds.Right);

        // The cursor delta is rebased after every move. Reversing by one pixel therefore moves
        // the window immediately instead of first paying back hidden overscroll at the edge.
        Assert.True(moveSession.TryGetNextBounds(
            edgeBounds,
            1999,
            300,
            workArea,
            out var movedAwayFromEdge));
        Assert.Equal(579, movedAwayFromEdge.Left);
        Assert.Equal(899, movedAwayFromEdge.Right);
        Assert.True(moveSession.End());
        Assert.Equal(false, moveSession.IsActive);
        Assert.Equal(false, moveSession.End());
        return Task.CompletedTask;
    }),
    ("detached native sizing hook constrains the proposed window rectangle", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                new FakeStreamlinkService(),
                new FakePlaybackEngineFactory(),
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action());
            var window = new DetachedVideoWindow(tab)
            {
                Left = 240,
                Top = 260,
                Width = 740,
                Height = 430
            };
            var hook = typeof(DetachedVideoWindow).GetMethod(
                "WindowMessageHook",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(hook);

            try
            {
                window.Show();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                var videoHost = (FrameworkElement)window.FindName("VideoHost")!;
                var nativeBounds = NativeWindowTest.GetWindowBounds(handle);
                var hostTopLeft = videoHost.PointToScreen(new Point(0, 0));
                var hostBottomRight = videoHost.PointToScreen(new Point(videoHost.ActualWidth, videoHost.ActualHeight));
                var insets = new PictureInPictureWindowInsets(
                    Math.Max(0, (int)Math.Round(hostTopLeft.X) - nativeBounds.Left),
                    Math.Max(0, (int)Math.Round(hostTopLeft.Y) - nativeBounds.Top),
                    Math.Max(0, nativeBounds.Right - (int)Math.Round(hostBottomRight.X)),
                    Math.Max(0, nativeBounds.Bottom - (int)Math.Round(hostBottomRight.Y)));
                var proposed = new NativeRectangle
                {
                    Left = nativeBounds.Left,
                    Top = nativeBounds.Top,
                    Right = nativeBounds.Right + 300,
                    Bottom = nativeBounds.Bottom + 200
                };
                var rectPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRectangle>());
                try
                {
                    Marshal.StructureToPtr(proposed, rectPointer, fDeleteOld: false);
                    var hookArguments = new object?[]
                    {
                        handle,
                        PictureInPictureWindowSizing.WmSizing,
                        new IntPtr(PictureInPictureWindowSizing.WmszBottomRight),
                        rectPointer,
                        false
                    };
                    hook!.Invoke(window, hookArguments);
                    var constrained = Marshal.PtrToStructure<NativeRectangle>(rectPointer);
                    var contentWidth = constrained.Right - constrained.Left - insets.Horizontal;
                    var contentHeight = constrained.Bottom - constrained.Top - insets.Vertical;
                    Assert.True(contentWidth > 0);
                    Assert.True(contentHeight > 0);
                    AssertNear(tab.VideoAspectRatio, contentWidth / (double)contentHeight, 0.01);
                }
                finally
                {
                    Marshal.FreeHGlobal(rectPointer);
                }
            }
            finally
            {
                window.CloseForTabDisposal();
            }
        });
    }),
    ("detached picture-in-picture moving hook follows the cursor across monitors and stays on screen", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                new FakeStreamlinkService(),
                new FakePlaybackEngineFactory(),
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action());
            var window = new DetachedVideoWindow([tab], tab, showTopBar: false)
            {
                Width = 520,
                Height = 292.5
            };
            var restoreCursor = NativeWindowTest.TryGetCursorPosition(out var originalCursor);
            var hook = typeof(DetachedVideoWindow).GetMethod(
                "WindowMessageHook",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(hook);

            try
            {
                window.Show();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                var nativeBounds = NativeWindowTest.GetWindowBounds(handle);
                var width = nativeBounds.Right - nativeBounds.Left;
                var height = nativeBounds.Bottom - nativeBounds.Top;
                var screens = System.Windows.Forms.Screen.AllScreens;
                var cursorScreen = screens[0];
                var proposalScreen = screens.Length > 1
                    ? screens[1]
                    : cursorScreen;
                var cursorX = cursorScreen.WorkingArea.Left + cursorScreen.WorkingArea.Width / 2;
                var cursorY = cursorScreen.WorkingArea.Top + cursorScreen.WorkingArea.Height / 2;
                NativeWindowTest.SetCursorPosition(cursorX, cursorY);
                Assert.True(WindowInteropHelpers.TryGetMonitorInfoForPoint(
                    new WindowPoint { X = cursorX, Y = cursorY },
                    out var targetMonitorInfo));
                var proposalLeft = proposalScreen.WorkingArea.Left +
                    (proposalScreen.WorkingArea.Width - width) / 2;
                var proposalTop = proposalScreen.WorkingArea.Top +
                    (proposalScreen.WorkingArea.Height - height) / 2;
                if (screens.Length == 1)
                {
                    proposalLeft = proposalScreen.WorkingArea.Left - Math.Max(1, width / 4);
                    proposalTop = proposalScreen.WorkingArea.Top - Math.Max(1, height / 4);
                }

                var proposed = new NativeRectangle
                {
                    Left = proposalLeft,
                    Top = proposalTop,
                    Right = proposalLeft + width,
                    Bottom = proposalTop + height
                };
                var rectPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRectangle>());
                try
                {
                    Marshal.StructureToPtr(proposed, rectPointer, fDeleteOld: false);
                    var hookArguments = new object?[]
                    {
                        handle,
                        PictureInPictureWindowSizing.WmMoving,
                        IntPtr.Zero,
                        rectPointer,
                        false
                    };
                    var hookResult = (IntPtr)hook!.Invoke(window, hookArguments)!;
                    var constrained = Marshal.PtrToStructure<NativeRectangle>(rectPointer);
                    Assert.Equal(new IntPtr(1), hookResult);
                    Assert.Equal(true, hookArguments[4]);
                    Assert.True(constrained.Left >= targetMonitorInfo.WorkArea.Left);
                    Assert.True(constrained.Top >= targetMonitorInfo.WorkArea.Top);
                    Assert.True(constrained.Right <= targetMonitorInfo.WorkArea.Right);
                    Assert.True(constrained.Bottom <= targetMonitorInfo.WorkArea.Bottom);
                    Assert.Equal(width, constrained.Right - constrained.Left);
                    Assert.Equal(height, constrained.Bottom - constrained.Top);
                    Assert.Equal(false, window.IsTopBarShown);

                    if (screens.Length > 1)
                    {
                        var switchedCursorX = proposalScreen.WorkingArea.Left +
                            proposalScreen.WorkingArea.Width / 2;
                        var switchedCursorY = proposalScreen.WorkingArea.Top +
                            proposalScreen.WorkingArea.Height / 2;
                        NativeWindowTest.SetCursorPosition(switchedCursorX, switchedCursorY);
                        Assert.True(WindowInteropHelpers.TryGetMonitorInfoForPoint(
                            new WindowPoint { X = switchedCursorX, Y = switchedCursorY },
                            out var switchedMonitorInfo));
                        Marshal.StructureToPtr(constrained, rectPointer, fDeleteOld: false);
                        hookArguments[4] = false;

                        var switchedHookResult = (IntPtr)hook.Invoke(window, hookArguments)!;
                        var switched = Marshal.PtrToStructure<NativeRectangle>(rectPointer);
                        Assert.Equal(new IntPtr(1), switchedHookResult);
                        Assert.Equal(true, hookArguments[4]);
                        Assert.True(switched.Left >= switchedMonitorInfo.WorkArea.Left);
                        Assert.True(switched.Top >= switchedMonitorInfo.WorkArea.Top);
                        Assert.True(switched.Right <= switchedMonitorInfo.WorkArea.Right);
                        Assert.True(switched.Bottom <= switchedMonitorInfo.WorkArea.Bottom);
                        Assert.Equal(width, switched.Right - switched.Left);
                        Assert.Equal(height, switched.Bottom - switched.Top);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(rectPointer);
                }
            }
            finally
            {
                if (restoreCursor)
                {
                    NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
                }

                window.CloseForTabDisposal();
            }
        });
    }),
    ("detached picture-in-picture owns caption dragging instead of entering the Windows move loop", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                new FakeStreamlinkService(),
                new FakePlaybackEngineFactory(),
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action());
            var window = new DetachedVideoWindow(tab)
            {
                Left = 240,
                Top = 260,
                Width = 740,
                Height = 430
            };
            var restoreCursor = NativeWindowTest.TryGetCursorPosition(out var originalCursor);
            var hook = typeof(DetachedVideoWindow).GetMethod(
                "WindowMessageHook",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var continueMove = typeof(DetachedVideoWindow).GetMethod(
                "ContinueWindowMove",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(hook);
            Assert.NotNull(continueMove);

            try
            {
                window.Show();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                var titleBar = (FrameworkElement)window.FindName("TitleBar")!;
                var titleBarPoint = titleBar.PointToScreen(new Point(
                    titleBar.ActualWidth / 2,
                    titleBar.ActualHeight / 2));
                NativeWindowTest.SetCursorPosition(
                    (int)Math.Round(titleBarPoint.X),
                    (int)Math.Round(titleBarPoint.Y));
                var initialBounds = NativeWindowTest.GetWindowBounds(handle);
                var topResizeHit = NativeWindowTest.SendMessage(
                    handle,
                    0x0084, // WM_NCHITTEST
                    IntPtr.Zero,
                    NativeWindowTest.MakeMouseLParam(
                        initialBounds.Left + initialBounds.Width / 2,
                        initialBounds.Top + 1));
                Assert.Equal(new IntPtr(12), topResizeHit); // HTTOP

                var captionMouseDown = new object?[]
                {
                    handle,
                    0x00A1, // WM_NCLBUTTONDOWN
                    new IntPtr(2), // HTCAPTION
                    IntPtr.Zero,
                    false
                };
                _ = hook!.Invoke(window, captionMouseDown);
                Assert.Equal(true, captionMouseDown[4]);
                Assert.True(window.HasActiveWindowMove);
                Assert.Equal(handle, NativeWindowTest.GetCapture());

                Assert.True(NativeWindowTest.TryGetCursorPosition(out var cursorPoint));
                var boundsBeforeMove = NativeWindowTest.GetWindowBounds(handle);
                Assert.True(WindowInteropHelpers.TryGetMonitorInfoForPoint(
                    new WindowPoint { X = cursorPoint.X, Y = cursorPoint.Y },
                    out var monitorInfo));
                var horizontalChange = boundsBeforeMove.Right + 10 <= monitorInfo.WorkArea.Right
                    ? 10
                    : -10;
                continueMove!.Invoke(window, [handle, cursorPoint.X + horizontalChange, cursorPoint.Y]);
                var boundsAfterMove = NativeWindowTest.GetWindowBounds(handle);
                Assert.Equal(boundsBeforeMove.Left + horizontalChange, boundsAfterMove.Left);
                Assert.Equal(boundsBeforeMove.Right + horizontalChange, boundsAfterMove.Right);

                var endMove = new object?[]
                {
                    handle,
                    0x0202, // WM_LBUTTONUP
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false
                };
                _ = hook.Invoke(window, endMove);
                Assert.Equal(true, endMove[4]);
                Assert.Equal(false, window.HasActiveWindowMove);
                Assert.True(NativeWindowTest.GetCapture() != handle);

                var captionSystemMove = new object?[]
                {
                    handle,
                    0x0112, // WM_SYSCOMMAND
                    new IntPtr(0xF012), // SC_MOVE + HTCAPTION
                    IntPtr.Zero,
                    false
                };
                _ = hook.Invoke(window, captionSystemMove);
                Assert.Equal(true, captionSystemMove[4]);
            }
            finally
            {
                window.CancelVideoMoveCandidate();
                if (restoreCursor)
                {
                    NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
                }

                window.CloseForTabDisposal();
            }
        });
    }),
    ("video surface grid panel partitions odd viewport sizes without center gaps", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var panel = new StreamlinkVlcStudio.App.Wpf.Controls.VideoSurfaceGridPanel
            {
                Rows = 2,
                Columns = 2
            };
            var topLeft = new Border();
            var topRight = new Border();
            var bottomLeft = new Border();
            var bottomRight = new Border();

            Grid.SetRow(topLeft, 0);
            Grid.SetColumn(topLeft, 0);
            Grid.SetRow(topRight, 0);
            Grid.SetColumn(topRight, 1);
            Grid.SetRow(bottomLeft, 1);
            Grid.SetColumn(bottomLeft, 0);
            Grid.SetRow(bottomRight, 1);
            Grid.SetColumn(bottomRight, 1);

            panel.Children.Add(topLeft);
            panel.Children.Add(topRight);
            panel.Children.Add(bottomLeft);
            panel.Children.Add(bottomRight);

            panel.Measure(new System.Windows.Size(1321, 733));
            panel.Arrange(new System.Windows.Rect(0, 0, 1321, 733));

            var topLeftOrigin = topLeft.TranslatePoint(new System.Windows.Point(0, 0), panel);
            var topRightOrigin = topRight.TranslatePoint(new System.Windows.Point(0, 0), panel);
            var bottomLeftOrigin = bottomLeft.TranslatePoint(new System.Windows.Point(0, 0), panel);
            var bottomRightOrigin = bottomRight.TranslatePoint(new System.Windows.Point(0, 0), panel);

            AssertNear(topLeftOrigin.X + topLeft.RenderSize.Width, topRightOrigin.X);
            AssertNear(bottomLeftOrigin.X + bottomLeft.RenderSize.Width, bottomRightOrigin.X);
            AssertNear(topLeftOrigin.Y + topLeft.RenderSize.Height, bottomLeftOrigin.Y);
            AssertNear(topRightOrigin.Y + topRight.RenderSize.Height, bottomRightOrigin.Y);
        });
    }),
    ("video surface grid panel keeps six-up tiles equal-sized and aspect-correct", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var panel = new StreamlinkVlcStudio.App.Wpf.Controls.VideoSurfaceGridPanel
            {
                Rows = 2,
                Columns = 3
            };
            var tiles = new Border[6];

            for (var index = 0; index < tiles.Length; index++)
            {
                var tile = new Border();
                Grid.SetRow(tile, index / 3);
                Grid.SetColumn(tile, index % 3);
                StreamlinkVlcStudio.App.Wpf.Controls.VideoSurfaceGridPanel.SetAspectRatio(tile, 16.0 / 9.0);
                panel.Children.Add(tile);
                tiles[index] = tile;
            }

            panel.Measure(new System.Windows.Size(1920, 1080));
            panel.Arrange(new System.Windows.Rect(0, 0, 1920, 1080));

            var origins = tiles.Select(tile => tile.TranslatePoint(new System.Windows.Point(0, 0), panel)).ToArray();
            AssertNear(0, origins.Min(point => point.X));
            AssertNear(180, origins.Min(point => point.Y));
            AssertNear(1920, tiles.Select((tile, index) => origins[index].X + tile.RenderSize.Width).Max());
            AssertNear(900, tiles.Select((tile, index) => origins[index].Y + tile.RenderSize.Height).Max());
            AssertNear(640, tiles[0].RenderSize.Width);
            AssertNear(360, tiles[0].RenderSize.Height);

            foreach (var tile in tiles)
            {
                AssertNear(tiles[0].RenderSize.Width, tile.RenderSize.Width);
                AssertNear(tiles[0].RenderSize.Height, tile.RenderSize.Height);
                AssertNear(16.0 / 9.0, tile.RenderSize.Width / tile.RenderSize.Height);
            }
        });
    }),
    ("video surface grid panel invalidates layout when a child grid placement changes", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var panel = new StreamlinkVlcStudio.App.Wpf.Controls.VideoSurfaceGridPanel
            {
                Rows = 2,
                Columns = 2
            };
            var child = new Border();

            Grid.SetRow(child, 0);
            Grid.SetColumn(child, 0);
            panel.Children.Add(child);
            var window = new Window
            {
                Content = panel,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };

            try
            {
                window.Show();
                panel.Measure(new System.Windows.Size(800, 600));
                panel.Arrange(new System.Windows.Rect(0, 0, 800, 600));
                Assert.Equal(true, panel.IsArrangeValid);

                Grid.SetColumn(child, 1);

                Assert.Equal(false, panel.IsArrangeValid);
                panel.Measure(new System.Windows.Size(800, 600));
                panel.Arrange(new System.Windows.Rect(0, 0, 800, 600));

                var origin = child.TranslatePoint(new System.Windows.Point(0, 0), panel);
                AssertNear(400, origin.X);
                AssertNear(0, origin.Y);
            }
            finally
            {
                window.Close();
            }

            var listenersField = typeof(StreamlinkVlcStudio.App.Wpf.Controls.VideoSurfaceGridPanel)
                .GetField("childPlacementListeners", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(listenersField);
            var listeners = (System.Collections.IDictionary)listenersField!.GetValue(panel)!;
            Assert.Equal(0, listeners.Count);
        });
    }),
    ("video surface grid panel reports a finite desired size under unbounded measure", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var panel = new StreamlinkVlcStudio.App.Wpf.Controls.VideoSurfaceGridPanel
            {
                Rows = 2,
                Columns = 3
            };
            var child = new Border
            {
                Width = 120,
                Height = 80
            };
            Grid.SetRow(child, 1);
            Grid.SetColumn(child, 2);
            Grid.SetRowSpan(child, 2);
            Grid.SetColumnSpan(child, 3);
            panel.Children.Add(child);

            panel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));

            AssertNear(360, panel.DesiredSize.Width);
            AssertNear(160, panel.DesiredSize.Height);
        });
    }),
    ("video surface grid panel keeps aspect-ratio unused width away from the center split", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var panel = new StreamlinkVlcStudio.App.Wpf.Controls.VideoSurfaceGridPanel
            {
                Rows = 2,
                Columns = 2
            };
            var left = new Border();
            var right = new Border();

            Grid.SetRow(left, 0);
            Grid.SetColumn(left, 0);
            Grid.SetRowSpan(left, 2);
            Grid.SetRow(right, 0);
            Grid.SetColumn(right, 1);
            Grid.SetRowSpan(right, 2);
            StreamlinkVlcStudio.App.Wpf.Controls.VideoSurfaceGridPanel.SetAspectRatio(left, 16.0 / 9.0);
            StreamlinkVlcStudio.App.Wpf.Controls.VideoSurfaceGridPanel.SetAspectRatio(right, 16.0 / 9.0);

            panel.Children.Add(left);
            panel.Children.Add(right);

            panel.Measure(new System.Windows.Size(2200, 600));
            panel.Arrange(new System.Windows.Rect(0, 0, 2200, 600));

            var leftOrigin = left.TranslatePoint(new System.Windows.Point(0, 0), panel);
            var rightOrigin = right.TranslatePoint(new System.Windows.Point(0, 0), panel);

            AssertNear(leftOrigin.X + left.RenderSize.Width, rightOrigin.X);
            AssertNear(1100, rightOrigin.X);
            Assert.True(leftOrigin.X > 0);
            Assert.True(rightOrigin.X + right.RenderSize.Width < 2200);
        });
    }),
    ("video surface grid panel keeps aspect-ratio unused height away from the center split", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var panel = new StreamlinkVlcStudio.App.Wpf.Controls.VideoSurfaceGridPanel
            {
                Rows = 2,
                Columns = 2
            };
            var top = new Border();
            var bottom = new Border();

            Grid.SetRow(top, 0);
            Grid.SetColumn(top, 0);
            Grid.SetColumnSpan(top, 2);
            Grid.SetRow(bottom, 1);
            Grid.SetColumn(bottom, 0);
            Grid.SetColumnSpan(bottom, 2);
            StreamlinkVlcStudio.App.Wpf.Controls.VideoSurfaceGridPanel.SetAspectRatio(top, 16.0 / 9.0);
            StreamlinkVlcStudio.App.Wpf.Controls.VideoSurfaceGridPanel.SetAspectRatio(bottom, 16.0 / 9.0);

            panel.Children.Add(top);
            panel.Children.Add(bottom);

            panel.Measure(new System.Windows.Size(800, 1000));
            panel.Arrange(new System.Windows.Rect(0, 0, 800, 1000));

            var topOrigin = top.TranslatePoint(new System.Windows.Point(0, 0), panel);
            var bottomOrigin = bottom.TranslatePoint(new System.Windows.Point(0, 0), panel);

            AssertNear(topOrigin.Y + top.RenderSize.Height, bottomOrigin.Y);
            AssertNear(500, bottomOrigin.Y);
            Assert.True(topOrigin.Y > 0);
            Assert.True(bottomOrigin.Y + bottom.RenderSize.Height < 1000);
        });
    }),
    ("video surface grid panel keeps 3x4 aspect-ratio rows contiguous", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var panel = new StreamlinkVlcStudio.App.Wpf.Controls.VideoSurfaceGridPanel
            {
                Rows = 3,
                Columns = 4
            };
            var tiles = new Border[3, 4];

            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    var tile = new Border();
                    Grid.SetRow(tile, row);
                    Grid.SetColumn(tile, column);
                    StreamlinkVlcStudio.App.Wpf.Controls.VideoSurfaceGridPanel.SetAspectRatio(tile, 16.0 / 9.0);
                    panel.Children.Add(tile);
                    tiles[row, column] = tile;
                }
            }

            panel.Measure(new System.Windows.Size(1920, 938));
            panel.Arrange(new System.Windows.Rect(0, 0, 1920, 938));

            for (var column = 0; column < 4; column++)
            {
                var top = tiles[0, column];
                var middle = tiles[1, column];
                var bottom = tiles[2, column];
                var topOrigin = top.TranslatePoint(new System.Windows.Point(0, 0), panel);
                var middleOrigin = middle.TranslatePoint(new System.Windows.Point(0, 0), panel);
                var bottomOrigin = bottom.TranslatePoint(new System.Windows.Point(0, 0), panel);

                AssertNear(topOrigin.Y + top.RenderSize.Height, middleOrigin.Y);
                AssertNear(middleOrigin.Y + middle.RenderSize.Height, bottomOrigin.Y);
                Assert.True(topOrigin.Y > 0);
                Assert.True(bottomOrigin.Y + bottom.RenderSize.Height < 938);
            }
        });
    }),
    ("video surface native host avoids system-colored static background", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var parentHandle = NativeWindowTest.CreateHiddenParentWindow();
            var surface = new StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface();
            var buildWindow = typeof(StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface).GetMethod(
                "BuildWindowCore",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var destroyWindow = typeof(StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface).GetMethod(
                "DestroyWindowCore",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(buildWindow);
            Assert.NotNull(destroyWindow);

            IntPtr surfaceHandle = IntPtr.Zero;
            try
            {
                var surfaceHandleRef = (HandleRef)buildWindow!.Invoke(surface, [new HandleRef(null, parentHandle)])!;
                surfaceHandle = surfaceHandleRef.Handle;

                Assert.True(surfaceHandle != IntPtr.Zero);
                Assert.Equal("StreamlinkVlcStudioVideoSurface", NativeWindowTest.GetClassName(surfaceHandle));
            }
            finally
            {
                if (surfaceHandle != IntPtr.Zero)
                {
                    destroyWindow!.Invoke(surface, [new HandleRef(surface, surfaceHandle)]);
                }

                NativeWindowTest.DestroyWindow(parentHandle);
            }
        });
    }),
    ("video surface native host raises parent drag mouse events without child subclassing", () =>
    {
        return TestSta.RunAsync(() =>
        {
            const int wmLeftButtonDown = 0x0201;
            const int wmLeftButtonUp = 0x0202;
            const int wmMouseMove = 0x0200;
            var surface = new StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface();
            var window = new System.Windows.Window
            {
                Content = surface,
                Width = 120,
                Height = 90,
                WindowStyle = System.Windows.WindowStyle.None,
                ShowInTaskbar = false
            };

            var nativeEvents = new List<string>();
            surface.NativeMouseLeftButtonDown += (_, e) => nativeEvents.Add($"down:{e.ScreenX},{e.ScreenY}");
            surface.NativeMouseMoved += (_, e) => nativeEvents.Add($"move:{e.ScreenX},{e.ScreenY}");
            surface.NativeMouseLeftButtonUp += (_, e) => nativeEvents.Add($"up:{e.ScreenX},{e.ScreenY}");

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var surfaceHandle = surface.Handle;
                Assert.True(surfaceHandle != IntPtr.Zero);
                NativeWindowTest.SendMessage(surfaceHandle, wmLeftButtonDown, IntPtr.Zero, NativeWindowTest.MakeMouseLParam(10, 20));
                NativeWindowTest.SendMessage(surfaceHandle, wmMouseMove, IntPtr.Zero, NativeWindowTest.MakeMouseLParam(30, 40));
                NativeWindowTest.SendMessage(surfaceHandle, wmLeftButtonUp, IntPtr.Zero, NativeWindowTest.MakeMouseLParam(50, 60));

                var topLeft = surface.PointToScreen(new System.Windows.Point(0, 0));
                Assert.SequenceEqual(
                    new[]
                    {
                        $"down:{(int)Math.Round(topLeft.X) + 10},{(int)Math.Round(topLeft.Y) + 20}",
                        $"move:{(int)Math.Round(topLeft.X) + 30},{(int)Math.Round(topLeft.Y) + 40}",
                        $"up:{(int)Math.Round(topLeft.X) + 50},{(int)Math.Round(topLeft.Y) + 60}"
                    },
                    nativeEvents);

                nativeEvents.Clear();
                var childHandle = NativeWindowTest.CreateVisibleChildWindow(surfaceHandle);
                try
                {
                    surface.Dispatcher.Invoke(
                        () => { },
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    var surfaceBounds = NativeWindowTest.GetWindowBounds(surfaceHandle);
                    var childBounds = NativeWindowTest.GetWindowBounds(childHandle);
                    Assert.Equal(surfaceBounds.Width, childBounds.Width);
                    Assert.Equal(surfaceBounds.Height, childBounds.Height);

                    NativeWindowTest.SendMessage(childHandle, wmLeftButtonDown, IntPtr.Zero, NativeWindowTest.MakeMouseLParam(11, 21));
                    NativeWindowTest.SendMessage(childHandle, wmMouseMove, IntPtr.Zero, NativeWindowTest.MakeMouseLParam(31, 41));
                    NativeWindowTest.SendMessage(childHandle, wmLeftButtonUp, IntPtr.Zero, NativeWindowTest.MakeMouseLParam(51, 61));

                    Assert.Equal(0, nativeEvents.Count);
                }
                finally
                {
                    NativeWindowTest.DestroyWindow(childHandle);
                }
            }
            finally
            {
                if (NativeWindowTest.GetCapture() == surface.Handle)
                {
                    NativeWindowTest.ReleaseCapture();
                }

                window.Close();
            }
        });
    }),
    ("detached window activation requests tab selection", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var window = new DetachedVideoWindow(tab);
            var activationCount = 0;
            window.TabActivated += _ => activationCount++;

            var onActivated = typeof(System.Windows.Window).GetMethod(
                "OnActivated",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(onActivated);
            onActivated!.Invoke(window, [EventArgs.Empty]);

            Assert.Equal(1, activationCount);
        });
    }),
    ("detached picture-in-picture windows show in the taskbar", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var first = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var second = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var firstWindow = new DetachedVideoWindow(first);
            var secondWindow = new DetachedVideoWindow(second);

            try
            {
                Assert.True(firstWindow.ShowInTaskbar);
                Assert.True(secondWindow.ShowInTaskbar);
            }
            finally
            {
                firstWindow.CloseForTabDisposal();
                secondWindow.CloseForTabDisposal();
            }
        });
    }),
    ("detached picture-in-picture stays visible when main window is minimized", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());

            viewModel.Tabs.Add(tab);
            viewModel.SelectedTab = tab;

            var window = new MainWindow
            {
                Width = 900,
                Height = 560,
                Topmost = true,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);

            var detachTab = typeof(MainWindow).GetMethod(
                "DetachTabToPictureInPicture",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachedWindowsField = typeof(MainWindow).GetField(
                "detachedWindows",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(detachTab);
            Assert.NotNull(detachedWindowsField);

            DetachedVideoWindow? detachedWindow = null;
            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                SetMainWindowHandle(window);

                var detachPoint = window.PointToScreen(new System.Windows.Point(
                    Math.Min(120, Math.Max(0, window.ActualWidth / 2)),
                    Math.Min(120, Math.Max(0, window.ActualHeight / 2))));
                detachTab!.Invoke(window, [tab, detachPoint, false]);

                var detachedWindows = (IDictionary<StreamTabViewModel, DetachedVideoWindow>)detachedWindowsField!.GetValue(window)!;
                Assert.True(detachedWindows.TryGetValue(tab, out detachedWindow));
                Assert.Equal(null, detachedWindow!.Owner);
                Assert.Equal(System.Windows.WindowState.Normal, detachedWindow.WindowState);
                Assert.Equal(false, detachedWindow.IsTopBarShown);

                window.WindowState = System.Windows.WindowState.Minimized;
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                Assert.Equal(System.Windows.WindowState.Minimized, window.WindowState);
                Assert.True(detachedWindow.IsVisible);
                Assert.Equal(System.Windows.WindowState.Normal, detachedWindow.WindowState);
            }
            finally
            {
                detachedWindow?.CloseForTabDisposal();
                if (window.WindowState == System.Windows.WindowState.Minimized)
                {
                    window.WindowState = System.Windows.WindowState.Normal;
                }

                window.Close();
            }
        });
    }),
    ("detached window accepts additional tabs into existing multi-stream grid", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var first = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var second = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var window = new DetachedVideoWindow(first);

            try
            {
                Assert.True(window.TryAddTabs([second], second));

                Assert.Equal(2, window.TabCount);
                Assert.SequenceEqual(new[] { first, second }, window.Tabs);
                Assert.Equal(second, window.ActiveTab);
                Assert.Equal("Multi-stream (2)", window.HeaderTitle);
                Assert.Equal(2, window.VideoItems.Count);
                Assert.Equal(false, window.TryAddTabs([second], second));
            }
            finally
            {
                window.CloseForTabDisposal();
            }
        });
    }),
    ("detaching explicit multi-stream group to picture-in-picture keeps tabs merged", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var first = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var second = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var third = TestViewModels.CreateTab(
                StreamInputParser.Parse("xqc", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var tabs = new[] { first, second, third };
            foreach (var tab in tabs)
            {
                viewModel.Tabs.Add(tab);
            }

            viewModel.SelectedTab = second;
            Assert.True(viewModel.TryMergeTabsIntoMultiView([second], first, second));
            Assert.True(viewModel.TryMergeTabsIntoMultiView([third], second, third));
            viewModel.SelectedTab = second;
            Assert.SequenceEqual(tabs, viewModel.GetPictureInPictureDragTabs(second).ToArray());

            var mainWindow = new MainWindow();
            var viewModelField = typeof(MainWindow).GetField(
                "viewModel",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachTab = typeof(MainWindow).GetMethod(
                "DetachTabToPictureInPicture",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachedWindowsField = typeof(MainWindow).GetField(
                "detachedWindows",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(viewModelField);
            Assert.NotNull(detachTab);
            Assert.NotNull(detachedWindowsField);
            viewModelField!.SetValue(mainWindow, viewModel);

            DetachedVideoWindow? detachedWindow = null;
            try
            {
                detachTab!.Invoke(mainWindow, [second, new System.Windows.Point(420, 380), false]);

                var detachedWindows = (IDictionary<StreamTabViewModel, DetachedVideoWindow>)detachedWindowsField!.GetValue(mainWindow)!;
                foreach (var tab in tabs)
                {
                    Assert.True(tab.IsDetached);
                    Assert.True(tab.IsMergedTabGroupMember);
                    Assert.True(detachedWindows.TryGetValue(tab, out var mappedWindow));
                    detachedWindow ??= mappedWindow;
                    Assert.Equal(detachedWindow, mappedWindow);
                }

                Assert.True(first.IsFirstMergedTabGroupMember);
                Assert.Equal(false, first.IsLastMergedTabGroupMember);
                Assert.Equal(false, second.IsFirstMergedTabGroupMember);
                Assert.Equal(false, second.IsLastMergedTabGroupMember);
                Assert.Equal(false, third.IsFirstMergedTabGroupMember);
                Assert.True(third.IsLastMergedTabGroupMember);
                Assert.NotNull(detachedWindow);
                Assert.SequenceEqual(tabs, detachedWindow!.Tabs);
                Assert.SequenceEqual(tabs, viewModel.GetPictureInPictureDragTabs(third).ToArray());
            }
            finally
            {
                detachedWindow?.CloseForTabDisposal();
            }
        });
    }),
    ("detaching separate multi-stream tabs creates separate picture-in-picture windows", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings
            {
                MultiStreamEnabled = true,
                PictureInPictureWindowLocation = new PictureInPictureWindowLocation(260, 280, 520, 340)
            };
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var first = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var second = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var third = TestViewModels.CreateTab(
                StreamInputParser.Parse("xqc", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            viewModel.Tabs.Add(first);
            viewModel.Tabs.Add(second);
            viewModel.Tabs.Add(third);
            viewModel.SelectedTab = second;

            var mainWindow = new MainWindow();
            SetMainWindowViewModel(mainWindow, viewModel);
            var detachTab = typeof(MainWindow).GetMethod(
                "DetachTabToPictureInPicture",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachedWindowsField = typeof(MainWindow).GetField(
                "detachedWindows",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(detachTab);
            Assert.NotNull(detachedWindowsField);

            DetachedVideoWindow? firstWindow = null;
            DetachedVideoWindow? secondWindow = null;
            try
            {
                detachTab!.Invoke(mainWindow, [second, new System.Windows.Point(420, 380), false]);
                var detachedWindows = (IDictionary<StreamTabViewModel, DetachedVideoWindow>)detachedWindowsField!.GetValue(mainWindow)!;
                Assert.True(detachedWindows.TryGetValue(second, out secondWindow));
                Assert.NotNull(secondWindow);
                Assert.True(second.IsDetached);
                Assert.Equal(false, first.IsDetached);
                Assert.Equal(false, third.IsDetached);
                Assert.Equal(1, secondWindow!.TabCount);
                Assert.SequenceEqual(new[] { second }, secondWindow.Tabs);
                var secondWindowBounds = secondWindow.GetRestorableBounds();

                viewModel.SelectedTab = first;
                detachTab.Invoke(mainWindow, [first, new System.Windows.Point(760, 420), false]);
                Assert.True(detachedWindows.TryGetValue(first, out firstWindow));
                Assert.NotNull(firstWindow);
                Assert.Equal(false, ReferenceEquals(firstWindow, secondWindow));
                Assert.Equal(2, detachedWindows.Values.Distinct().Count());
                Assert.True(first.IsDetached);
                Assert.Equal(false, third.IsDetached);
                Assert.Equal(1, firstWindow!.TabCount);
                Assert.SequenceEqual(new[] { first }, firstWindow.Tabs);
                var firstWindowBounds = firstWindow.GetRestorableBounds();
                Assert.True(
                    Math.Abs(firstWindowBounds.Left - secondWindowBounds.Left) > 1 ||
                    Math.Abs(firstWindowBounds.Top - secondWindowBounds.Top) > 1,
                    "Expected the second PiP window to avoid reopening directly over the existing PiP window.");
            }
            finally
            {
                var windowsToClose = new HashSet<DetachedVideoWindow>();
                if (firstWindow is not null)
                {
                    windowsToClose.Add(firstWindow);
                }

                if (secondWindow is not null)
                {
                    windowsToClose.Add(secondWindow);
                }

                foreach (var window in windowsToClose)
                {
                    window.CloseForTabDisposal();
                }
            }
        });
    }),
    ("picture-in-picture VLC plugin chat follows three-stream multiview visibility", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var streamlink = new FakeStreamlinkService();
            var pipeNames = new[]
            {
                $"svs_pip_first_{Guid.NewGuid():N}",
                $"svs_pip_second_{Guid.NewGuid():N}",
                $"svs_pip_third_{Guid.NewGuid():N}"
            };
            var engineIndex = -1;
            var playbackFactory = new FakePlaybackEngineFactory(() =>
            {
                var index = Math.Clamp(Interlocked.Increment(ref engineIndex), 0, pipeNames.Length - 1);
                return new FakePlaybackEngine
                {
                    UsesNativeOverlayOverride = true,
                    NativeOverlayPipeNameOverride = pipeNames[index]
                };
            });
            var logger = new MemoryLogger();
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\Program Files\VideoLAN\VLC",
                MultiStreamEnabled = true
            };
            settings.Chat.ConnectAutomatically = false;
            settings.Chat.Layout = ChatLayout.Overlay;
            settings.Chat.VlcOverlayDirectory = @"C:\Tools\vlc-overlay";

            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var tabs = new[]
            {
                TestViewModels.CreateTab(
                    StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                    "best",
                    streamlink,
                    playbackFactory,
                    new FakeChatClientFactory(),
                    logger,
                    action => action()),
                TestViewModels.CreateTab(
                    StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                    "best",
                    streamlink,
                    playbackFactory,
                    new FakeChatClientFactory(),
                    logger,
                    action => action()),
                TestViewModels.CreateTab(
                    StreamInputParser.Parse("xqc", PlatformKind.Twitch),
                    "best",
                    streamlink,
                    playbackFactory,
                    new FakeChatClientFactory(),
                    logger,
                    action => action()),
            };

            for (var index = 0; index < tabs.Length; index++)
            {
                tabs[index].SetVideoHandle(new IntPtr(1234 + index));
                viewModel.Tabs.Add(tabs[index]);
            }

            viewModel.SelectedTab = tabs[1];
            foreach (var tab in tabs)
            {
                await tab.StartAsync(settings);
            }

            Assert.True(tabs.All(tab => tab.IsChatVisible));
            viewModel.SetPictureInPictureTabGroup(tabs);

            var mainWindow = new MainWindow();
            var viewModelField = typeof(MainWindow).GetField(
                "viewModel",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachTab = typeof(MainWindow).GetMethod(
                "DetachTabToPictureInPicture",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachedWindowsField = typeof(MainWindow).GetField(
                "detachedWindows",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(viewModelField);
            Assert.NotNull(detachTab);
            Assert.NotNull(detachedWindowsField);
            viewModelField!.SetValue(mainWindow, viewModel);

            DetachedVideoWindow? detachedWindow = null;
            try
            {
                byte[] initialBlankMessage;
                await using (var initialBlankServer = CreateNativeOverlayPipeServer(pipeNames[1]))
                {
                    var initialBlankTask = ReadNativeOverlayPipeMessageAsync(initialBlankServer);
                    detachTab!.Invoke(mainWindow, [tabs[1], new System.Windows.Point(420, 380), false]);

                    var detachedWindows = (IDictionary<StreamTabViewModel, DetachedVideoWindow>)detachedWindowsField!.GetValue(mainWindow)!;
                    Assert.True(detachedWindows.TryGetValue(tabs[1], out detachedWindow));
                    Assert.NotNull(detachedWindow);
                    Assert.SequenceEqual(tabs, detachedWindow!.VisibleTabs);

                    await TestWait.UntilAsync(
                        () => streamlink.StartCount == 3 &&
                            playbackFactory.EnableNativeOverlayRequests.SequenceEqual(new[] { true, true, true }) &&
                            tabs.All(tab => !tab.IsChatVisible),
                        TimeSpan.FromSeconds(2));
                    initialBlankMessage = await initialBlankTask.WaitAsync(TimeSpan.FromSeconds(1));
                }

                AssertNativeOverlayBlankFrame(initialBlankMessage);

                await using (var rebindBlankServer = CreateNativeOverlayPipeServer(pipeNames[1]))
                {
                    var rebindBlankTask = ReadNativeOverlayPipeMessageAsync(rebindBlankServer);
                    playbackFactory.Engines[1].RaiseVideoOutputRebound();
                    var rebindBlankMessage = await rebindBlankTask.WaitAsync(TimeSpan.FromSeconds(1));
                    AssertNativeOverlayBlankFrame(rebindBlankMessage);
                }

                detachedWindow.EnterStreamFullscreen();

                await TestWait.UntilAsync(
                    () => detachedWindow.VisibleTabs.Count == 1 &&
                        detachedWindow.VisibleTabs[0] == tabs[1] &&
                        tabs.All(tab => tab.IsChatVisible),
                    TimeSpan.FromSeconds(2));

                detachedWindow.ExitStreamFullscreen();

                await TestWait.UntilAsync(
                    () => detachedWindow.VisibleTabs.SequenceEqual(tabs) &&
                        tabs.All(tab => !tab.IsChatVisible),
                    TimeSpan.FromSeconds(2));
            }
            finally
            {
                detachedWindow?.CloseForTabDisposal();
                await viewModel.DisposeAsync();
            }
        });
    }),
    ("dragging main tab onto picture-in-picture tab adds it to existing window", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var pipTab = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var newTab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            viewModel.Tabs.Add(pipTab);
            viewModel.Tabs.Add(newTab);

            var mainWindow = new MainWindow();
            var viewModelField = typeof(MainWindow).GetField(
                "viewModel",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachTab = typeof(MainWindow).GetMethod(
                "DetachTabToPictureInPicture",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachedWindowsField = typeof(MainWindow).GetField(
                "detachedWindows",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(viewModelField);
            Assert.NotNull(detachTab);
            Assert.NotNull(detachedWindowsField);
            viewModelField!.SetValue(mainWindow, viewModel);

            DetachedVideoWindow? detachedWindow = null;
            try
            {
                detachTab!.Invoke(mainWindow, [pipTab, new System.Windows.Point(420, 380), false]);

                var detachedWindows = (IDictionary<StreamTabViewModel, DetachedVideoWindow>)detachedWindowsField!.GetValue(mainWindow)!;
                Assert.True(detachedWindows.TryGetValue(pipTab, out detachedWindow), "Expected setup detach to create a PiP window for the target tab.");
                Assert.NotNull(detachedWindow);
                Assert.True(pipTab.IsDetached);
                Assert.Equal(false, newTab.IsDetached);

                Assert.True(mainWindow.AddTabsToPictureInPictureWindow(detachedWindow!, [newTab], newTab));

                Assert.True(detachedWindows.TryGetValue(newTab, out var newTabWindow));
                Assert.Equal(detachedWindow, newTabWindow);
                Assert.Equal(1, detachedWindows.Values.Distinct().Count());
                Assert.Equal(2, detachedWindow!.TabCount);
                Assert.SequenceEqual(new[] { pipTab, newTab }, detachedWindow.Tabs);
                Assert.True(newTab.IsDetached);
                Assert.Equal(newTab, detachedWindow.ActiveTab);
                Assert.Equal(newTab, viewModel.SelectedTab);
                Assert.Equal(false, viewModel.VideoTabs.Contains(newTab));
                Assert.True(pipTab.IsMergedTabGroupMember);
                Assert.True(pipTab.IsFirstMergedTabGroupMember);
                Assert.True(newTab.IsMergedTabGroupMember);
                Assert.True(newTab.IsLastMergedTabGroupMember);
                Assert.SequenceEqual(new[] { pipTab, newTab }, viewModel.GetPictureInPictureDragTabs(pipTab).ToArray());
            }
            finally
            {
                detachedWindow?.CloseForTabDisposal();
            }
        });
    }),
    ("reordering a picture-in-picture tab keeps its detached window open", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var pipTab = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var otherTab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            viewModel.Tabs.Add(otherTab);
            viewModel.Tabs.Add(pipTab);
            viewModel.SelectedTab = pipTab;

            var mainWindow = new MainWindow
            {
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(mainWindow);
            SetMainWindowViewModel(mainWindow, viewModel);
            var detachTab = typeof(MainWindow).GetMethod(
                "DetachTabToPictureInPicture",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabsChanged = typeof(MainWindow).GetMethod(
                "ViewModelTabsCollectionChanged",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachedWindowsField = typeof(MainWindow).GetField(
                "detachedWindows",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(detachTab);
            Assert.NotNull(tabsChanged);
            Assert.NotNull(detachedWindowsField);

            var tabsChangedHandler = (NotifyCollectionChangedEventHandler)Delegate.CreateDelegate(
                typeof(NotifyCollectionChangedEventHandler),
                mainWindow,
                tabsChanged!);
            viewModel.Tabs.CollectionChanged += tabsChangedHandler;

            DetachedVideoWindow? detachedWindow = null;
            try
            {
                detachTab!.Invoke(mainWindow, [pipTab, new System.Windows.Point(420, 380), false]);

                var detachedWindows = (IDictionary<StreamTabViewModel, DetachedVideoWindow>)detachedWindowsField!.GetValue(mainWindow)!;
                Assert.True(detachedWindows.TryGetValue(pipTab, out detachedWindow));
                Assert.NotNull(detachedWindow);
                Assert.True(detachedWindow!.IsVisible);

                Assert.True(viewModel.TryReorderTabStripTabs(
                    [pipTab],
                    otherTab,
                    insertAfterTarget: false,
                    selectedDraggedTab: pipTab));

                Assert.SequenceEqual(new[] { pipTab, otherTab }, viewModel.Tabs);
                Assert.True(detachedWindows.TryGetValue(pipTab, out var windowAfterReorder));
                Assert.Equal(detachedWindow, windowAfterReorder);
                Assert.True(detachedWindow.IsVisible);
                Assert.Equal(false, detachedWindow.IsClosing);
                Assert.Equal(1, detachedWindow.TabCount);
                Assert.SequenceEqual(new[] { pipTab }, detachedWindow.Tabs);
                Assert.True(pipTab.IsDetached);
            }
            finally
            {
                viewModel.Tabs.CollectionChanged -= tabsChangedHandler;
                detachedWindow?.CloseForTabDisposal();
                mainWindow.Close();
            }
        });
    }),
    ("ctrl releasing dragged tab over picture-in-picture tab uses tab-strip drop target", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var pipTab = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var newTab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            viewModel.Tabs.Add(pipTab);
            viewModel.Tabs.Add(newTab);

            var mainWindow = new MainWindow
            {
                Left = 120,
                Top = 120,
                Width = 1320,
                Height = 820,
                ShowActivated = false
            };
            var loadedMethod = typeof(MainWindow).GetMethod(
                "MainWindowLoaded",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var viewModelField = typeof(MainWindow).GetField(
                "viewModel",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragTabField = typeof(MainWindow).GetField(
                "tabDetachDragTab",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragSourceField = typeof(MainWindow).GetField(
                "tabDetachDragSource",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragStartPointField = typeof(MainWindow).GetField(
                "tabDetachDragStartPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragStartScreenPointField = typeof(MainWindow).GetField(
                "tabDetachDragStartScreenPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var closeConfirmedField = typeof(MainWindow).GetField(
                "closeConfirmed",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachTab = typeof(MainWindow).GetMethod(
                "DetachTabToPictureInPicture",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var completeDrag = typeof(MainWindow).GetMethod(
                "TryCompleteTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var hasExceededDragDistance = typeof(MainWindow).GetMethod(
                "HasExceededTabDetachDragDistance",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tryGetTabAtScreenPoint = typeof(MainWindow).GetMethod(
                "TryGetTabAtTabStripScreenPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var getDragTabs = typeof(MainWindow).GetMethod(
                "GetPictureInPictureDragTabs",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var getDropTarget = typeof(MainWindow).GetMethod(
                "GetPictureInPictureDropTarget",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachedWindowsField = typeof(MainWindow).GetField(
                "detachedWindows",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var nativePointType = typeof(MainWindow).GetNestedType(
                "NativePoint",
                BindingFlags.NonPublic);
            Assert.NotNull(loadedMethod);
            Assert.NotNull(viewModelField);
            Assert.NotNull(tabDetachDragTabField);
            Assert.NotNull(tabDetachDragSourceField);
            Assert.NotNull(tabDetachDragStartPointField);
            Assert.NotNull(tabDetachDragStartScreenPointField);
            Assert.NotNull(closeConfirmedField);
            Assert.NotNull(detachTab);
            Assert.NotNull(completeDrag);
            Assert.NotNull(hasExceededDragDistance);
            Assert.NotNull(tryGetTabAtScreenPoint);
            Assert.NotNull(getDragTabs);
            Assert.NotNull(getDropTarget);
            Assert.NotNull(detachedWindowsField);
            Assert.NotNull(nativePointType);

            var loadedHandler = (System.Windows.RoutedEventHandler)Delegate.CreateDelegate(
                typeof(System.Windows.RoutedEventHandler),
                mainWindow,
                loadedMethod!);
            mainWindow.Loaded -= loadedHandler;
            viewModelField!.SetValue(mainWindow, viewModel);
            mainWindow.DataContext = viewModel;
            SetMainWindowControlModifier(mainWindow, pressed: true);

            DetachedVideoWindow? detachedWindow = null;
            try
            {
                mainWindow.Show();
                mainWindow.UpdateLayout();
                mainWindow.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                detachTab!.Invoke(mainWindow, [pipTab, new System.Windows.Point(420, 380), false]);
                mainWindow.UpdateLayout();
                mainWindow.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                var detachedWindows = (IDictionary<StreamTabViewModel, DetachedVideoWindow>)detachedWindowsField!.GetValue(mainWindow)!;
                Assert.True(detachedWindows.TryGetValue(pipTab, out detachedWindow));
                Assert.NotNull(detachedWindow);

                var newTabItem = GetGeneratedTabStripListBoxItem(mainWindow, viewModel, newTab);
                var pipTabItem = GetGeneratedTabStripListBoxItem(mainWindow, viewModel, pipTab);

                var startPoint = newTabItem.TransformToAncestor(mainWindow).Transform(new System.Windows.Point(
                    newTabItem.ActualWidth / 2,
                    newTabItem.ActualHeight / 2));
                var startScreenPoint = newTabItem.PointToScreen(new System.Windows.Point(
                    newTabItem.ActualWidth / 2,
                    newTabItem.ActualHeight / 2));
                var nativeStartPoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(startScreenPoint.X), (int)Math.Round(startScreenPoint.Y)]);
                Assert.NotNull(nativeStartPoint);
                tabDetachDragSourceField!.SetValue(mainWindow, newTabItem);
                tabDetachDragTabField!.SetValue(mainWindow, newTab);
                tabDetachDragStartPointField!.SetValue(mainWindow, startPoint);
                tabDetachDragStartScreenPointField!.SetValue(mainWindow, nativeStartPoint);
                Assert.Equal(newTab, tabDetachDragTabField!.GetValue(mainWindow));

                var pipScreenPoint = pipTabItem.PointToScreen(new System.Windows.Point(
                    pipTabItem.ActualWidth / 2,
                    pipTabItem.ActualHeight / 2));
                var nativePoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(pipScreenPoint.X), (int)Math.Round(pipScreenPoint.Y)]);
                Assert.NotNull(nativePoint);

                var dragDistanceExceeded = (bool)hasExceededDragDistance!.Invoke(mainWindow, [nativePoint])!;
                Assert.True(dragDistanceExceeded, "Expected drag from normal tab to PiP tab to exceed the system drag threshold.");
                var targetArgs = new object?[] { nativePoint, null };
                var foundTargetTab = (bool)tryGetTabAtScreenPoint!.Invoke(mainWindow, targetArgs)!;
                Assert.True(foundTargetTab, "Expected PiP tab strip item under the simulated drop point.");
                Assert.Equal(pipTab, targetArgs[1]);

                viewModelField.SetValue(mainWindow, viewModel);
                mainWindow.DataContext = viewModel;
                var dragTabs = (StreamTabViewModel[])getDragTabs!.Invoke(mainWindow, [newTab])!;
                Assert.SequenceEqual(new[] { newTab }, dragTabs);
                var dropTarget = (DetachedVideoWindow?)getDropTarget!.Invoke(mainWindow, [nativePoint, dragTabs]);
                Assert.Equal(detachedWindow, dropTarget);
                Assert.Equal(newTab, tabDetachDragTabField!.GetValue(mainWindow));
                var completed = (bool)completeDrag!.Invoke(mainWindow, [nativePoint])!;

                Assert.True(completed, "Expected mouse-up over PiP tab to complete tab-to-PiP transfer.");
                Assert.True(detachedWindows.TryGetValue(newTab, out var newTabWindow), "Expected dragged tab to be mapped to the PiP window after drop.");
                Assert.Equal(detachedWindow, newTabWindow);
                Assert.Equal(2, detachedWindow!.TabCount);
                Assert.True(newTab.IsDetached, "Expected dragged tab to be marked detached after drop.");
                Assert.Equal(newTab, detachedWindow.ActiveTab);
            }
            finally
            {
                detachedWindow?.CloseForTabDisposal();
                closeConfirmedField!.SetValue(mainWindow, true);
                mainWindow.Close();
            }
        });
    }),
    ("picture-in-picture top bar button hides chrome and video menu restores it", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                new FakeStreamlinkService(),
                new FakePlaybackEngineFactory(),
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action());
            var window = new DetachedVideoWindow(tab)
            {
                Left = 240,
                Top = 260,
                Width = 740,
                Height = 450.25
            };
            var changes = new List<(StreamTabViewModel Tab, bool ShowTopBar)>();
            window.TopBarVisibilityChanged += (changedTab, showTopBar) => changes.Add((changedTab, showTopBar));

            try
            {
                window.Show();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var hideButton = (System.Windows.Controls.Button)window.FindName("HideTopBarButton");
                var titleBar = (System.Windows.FrameworkElement)window.FindName("TitleBar");
                var bottomResizeGrip = (Grid)window.FindName("BottomResizeGrip");
                var videoHost = (System.Windows.FrameworkElement)window.FindName("VideoHost");
                var titleBarRow = (RowDefinition)typeof(DetachedVideoWindow).GetField(
                    "TitleBarRow",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(window)!;
                var bottomResizeGripRow = (RowDefinition)typeof(DetachedVideoWindow).GetField(
                    "BottomResizeGripRow",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(window)!;
                var showTopBarMenuItem = (System.Windows.Controls.MenuItem)window.FindName("ShowTopBarMenuItem");
                var videoContextMenu = (System.Windows.Controls.ContextMenu)window.FindName("VideoContextMenu");
                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
                Assert.NotNull(chrome);
                var shownWindowHeight = window.ActualHeight;
                var shownVideoWidth = videoHost.ActualWidth;
                var shownVideoHeight = videoHost.ActualHeight;

                hideButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent, hideButton));
                window.UpdateLayout();

                Assert.Equal(false, window.IsTopBarShown);
                Assert.Equal(System.Windows.Visibility.Collapsed, titleBar.Visibility);
                Assert.Equal(System.Windows.Visibility.Collapsed, bottomResizeGrip.Visibility);
                Assert.Equal(0d, titleBarRow.Height.Value);
                Assert.Equal(0d, bottomResizeGripRow.Height.Value);
                Assert.Equal(System.Windows.ResizeMode.CanResize, window.ResizeMode);
                Assert.Equal(0d, chrome!.CaptionHeight);
                Assert.Equal(new System.Windows.Thickness(6), chrome.ResizeBorderThickness);
                Assert.SequenceEqual(new[] { (tab, false) }, changes.ToArray());
                AssertNear(shownWindowHeight - 34, window.ActualHeight, 1.0);
                AssertNear(shownVideoWidth, videoHost.ActualWidth, 1.0);
                AssertNear(shownVideoHeight, videoHost.ActualHeight, 1.0);
                AssertNear(window.ContentAspectRatio, videoHost.ActualWidth / videoHost.ActualHeight, 0.01);

                var surface = FindVisualDescendants<StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface>(window).Single();
                var hostTopLeft = videoHost.PointToScreen(new Point(0, 0));
                var hostBottomRight = videoHost.PointToScreen(new Point(videoHost.ActualWidth, videoHost.ActualHeight));
                var surfaceTopLeft = surface.PointToScreen(new Point(0, 0));
                var surfaceBottomRight = surface.PointToScreen(new Point(surface.ActualWidth, surface.ActualHeight));
                AssertNear(hostTopLeft.X, surfaceTopLeft.X, 1.0);
                AssertNear(hostTopLeft.Y, surfaceTopLeft.Y, 1.0);
                AssertNear(hostBottomRight.X, surfaceBottomRight.X, 1.0);
                AssertNear(hostBottomRight.Y, surfaceBottomRight.Y, 1.0);

                var updateVideoAspectRatio = typeof(StreamTabViewModel).GetMethod(
                    "UpdateVideoAspectRatio",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    types: [typeof(double)],
                    modifiers: null);
                Assert.NotNull(updateVideoAspectRatio);
                updateVideoAspectRatio!.Invoke(tab, [4.0 / 3.0]);
                window.UpdateLayout();
                AssertNear(4.0 / 3.0, videoHost.ActualWidth / videoHost.ActualHeight, 0.01);
                updateVideoAspectRatio.Invoke(tab, [16.0 / 9.0]);
                window.UpdateLayout();
                AssertNear(16.0 / 9.0, videoHost.ActualWidth / videoHost.ActualHeight, 0.01);

                var streamPoint = surface.PointToScreen(new System.Windows.Point(
                    surface.ActualWidth / 2,
                    surface.ActualHeight / 2));
                Assert.True(window.TryOpenVideoContextMenu(
                    (int)Math.Round(streamPoint.X),
                    (int)Math.Round(streamPoint.Y)));
                Assert.True(videoContextMenu.IsOpen);
                Assert.Equal(false, showTopBarMenuItem.IsChecked);

                showTopBarMenuItem.IsChecked = true;
                showTopBarMenuItem.RaiseEvent(new System.Windows.RoutedEventArgs(
                    System.Windows.Controls.MenuItem.ClickEvent,
                    showTopBarMenuItem));
                videoContextMenu.IsOpen = false;
                window.UpdateLayout();

                Assert.True(window.IsTopBarShown);
                Assert.Equal(System.Windows.Visibility.Visible, titleBar.Visibility);
                Assert.Equal(System.Windows.Visibility.Visible, bottomResizeGrip.Visibility);
                Assert.Equal(34d, titleBarRow.Height.Value);
                Assert.Equal(10d, bottomResizeGripRow.Height.Value);
                Assert.Equal(2, Grid.GetRowSpan(videoHost));
                Assert.Equal(Colors.Transparent, ((SolidColorBrush)bottomResizeGrip.Background).Color);
                var videoHostBottom = videoHost.PointToScreen(new Point(0, videoHost.ActualHeight)).Y;
                var resizeGripBottom = bottomResizeGrip.PointToScreen(new Point(0, bottomResizeGrip.ActualHeight)).Y;
                AssertNear(videoHostBottom, resizeGripBottom);
                Assert.Equal(34d, chrome.CaptionHeight);
                Assert.SequenceEqual(new[] { (tab, false), (tab, true) }, changes.ToArray());
                AssertNear(shownWindowHeight, window.ActualHeight, 1.0);
                AssertNear(window.ContentAspectRatio, videoHost.ActualWidth / videoHost.ActualHeight, 0.01);
            }
            finally
            {
                window.Close();
            }
        });
    }),
    ("picture-in-picture fullscreen exit preserves hidden top bar and resizing", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                new FakeStreamlinkService(),
                new FakePlaybackEngineFactory(),
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action());
            var window = new DetachedVideoWindow([tab], tab, showTopBar: false)
            {
                Left = 240,
                Top = 260,
                Width = 740,
                Height = 430
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                var titleBar = (System.Windows.FrameworkElement)window.FindName("TitleBar");
                var bottomResizeGrip = (System.Windows.FrameworkElement)window.FindName("BottomResizeGrip");
                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
                Assert.NotNull(chrome);

                window.EnterStreamFullscreen();
                Assert.True(window.IsStreamFullscreen);
                Assert.Equal(System.Windows.Visibility.Collapsed, titleBar.Visibility);
                Assert.Equal(System.Windows.Visibility.Collapsed, bottomResizeGrip.Visibility);
                Assert.Equal(System.Windows.ResizeMode.NoResize, window.ResizeMode);

                window.ExitStreamFullscreen();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.Equal(false, window.IsStreamFullscreen);
                Assert.Equal(false, window.IsTopBarShown);
                Assert.Equal(System.Windows.Visibility.Collapsed, titleBar.Visibility);
                Assert.Equal(System.Windows.Visibility.Collapsed, bottomResizeGrip.Visibility);
                Assert.Equal(System.Windows.ResizeMode.CanResize, window.ResizeMode);
                Assert.Equal(0d, chrome!.CaptionHeight);
                Assert.Equal(new System.Windows.Thickness(6), chrome.ResizeBorderThickness);
            }
            finally
            {
                window.Close();
            }
        });
    }),
    ("picture-in-picture top bar saves only the active stream immediately", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var settings = new AppSettings();
            var settingsService = new FakeSettingsService(settings);
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var viewModel = TestViewModels.CreateMain(
                settings,
                settingsService,
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var first = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var second = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Kick),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            viewModel.Tabs.Add(first);
            viewModel.Tabs.Add(second);
            var window = new DetachedVideoWindow([first, second], first);
            window.TopBarVisibilityChanged += (tab, showTopBar) =>
                viewModel.RememberStreamPictureInPictureTopBarVisibilityAsync(tab.Target, showTopBar)
                    .GetAwaiter()
                    .GetResult();

            try
            {
                window.Show();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var hideButton = (System.Windows.Controls.Button)window.FindName("HideTopBarButton");
                var showTopBarMenuItem = (System.Windows.Controls.MenuItem)window.FindName("ShowTopBarMenuItem");
                hideButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent, hideButton));

                Assert.Equal(1, settingsService.SaveCount);
                Assert.Equal(false, settings.StreamPictureInPictureTopBarVisibility[first.Target.StateKey]);
                Assert.Equal(false, settings.StreamPictureInPictureTopBarVisibility.ContainsKey(second.Target.StateKey));

                var secondSurface = FindVisualDescendants<StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface>(window)
                    .Single(surface => ReferenceEquals(surface.Tag, second));
                var secondPoint = secondSurface.PointToScreen(new System.Windows.Point(
                    secondSurface.ActualWidth / 2,
                    secondSurface.ActualHeight / 2));
                Assert.True(window.TryActivateTabFromScreenClick(
                    (int)Math.Round(secondPoint.X),
                    (int)Math.Round(secondPoint.Y)));
                Assert.Equal(second, window.ActiveTab);
                Assert.Equal(false, window.IsTopBarShown);

                showTopBarMenuItem.IsChecked = true;
                showTopBarMenuItem.RaiseEvent(new System.Windows.RoutedEventArgs(
                    System.Windows.Controls.MenuItem.ClickEvent,
                    showTopBarMenuItem));

                Assert.Equal(2, settingsService.SaveCount);
                Assert.Equal(false, settings.StreamPictureInPictureTopBarVisibility[first.Target.StateKey]);
                Assert.True(settings.StreamPictureInPictureTopBarVisibility[second.Target.StateKey]);

                var reopenedForFirst = new DetachedVideoWindow(
                    [first, second],
                    first,
                    settings.StreamPictureInPictureTopBarVisibility[first.Target.StateKey]);
                var reopenedForSecond = new DetachedVideoWindow(
                    [first, second],
                    second,
                    settings.StreamPictureInPictureTopBarVisibility[second.Target.StateKey]);
                Assert.Equal(false, reopenedForFirst.IsTopBarShown);
                Assert.True(reopenedForSecond.IsTopBarShown);
                reopenedForFirst.CloseForTabDisposal();
                reopenedForSecond.CloseForTabDisposal();
            }
            finally
            {
                window.Close();
            }
        });
    }),
    ("picture-in-picture drag candidate waits for threshold and cancels on mouse-up", () =>
    {
        var candidate = new PictureInPictureDragCandidate();
        candidate.Begin(100, 200);
        Assert.True(candidate.IsActive);
        Assert.Equal(false, candidate.TryStartDrag(103, 203, 4, 4));
        Assert.True(candidate.IsActive);
        Assert.True(candidate.Cancel());
        Assert.Equal(false, candidate.IsActive);
        Assert.Equal(false, candidate.TryStartDrag(110, 210, 4, 4));

        candidate.Begin(100, 200);
        Assert.True(candidate.TryStartDrag(104, 200, 4, 4));
        Assert.Equal(false, candidate.IsActive);
        Assert.Equal(false, candidate.Cancel());
        return Task.CompletedTask;
    }),
    ("picture-in-picture low-level drag candidate starts and cancels application-owned movement", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                new FakeStreamlinkService(),
                new FakePlaybackEngineFactory(),
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action());
            var mainWindow = new MainWindow();
            var detachedWindow = new DetachedVideoWindow(tab)
            {
                Left = 260,
                Top = 280,
                Width = 740,
                Height = 430
            };
            var detachedWindowsField = typeof(MainWindow).GetField(
                "detachedWindows",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(detachedWindowsField);

            try
            {
                detachedWindow.Show();
                detachedWindow.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                detachedWindow.UpdateLayout();
                var detachedWindows = (IDictionary<StreamTabViewModel, DetachedVideoWindow>)detachedWindowsField!.GetValue(mainWindow)!;
                detachedWindows[tab] = detachedWindow;

                var surface = FindVisualDescendants<StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface>(detachedWindow).Single();
                var point = surface.PointToScreen(new System.Windows.Point(
                    surface.ActualWidth / 2,
                    surface.ActualHeight / 2));
                var x = (int)Math.Round(point.X);
                var y = (int)Math.Round(point.Y);
                var overlayPoint = surface.PointToScreen(new System.Windows.Point(20, 20));
                var overlayX = (int)Math.Round(overlayPoint.X);
                var overlayY = (int)Math.Round(overlayPoint.Y);

                detachedWindow.IsPointerOverOverlayChat = (_, screenX, screenY) =>
                    screenX == overlayX && screenY == overlayY;
                Assert.True(!mainWindow.RouteLowLevelMouseHookEvent(new LowLevelMouseHookEvent(
                    LowLevelMouseHookEvent.WmLeftButtonDown,
                    overlayX,
                    overlayY,
                    0)), "Overlay click should not be consumed by the PiP drag route.");
                Assert.True(!detachedWindow.HasVideoMoveCandidate, "Overlay click armed a PiP move candidate.");
                Assert.True(!mainWindow.HasActiveLowLevelMouseMoveRoute(), "Overlay click enabled low-level move routing.");

                detachedWindow.IsPointerOverOverlayChat = null;
                Assert.Equal(false, mainWindow.RouteLowLevelMouseHookEvent(new LowLevelMouseHookEvent(
                    LowLevelMouseHookEvent.WmLeftButtonDown,
                    x,
                    y,
                    0)));
                Assert.True(detachedWindow.HasVideoMoveCandidate);
                Assert.True(mainWindow.HasActiveLowLevelMouseMoveRoute());

                Assert.Equal(false, mainWindow.RouteLowLevelMouseHookEvent(new LowLevelMouseHookEvent(
                    LowLevelMouseHookEvent.WmMouseMove,
                    x,
                    y,
                    0)));
                Assert.True(detachedWindow.HasVideoMoveCandidate);

                Assert.Equal(false, mainWindow.RouteLowLevelMouseHookEvent(new LowLevelMouseHookEvent(
                    LowLevelMouseHookEvent.WmMouseMove,
                    x + 100,
                    y,
                    0)));
                Assert.Equal(false, detachedWindow.HasVideoMoveCandidate);
                Assert.True(detachedWindow.HasActiveWindowMove);

                Assert.Equal(false, mainWindow.RouteLowLevelMouseHookEvent(new LowLevelMouseHookEvent(
                    LowLevelMouseHookEvent.WmLeftButtonUp,
                    x + 100,
                    y,
                    0)));
                Assert.Equal(false, detachedWindow.HasVideoMoveCandidate);
                Assert.Equal(false, detachedWindow.HasActiveWindowMove);
                Assert.Equal(false, mainWindow.HasActiveLowLevelMouseMoveRoute());

                Assert.True(mainWindow.RouteLowLevelMouseHookEvent(new LowLevelMouseHookEvent(
                    LowLevelMouseHookEvent.WmRightButtonDown,
                    x,
                    y,
                    0)));
                var videoContextMenu = (System.Windows.Controls.ContextMenu)detachedWindow.FindName("VideoContextMenu");
                Assert.True(videoContextMenu.IsOpen);
                videoContextMenu.IsOpen = false;
            }
            finally
            {
                detachedWindow.CloseForTabDisposal();
                typeof(MainWindow).GetField("closeConfirmed", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(mainWindow, true);
                mainWindow.Close();
            }
        });
    }),
    ("detached multi-stream wheel adjusts only hovered stream volume", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var first = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var second = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var window = new DetachedVideoWindow([first, second], first)
            {
                Left = 240,
                Top = 260,
                Width = 740,
                Height = 430
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var surfaces = FindVisualDescendants<StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface>(window).ToArray();
                var firstSurface = surfaces.First(surface => ReferenceEquals(surface.Tag, first));
                var secondSurface = surfaces.First(surface => ReferenceEquals(surface.Tag, second));
                Assert.True(firstSurface.ActualWidth > 0);
                Assert.True(secondSurface.ActualWidth > 0);

                var firstPoint = firstSurface.PointToScreen(new System.Windows.Point(firstSurface.ActualWidth / 2, firstSurface.ActualHeight / 2));
                var secondPoint = secondSurface.PointToScreen(new System.Windows.Point(secondSurface.ActualWidth / 2, secondSurface.ActualHeight / 2));

                Assert.True(window.TryRouteMouseWheel((int)Math.Round(firstPoint.X), (int)Math.Round(firstPoint.Y), Mouse.MouseWheelDeltaForOneLine));
                Assert.Equal(85, first.Volume);
                Assert.Equal(80, second.Volume);

                Assert.True(window.TryRouteMouseWheel((int)Math.Round(secondPoint.X), (int)Math.Round(secondPoint.Y), -Mouse.MouseWheelDeltaForOneLine));
                Assert.Equal(85, first.Volume);
                Assert.Equal(75, second.Volume);
            }
            finally
            {
                window.Close();
            }
        });
    }),
    ("detached multi-stream native left click activates clicked stream", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var first = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var second = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var window = new DetachedVideoWindow([first, second], first)
            {
                Left = 260,
                Top = 280,
                Width = 740,
                Height = 430
            };
            var activated = new List<StreamTabViewModel>();
            window.TabActivated += tab => activated.Add(tab);

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var surfaces = FindVisualDescendants<StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface>(window).ToArray();
                var secondSurface = surfaces.First(surface => ReferenceEquals(surface.Tag, second));
                Assert.True(secondSurface.ActualWidth > 0);
                Assert.True(secondSurface.ActualHeight > 0);

                var secondPoint = secondSurface.PointToScreen(new System.Windows.Point(
                    secondSurface.ActualWidth / 2,
                    secondSurface.ActualHeight / 2));

                Assert.True(window.TryActivateTabFromScreenClick(
                    (int)Math.Round(secondPoint.X),
                    (int)Math.Round(secondPoint.Y)));
                Assert.Equal(second, window.ActiveTab);
                Assert.Equal(second, activated.Last());
            }
            finally
            {
                window.Close();
            }
        });
    }),
    ("occluded picture-in-picture window ignores screen clicks landing on the window above it", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                new FakeStreamlinkService(),
                new FakePlaybackEngineFactory(),
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action());
            var window = new DetachedVideoWindow([tab], tab)
            {
                Left = 240,
                Top = 260,
                Width = 740,
                Height = 430,
                Topmost = false
            };
            // Fully covers the picture-in-picture window and, being topmost, sits above it.
            var cover = new System.Windows.Window
            {
                WindowStyle = System.Windows.WindowStyle.None,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                Background = System.Windows.Media.Brushes.Black,
                Left = 230,
                Top = 250,
                Width = 760,
                Height = 450
            };
            var activated = new List<StreamTabViewModel>();
            window.TabActivated += activatedTab => activated.Add(activatedTab);

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var surface = FindVisualDescendants<StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface>(window).Single();
                var bottomResizeGrip = (System.Windows.FrameworkElement)window.FindName("BottomResizeGrip");
                var videoContextMenu = (System.Windows.Controls.ContextMenu)window.FindName("VideoContextMenu");
                Assert.True(surface.ActualWidth > 0);
                Assert.True(bottomResizeGrip.ActualHeight > 0);

                var streamPoint = surface.PointToScreen(new System.Windows.Point(
                    surface.ActualWidth / 2,
                    surface.ActualHeight / 2));
                var streamX = (int)Math.Round(streamPoint.X);
                var streamY = (int)Math.Round(streamPoint.Y);
                var gripPoint = bottomResizeGrip.PointToScreen(new System.Windows.Point(
                    bottomResizeGrip.ActualWidth / 2,
                    bottomResizeGrip.ActualHeight / 2));

                cover.Show();
                cover.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                // Showing the window raises Activated, which reports the active tab on its own.
                var activationsBeforeOccludedClicks = activated.Count;

                // Every route below is fed by the app-wide mouse hook, so it also fires for clicks
                // that belong to the covering window. Acting on them dragged, resized, or raised the
                // buried picture-in-picture window out from under the user.
                Assert.True(
                    !window.TryActivateTabFromScreenClick(streamX, streamY),
                    "Occluded window claimed a tab activation click.");
                Assert.True(
                    !window.TryBeginVideoMoveFromScreenClick(streamX, streamY),
                    "Occluded window started a window move from a click it does not own.");
                Assert.True(
                    !window.HasVideoMoveCandidate,
                    "Occluded window left a pending window-move candidate.");
                Assert.True(
                    !window.TryBeginBottomResizeFromScreenClick(
                        (int)Math.Round(gripPoint.X),
                        (int)Math.Round(gripPoint.Y)),
                    "Occluded window started a resize from a click it does not own.");
                Assert.True(
                    !window.TryOpenVideoContextMenu(streamX, streamY),
                    "Occluded window opened its context menu for a click it does not own.");
                Assert.Equal(false, videoContextMenu.IsOpen);
                Assert.True(
                    !window.TryRouteMouseWheel(streamX, streamY, Mouse.MouseWheelDeltaForOneLine),
                    "Occluded window consumed a wheel event it does not own.");
                Assert.Equal(80, tab.Volume);
                Assert.True(
                    !window.TryToggleStreamFullscreenFromScreenClick(streamX, streamY),
                    "Occluded window tracked the first click of a double click it does not own.");
                Assert.True(
                    !window.TryToggleStreamFullscreenFromScreenClick(streamX, streamY),
                    "Occluded window toggled fullscreen from a double click it does not own.");
                Assert.Equal(false, window.IsStreamFullscreen);
                Assert.Equal(activationsBeforeOccludedClicks, activated.Count);

                // Uncovered again the same routes must work, including once the volume OSD popup
                // (an owned top-level window) is floating over the video surface.
                cover.Hide();
                cover.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                // The routes below deliberately consult WindowFromPoint. Keep this test window
                // above unrelated desktop applications so the exposed-phase precondition is
                // deterministic even while a user is working in another maximized window.
                window.Topmost = true;
                var windowHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                NativeWindowTest.ActivateWindow(windowHandle);
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.True(
                    NativeWindowTest.IsRootWindowAtPoint(windowHandle, streamX, streamY),
                    "The exposed picture-in-picture window did not own its test point. " +
                        NativeWindowTest.DescribeWindowAtPoint(streamX, streamY));

                Assert.True(
                    window.TryActivateTabFromScreenClick(streamX, streamY),
                    "Exposed window ignored a tab activation click. " + NativeWindowTest.DescribeWindowAtPoint(streamX, streamY) +
                        $" expected-root={windowHandle}");
                Assert.True(
                    window.TryBeginVideoMoveFromScreenClick(streamX, streamY),
                    "Exposed window ignored a window move click.");
                window.CancelVideoMoveCandidate();
                Assert.True(
                    window.TryRouteMouseWheel(streamX, streamY, Mouse.MouseWheelDeltaForOneLine),
                    "Exposed window ignored a wheel event.");
                Assert.Equal(85, tab.Volume);

                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.True(
                    window.TryRouteMouseWheel(streamX, streamY, Mouse.MouseWheelDeltaForOneLine),
                    "Volume OSD popup blocked further wheel events over the video surface.");
                Assert.Equal(90, tab.Volume);
            }
            finally
            {
                cover.Close();
                window.Close();
            }
        });
    }),
    ("detached multi-stream double click fullscreens only clicked stream and restores grid", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var first = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var second = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var window = new DetachedVideoWindow([first, second], first)
            {
                Left = 240,
                Top = 260,
                Width = 740,
                Height = 430
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(handle != IntPtr.Zero);
                var originalNativeBounds = NativeWindowTest.GetWindowBounds(handle);
                var originalRestorableBounds = window.GetRestorableBounds();

                Assert.SequenceEqual(new[] { first, second }, window.Tabs);
                Assert.SequenceEqual(new[] { first, second }, window.VideoItems.Select(item => item.Tab).ToArray());
                Assert.SequenceEqual(new[] { first, second }, window.MountedVideoItems.Select(item => item.Tab).ToArray());

                var doubleClickHandler = typeof(DetachedVideoWindow).GetMethod(
                    "VideoSurface_MouseLeftButtonDoubleClicked",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(doubleClickHandler);

                var surfaces = FindVisualDescendants<StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface>(window).ToArray();
                Assert.Equal(2, surfaces.Length);
                var secondSurface = surfaces.First(surface => ReferenceEquals(surface.Tag, second));

                doubleClickHandler!.Invoke(window, [secondSurface, EventArgs.Empty]);
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.True(window.IsStreamFullscreen);
                Assert.Equal(second, window.ActiveTab);
                Assert.SequenceEqual(new[] { first, second }, window.Tabs);
                Assert.SequenceEqual(new[] { second }, window.VideoItems.Select(item => item.Tab).ToArray());
                Assert.SequenceEqual(new[] { first, second }, window.MountedVideoItems.Select(item => item.Tab).ToArray());
                var fullscreenSurfaces = FindVisualDescendants<StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface>(window).ToArray();
                Assert.Equal(2, fullscreenSurfaces.Length);
                Assert.Equal(false, fullscreenSurfaces.Single(surface => ReferenceEquals(surface.Tag, first)).IsVisible);
                var fullscreenSurface = fullscreenSurfaces.Single(surface => ReferenceEquals(surface.Tag, second));
                Assert.True(fullscreenSurface.IsVisible);

                doubleClickHandler.Invoke(window, [fullscreenSurface, EventArgs.Empty]);
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.Equal(false, window.IsStreamFullscreen);
                Assert.Equal(second, window.ActiveTab);
                Assert.SequenceEqual(new[] { first, second }, window.Tabs);
                Assert.SequenceEqual(new[] { first, second }, window.VideoItems.Select(item => item.Tab).ToArray());
                Assert.SequenceEqual(new[] { first, second }, window.MountedVideoItems.Select(item => item.Tab).ToArray());
                var restoredSurfaces = FindVisualDescendants<StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface>(window).ToArray();
                Assert.Equal(2, restoredSurfaces.Length);
                Assert.True(restoredSurfaces.Any(surface => ReferenceEquals(surface.Tag, first) && surface.IsVisible));
                Assert.True(restoredSurfaces.Any(surface => ReferenceEquals(surface.Tag, second) && surface.IsVisible));
                var restoredNativeBounds = NativeWindowTest.GetWindowBounds(handle);
                var restoredRestorableBounds = window.GetRestorableBounds();
                AssertNear(originalNativeBounds.Left, restoredNativeBounds.Left, 1.0);
                AssertNear(originalNativeBounds.Top, restoredNativeBounds.Top, 1.0);
                AssertNear(originalNativeBounds.Width, restoredNativeBounds.Width, 1.0);
                AssertNear(originalNativeBounds.Height, restoredNativeBounds.Height, 1.0);
                AssertNear(originalRestorableBounds.Left, restoredRestorableBounds.Left, 1.0);
                AssertNear(originalRestorableBounds.Top, restoredRestorableBounds.Top, 1.0);
                AssertNear(originalRestorableBounds.Width, restoredRestorableBounds.Width, 1.0);
                AssertNear(originalRestorableBounds.Height, restoredRestorableBounds.Height, 1.0);
            }
            finally
            {
                window.Close();
            }
        });
    }),
    ("detached multi-stream fullscreen keeps inactive VLC surfaces mounted", () =>
    {
        return TestSta.RunAsync(async () =>
        {
            var streamlink = new FakeStreamlinkService();
            var firstEngine = new FakePlaybackEngine();
            var secondEngine = new FakePlaybackEngine();
            var engines = new Queue<FakePlaybackEngine>([firstEngine, secondEngine]);
            var playbackFactory = new FakePlaybackEngineFactory(() => engines.Dequeue());
            var logger = new MemoryLogger();
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
            };
            settings.Chat.ConnectAutomatically = false;
            var first = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var second = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var window = new DetachedVideoWindow([first, second], first)
            {
                Left = 240,
                Top = 260,
                Width = 740,
                Height = 430
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var surfaces = FindVisualDescendants<StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface>(window).ToArray();
                Assert.Equal(2, surfaces.Length);
                var firstSurface = surfaces.Single(surface => ReferenceEquals(surface.Tag, first));
                var secondSurface = surfaces.Single(surface => ReferenceEquals(surface.Tag, second));

                await first.StartAsync(settings);
                await second.StartAsync(settings);
                var firstHandle = firstEngine.VideoHandle;
                var secondHandle = secondEngine.VideoHandle;
                Assert.True(firstHandle != IntPtr.Zero);
                Assert.True(secondHandle != IntPtr.Zero);
                Assert.Equal(firstSurface.Handle, firstHandle);
                Assert.Equal(secondSurface.Handle, secondHandle);
                Assert.SequenceEqual(new[] { firstHandle }, firstEngine.VideoHandleHistory);
                Assert.SequenceEqual(new[] { secondHandle }, secondEngine.VideoHandleHistory);

                var doubleClickHandler = typeof(DetachedVideoWindow).GetMethod(
                    "VideoSurface_MouseLeftButtonDoubleClicked",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(doubleClickHandler);

                doubleClickHandler!.Invoke(window, [secondSurface, EventArgs.Empty]);
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.True(window.IsStreamFullscreen);
                Assert.Equal(second, window.ActiveTab);
                Assert.SequenceEqual(new[] { second }, window.VideoItems.Select(item => item.Tab).ToArray());
                Assert.SequenceEqual(new[] { first, second }, window.MountedVideoItems.Select(item => item.Tab).ToArray());
                Assert.Equal(false, firstSurface.IsVisible);
                Assert.True(secondSurface.IsVisible);
                Assert.Equal(firstHandle, firstEngine.VideoHandle);
                Assert.Equal(secondHandle, secondEngine.VideoHandle);
                Assert.Equal(false, firstEngine.VideoHandleHistory.Contains(IntPtr.Zero));
                Assert.Equal(false, secondEngine.VideoHandleHistory.Contains(IntPtr.Zero));

                doubleClickHandler.Invoke(window, [secondSurface, EventArgs.Empty]);
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.Equal(false, window.IsStreamFullscreen);
                Assert.SequenceEqual(new[] { first, second }, window.VideoItems.Select(item => item.Tab).ToArray());
                Assert.SequenceEqual(new[] { first, second }, window.MountedVideoItems.Select(item => item.Tab).ToArray());
                Assert.True(firstSurface.IsVisible);
                Assert.True(secondSurface.IsVisible);
                Assert.Equal(firstHandle, firstEngine.VideoHandle);
                Assert.Equal(secondHandle, secondEngine.VideoHandle);
                Assert.SequenceEqual(new[] { firstHandle }, firstEngine.VideoHandleHistory);
                Assert.SequenceEqual(new[] { secondHandle }, secondEngine.VideoHandleHistory);
            }
            finally
            {
                window.Close();
                await first.DisposeAsync();
                await second.DisposeAsync();
            }
        });
    }),
    ("detached window captures actual resized bounds", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var window = new DetachedVideoWindow(tab)
            {
                Left = 120,
                Top = 140
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(handle != IntPtr.Zero);

                var transform = System.Windows.PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
                var topLeft = transform.Transform(new System.Windows.Point(240, 260));
                var bottomRight = transform.Transform(new System.Windows.Point(980, 690));
                NativeWindowTest.SetWindowBounds(
                    handle,
                    (int)Math.Round(topLeft.X),
                    (int)Math.Round(topLeft.Y),
                    (int)Math.Round(bottomRight.X - topLeft.X),
                    (int)Math.Round(bottomRight.Y - topLeft.Y));

                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var bounds = window.GetRestorableBounds();
                AssertNear(240, bounds.Left, 1.0);
                AssertNear(260, bounds.Top, 1.0);
                AssertNear(740, bounds.Width, 1.0);
                AssertNear(430, bounds.Height, 1.0);
            }
            finally
            {
                window.Close();
            }
        });
    }),
    ("detached stream double click fullscreen hides PiP chrome and uses full monitor bounds", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var window = new DetachedVideoWindow(tab)
            {
                Left = 240,
                Top = 260,
                Width = 740,
                Height = 430
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(handle != IntPtr.Zero);
                var screen = System.Windows.Forms.Screen.FromHandle(handle);

                var doubleClickHandler = typeof(DetachedVideoWindow).GetMethod(
                    "VideoSurface_MouseLeftButtonDoubleClicked",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var titleBar = (System.Windows.FrameworkElement)window.FindName("TitleBar");
                var bottomResizeGrip = (System.Windows.FrameworkElement)window.FindName("BottomResizeGrip");
                var titleBarRow = (RowDefinition)typeof(DetachedVideoWindow).GetField(
                    "TitleBarRow",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(window)!;
                var bottomResizeGripRow = (RowDefinition)typeof(DetachedVideoWindow).GetField(
                    "BottomResizeGripRow",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(window)!;
                Assert.NotNull(doubleClickHandler);

                doubleClickHandler!.Invoke(window, [null, EventArgs.Empty]);
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.True(window.IsStreamFullscreen);
                Assert.Equal(System.Windows.WindowState.Normal, window.WindowState);
                Assert.Equal(System.Windows.Visibility.Collapsed, titleBar.Visibility);
                Assert.Equal(System.Windows.Visibility.Collapsed, bottomResizeGrip.Visibility);
                Assert.Equal(0d, titleBarRow.Height.Value);
                Assert.Equal(0d, bottomResizeGripRow.Height.Value);
                var fullscreenBounds = NativeWindowTest.GetWindowBounds(handle);
                Assert.Equal(screen.Bounds.Left, fullscreenBounds.Left);
                Assert.Equal(screen.Bounds.Top, fullscreenBounds.Top);
                Assert.Equal(screen.Bounds.Width, fullscreenBounds.Width);
                Assert.Equal(screen.Bounds.Height, fullscreenBounds.Height);

                doubleClickHandler.Invoke(window, [null, EventArgs.Empty]);
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.Equal(false, window.IsStreamFullscreen);
                Assert.Equal(System.Windows.WindowState.Normal, window.WindowState);
                Assert.Equal(System.Windows.Visibility.Visible, titleBar.Visibility);
                Assert.Equal(System.Windows.Visibility.Visible, bottomResizeGrip.Visibility);
                Assert.Equal(34d, titleBarRow.Height.Value);
                Assert.Equal(10d, bottomResizeGripRow.Height.Value);
                AssertNear(240, window.Left, 1.0);
                AssertNear(260, window.Top, 1.0);
                AssertNear(740, window.Width, 1.0);
                AssertNear(430, window.Height, 1.0);
            }
            finally
            {
                window.Close();
            }
        });
    }),
    ("detached fullscreen button enters single-stream fullscreen with full monitor bounds", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var window = new DetachedVideoWindow(tab)
            {
                Left = 240,
                Top = 260,
                Width = 740,
                Height = 430
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(handle != IntPtr.Zero);
                var screen = System.Windows.Forms.Screen.FromHandle(handle);

                var fullscreenButton = (System.Windows.Controls.Button)window.FindName("FullscreenButton");
                var titleBar = (System.Windows.FrameworkElement)window.FindName("TitleBar");
                var bottomResizeGrip = (System.Windows.FrameworkElement)window.FindName("BottomResizeGrip");
                var titleBarRow = (RowDefinition)typeof(DetachedVideoWindow).GetField(
                    "TitleBarRow",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(window)!;
                var bottomResizeGripRow = (RowDefinition)typeof(DetachedVideoWindow).GetField(
                    "BottomResizeGripRow",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(window)!;
                Assert.NotNull(fullscreenButton);
                Assert.Equal("\uE740", fullscreenButton.Content);
                Assert.Equal("Fullscreen stream", fullscreenButton.ToolTip);

                fullscreenButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent, fullscreenButton));
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.True(window.IsStreamFullscreen);
                Assert.Equal(PictureInPictureFullscreenMode.StreamOnly, window.GetRestorableFullscreenMode());
                Assert.Equal(System.Windows.WindowState.Normal, window.WindowState);
                Assert.Equal(System.Windows.Visibility.Collapsed, titleBar.Visibility);
                Assert.Equal(System.Windows.Visibility.Collapsed, bottomResizeGrip.Visibility);
                Assert.Equal(0d, titleBarRow.Height.Value);
                Assert.Equal(0d, bottomResizeGripRow.Height.Value);
                Assert.Equal("\uE73F", fullscreenButton.Content);
                Assert.Equal("Exit fullscreen", fullscreenButton.ToolTip);
                var fullscreenBounds = NativeWindowTest.GetWindowBounds(handle);
                Assert.Equal(screen.Bounds.Left, fullscreenBounds.Left);
                Assert.Equal(screen.Bounds.Top, fullscreenBounds.Top);
                Assert.Equal(screen.Bounds.Width, fullscreenBounds.Width);
                Assert.Equal(screen.Bounds.Height, fullscreenBounds.Height);
            }
            finally
            {
                if (window.IsStreamFullscreen)
                {
                    window.ExitStreamFullscreen();
                }
                window.Close();
            }
        });
    }),
    ("detached fullscreen stays below other windows and restores prior topmost state", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var window = new DetachedVideoWindow(tab)
            {
                Left = 240,
                Top = 260,
                Width = 740,
                Height = 430,
                Topmost = true
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(handle != IntPtr.Zero);
                Assert.True(window.Topmost, "WPF Topmost was not set before entering detached fullscreen.");
                Assert.True(NativeWindowTest.IsTopmost(handle), "Native topmost style was not set before entering detached fullscreen.");
                var screen = System.Windows.Forms.Screen.FromHandle(handle);

                window.EnterStreamFullscreen();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.True(window.IsStreamFullscreen, "Expected PiP to enter stream fullscreen.");
                Assert.Equal(false, window.Topmost);
                Assert.Equal(false, NativeWindowTest.IsTopmost(handle));
                var fullscreenBounds = NativeWindowTest.GetWindowBounds(handle);
                Assert.Equal(screen.Bounds.Left, fullscreenBounds.Left);
                Assert.Equal(screen.Bounds.Top, fullscreenBounds.Top);
                Assert.Equal(screen.Bounds.Width, fullscreenBounds.Width);
                Assert.Equal(screen.Bounds.Height, fullscreenBounds.Height);

                window.ExitStreamFullscreen();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.Equal(false, window.IsStreamFullscreen);
                Assert.True(window.Topmost, "WPF Topmost was not restored after leaving detached fullscreen.");
                Assert.True(NativeWindowTest.IsTopmost(handle), "Native topmost style was not restored after leaving detached fullscreen.");
            }
            finally
            {
                window.Close();
            }
        });
    }),
    ("detached fullscreen marks shell taskbar fullscreen state until exit", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var previousTaskbarController = DetachedVideoWindow.TaskbarFullscreenController;
            var taskbarController = new FakeTaskbarFullscreenController();
            DetachedVideoWindow.TaskbarFullscreenController = taskbarController;
            var window = new DetachedVideoWindow(tab)
            {
                Left = 240,
                Top = 260,
                Width = 740,
                Height = 430
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(handle != IntPtr.Zero);

                window.EnterStreamFullscreen();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                Assert.SequenceEqual(new[] { (handle, true) }, taskbarController.Requests.ToArray());

                window.ExitStreamFullscreen();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                Assert.SequenceEqual(
                    new[] { (handle, true), (handle, false) },
                    taskbarController.Requests.ToArray());
            }
            finally
            {
                window.Close();
                DetachedVideoWindow.TaskbarFullscreenController = previousTaskbarController;
            }
        });
    }),
    ("closing fullscreen detached window clears shell taskbar fullscreen state", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var previousTaskbarController = DetachedVideoWindow.TaskbarFullscreenController;
            var taskbarController = new FakeTaskbarFullscreenController();
            DetachedVideoWindow.TaskbarFullscreenController = taskbarController;
            var window = new DetachedVideoWindow(tab)
            {
                Left = 240,
                Top = 260,
                Width = 740,
                Height = 430
            };
            var windowClosed = false;

            try
            {
                window.Show();
                window.UpdateLayout();
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(handle != IntPtr.Zero);

                window.EnterStreamFullscreen();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                window.Close();
                windowClosed = true;

                Assert.SequenceEqual(
                    new[] { (handle, true), (handle, false) },
                    taskbarController.Requests.ToArray());
            }
            finally
            {
                if (!windowClosed)
                {
                    window.Close();
                }

                DetachedVideoWindow.TaskbarFullscreenController = previousTaskbarController;
            }
        });
    }),
    ("detached fullscreen button preserves multi-stream picture-in-picture grid", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var first = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var second = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var window = new DetachedVideoWindow([first, second], second)
            {
                Left = 240,
                Top = 260,
                Width = 740,
                Height = 430
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(handle != IntPtr.Zero);
                var screen = System.Windows.Forms.Screen.FromHandle(handle);
                var fullscreenButton = (System.Windows.Controls.Button)window.FindName("FullscreenButton");
                Assert.NotNull(fullscreenButton);
                Assert.Equal("Fullscreen multiview", fullscreenButton.ToolTip);
                Assert.SequenceEqual(new[] { first, second }, window.VideoItems.Select(item => item.Tab).ToArray());
                Assert.SequenceEqual(new[] { first, second }, window.VisibleTabs.ToArray());

                fullscreenButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent, fullscreenButton));
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.True(window.IsStreamFullscreen);
                Assert.Equal(PictureInPictureFullscreenMode.MultiView, window.GetRestorableFullscreenMode());
                Assert.Equal(System.Windows.WindowState.Normal, window.WindowState);
                Assert.SequenceEqual(new[] { first, second }, window.VideoItems.Select(item => item.Tab).ToArray());
                Assert.SequenceEqual(new[] { first, second }, window.MountedVideoItems.Select(item => item.Tab).ToArray());
                Assert.SequenceEqual(new[] { first, second }, window.VisibleTabs.ToArray());
                var surfaces = FindVisualDescendants<StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface>(window).ToArray();
                Assert.Equal(2, surfaces.Length);
                Assert.True(surfaces.Single(surface => ReferenceEquals(surface.Tag, first)).IsVisible);
                Assert.True(surfaces.Single(surface => ReferenceEquals(surface.Tag, second)).IsVisible);
                var fullscreenBounds = NativeWindowTest.GetWindowBounds(handle);
                Assert.Equal(screen.Bounds.Left, fullscreenBounds.Left);
                Assert.Equal(screen.Bounds.Top, fullscreenBounds.Top);
                Assert.Equal(screen.Bounds.Width, fullscreenBounds.Width);
                Assert.Equal(screen.Bounds.Height, fullscreenBounds.Height);
            }
            finally
            {
                window.Close();
            }
        });
    }),
    ("saved detached multiview fullscreen restores all picture-in-picture streams", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings
            {
                PictureInPictureWindowLocation = new PictureInPictureWindowLocation(240, 260, 740, 430)
                {
                    IsFullscreen = true,
                    FullscreenMode = PictureInPictureFullscreenMode.MultiView
                }
            };
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var first = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var second = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            viewModel.Tabs.Add(first);
            viewModel.Tabs.Add(second);
            viewModel.SelectedTab = second;
            viewModel.SetPictureInPictureTabGroup([first, second]);

            var mainWindow = new MainWindow();
            SetMainWindowViewModel(mainWindow, viewModel);
            var detachTab = typeof(MainWindow).GetMethod(
                "DetachTabToPictureInPicture",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachedWindowsField = typeof(MainWindow).GetField(
                "detachedWindows",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(detachTab);
            Assert.NotNull(detachedWindowsField);

            DetachedVideoWindow? detachedWindow = null;
            try
            {
                detachTab!.Invoke(mainWindow, [second, new System.Windows.Point(420, 380), false]);
                var detachedWindows = (IDictionary<StreamTabViewModel, DetachedVideoWindow>)detachedWindowsField!.GetValue(mainWindow)!;
                Assert.True(detachedWindows.TryGetValue(second, out detachedWindow));
                Assert.NotNull(detachedWindow);
                detachedWindow!.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                detachedWindow.UpdateLayout();

                Assert.True(detachedWindow.IsStreamFullscreen);
                Assert.Equal(PictureInPictureFullscreenMode.MultiView, detachedWindow.GetRestorableFullscreenMode());
                Assert.SequenceEqual(new[] { first, second }, detachedWindow.VideoItems.Select(item => item.Tab).ToArray());
                Assert.SequenceEqual(new[] { first, second }, detachedWindow.VisibleTabs.ToArray());
            }
            finally
            {
                detachedWindow?.Close();
            }
        });
    }),
    ("detached maximized PiP uses work area and removes resize border so title bar is not clipped", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var window = new DetachedVideoWindow(tab)
            {
                Left = 240,
                Top = 260,
                Width = 740,
                Height = 430
            };
            var beginWindowMove = typeof(DetachedVideoWindow).GetMethod(
                "TryBeginWindowMove",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var continueWindowMoveInput = typeof(DetachedVideoWindow).GetMethod(
                "ContinueWindowMoveInput",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(beginWindowMove);
            Assert.NotNull(continueWindowMoveInput);

            try
            {
                window.Show();
                window.UpdateLayout();
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(handle != IntPtr.Zero);

                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
                var titleBar = (System.Windows.FrameworkElement)window.FindName("TitleBar");
                Assert.NotNull(chrome);
                Assert.NotNull(titleBar);
                Assert.Equal(new System.Windows.Thickness(6), chrome!.ResizeBorderThickness);
                Assert.Equal(34d, chrome.CaptionHeight);

                var minimumVisibleButtonTop = chrome.ResizeBorderThickness.Top + 1;
                var titleButtons = FindVisualDescendants<System.Windows.Controls.Button>(titleBar).ToArray();
                Assert.Equal(5, titleButtons.Length);
                foreach (var button in titleButtons)
                {
                    var topLeft = button.TransformToAncestor(window).Transform(new System.Windows.Point(0, 0));
                    Assert.True(
                        topLeft.Y >= minimumVisibleButtonTop,
                        $"Expected PiP title button top {topLeft.Y:0.###} to be at least {minimumVisibleButtonTop:0.###}.");
                }

                window.WindowState = System.Windows.WindowState.Maximized;
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.Equal(false, window.IsStreamFullscreen);
                Assert.Equal(new System.Windows.Thickness(0), chrome.ResizeBorderThickness);
                Assert.Equal(34d, chrome.CaptionHeight);
                var screen = System.Windows.Forms.Screen.FromHandle(handle);
                var maximizedBounds = NativeWindowTest.GetWindowBounds(handle);
                Assert.Equal(screen.WorkingArea.Left, maximizedBounds.Left);
                Assert.Equal(screen.WorkingArea.Top, maximizedBounds.Top);
                Assert.Equal(screen.WorkingArea.Width, maximizedBounds.Width);
                Assert.Equal(screen.WorkingArea.Height, maximizedBounds.Height);

                var maximizedTitlePoint = titleBar.PointToScreen(new Point(
                    titleBar.ActualWidth / 2,
                    titleBar.ActualHeight / 2));
                var maximizedTitleX = (int)Math.Round(maximizedTitlePoint.X);
                var maximizedTitleY = (int)Math.Round(maximizedTitlePoint.Y);
                Assert.True((bool)beginWindowMove!.Invoke(window, [
                    maximizedTitleX,
                    maximizedTitleY])!);
                Assert.Equal(System.Windows.WindowState.Maximized, window.WindowState);
                Assert.True(window.HasActiveWindowMove);
                Assert.Equal(handle, NativeWindowTest.GetCapture());
                window.CancelVideoMoveCandidate();
                Assert.Equal(System.Windows.WindowState.Maximized, window.WindowState);
                Assert.Equal(false, window.HasActiveWindowMove);
                Assert.True(NativeWindowTest.GetCapture() != handle);

                Assert.True((bool)beginWindowMove.Invoke(window, [
                    maximizedTitleX,
                    maximizedTitleY])!);

                var dragScreenX = maximizedTitleX + 100;
                var dragScreenY = maximizedTitleY + 100;
                continueWindowMoveInput!.Invoke(window, [
                    handle,
                    dragScreenX,
                    dragScreenY]);
                Assert.Equal(System.Windows.WindowState.Normal, window.WindowState);
                Assert.True(window.HasActiveWindowMove);
                Assert.Equal(handle, NativeWindowTest.GetCapture());
                window.UpdateLayout();
                var restoredTitleTop = titleBar.PointToScreen(new Point(0, 0)).Y;
                var restoredTitleBottom = titleBar.PointToScreen(new Point(0, titleBar.ActualHeight)).Y;
                Assert.True(dragScreenY >= restoredTitleTop);
                Assert.True(dragScreenY < restoredTitleBottom);
                window.CancelVideoMoveCandidate();
                Assert.Equal(false, window.HasActiveWindowMove);
                Assert.True(NativeWindowTest.GetCapture() != handle);
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.Equal(new System.Windows.Thickness(6), chrome.ResizeBorderThickness);
                Assert.Equal(34d, chrome.CaptionHeight);
            }
            finally
            {
                window.Close();
            }
        });
    }),
    ("detached stream low-level double click fallback fullscreens PiP stream", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var settingsService = new FakeSettingsService(settings);
            var viewModel = TestViewModels.CreateMain(
                settings,
                settingsService,
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            viewModel.Tabs.Add(tab);

            var mainWindow = new MainWindow();
            var viewModelField = typeof(MainWindow).GetField(
                "viewModel",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachTab = typeof(MainWindow).GetMethod(
                "DetachTabToPictureInPicture",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachedWindowsField = typeof(MainWindow).GetField(
                "detachedWindows",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var toggleDetachedStreamFullscreen = typeof(MainWindow).GetMethod(
                "TryToggleDetachedStreamFullscreenFromVideoDoubleClick",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var nativePointType = typeof(MainWindow).GetNestedType(
                "NativePoint",
                BindingFlags.NonPublic);
            Assert.NotNull(viewModelField);
            Assert.NotNull(detachTab);
            Assert.NotNull(detachedWindowsField);
            Assert.NotNull(toggleDetachedStreamFullscreen);
            Assert.NotNull(nativePointType);
            viewModelField!.SetValue(mainWindow, viewModel);

            DetachedVideoWindow? detachedWindow = null;
            try
            {
                detachTab!.Invoke(mainWindow, [tab, new System.Windows.Point(420, 380), false]);

                var detachedWindows = (IDictionary<StreamTabViewModel, DetachedVideoWindow>)detachedWindowsField!.GetValue(mainWindow)!;
                Assert.True(detachedWindows.TryGetValue(tab, out detachedWindow));
                Assert.NotNull(detachedWindow);
                detachedWindow!.UpdateLayout();

                var streamPoint = detachedWindow.PointToScreen(new System.Windows.Point(
                    detachedWindow.ActualWidth / 2,
                    34 + Math.Max(1, detachedWindow.ActualHeight - 44) / 2));
                var nativePoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(streamPoint.X), (int)Math.Round(streamPoint.Y)]);
                Assert.NotNull(nativePoint);

                var firstClickHandled = (bool)toggleDetachedStreamFullscreen!.Invoke(mainWindow, [nativePoint])!;
                var secondClickHandled = (bool)toggleDetachedStreamFullscreen.Invoke(mainWindow, [nativePoint])!;

                Assert.Equal(false, firstClickHandled);
                Assert.True(secondClickHandled);
                Assert.True(detachedWindow.IsStreamFullscreen);
                Assert.Equal(tab, viewModel.SelectedTab);
            }
            finally
            {
                detachedWindow?.Close();
            }
        });
    }),
    ("detached multi-stream low-level click is not counted once per mapped tab", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var first = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var second = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            viewModel.Tabs.Add(first);
            viewModel.Tabs.Add(second);
            viewModel.SelectedTab = first;

            var mainWindow = new MainWindow();
            SetMainWindowViewModel(mainWindow, viewModel);

            var detachedWindow = new DetachedVideoWindow([first, second], first)
            {
                Left = 360,
                Top = 320,
                Width = 740,
                Height = 430
            };
            var detachedWindowsField = typeof(MainWindow).GetField(
                "detachedWindows",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var toggleDetachedStreamFullscreen = typeof(MainWindow).GetMethod(
                "TryToggleDetachedStreamFullscreenFromVideoDoubleClick",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var nativePointType = typeof(MainWindow).GetNestedType(
                "NativePoint",
                BindingFlags.NonPublic);
            Assert.NotNull(detachedWindowsField);
            Assert.NotNull(toggleDetachedStreamFullscreen);
            Assert.NotNull(nativePointType);

            try
            {
                var detachedWindows = (IDictionary<StreamTabViewModel, DetachedVideoWindow>)detachedWindowsField!.GetValue(mainWindow)!;
                detachedWindows[first] = detachedWindow;
                detachedWindows[second] = detachedWindow;

                detachedWindow.Show();
                detachedWindow.UpdateLayout();
                detachedWindow.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                detachedWindow.UpdateLayout();

                var surfaces = FindVisualDescendants<StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface>(detachedWindow).ToArray();
                var secondSurface = surfaces.First(surface => ReferenceEquals(surface.Tag, second));
                Assert.True(secondSurface.ActualWidth > 0);
                Assert.True(secondSurface.ActualHeight > 0);
                var streamPoint = secondSurface.PointToScreen(new System.Windows.Point(
                    secondSurface.ActualWidth / 2,
                    secondSurface.ActualHeight / 2));
                var nativePoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(streamPoint.X), (int)Math.Round(streamPoint.Y)]);

                var firstClickHandled = (bool)toggleDetachedStreamFullscreen!.Invoke(mainWindow, [nativePoint])!;
                Assert.Equal(false, firstClickHandled);
                Assert.Equal(false, detachedWindow.IsStreamFullscreen);
                var secondClickHandled = (bool)toggleDetachedStreamFullscreen.Invoke(mainWindow, [nativePoint])!;

                Assert.True(secondClickHandled);
                Assert.True(detachedWindow.IsStreamFullscreen);
                Assert.Equal(second, viewModel.SelectedTab);
            }
            finally
            {
                detachedWindow.Close();
            }
        });
    }),
    ("detached multi-stream fullscreen exit restores remembered PiP placement", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var settingsService = new FakeSettingsService(settings);
            var viewModel = TestViewModels.CreateMain(
                settings,
                settingsService,
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var first = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var second = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            viewModel.Tabs.Add(first);
            viewModel.Tabs.Add(second);
            viewModel.SelectedTab = first;
            viewModel.SetPictureInPictureTabGroup([first, second]);

            var mainWindow = new MainWindow();
            SetMainWindowViewModel(mainWindow, viewModel);
            var detachTab = typeof(MainWindow).GetMethod(
                "DetachTabToPictureInPicture",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachedWindowsField = typeof(MainWindow).GetField(
                "detachedWindows",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var doubleClickHandler = typeof(DetachedVideoWindow).GetMethod(
                "VideoSurface_MouseLeftButtonDoubleClicked",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(detachTab);
            Assert.NotNull(detachedWindowsField);
            Assert.NotNull(doubleClickHandler);

            DetachedVideoWindow? detachedWindow = null;
            try
            {
                detachTab!.Invoke(mainWindow, [first, new System.Windows.Point(420, 380), false]);
                var detachedWindows = (IDictionary<StreamTabViewModel, DetachedVideoWindow>)detachedWindowsField!.GetValue(mainWindow)!;
                Assert.True(detachedWindows.TryGetValue(first, out detachedWindow));
                Assert.NotNull(detachedWindow);
                Assert.SequenceEqual(new[] { first, second }, detachedWindow!.Tabs);

                detachedWindow.UpdateLayout();
                var handle = new System.Windows.Interop.WindowInteropHelper(detachedWindow).Handle;
                Assert.True(handle != IntPtr.Zero);
                var transform = System.Windows.PresentationSource.FromVisual(detachedWindow)?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
                var topLeft = transform.Transform(new System.Windows.Point(260, 280));
                var bottomRight = transform.Transform(new System.Windows.Point(910, 640));
                NativeWindowTest.SetWindowBounds(
                    handle,
                    (int)Math.Round(topLeft.X),
                    (int)Math.Round(topLeft.Y),
                    (int)Math.Round(bottomRight.X - topLeft.X),
                    (int)Math.Round(bottomRight.Y - topLeft.Y));

                detachedWindow.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                detachedWindow.UpdateLayout();

                var originalBounds = detachedWindow.GetRestorableBounds();
                Assert.NotNull(settings.PictureInPictureWindowLocation);
                AssertNear(originalBounds.Left, settings.PictureInPictureWindowLocation!.Left, 1.0);
                AssertNear(originalBounds.Top, settings.PictureInPictureWindowLocation.Top, 1.0);
                AssertNear(originalBounds.Width, settings.PictureInPictureWindowLocation.Width, 1.0);
                AssertNear(originalBounds.Height, settings.PictureInPictureWindowLocation.Height, 1.0);
                Assert.Equal(false, settings.PictureInPictureWindowLocation.IsFullscreen);

                var surfaces = FindVisualDescendants<StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface>(detachedWindow).ToArray();
                Assert.Equal(2, surfaces.Length);
                var secondSurface = surfaces.First(surface => ReferenceEquals(surface.Tag, second));
                doubleClickHandler!.Invoke(detachedWindow, [secondSurface, EventArgs.Empty]);
                detachedWindow.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                detachedWindow.UpdateLayout();

                Assert.True(detachedWindow.IsStreamFullscreen);
                Assert.Equal(second, detachedWindow.ActiveTab);
                Assert.NotNull(settings.PictureInPictureWindowLocation);
                Assert.True(settings.PictureInPictureWindowLocation!.IsFullscreen);
                AssertNear(originalBounds.Left, settings.PictureInPictureWindowLocation.Left, 1.0);
                AssertNear(originalBounds.Top, settings.PictureInPictureWindowLocation.Top, 1.0);
                AssertNear(originalBounds.Width, settings.PictureInPictureWindowLocation.Width, 1.0);
                AssertNear(originalBounds.Height, settings.PictureInPictureWindowLocation.Height, 1.0);

                var fullscreenSurface = FindVisualDescendants<StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface>(detachedWindow)
                    .Single(surface => surface.IsVisible);
                doubleClickHandler.Invoke(detachedWindow, [fullscreenSurface, EventArgs.Empty]);
                detachedWindow.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                detachedWindow.UpdateLayout();

                Assert.Equal(false, detachedWindow.IsStreamFullscreen);
                Assert.Equal(System.Windows.WindowState.Normal, detachedWindow.WindowState);
                var restoredBounds = detachedWindow.GetRestorableBounds();
                AssertNear(originalBounds.Left, restoredBounds.Left, 1.0);
                AssertNear(originalBounds.Top, restoredBounds.Top, 1.0);
                AssertNear(originalBounds.Width, restoredBounds.Width, 1.0);
                AssertNear(originalBounds.Height, restoredBounds.Height, 1.0);
                Assert.NotNull(settings.PictureInPictureWindowLocation);
                Assert.Equal(false, settings.PictureInPictureWindowLocation!.IsFullscreen);
                AssertNear(originalBounds.Left, settings.PictureInPictureWindowLocation.Left, 1.0);
                AssertNear(originalBounds.Top, settings.PictureInPictureWindowLocation.Top, 1.0);
                AssertNear(originalBounds.Width, settings.PictureInPictureWindowLocation.Width, 1.0);
                AssertNear(originalBounds.Height, settings.PictureInPictureWindowLocation.Height, 1.0);
            }
            finally
            {
                detachedWindow?.Close();
            }
        });
    }),
    ("detached window closed bounds are aspect-fitted for the next picture-in-picture window", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var settingsService = new FakeSettingsService(settings);
            var viewModel = TestViewModels.CreateMain(
                settings,
                settingsService,
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var mainWindow = new MainWindow();
            var viewModelField = typeof(MainWindow).GetField(
                "viewModel",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var rememberBounds = typeof(MainWindow).GetMethod(
                "RememberPictureInPictureWindowBoundsAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var positionWindow = typeof(MainWindow).GetMethod(
                "PositionDetachedWindow",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(viewModelField);
            Assert.NotNull(rememberBounds);
            Assert.NotNull(positionWindow);
            viewModelField!.SetValue(mainWindow, viewModel);

            var closedWindow = new DetachedVideoWindow(tab)
            {
                Left = 240,
                Top = 320,
                Width = 640,
                Height = 360
            };
            ((Task)rememberBounds!.Invoke(mainWindow, [closedWindow])!).GetAwaiter().GetResult();

            Assert.NotNull(settings.PictureInPictureWindowLocation);
            Assert.Equal(240d, settings.PictureInPictureWindowLocation!.Left);
            Assert.Equal(320d, settings.PictureInPictureWindowLocation.Top);
            Assert.Equal(640d, settings.PictureInPictureWindowLocation.Width);
            Assert.Equal(360d, settings.PictureInPictureWindowLocation.Height);
            Assert.Equal(1, settingsService.SaveCount);

            var nextWindow = new DetachedVideoWindow(tab);
            var usedSavedLocation = (bool)positionWindow!.Invoke(
                mainWindow,
                [nextWindow, new System.Windows.Point(900, 700), true])!;

        Assert.True(usedSavedLocation);
        Assert.Equal(240d, nextWindow.Left);
        Assert.Equal(320d, nextWindow.Top);
        AssertNear((360 - 34) * nextWindow.ContentAspectRatio, nextWindow.Width);
        Assert.Equal(360d, nextWindow.Height);
        });
    }),
    ("detached window resize updates remembered picture-in-picture bounds before close", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var settingsService = new FakeSettingsService(settings);
            var viewModel = TestViewModels.CreateMain(
                settings,
                settingsService,
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            viewModel.Tabs.Add(tab);

            var mainWindow = new MainWindow();
            var viewModelField = typeof(MainWindow).GetField(
                "viewModel",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachTab = typeof(MainWindow).GetMethod(
                "DetachTabToPictureInPicture",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachedWindowsField = typeof(MainWindow).GetField(
                "detachedWindows",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(viewModelField);
            Assert.NotNull(detachTab);
            Assert.NotNull(detachedWindowsField);
            viewModelField!.SetValue(mainWindow, viewModel);

            DetachedVideoWindow? detachedWindow = null;
            try
            {
                detachTab!.Invoke(mainWindow, [tab, new System.Windows.Point(400, 360), false]);

                var detachedWindows = (IDictionary<StreamTabViewModel, DetachedVideoWindow>)detachedWindowsField!.GetValue(mainWindow)!;
                Assert.True(detachedWindows.TryGetValue(tab, out detachedWindow));
                Assert.NotNull(detachedWindow);
                detachedWindow!.UpdateLayout();

                var handle = new System.Windows.Interop.WindowInteropHelper(detachedWindow).Handle;
                Assert.True(handle != IntPtr.Zero);

                var transform = System.Windows.PresentationSource.FromVisual(detachedWindow)?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
                var topLeft = transform.Transform(new System.Windows.Point(260, 280));
                var bottomRight = transform.Transform(new System.Windows.Point(910, 640));
                NativeWindowTest.SetWindowBounds(
                    handle,
                    (int)Math.Round(topLeft.X),
                    (int)Math.Round(topLeft.Y),
                    (int)Math.Round(bottomRight.X - topLeft.X),
                    (int)Math.Round(bottomRight.Y - topLeft.Y));

                detachedWindow.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                detachedWindow.UpdateLayout();

                Assert.NotNull(settings.PictureInPictureWindowLocation);
                AssertNear(260, settings.PictureInPictureWindowLocation!.Left, 1.0);
                AssertNear(280, settings.PictureInPictureWindowLocation.Top, 1.0);
                AssertNear(650, settings.PictureInPictureWindowLocation.Width, 1.0);
                AssertNear(360, settings.PictureInPictureWindowLocation.Height, 1.0);
                Assert.Equal(0, settingsService.SaveCount);

                detachedWindow.Close();
                detachedWindow = null;
                Assert.Equal(1, settingsService.SaveCount);
                AssertNear(650, settings.PictureInPictureWindowLocation.Width, 1.0);
                AssertNear(360, settings.PictureInPictureWindowLocation.Height, 1.0);
            }
            finally
            {
                detachedWindow?.Close();
            }
        });
    }),
    ("detached maximized window remembers fullscreen screen and restores native fullscreen there", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var settingsService = new FakeSettingsService(settings);
            var targetScreen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(screen => !screen.Primary) ??
                System.Windows.Forms.Screen.PrimaryScreen ??
                System.Windows.Forms.Screen.AllScreens[0];
            var viewModel = TestViewModels.CreateMain(
                settings,
                settingsService,
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            viewModel.Tabs.Add(tab);
            var mainWindow = new MainWindow();
            var viewModelField = typeof(MainWindow).GetField(
                "viewModel",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var rememberBounds = typeof(MainWindow).GetMethod(
                "RememberPictureInPictureWindowBoundsAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachTab = typeof(MainWindow).GetMethod(
                "DetachTabToPictureInPicture",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachedWindowsField = typeof(MainWindow).GetField(
                "detachedWindows",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(viewModelField);
            Assert.NotNull(rememberBounds);
            Assert.NotNull(detachTab);
            Assert.NotNull(detachedWindowsField);
            viewModelField!.SetValue(mainWindow, viewModel);

            var maximizedWindow = new DetachedVideoWindow(tab)
            {
                Left = targetScreen.WorkingArea.Left + 240,
                Top = targetScreen.WorkingArea.Top + 120,
                Width = 640,
                Height = 360
            };
            DetachedVideoWindow? restoredWindow = null;
            var maximizedWindowClosed = false;

            try
            {
                maximizedWindow.Show();
                maximizedWindow.UpdateLayout();
                var handle = new System.Windows.Interop.WindowInteropHelper(maximizedWindow).Handle;
                Assert.True(handle != IntPtr.Zero);

                maximizedWindow.WindowState = System.Windows.WindowState.Maximized;
                maximizedWindow.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                maximizedWindow.UpdateLayout();

                ((Task)rememberBounds!.Invoke(mainWindow, [maximizedWindow])!).GetAwaiter().GetResult();

                Assert.NotNull(settings.PictureInPictureWindowLocation);
                Assert.True(settings.PictureInPictureWindowLocation!.IsFullscreen);
                Assert.NotNull(settings.PictureInPictureWindowLocation.FullscreenScreen);
                Assert.Equal(targetScreen.DeviceName, settings.PictureInPictureWindowLocation.FullscreenScreen!.DeviceName);
                Assert.Equal((double)targetScreen.Bounds.Left, settings.PictureInPictureWindowLocation.FullscreenScreen.Left);
                Assert.Equal((double)targetScreen.Bounds.Top, settings.PictureInPictureWindowLocation.FullscreenScreen.Top);
                Assert.Equal((double)targetScreen.Bounds.Width, settings.PictureInPictureWindowLocation.FullscreenScreen.Width);
                Assert.Equal((double)targetScreen.Bounds.Height, settings.PictureInPictureWindowLocation.FullscreenScreen.Height);
                Assert.Equal(1, settingsService.SaveCount);

                settings.PictureInPictureWindowLocation.Left = 71;
                settings.PictureInPictureWindowLocation.Top = 73;
                settings.PictureInPictureWindowLocation.Width = 1758;
                settings.PictureInPictureWindowLocation.Height = 1008;

                maximizedWindow.Close();
                maximizedWindowClosed = true;

                detachTab!.Invoke(mainWindow, [tab, new System.Windows.Point(900, 700), false]);
                var detachedWindows = (IDictionary<StreamTabViewModel, DetachedVideoWindow>)detachedWindowsField!.GetValue(mainWindow)!;
                Assert.True(detachedWindows.TryGetValue(tab, out restoredWindow));
                Assert.NotNull(restoredWindow);
                restoredWindow!.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                restoredWindow.UpdateLayout();

                var restoredHandle = new System.Windows.Interop.WindowInteropHelper(restoredWindow).Handle;
                Assert.True(restoredHandle != IntPtr.Zero);
                var restoredBounds = NativeWindowTest.GetWindowBounds(restoredHandle);
                Assert.Equal(System.Windows.WindowState.Normal, restoredWindow.WindowState);
                Assert.True(restoredWindow.IsStreamFullscreen);
                Assert.Equal(targetScreen.DeviceName, System.Windows.Forms.Screen.FromHandle(restoredHandle).DeviceName);
                Assert.Equal(targetScreen.Bounds.Left, restoredBounds.Left);
                Assert.Equal(targetScreen.Bounds.Top, restoredBounds.Top);
                Assert.Equal(targetScreen.Bounds.Width, restoredBounds.Width);
                Assert.Equal(targetScreen.Bounds.Height, restoredBounds.Height);
            }
            finally
            {
                restoredWindow?.Close();
                if (!maximizedWindowClosed)
                {
                    maximizedWindow.Close();
                }
            }
        });
    }),
    ("top control toggle buttons share selected visuals", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var window = new MainWindow();

            // Asserted against palette slots, not literal hex: the studio palette is theme-swappable,
            // so what matters is that every toggle reaches for the same slot in each state.
            var selectedBackground = WpfVisualTest.PaletteColor(window, "StudioAccentPressedColor");
            var selectedBorder = WpfVisualTest.PaletteColor(window, "StudioAccentColor");
            const string selectedForeground = "#FFFFFFFF";
            var defaultBackground = WpfVisualTest.PaletteColor(window, "StudioSurface2Color");
            var defaultBorder = WpfVisualTest.PaletteColor(window, "StudioBorderStrongColor");
            var defaultForeground = WpfVisualTest.PaletteColor(window, "StudioTextColor");
            var unavailableBackground = WpfVisualTest.PaletteColor(window, "StudioDisabledColor");
            var unavailableBorder = WpfVisualTest.PaletteColor(window, "StudioDisabledBorderColor");

            AssertTopControlToggleVisual(
                window,
                "ReplaySeekBarToggleIconButton",
                new TopControlToggleState { IsReplaySeekBarUiVisible = true },
                selectedBackground,
                selectedBorder,
                selectedForeground);
            AssertTopControlToggleVisual(
                window,
                "ChatToggleIconButton",
                new TopControlToggleState { IsSelectedChatShowing = true },
                selectedBackground,
                selectedBorder,
                selectedForeground);
            AssertTopControlToggleVisual(
                window,
                "MultiStreamToggleIconButton",
                new TopControlToggleState { IsMultiStreamEnabled = true },
                selectedBackground,
                selectedBorder,
                selectedForeground);
            AssertTopControlToggleVisual(
                window,
                "SettingsToggleIconButton",
                new TopControlToggleState { IsSettingsOpen = true },
                selectedBackground,
                selectedBorder,
                selectedForeground);
            AssertTopControlToggleVisual(
                window,
                "MuteToggleIconButton",
                new TopControlToggleState { SelectedTab = new TopControlToggleTabState { IsMuted = true } },
                selectedBackground,
                selectedBorder,
                selectedForeground);

            AssertTopControlToggleVisual(
                window,
                "ChatToggleIconButton",
                new TopControlToggleState(),
                defaultBackground,
                defaultBorder,
                defaultForeground);
            AssertTopControlToggleVisual(
                window,
                "ChatToggleIconButton",
                new TopControlToggleState { IsSelectedChatShowing = true, IsChatLayoutHidden = true },
                unavailableBackground,
                unavailableBorder,
                selectedForeground);

            static void AssertTopControlToggleVisual(
                MainWindow window,
                string styleKey,
                TopControlToggleState state,
                string expectedBackground,
                string expectedBorder,
                string expectedForeground)
            {
                var button = new System.Windows.Controls.Button
                {
                    DataContext = state
                };
                button.Style = (System.Windows.Style)window.Resources[styleKey];

                button.ApplyTemplate();
                button.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.DataBind);
                button.UpdateLayout();

                WpfVisualTest.AssertTemplateBorderColor(button, expectedBackground, expectedBorder);
                WpfVisualTest.AssertSolidBrushColor(expectedForeground, button.Foreground);
            }
        });
    }),
    ("studio theme exposes shared palette focus and disabled visuals", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var window = new MainWindow();
            Assert.True(window.Resources.MergedDictionaries.Any(dictionary =>
                dictionary.Source?.OriginalString.Contains("Themes/StudioTheme.xaml", StringComparison.Ordinal) == true));

            // The palette itself is merged into application resources (ThemeManager swaps that
            // dictionary), so it resolves through the tree rather than out of window.Resources.
            // These three pin the default dark palette so an accidental recolour is caught.
            WpfVisualTest.AssertSolidBrushColor(
                "#FF0A0A0B",
                WpfVisualTest.PaletteBrush(window, "StudioBaseBrush"));
            WpfVisualTest.AssertSolidBrushColor(
                "#FF2DD4BF",
                WpfVisualTest.PaletteBrush(window, "StudioAccentBrush"));
            WpfVisualTest.AssertSolidBrushColor(
                "#FF48C7B5",
                WpfVisualTest.PaletteBrush(window, "StudioFocusBrush"));

            var button = new System.Windows.Controls.Button
            {
                Style = (System.Windows.Style)window.Resources["TopControlIconButton"],
                IsEnabled = false
            };
            button.ApplyTemplate();
            button.Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.DataBind);

            Assert.NotNull(button.FocusVisualStyle);
            WpfVisualTest.AssertTemplateBorderColor(
                button,
                WpfVisualTest.PaletteColor(window, "StudioDisabledColor"),
                WpfVisualTest.PaletteColor(window, "StudioDisabledBorderColor"));
            WpfVisualTest.AssertSolidBrushColor(
                WpfVisualTest.PaletteColor(window, "StudioTextMutedColor"),
                button.Foreground);
        });
    }),
    ("combo box hover keeps the configured surface instead of platform highlight chrome", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var comboBox = new ComboBox
            {
                Width = 240,
                ItemsSource = new[] { "Exit", "Minimize to tray" },
                SelectedIndex = 0,
                Style = (Style)Application.Current!.Resources["StudioComboBoxStyle"]
            };
            var host = new Window
            {
                Width = 320,
                Height = 140,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                Content = comboBox
            };
            var restoreCursor = NativeWindowTest.TryGetCursorPosition(out var originalCursor);

            try
            {
                host.Show();
                host.Activate();
                host.UpdateLayout();

                var toggleButton = FindVisualDescendants<ToggleButton>(comboBox).Single();
                var screenPoint = comboBox.PointToScreen(new Point(
                    comboBox.ActualWidth / 2,
                    comboBox.ActualHeight / 2));
                NativeWindowTest.SetCursorPosition(
                    (int)Math.Round(screenPoint.X),
                    (int)Math.Round(screenPoint.Y));
                Mouse.Synchronize();
                host.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.Input);
                host.UpdateLayout();

                Assert.True(comboBox.IsMouseOver);
                Assert.True(toggleButton.IsMouseOver);
                Assert.Equal(
                    WpfVisualTest.PaletteColor(host, "StudioSurface0Color"),
                    WpfVisualTest.PixelColor(
                        WpfVisualTest.Render(comboBox),
                        (int)comboBox.ActualWidth - 40,
                        (int)comboBox.ActualHeight / 2));
            }
            finally
            {
                if (restoreCursor)
                {
                    NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
                }

                host.Close();
            }
        });
    }),
    ("switching the theme repaints the picture-in-picture window and volume overlay", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                new FakeStreamlinkService(),
                new FakePlaybackEngineFactory(),
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action());
            var window = new DetachedVideoWindow([tab], tab);
            try
            {
                window.Show();
                window.UpdateLayout();

                var titleBar = (System.Windows.Controls.Border)window.FindName("TitleBar");
                var volumeOsd = (StreamlinkVlcStudio.App.Wpf.Controls.VolumeOverlay)window.FindName("VolumeOsd");
                var osdIcon = (System.Windows.Controls.TextBlock)volumeOsd.FindName("Icon");

                // These are resolved off the swappable palette. Before the fix they were bound with
                // StaticResource, so the detached window and the OSD stayed on whichever palette was
                // loaded when they were built and a theme change never reached them.
                WpfVisualTest.AssertSolidBrushColor(
                    WpfVisualTest.PaletteColor(window, "StudioSurface1Color"),
                    titleBar.Background);
                WpfVisualTest.AssertSolidBrushColor(
                    WpfVisualTest.PaletteColor(window, "StudioTextColor"),
                    osdIcon.Foreground);

                StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Light);
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.DataBind);

                var lightPanel = WpfVisualTest.PaletteColor(window, "StudioSurface1Color");
                var lightText = WpfVisualTest.PaletteColor(window, "StudioTextColor");
                Assert.Equal("#FFF0F0F3", lightPanel);
                WpfVisualTest.AssertSolidBrushColor(lightPanel, titleBar.Background);
                WpfVisualTest.AssertSolidBrushColor(lightText, osdIcon.Foreground);
            }
            finally
            {
                StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Dark);
                window.Close();
            }
        });
    }),
    ("volume overlay releases its popup target when its window unloads", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var target = new Border
            {
                Width = 640,
                Height = 360
            };
            var volumeOsd = new StreamlinkVlcStudio.App.Wpf.Controls.VolumeOverlay();
            var content = new Grid();
            content.Children.Add(target);
            content.Children.Add(volumeOsd);
            var window = new Window
            {
                Width = 640,
                Height = 360,
                Content = content,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };
            window.Show();
            window.UpdateLayout();

            volumeOsd.Show(target, 75, muted: false);
            var popup = (System.Windows.Controls.Primitives.Popup)volumeOsd.FindName("Popup");
            Assert.Equal(true, popup.IsOpen);
            Assert.True(ReferenceEquals(target, popup.PlacementTarget));

            window.Close();

            Assert.Equal(false, popup.IsOpen);
            Assert.Equal<UIElement?>(null, popup.PlacementTarget);
        });
    }),
    ("home navigation selection uses contextual teal state", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var window = new MainWindow();

            var selected = new System.Windows.Controls.Button
            {
                DataContext = new HomeNavigationVisualState { IsFollowedHomePageSelected = true },
                Style = (System.Windows.Style)window.Resources["FollowedHomeSegmentButton"]
            };
            selected.ApplyTemplate();
            selected.Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.DataBind);
            WpfVisualTest.AssertTemplateBorderColor(
                selected,
                WpfVisualTest.PaletteColor(window, "StudioAccentPressedColor"),
                WpfVisualTest.PaletteColor(window, "StudioAccentColor"));
            WpfVisualTest.AssertSolidBrushColor("#FFFFFFFF", selected.Foreground);

            var idle = new System.Windows.Controls.Button
            {
                DataContext = new HomeNavigationVisualState(),
                Style = (System.Windows.Style)window.Resources["FollowedHomeSegmentButton"]
            };
            idle.ApplyTemplate();
            idle.Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.DataBind);
            WpfVisualTest.AssertTemplateBorderColor(
                idle,
                WpfVisualTest.PaletteColor(window, "StudioSurface0Color"),
                WpfVisualTest.PaletteColor(window, "StudioBorderColor"));
            WpfVisualTest.AssertSolidBrushColor(
                WpfVisualTest.PaletteColor(window, "StudioTextSecondaryColor"),
                idle.Foreground);
        });
    }),
    ("stream tab chrome distinguishes selected and idle states", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var settings = new AppSettings();
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var selectedTab = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var idleTab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Kick),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            viewModel.Tabs.Add(selectedTab);
            viewModel.Tabs.Add(idleTab);
            viewModel.SelectedTab = selectedTab;

            var window = new MainWindow
            {
                Width = 1320,
                Height = 820,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);
            var tabListBox = (System.Windows.Controls.ListBox)window.FindName("TabListBox");
            tabListBox.SelectedItem = viewModel.SelectedTabStripItem;
            window.Show();
            window.Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            WpfVisualTest.AssertBorderColor(
                FindTabStripChrome(window, selectedTab),
                WpfVisualTest.PaletteColor(window, "StudioAccentPressedColor"),
                WpfVisualTest.PaletteColor(window, "StudioFocusColor"));
            WpfVisualTest.AssertBorderColor(
                FindTabStripChrome(window, idleTab),
                WpfVisualTest.PaletteColor(window, "StudioSurface1Color"),
                WpfVisualTest.PaletteColor(window, "StudioBorderColor"));
            window.Close();
        });
    }),
    ("settings hub collapses native workspace and switches categories", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var settings = new AppSettings();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                new FakeStreamlinkService(),
                new FakePlaybackEngineFactory(),
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action());
            var window = new MainWindow { DataContext = viewModel };
            var playbackHost = (System.Windows.Controls.DockPanel)window.FindName("PlaybackHost");
            var settingsPanel = (System.Windows.Controls.Border)window.FindName("SettingsPanel");
            var generalPage = (System.Windows.Controls.StackPanel)window.FindName("GeneralSettingsPage");
            var chatPage = (System.Windows.Controls.StackPanel)window.FindName("ChatSettingsPage");
            var hotkeysPage = (System.Windows.Controls.StackPanel)window.FindName("HotkeysSettingsPage");
            var previousTabRecorder = (HotkeyRecorderButton)window.FindName("PreviousTabHotkeyRecorder");
            var resetHotkeysButton = (System.Windows.Controls.Button)window.FindName("ResetHotkeysButton");
            var stickyFooter = (System.Windows.Controls.Border)window.FindName("SettingsStickyFooter");
            var saveButton = (System.Windows.Controls.Button)window.FindName("SettingsSaveButton");
            SetMainWindowViewModel(window, viewModel);

            viewModel.IsSettingsOpen = true;
            window.Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.DataBind);
            Assert.Equal(System.Windows.Visibility.Collapsed, playbackHost.Visibility);
            Assert.Equal(System.Windows.Visibility.Visible, settingsPanel.Visibility);
            Assert.Equal(2, System.Windows.Controls.Grid.GetColumnSpan(settingsPanel));
            Assert.Equal(System.Windows.Visibility.Visible, generalPage.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, chatPage.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, hotkeysPage.Visibility);
            Assert.Equal(System.Windows.Visibility.Visible, stickyFooter.Visibility);
            Assert.NotNull(saveButton.Command);
            Assert.Equal(false, viewModel.IsPlaybackWorkspaceVisible);

            viewModel.ShowChatSettingsCommand.Execute(null);
            window.Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.DataBind);
            Assert.Equal(SettingsCategory.Chat, viewModel.SelectedSettingsCategory);
            Assert.Equal(System.Windows.Visibility.Collapsed, generalPage.Visibility);
            Assert.Equal(System.Windows.Visibility.Visible, chatPage.Visibility);

            viewModel.ShowHotkeysSettingsCommand.Execute(null);
            window.Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.DataBind);
            Assert.Equal(SettingsCategory.Hotkeys, viewModel.SelectedSettingsCategory);
            Assert.Equal(System.Windows.Visibility.Collapsed, generalPage.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, chatPage.Visibility);
            Assert.Equal(System.Windows.Visibility.Visible, hotkeysPage.Visibility);
            settingsPanel.Measure(new System.Windows.Size(980, 544));
            settingsPanel.Arrange(new System.Windows.Rect(0, 0, 980, 544));
            settingsPanel.UpdateLayout();
            Assert.True(previousTabRecorder.ActualWidth >= 178);
            Assert.True(previousTabRecorder.ActualHeight >= 42);
            Assert.Equal("Left", previousTabRecorder.Content?.ToString());

            settings.Hotkeys.PreviousTab = "Ctrl+Prior";
            window.Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.DataBind);
            Assert.Equal("Ctrl + Page Up", previousTabRecorder.Content?.ToString());
            resetHotkeysButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            window.Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.DataBind);
            Assert.Equal(HotkeySettings.DefaultPreviousTab, settings.Hotkeys.PreviousTab);
            Assert.Equal("Left", previousTabRecorder.Content?.ToString());

            viewModel.ToggleSettingsCommand.Execute(null);
            window.Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.DataBind);
            Assert.Equal(System.Windows.Visibility.Visible, playbackHost.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, settingsPanel.Visibility);
            Assert.True(viewModel.IsPlaybackWorkspaceVisible);
        });
    }),
    ("settings expose and save the Windows toast notification toggle", () =>
    {
        return TestSta.RunAsync(async () =>
        {
            var settings = new AppSettings();
            var settingsService = new FakeSettingsService(settings);
            var notifications = new FakeLiveNotificationService();
            var viewModel = TestViewModels.CreateMain(
                settings,
                settingsService,
                new FakeStreamlinkService(),
                new FakePlaybackEngineFactory(),
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action(),
                liveNotificationService: notifications);
            var window = new MainWindow { DataContext = viewModel };
            var toggle = (System.Windows.Controls.CheckBox)window.FindName("WindowsToastNotificationsCheckBox");

            window.Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.DataBind);
            Assert.Equal("Windows toast notifications", toggle.Content?.ToString());
            Assert.True(toggle.IsChecked == true);
            Assert.True(notifications.IsEnabled);

            toggle.IsChecked = false;
            window.Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.DataBind);
            Assert.Equal(false, settings.FollowedChannels.NotifyWhenLive);
            Assert.Equal(false, notifications.IsEnabled);

            await viewModel.SaveSettingsCommand.ExecuteAsync();
            Assert.Equal(1, settingsService.SaveCount);
            Assert.Equal("Settings saved", viewModel.StatusMessage);

            await viewModel.DisposeAsync();
        });
    }),
    ("top controls bar owns playback toggles without a bottom deck", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var window = new MainWindow();
            var videoHost = (System.Windows.Controls.Grid)window.FindName("VideoAndReplayHost");
            Assert.Equal(2, videoHost.RowDefinitions.Count);
            Assert.True(window.FindName("BottomControlDeck") is null);
            Assert.NotNull(window.FindName("TopPlayPauseButton"));
            Assert.NotNull(window.FindName("TopReplayToggleButton"));
            Assert.NotNull(window.FindName("TopTheatreButton"));
            Assert.NotNull(window.FindName("TopFullscreenButton"));
            Assert.NotNull(window.FindName("TopMuteButton"));

            var pause = new System.Windows.Controls.Button
            {
                DataContext = new TopControlToggleState
                {
                    SelectedTab = new TopControlToggleTabState { Status = PlaybackStatus.Paused }
                },
                Style = (System.Windows.Style)window.Resources["PauseResumeIconButton"]
            };
            pause.ApplyTemplate();
            pause.Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.DataBind);
            Assert.Equal("\uE768", pause.Content?.ToString());
            WpfVisualTest.AssertTemplateBorderColor(
                pause,
                WpfVisualTest.PaletteColor(window, "StudioAccentPressedColor"),
                WpfVisualTest.PaletteColor(window, "StudioAccentColor"));

            var replay = new System.Windows.Controls.Button
            {
                DataContext = new TopControlToggleState { IsReplaySeekBarUiVisible = true },
                Style = (System.Windows.Style)window.Resources["ReplaySeekBarToggleIconButton"]
            };
            replay.ApplyTemplate();
            replay.Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.DataBind);
            WpfVisualTest.AssertTemplateBorderColor(
                replay,
                WpfVisualTest.PaletteColor(window, "StudioAccentPressedColor"),
                WpfVisualTest.PaletteColor(window, "StudioAccentColor"));
        });
    }),
    ("switching settings from VLC plugin overlay to docked rebuilds playback without native overlay", async () =>
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Overlay;

        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        await viewModel.OpenDetectedStreamAsync(StreamInputParser.Parse("albralelie", PlatformKind.Twitch));
        viewModel.SelectedTab!.SetVideoHandle(new IntPtr(1234));
        await TestWait.UntilAsync(
            () => playbackFactory.CreateCount == 1 &&
                playbackFactory.LastEnableNativeOverlay == true &&
                playbackFactory.Engine?.Played == true,
            TimeSpan.FromMilliseconds(500));

        settings.Chat.Layout = ChatLayout.Docked;

        await TestWait.UntilAsync(
            () => playbackFactory.CreateCount == 2 &&
                playbackFactory.LastEnableNativeOverlay == false &&
                playbackFactory.Engine?.Played == true,
            TimeSpan.FromSeconds(2));
        Assert.Equal(false, playbackFactory.LastEnableNativeOverlay);
        await viewModel.DisposeAsync();
    }),
    ("toggling VLC plugin chat visibility keeps playback running with native overlay loaded", async () =>
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Overlay;
        settings.Chat.VlcOverlayDirectory = @"C:\Tools\vlc-overlay";

        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        await viewModel.OpenDetectedStreamAsync(StreamInputParser.Parse("albralelie", PlatformKind.Twitch));
        viewModel.SelectedTab!.SetVideoHandle(new IntPtr(1234));
        await TestWait.UntilAsync(
            () => playbackFactory.CreateCount == 1 &&
                playbackFactory.LastEnableNativeOverlay == true &&
                playbackFactory.Engine?.Played == true,
            TimeSpan.FromMilliseconds(500));

        await viewModel.ToggleChatCommand.ExecuteAsync();

        Assert.Equal(false, viewModel.SelectedTab!.IsChatVisible);
        Assert.Equal(1, playbackFactory.CreateCount);
        Assert.Equal(true, playbackFactory.LastEnableNativeOverlay);
        Assert.Equal(1, streamlink.StartCount);

        await viewModel.ToggleChatCommand.ExecuteAsync();

        Assert.Equal(true, viewModel.SelectedTab!.IsChatVisible);
        Assert.Equal(1, playbackFactory.CreateCount);
        Assert.Equal(true, playbackFactory.LastEnableNativeOverlay);
        Assert.Equal(1, streamlink.StartCount);
        await viewModel.DisposeAsync();
    }),
    ("theatre docked chat override keeps VLC plugin playback running", async () =>
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Overlay;
        settings.Chat.VlcOverlayDirectory = @"C:\Tools\vlc-overlay";

        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            UsesNativeOverlayOverride = true,
            NativeOverlayPipeNameOverride = "svs_test"
        });
        var logger = new MemoryLogger();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        await viewModel.OpenDetectedStreamAsync(StreamInputParser.Parse("albralelie", PlatformKind.Twitch));
        viewModel.SelectedTab!.SetVideoHandle(new IntPtr(1234));
        await TestWait.UntilAsync(
            () => playbackFactory.CreateCount == 1 &&
                playbackFactory.LastEnableNativeOverlay == true &&
                playbackFactory.Engine?.Played == true,
            TimeSpan.FromMilliseconds(500));

        var tab = viewModel.SelectedTab!;
        viewModel.ApplyTheatreModeDockedChat(viewModel.GetTheatreModeChatTargetTabs());

        await TestWait.UntilAsync(
            () => tab.IsDockedChatOverrideActive && viewModel.IsDockedChatVisible,
            TimeSpan.FromMilliseconds(500));
        Assert.Equal(ChatLayout.Overlay, settings.Chat.Layout);
        Assert.Equal(true, tab.IsChatVisible);
        Assert.Equal(true, tab.IsDockedChatPanelVisible);
        Assert.Equal(1, playbackFactory.CreateCount);
        Assert.Equal(true, playbackFactory.LastEnableNativeOverlay);
        Assert.Equal(1, streamlink.StartCount);

        viewModel.ClearTheatreModeDockedChatOverrides();

        Assert.Equal(false, tab.IsDockedChatOverrideActive);
        Assert.Equal(false, viewModel.IsDockedChatVisible);
        Assert.Equal(ChatLayout.Overlay, settings.Chat.Layout);
        Assert.Equal(1, playbackFactory.CreateCount);
        Assert.Equal(1, streamlink.StartCount);
        await viewModel.DisposeAsync();
    }),
    ("theatre mode applies docked chat without changing overlay layout setting", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var settings = new AppSettings();
            settings.Chat.ConnectAutomatically = false;
            settings.Chat.Layout = ChatLayout.Overlay;
            settings.Chat.VlcOverlayDirectory = @"C:\Tools\vlc-overlay";
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());

            viewModel.Tabs.Add(tab);
            viewModel.VideoTabs.Add(tab);
            viewModel.SelectedTab = tab;

            var window = new MainWindow
            {
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);

            try
            {
                var applyTheatreChat = typeof(MainWindow).GetMethod(
                    "ApplyTheatreModeChatToSelectedTab",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(applyTheatreChat);

                applyTheatreChat!.Invoke(window, []);

                Assert.Equal(ChatLayout.Overlay, settings.Chat.Layout);
                Assert.Equal(true, tab.IsDockedChatOverrideActive);
                Assert.Equal(true, viewModel.IsDockedChatVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }),
    ("inactive window first click focuses docked chat input and accepts typing", () =>
    {
        return TestSta.RunAsync(async () =>
        {
            var settings = new AppSettings();
            settings.Chat.ConnectAutomatically = false;
            settings.Chat.Layout = ChatLayout.Docked;
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            tab.IsChatVisible = true;
            tab.IsDockedChatPanelVisible = true;

            viewModel.Tabs.Add(tab);
            viewModel.VideoTabs.Add(tab);
            viewModel.SelectedTab = tab;

            var window = new MainWindow
            {
                DataContext = viewModel,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = 400,
                Top = 160,
                Width = 1080,
                Height = 700
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);

            System.Windows.Threading.Dispatcher? foregroundDispatcher = null;
            Thread? foregroundThread = null;
            IntPtr foregroundHandle = IntPtr.Zero;
            Action? closeForegroundWindow = null;
            var previousForegroundWindow = NativeWindowTest.GetForegroundWindow();
            var restoreCursor = NativeWindowTest.TryGetCursorPosition(out var originalCursor);
            try
            {
                window.Show();
                window.UpdateLayout();
                window.Topmost = true;
                window.Topmost = false;
                SetMainWindowHandle(window);
                AttachMainWindowMessageHook(window);

                var input = (System.Windows.Controls.TextBox)window.FindName("ChatInputTextBox");
                Assert.Equal(System.Windows.Visibility.Visible, input.Visibility);
                Assert.True(input.IsEnabled);
                Assert.Equal(false, input.IsReadOnly);

                var foregroundReady = new TaskCompletionSource<(
                    System.Windows.Threading.Dispatcher Dispatcher,
                    IntPtr Handle,
                    Action Close)>(TaskCreationOptions.RunContinuationsAsynchronously);
                foregroundThread = new Thread(() =>
                {
                    try
                    {
                        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                        var foregroundWindow = new System.Windows.Window
                        {
                            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                            Left = 20,
                            Top = 20,
                            Width = 220,
                            Height = 140,
                            ShowInTaskbar = false,
                            WindowStyle = System.Windows.WindowStyle.ToolWindow,
                            Topmost = true
                        };
                        foregroundWindow.Show();
                        foregroundWindow.UpdateLayout();
                        foregroundReady.SetResult((
                            dispatcher,
                            new System.Windows.Interop.WindowInteropHelper(foregroundWindow).Handle,
                            foregroundWindow.Close));
                        System.Windows.Threading.Dispatcher.Run();
                    }
                    catch (Exception ex)
                    {
                        foregroundReady.TrySetException(ex);
                    }
                })
                {
                    IsBackground = true,
                    Name = "InactiveChatForegroundWindow"
                };
                foregroundThread.SetApartmentState(ApartmentState.STA);
                foregroundThread.Start();
                (foregroundDispatcher, foregroundHandle, closeForegroundWindow) =
                    await foregroundReady.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await NativeWindowTest.RequireForegroundAsync(
                    foregroundHandle,
                    TimeSpan.FromSeconds(2),
                    "inactive-chat foreground precondition");

                var inputCenter = input.PointToScreen(new System.Windows.Point(
                    input.ActualWidth / 2,
                    input.ActualHeight / 2));
                var windowHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(
                    NativeWindowTest.IsRootWindowAtPoint(
                        windowHandle,
                        (int)Math.Round(inputCenter.X),
                        (int)Math.Round(inputCenter.Y)),
                    "The inactive main window did not own the chat input click point. " +
                    NativeWindowTest.DescribeWindowAtPoint(
                        (int)Math.Round(inputCenter.X),
                        (int)Math.Round(inputCenter.Y)));

                NativeWindowTest.SendLeftClick(
                    (int)Math.Round(inputCenter.X),
                    (int)Math.Round(inputCenter.Y));
                await TestWait.UntilAsync(
                    () => input.IsKeyboardFocusWithin,
                    TimeSpan.FromSeconds(2));

                const string message = "inactive click тест";
                NativeWindowTest.SendUnicodeText(message);
                await TestWait.UntilAsync(
                    () => input.Text == message && tab.OutgoingChatText == message,
                    TimeSpan.FromSeconds(2));
            }
            finally
            {
                if (restoreCursor)
                {
                    NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
                }

                if (foregroundDispatcher is not null &&
                    !foregroundDispatcher.HasShutdownStarted &&
                    !foregroundDispatcher.HasShutdownFinished)
                {
                    foregroundDispatcher.Invoke(closeForegroundWindow!);
                    foregroundDispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
                }

                foregroundThread?.Join(TimeSpan.FromSeconds(2));
                window.Close();
                if (previousForegroundWindow != IntPtr.Zero)
                {
                    NativeWindowTest.ActivateWindow(previousForegroundWindow);
                }

                await viewModel.DisposeAsync();
            }
        });
    }),
    ("theatre chat input stays above the taskbar and accepts physical typing", () =>
    {
        return TestSta.RunAsync(async () =>
        {
            var previousForegroundWindow = NativeWindowTest.GetForegroundWindow();
            var restoreCursor = NativeWindowTest.TryGetCursorPosition(out var originalCursor);
            var settings = new AppSettings();
            settings.Chat.ConnectAutomatically = false;
            settings.Chat.Layout = ChatLayout.Docked;
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            tab.IsChatVisible = true;
            tab.IsDockedChatPanelVisible = true;

            viewModel.Tabs.Add(tab);
            viewModel.VideoTabs.Add(tab);
            viewModel.SelectedTab = tab;

            var window = new MainWindow
            {
                DataContext = viewModel,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = 320,
                Top = 120,
                Width = 1100,
                Height = 720
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);

            try
            {
                window.Show();
                window.UpdateLayout();
                var windowHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(windowHandle != IntPtr.Zero);

                ToggleMainWindowFullscreen(window, "Theatre");
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                await NativeWindowTest.RequireForegroundAsync(
                    windowHandle,
                    TimeSpan.FromSeconds(2),
                    "theatre-chat foreground precondition");

                var input = (System.Windows.Controls.TextBox)window.FindName("ChatInputTextBox");
                Assert.True(input.IsVisible);
                Assert.True(input.ActualWidth > 0 && input.ActualHeight > 0);
                var inputCenter = input.PointToScreen(new System.Windows.Point(
                    input.ActualWidth / 2,
                    input.ActualHeight / 2));
                var inputX = (int)Math.Round(inputCenter.X);
                var inputY = (int)Math.Round(inputCenter.Y);
                await TestWait.UntilAsync(
                    () => NativeWindowTest.IsRootWindowAtPoint(windowHandle, inputX, inputY),
                    TimeSpan.FromSeconds(3),
                    $"Theatre chat input was occluded at ({inputX}, {inputY})");

                var inputMouseDownReceived = false;
                input.PreviewMouseDown += (_, _) => inputMouseDownReceived = true;
                NativeWindowTest.SendLeftClick(inputX, inputY);
                await TestWait.UntilAsync(
                    () => inputMouseDownReceived && input.IsKeyboardFocusWithin,
                    TimeSpan.FromSeconds(2),
                    "physical click did not reach or focus the Theatre chat input");

                const string message = "theatre input works";
                NativeWindowTest.SendUnicodeText(message);
                await TestWait.UntilAsync(
                    () => input.Text == message && tab.OutgoingChatText == message,
                    TimeSpan.FromSeconds(2),
                    "physical typing did not update the Theatre chat input");
            }
            finally
            {
                if (restoreCursor)
                {
                    NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
                }

                ExitMainWindowFullscreenIfActive(window);

                window.Close();
                if (previousForegroundWindow != IntPtr.Zero)
                {
                    NativeWindowTest.ActivateWindow(previousForegroundWindow);
                }

                await viewModel.DisposeAsync();
            }
        });
    }),
    ("docked and theatre chat release native overlay keyboard capture before typing", () =>
    {
        return TestSta.RunAsync(async () =>
        {
            var extractRoot = CreateTempTestDirectory();
            var pipeName = $"svs_keyboard_focus_{Guid.NewGuid():N}";
            Process? overlayController = null;
            var previousForegroundWindow = NativeWindowTest.GetForegroundWindow();
            var restoreCursor = NativeWindowTest.TryGetCursorPosition(out var originalCursor);

            var settings = new AppSettings();
            settings.Chat.ConnectAutomatically = false;
            settings.Chat.Layout = ChatLayout.Docked;
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            tab.IsChatVisible = true;
            tab.IsDockedChatPanelVisible = true;

            viewModel.Tabs.Add(tab);
            viewModel.VideoTabs.Add(tab);
            viewModel.SelectedTab = tab;

            var nativeOverlayPipeNameField = typeof(StreamTabViewModel).GetField(
                "nativeOverlayPipeName",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(nativeOverlayPipeNameField);
            nativeOverlayPipeNameField!.SetValue(tab, pipeName);
            var nativeOverlayProcessField = typeof(StreamTabViewModel).GetField(
                "nativeOverlayProcess",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(nativeOverlayProcessField);
            var playbackEngineField = typeof(StreamTabViewModel).GetField(
                "playbackEngine",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(playbackEngineField);

            var window = new MainWindow
            {
                DataContext = viewModel,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = 320,
                Top = 120,
                Width = 1100,
                Height = 720
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);

            try
            {
                var overlayDirectory = VlcOverlayBundledResourceExtractor.TryExtract(logger, extractRoot);
                Assert.True(!string.IsNullOrWhiteSpace(overlayDirectory));
                var controllerPath = VlcOverlayDirectoryResolver.GetControllerPath(overlayDirectory!);
                Assert.True(File.Exists(controllerPath));

                var startInfo = new ProcessStartInfo
                {
                    FileName = controllerPath,
                    WorkingDirectory = Path.GetDirectoryName(controllerPath)!,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("--channel");
                startInfo.ArgumentList.Add("albralelie");
                startInfo.ArgumentList.Add("--provider");
                startInfo.ArgumentList.Add("twitch");
                startInfo.ArgumentList.Add("--pipe-name");
                startInfo.ArgumentList.Add(pipeName);
                startInfo.ArgumentList.Add("--owner-process-id");
                startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

                overlayController = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                overlayController.OutputDataReceived += (_, _) => { };
                overlayController.ErrorDataReceived += (_, _) => { };
                Assert.True(overlayController.Start());
                overlayController.BeginOutputReadLine();
                overlayController.BeginErrorReadLine();
                nativeOverlayProcessField!.SetValue(tab, overlayController);

                window.Show();
                window.UpdateLayout();
                window.Topmost = true;
                window.Topmost = false;
                var windowHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                await NativeWindowTest.RequireForegroundAsync(
                    windowHandle,
                    TimeSpan.FromSeconds(2),
                    "native-overlay keyboard foreground precondition");

                var input = (System.Windows.Controls.TextBox)window.FindName("ChatInputTextBox");
                Assert.True(input.Focus());
                Assert.True(input.IsKeyboardFocusWithin);

                await NativeOverlayControllerTest.SendEventAsync(
                    pipeName,
                    NativeOverlayControllerTest.ChatInputFocusEvent,
                    value: 1,
                    TimeSpan.FromSeconds(3));
                await Task.Delay(100);

                NativeWindowTest.SendUnicodeText("blocked");
                await Task.Delay(100);
                Assert.Equal("", input.Text);
                Assert.Equal("", tab.OutgoingChatText);

                Assert.True(tab.TryReleaseNativeOverlayChatInputFocus());
                NativeWindowTest.SendUnicodeText("direct release works");
                await TestWait.UntilAsync(
                    () => input.Text == "direct release works" &&
                        tab.OutgoingChatText == "direct release works",
                    TimeSpan.FromSeconds(2),
                    "direct native overlay focus release did not restore WPF typing");
                input.Clear();
                Assert.Equal("", input.Text);
                Assert.Equal("", tab.OutgoingChatText);

                await NativeOverlayControllerTest.SendEventAsync(
                    pipeName,
                    NativeOverlayControllerTest.ChatInputFocusEvent,
                    value: 1,
                    TimeSpan.FromSeconds(3));
                await Task.Delay(100);
                NativeWindowTest.SendUnicodeText("blocked again");
                await Task.Delay(100);
                Assert.Equal("", input.Text);
                Assert.Equal("", tab.OutgoingChatText);

                var inputCenter = input.PointToScreen(new System.Windows.Point(
                    input.ActualWidth / 2,
                    input.ActualHeight / 2));
                var inputMouseDownReceived = false;
                input.PreviewMouseDown += (_, _) => inputMouseDownReceived = true;
                NativeWindowTest.SendLeftClick(
                    (int)Math.Round(inputCenter.X),
                    (int)Math.Round(inputCenter.Y));
                await TestWait.UntilAsync(
                    () => inputMouseDownReceived,
                    TimeSpan.FromSeconds(2),
                    "physical click did not route through the chat input");
                Assert.True(input.IsKeyboardFocusWithin);

                const string message = "docked input works";
                NativeWindowTest.SendUnicodeText(message);
                await TestWait.UntilAsync(
                    () => input.Text == message && tab.OutgoingChatText == message,
                    TimeSpan.FromSeconds(2),
                    "clicking the WPF chat input did not release native overlay keyboard capture");

                // Detaching the controller clears the tab's process/pipe tracking before the
                // asynchronous process shutdown completes. The real process must still release
                // its hook when WPF focus arrives during that interval.
                input.Clear();
                playbackEngineField!.SetValue(tab, new FakePlaybackEngine
                {
                    UsesNativeOverlayOverride = true,
                    NativeOverlayPipeNameOverride = pipeName
                });
                nativeOverlayProcessField!.SetValue(tab, null);
                nativeOverlayPipeNameField!.SetValue(tab, null);
                await NativeOverlayControllerTest.SendEventAsync(
                    pipeName,
                    NativeOverlayControllerTest.ChatInputFocusEvent,
                    value: 1,
                    TimeSpan.FromSeconds(3));
                await Task.Delay(100);

                NativeWindowTest.SendUnicodeText("detached controller works");
                await Task.Delay(100);
                Assert.Equal("", input.Text);

                var detachedMouseDownReceived = false;
                input.PreviewMouseDown += (_, _) => detachedMouseDownReceived = true;
                NativeWindowTest.SendLeftClick(
                    (int)Math.Round(inputCenter.X),
                    (int)Math.Round(inputCenter.Y));
                await TestWait.UntilAsync(
                    () => detachedMouseDownReceived && input.IsKeyboardFocusWithin,
                    TimeSpan.FromSeconds(2),
                    "physical click did not focus the chat input after controller tracking detached");
                NativeWindowTest.SendUnicodeText("detached controller works");
                await TestWait.UntilAsync(
                    () => input.Text == "detached controller works" &&
                        tab.OutgoingChatText == "detached controller works",
                    TimeSpan.FromSeconds(2),
                    "chat input did not release a still-running detached native controller");

                // Theatre uses the same WPF textbox after moving the window to the monitor bounds.
                // Re-arm the real hook there as well so this covers both user-facing modes.
                input.Clear();
                await NativeOverlayControllerTest.SendEventAsync(
                    pipeName,
                    NativeOverlayControllerTest.ChatInputFocusEvent,
                    value: 1,
                    TimeSpan.FromSeconds(3));
                await Task.Delay(100);
                ToggleMainWindowFullscreen(window, "Theatre");
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                await NativeWindowTest.RequireForegroundAsync(
                    windowHandle,
                    TimeSpan.FromSeconds(2),
                    "native-overlay theatre foreground precondition");

                var theatreInputCenter = input.PointToScreen(new System.Windows.Point(
                    input.ActualWidth / 2,
                    input.ActualHeight / 2));
                NativeWindowTest.SendLeftClick(
                    (int)Math.Round(theatreInputCenter.X),
                    (int)Math.Round(theatreInputCenter.Y));
                await TestWait.UntilAsync(
                    () => input.IsKeyboardFocusWithin,
                    TimeSpan.FromSeconds(2),
                    "Theatre click did not focus the chat input after controller tracking detached");
                NativeWindowTest.SendUnicodeText("detached theatre works");
                await TestWait.UntilAsync(
                    () => input.Text == "detached theatre works" &&
                        tab.OutgoingChatText == "detached theatre works",
                    TimeSpan.FromSeconds(2),
                    "Theatre chat input did not release a still-running detached native controller");
            }
            finally
            {
                if (restoreCursor)
                {
                    NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
                }

                if (overlayController is { HasExited: false })
                {
                    overlayController.Kill(entireProcessTree: true);
                    await overlayController.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }

                window.Close();
                if (previousForegroundWindow != IntPtr.Zero)
                {
                    NativeWindowTest.ActivateWindow(previousForegroundWindow);
                }

                await viewModel.DisposeAsync();
                overlayController?.Dispose();
                DeleteTempTestDirectory(extractRoot);
            }
        });
    }),
    ("native video double click exits theatre mode when the first click activates the window", () =>
    {
        return TestSta.RunAsync(async () =>
        {
            var settings = new AppSettings();
            settings.Chat.ConnectAutomatically = false;
            settings.Chat.Layout = ChatLayout.Overlay;
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());

            viewModel.Tabs.Add(tab);
            viewModel.VideoTabs.Add(tab);
            viewModel.SelectedTab = tab;

            var window = new MainWindow
            {
                DataContext = viewModel
            };
            RemoveMainWindowHandler<System.Windows.RoutedEventHandler>(
                window,
                nameof(System.Windows.Window.Loaded),
                "MainWindowLoaded");
            RemoveMainWindowHandler<System.ComponentModel.CancelEventHandler>(
                window,
                nameof(System.Windows.Window.Closing),
                "MainWindowClosing");
            SetMainWindowViewModel(window, viewModel);

            var fullscreenField = typeof(MainWindow).GetField(
                "fullscreen",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var toggleFullscreenMode = typeof(MainWindow).GetMethod(
                "ToggleFullscreenMode",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var fullscreenModeType = typeof(MainWindow).GetNestedType(
                "FullscreenMode",
                BindingFlags.NonPublic);
            var mouseHookPumpField = typeof(MainWindow).GetField(
                "mouseHookPump",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(fullscreenField);
            Assert.NotNull(toggleFullscreenMode);
            Assert.NotNull(fullscreenModeType);
            Assert.NotNull(mouseHookPumpField);

            var restoreCursor = NativeWindowTest.TryGetCursorPosition(out var originalCursor);
            var previousForegroundWindow = NativeWindowTest.GetForegroundWindow();
            IntPtr nativeVideoChild = IntPtr.Zero;
            System.Windows.Window? foregroundWindow = null;
            try
            {
                window.Show();
                window.UpdateLayout();
                await TestWait.UntilAsync(
                    () => mouseHookPumpField!.GetValue(window) is not null,
                    TimeSpan.FromSeconds(2));

                var theatreMode = Enum.Parse(fullscreenModeType!, "Theatre");
                toggleFullscreenMode!.Invoke(window, [theatreMode]);
                window.UpdateLayout();
                Assert.Equal(true, fullscreenField!.GetValue(window));

                var surface = FindVisualDescendants<StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface>(window)
                    .Single(candidate => ReferenceEquals(candidate.Tag, tab));
                Assert.True(surface.Handle != IntPtr.Zero);
                Assert.True(surface.ActualWidth > 0);
                Assert.True(surface.ActualHeight > 0);
                nativeVideoChild = NativeWindowTest.CreateVisibleChildWindow(
                    surface.Handle,
                    "StreamlinkVlcStudioVideoSurface");
                surface.SyncNativeBounds();

                var surfaceCenter = surface.PointToScreen(new System.Windows.Point(
                    surface.ActualWidth / 2,
                    surface.ActualHeight / 2));
                foregroundWindow = new System.Windows.Window
                {
                    Owner = window,
                    Left = surfaceCenter.X - 50,
                    Top = surfaceCenter.Y - 250,
                    Width = 100,
                    Height = 100,
                    ShowInTaskbar = false,
                    WindowStyle = System.Windows.WindowStyle.ToolWindow
                };
                foregroundWindow.Show();
                foregroundWindow.Activate();
                var foregroundHandle = new System.Windows.Interop.WindowInteropHelper(foregroundWindow).Handle;
                NativeWindowTest.ActivateWindow(foregroundHandle);
                await Task.Delay(100);
                // The precondition is only that the main window does not own the foreground, which
                // is what the double-click route used to require. Asserting on foregroundHandle
                // itself would make this depend on Windows granting a foreground change - it
                // refuses whenever an unrelated app is active, which is the normal desktop state.
                var mainWindowHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(
                    NativeWindowTest.GetForegroundWindow() != mainWindowHandle,
                    "The main window kept the foreground, so this cannot exercise the activating first click.");
                Assert.True(
                    NativeWindowTest.IsRootWindowAtPoint(
                        mainWindowHandle,
                        (int)Math.Round(surfaceCenter.X),
                        (int)Math.Round(surfaceCenter.Y)),
                    "The inactive main window did not own the double-click point. " +
                        NativeWindowTest.DescribeWindowAtPoint(
                            (int)Math.Round(surfaceCenter.X),
                            (int)Math.Round(surfaceCenter.Y)));

                await Task.Run(() => NativeWindowTest.SendLeftDoubleClick(
                    (int)Math.Round(surfaceCenter.X),
                    (int)Math.Round(surfaceCenter.Y)));

                await TestWait.UntilAsync(
                    () => fullscreenField.GetValue(window) is false,
                    TimeSpan.FromSeconds(2));
                Assert.Equal(System.Windows.Visibility.Visible, window.FindName("TitleBar") is System.Windows.UIElement titleBar
                    ? titleBar.Visibility
                    : System.Windows.Visibility.Collapsed);
            }
            finally
            {
                foregroundWindow?.Close();

                if (nativeVideoChild != IntPtr.Zero)
                {
                    NativeWindowTest.DestroyWindow(nativeVideoChild);
                }

                if (restoreCursor)
                {
                    NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
                }

                if (previousForegroundWindow != IntPtr.Zero)
                {
                    NativeWindowTest.ActivateWindow(previousForegroundWindow);
                }

                window.Close();
            }
        });
    }),
    ("multi-stream VLC plugin chat leaves two visible grid chats enabled", async () =>
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC",
            MultiStreamEnabled = true
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Overlay;
        settings.Chat.VlcOverlayDirectory = @"C:\Tools\vlc-overlay";

        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            UsesNativeOverlayOverride = true
        });
        var logger = new MemoryLogger();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var first = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var second = TestViewModels.CreateTab(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        viewModel.Tabs.Add(first);
        viewModel.Tabs.Add(second);
        viewModel.SelectedTab = first;
        first.SetVideoHandle(new IntPtr(1234));
        second.SetVideoHandle(new IntPtr(5678));
        await first.StartAsync(settings);
        await second.StartAsync(settings);

        await TestWait.UntilAsync(
            () => streamlink.StartCount == 2 &&
                playbackFactory.EnableNativeOverlayRequests.SequenceEqual(new[] { true, true }) &&
                first.IsChatVisible &&
                second.IsChatVisible,
            TimeSpan.FromSeconds(2));
        Assert.True(first.IsChatVisible);
        Assert.True(second.IsChatVisible);
        Assert.Equal(2, streamlink.StartCount);
        Assert.SequenceEqual(new[] { true, true }, playbackFactory.EnableNativeOverlayRequests);
        await viewModel.DisposeAsync();
    }),
    ("multi-view VLC plugin chat keeps two merged chats enabled", async () =>
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC",
            MultiStreamEnabled = false
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Overlay;
        settings.Chat.VlcOverlayDirectory = @"C:\Tools\vlc-overlay";

        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            UsesNativeOverlayOverride = true
        });
        var logger = new MemoryLogger();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var tabs = new[]
        {
            TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()),
            TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()),
        };

        for (var index = 0; index < tabs.Length; index++)
        {
            tabs[index].SetVideoHandle(new IntPtr(1234 + index));
            viewModel.Tabs.Add(tabs[index]);
        }

        viewModel.SelectedTab = tabs[0];
        Assert.True(viewModel.TryMergeTabsIntoMultiView([tabs[1]], tabs[0], tabs[1]));
        Assert.Equal(tabs[1], viewModel.SelectedTab);
        await viewModel.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(2));

        foreach (var tab in tabs)
        {
            await tab.StartAsync(settings);
            await viewModel.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
        }

        await TestWait.UntilAsync(
            () => streamlink.StartCount == 2 &&
                playbackFactory.EnableNativeOverlayRequests.SequenceEqual(new[] { true, true }) &&
                tabs.All(tab => tab.IsChatVisible),
            TimeSpan.FromSeconds(2));

        Assert.True(tabs.All(tab => tab.IsChatVisible));
        Assert.Equal(2, streamlink.StartCount);
        Assert.SequenceEqual(new[] { true, true }, playbackFactory.EnableNativeOverlayRequests);
        await viewModel.DisposeAsync();
    }),
    ("multi-view VLC plugin chat clears three native overlays without waiting one by one", async () =>
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC",
            MultiStreamEnabled = false
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Overlay;
        settings.Chat.VlcOverlayDirectory = @"C:\Tools\vlc-overlay";

        var pipeNames = new[]
        {
            $"svs_batch_first_{Guid.NewGuid():N}",
            $"svs_batch_second_{Guid.NewGuid():N}",
            $"svs_batch_third_{Guid.NewGuid():N}"
        };
        var engineIndex = -1;
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory(() =>
        {
            var index = Math.Clamp(Interlocked.Increment(ref engineIndex), 0, pipeNames.Length - 1);
            return new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeNames[index]
            };
        });
        var logger = new MemoryLogger();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var tabs = new[]
        {
            TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()),
            TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()),
            TestViewModels.CreateTab(
                StreamInputParser.Parse("xqc", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()),
        };

        for (var index = 0; index < tabs.Length; index++)
        {
            tabs[index].SetVideoHandle(new IntPtr(1234 + index));
            viewModel.Tabs.Add(tabs[index]);
        }

        viewModel.SelectedTab = tabs[0];
        Assert.True(viewModel.TryMergeTabsIntoMultiView([tabs[1]], tabs[0], tabs[1]));
        Assert.True(viewModel.TryMergeTabsIntoMultiView([tabs[2]], tabs[1], tabs[2]));

        await tabs[0].StartAsync(settings);
        await tabs[1].StartAsync(settings);

        await using var thirdServer = CreateNativeOverlayPipeServer(pipeNames[2]);
        var thirdMessageTask = ReadNativeOverlayPipeMessageAsync(thirdServer);
        await tabs[2].StartAsync(settings);

        var thirdMessage = await thirdMessageTask.WaitAsync(TimeSpan.FromSeconds(1));
        AssertNativeOverlayBlankFrame(thirdMessage);

        await using var firstServer = CreateNativeOverlayPipeServer(pipeNames[0]);
        await using var secondServer = CreateNativeOverlayPipeServer(pipeNames[1]);
        var firstMessageTask = ReadNativeOverlayPipeMessageAsync(firstServer);
        var secondMessageTask = ReadNativeOverlayPipeMessageAsync(secondServer);
        var firstMessage = await firstMessageTask.WaitAsync(TimeSpan.FromSeconds(1));
        var secondMessage = await secondMessageTask.WaitAsync(TimeSpan.FromSeconds(1));
        AssertNativeOverlayBlankFrame(firstMessage);
        AssertNativeOverlayBlankFrame(secondMessage);

        Assert.True(tabs.All(tab => !tab.IsChatVisible));
        Assert.Equal(3, streamlink.StartCount);
        Assert.SequenceEqual(new[] { true, true, true }, playbackFactory.EnableNativeOverlayRequests);
        await viewModel.DisposeAsync();
    }),
    ("multi-view VLC plugin chat disables all selected merged chats when three streams play", async () =>
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC",
            MultiStreamEnabled = false
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Overlay;
        settings.Chat.VlcOverlayDirectory = @"C:\Tools\vlc-overlay";

        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            UsesNativeOverlayOverride = true
        });
        var logger = new MemoryLogger();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var tabs = new[]
        {
            TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()),
            TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()),
            TestViewModels.CreateTab(
                StreamInputParser.Parse("xqc", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()),
        };

        for (var index = 0; index < tabs.Length; index++)
        {
            tabs[index].SetVideoHandle(new IntPtr(1234 + index));
            viewModel.Tabs.Add(tabs[index]);
        }

        viewModel.SelectedTab = tabs[0];
        Assert.True(viewModel.TryMergeTabsIntoMultiView([tabs[1]], tabs[0], tabs[1]));
        Assert.True(viewModel.TryMergeTabsIntoMultiView([tabs[2]], tabs[1], tabs[2]));
        Assert.Equal(tabs[2], viewModel.SelectedTab);
        await viewModel.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(2));

        foreach (var tab in tabs)
        {
            await tab.StartAsync(settings);
            await viewModel.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
        }

        await TestWait.UntilAsync(
            () => streamlink.StartCount == 3 &&
                playbackFactory.EnableNativeOverlayRequests.SequenceEqual(new[] { true, true, true }) &&
                tabs.All(tab => !tab.IsChatVisible),
            TimeSpan.FromSeconds(2));

        Assert.True(tabs.All(tab => !tab.IsChatVisible));
        Assert.Equal(3, streamlink.StartCount);
        Assert.SequenceEqual(new[] { true, true, true }, playbackFactory.EnableNativeOverlayRequests);

        await viewModel.ToggleChatCommand.ExecuteAsync();

        Assert.True(tabs.All(tab => !tab.IsChatVisible));
        Assert.Equal(3, streamlink.StartCount);
        Assert.SequenceEqual(new[] { true, true, true }, playbackFactory.EnableNativeOverlayRequests);
        await viewModel.DisposeAsync();
    }),
    ("multi-view VLC plugin chat restores policy-hidden chats when merged group drops below three", async () =>
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC",
            MultiStreamEnabled = false
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Overlay;
        settings.Chat.VlcOverlayDirectory = @"C:\Tools\vlc-overlay";

        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            UsesNativeOverlayOverride = true
        });
        var logger = new MemoryLogger();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var tabs = new[]
        {
            TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()),
            TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()),
            TestViewModels.CreateTab(
                StreamInputParser.Parse("xqc", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()),
        };

        for (var index = 0; index < tabs.Length; index++)
        {
            tabs[index].SetVideoHandle(new IntPtr(1234 + index));
            viewModel.Tabs.Add(tabs[index]);
        }

        viewModel.SelectedTab = tabs[0];
        Assert.True(viewModel.TryMergeTabsIntoMultiView([tabs[1]], tabs[0], tabs[1]));
        Assert.True(viewModel.TryMergeTabsIntoMultiView([tabs[2]], tabs[1], tabs[2]));

        foreach (var tab in tabs)
        {
            await tab.StartAsync(settings);
        }

        await TestWait.UntilAsync(
            () => streamlink.StartCount == 3 &&
                playbackFactory.EnableNativeOverlayRequests.SequenceEqual(new[] { true, true, true }) &&
                !tabs[0].IsChatVisible &&
                !tabs[1].IsChatVisible &&
                !tabs[2].IsChatVisible,
            TimeSpan.FromSeconds(2));
        Assert.Equal(false, tabs[0].IsChatVisible);
        Assert.Equal(false, tabs[1].IsChatVisible);
        Assert.Equal(false, tabs[2].IsChatVisible);

        Assert.True(viewModel.SetTabsDetached([tabs[2]], detached: true));

        Assert.True(tabs[0].IsChatVisible);
        Assert.True(tabs[1].IsChatVisible);
        Assert.True(tabs[2].IsChatVisible);
        Assert.Equal(3, streamlink.StartCount);
        Assert.SequenceEqual(new[] { true, true, true }, playbackFactory.EnableNativeOverlayRequests);
        await viewModel.DisposeAsync();
    }),
    ("multi-view VLC plugin chat restore leaves manually hidden chat disabled", async () =>
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC",
            MultiStreamEnabled = false
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Overlay;
        settings.Chat.VlcOverlayDirectory = @"C:\Tools\vlc-overlay";

        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            UsesNativeOverlayOverride = true
        });
        var logger = new MemoryLogger();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var tabs = new[]
        {
            TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()),
            TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()),
            TestViewModels.CreateTab(
                StreamInputParser.Parse("xqc", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()),
        };

        for (var index = 0; index < tabs.Length; index++)
        {
            tabs[index].SetVideoHandle(new IntPtr(1234 + index));
            viewModel.Tabs.Add(tabs[index]);
        }

        tabs[0].IsChatVisible = false;
        viewModel.SelectedTab = tabs[0];
        Assert.True(viewModel.TryMergeTabsIntoMultiView([tabs[1]], tabs[0], tabs[1]));
        Assert.True(viewModel.TryMergeTabsIntoMultiView([tabs[2]], tabs[1], tabs[2]));

        foreach (var tab in tabs)
        {
            await tab.StartAsync(settings);
        }

        await TestWait.UntilAsync(
            () => streamlink.StartCount == 3 &&
                playbackFactory.EnableNativeOverlayRequests.SequenceEqual(new[] { true, true, true }) &&
                !tabs[1].IsChatVisible &&
                !tabs[2].IsChatVisible,
            TimeSpan.FromSeconds(2));

        Assert.True(viewModel.SetTabsDetached([tabs[2]], detached: true));

        Assert.Equal(false, tabs[0].IsChatVisible);
        Assert.True(tabs[1].IsChatVisible);
        Assert.True(tabs[2].IsChatVisible);
        Assert.Equal(3, streamlink.StartCount);
        Assert.SequenceEqual(new[] { true, true, true }, playbackFactory.EnableNativeOverlayRequests);
        await viewModel.DisposeAsync();
    }),
    ("hiding VLC plugin overlay sends transparent frame to native overlay pipe", async () =>
    {
        var pipeName = $"svs_hide_{Guid.NewGuid():N}";
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            UsesNativeOverlayOverride = true,
            NativeOverlayPipeNameOverride = pipeName
        });
        var logger = new MemoryLogger();
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Overlay;
        settings.Chat.VlcOverlayDirectory = @"C:\Tools\vlc-overlay";

        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);

        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var readTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var buffer = new byte[40];
            await server.ReadExactlyAsync(buffer);
            return buffer;
        });

        tab.IsChatVisible = false;

        var message = await readTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0x564C4F56u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(0, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(4, 4)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(8, 4)));
        Assert.Equal(1, message[12]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(24, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(28, 4)));
        Assert.Equal(0, message[32]);
        Assert.True(message.AsSpan(36, 4).ToArray().All(value => value == 0));
        await tab.DisposeAsync();
    }),
    ("stream fullscreen hides docked chat UI without stopping chat", async () =>
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var chatFactory = new FakeChatClientFactory();
        var logger = new MemoryLogger();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            chatFactory,
            logger,
            action => action());
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            chatFactory,
            logger,
            action => action());
        var chatVisibilityChanges = 0;
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StreamTabViewModel.IsChatVisible))
            {
                chatVisibilityChanges++;
            }
        };

        viewModel.Tabs.Add(tab);
        viewModel.VideoTabs.Add(tab);
        viewModel.SelectedTab = tab;
        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => chatFactory.Client.Connected,
            TimeSpan.FromSeconds(1));

        Assert.Equal(true, tab.IsChatVisible);
        Assert.Equal(true, chatFactory.Client.Connected);
        Assert.Equal(true, viewModel.IsDockedChatVisible);

        viewModel.IsStreamOnlyFullscreenActive = true;

        Assert.Equal(true, tab.IsChatVisible);
        Assert.Equal(0, chatVisibilityChanges);
        Assert.Equal(true, chatFactory.Client.Connected);
        Assert.Equal(false, viewModel.IsDockedChatVisible);

        viewModel.IsStreamOnlyFullscreenActive = false;

        Assert.Equal(true, tab.IsChatVisible);
        Assert.Equal(true, chatFactory.Client.Connected);
        Assert.Equal(true, viewModel.IsDockedChatVisible);
        await tab.DisposeAsync();
    }),
    ("does not fall back to legacy Kick chat when native overlay controller is unavailable", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            UsesNativeOverlayOverride = true,
            NativeOverlayPipeNameOverride = "svs_test",
            NativeOverlayDirectoryOverride = @"C:\Missing\vlc-overlay"
        });
        var logger = new MemoryLogger();
        var chatFactory = new FakeChatClientFactory();
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/some-channel", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            chatFactory,
            logger,
            action => action());

        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Overlay;
        settings.Chat.VlcOverlayDirectory = @"C:\Missing\vlc-overlay";

        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);

        await TestWait.UntilAsync(
            () => tab.ChatMessages.Any(message => message.Message.Contains("Native VLC chat overlay controller was not found", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(2));
        Assert.Equal(false, chatFactory.Client.Connected);
        Assert.True(tab.ChatMessages.Any(message => message.Message.Contains("Native VLC chat overlay controller was not found", StringComparison.Ordinal)));        await tab.DisposeAsync();
    }),
    ("native VLC overlay chat waits until playback is resolved", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var streamlinkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamlinkReady = new TaskCompletionSource<IStreamTransportSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        streamlink.StartExternalHttpOverride = (_, cancellationToken) =>
        {
            streamlinkStarted.TrySetResult();
            return streamlinkReady.Task.WaitAsync(cancellationToken);
        };
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            UsesNativeOverlayOverride = true,
            NativeOverlayPipeNameOverride = "svs_test",
            NativeOverlayDirectoryOverride = Path.Combine(Path.GetTempPath(), $"missing-vlc-overlay-{Guid.NewGuid():N}")
        });
        var logger = new MemoryLogger();
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Overlay;
        settings.Chat.VlcOverlayDirectory = Path.Combine(Path.GetTempPath(), $"missing-vlc-overlay-{Guid.NewGuid():N}");

        var startTask = tab.StartAsync(settings);
        tab.SetVideoHandle(new IntPtr(1234));
        await streamlinkStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        await Task.Delay(200);

        Assert.Equal(false, tab.ChatMessages.Any(message => message.Message.Contains("Native VLC chat overlay controller was not found", StringComparison.Ordinal)));

        streamlinkReady.SetResult(new FakeTransportSession());
        await startTask;
        await TestWait.UntilAsync(
            () => tab.ChatMessages.Any(message => message.Message.Contains("Native VLC chat overlay controller was not found", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(2));
        await tab.DisposeAsync();
    }),
    ("closing selected tab does not wait for stalled playback shutdown", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var stopCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            StopCompletion = stopCompletion.Task
        });
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC",
            // This test stalls only the close-time engine stop. Keeping inactive
            // tabs running prevents the setup policy race from consuming that
            // same deliberate stall before the close command is exercised.
            KeepInactiveTabsRunning = true
        };
        settings.Chat.ConnectAutomatically = false;

        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        try
        {
            viewModel.Tabs.Add(tab);
            viewModel.VideoTabs.Add(tab);
            viewModel.SelectedTab = tab;
            tab.SetVideoHandle(new IntPtr(1234));
            await tab.StartAsync(settings).WaitAsync(TimeSpan.FromSeconds(2));

            await viewModel.CloseSelectedCommand.ExecuteAsync().WaitAsync(TimeSpan.FromMilliseconds(500));

            Assert.Equal(0, viewModel.Tabs.Count);
            Assert.Equal(0, viewModel.VideoTabs.Count);
            Assert.Equal<StreamTabViewModel?>(null, viewModel.SelectedTab);
            await playbackFactory.Engine!.StopStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            stopCompletion.TrySetResult();
            try
            {
                await tab.PlaybackCleanupIdleTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                await viewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
    }),
    ("disposing tab gives up on stalled playback shutdown", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var stopCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            StopCompletion = stopCompletion.Task
        });
        var logger = new MemoryLogger();
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;

        try
        {
            tab.SetVideoHandle(new IntPtr(1234));
            await tab.StartAsync(settings);

            await tab.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            await playbackFactory.Engine!.StopStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.Equal(PlaybackStatus.Stopped, tab.Status);
        }
        finally
        {
            stopCompletion.TrySetResult();
            await tab.PlaybackCleanupIdleTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }),
    ("selects adjacent tabs with wraparound", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var first = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var second = TestViewModels.CreateTab(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var third = TestViewModels.CreateTab(
            StreamInputParser.Parse("xqc", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        viewModel.Tabs.Add(first);
        viewModel.VideoTabs.Add(first);
        viewModel.Tabs.Add(second);
        viewModel.VideoTabs.Add(second);
        viewModel.Tabs.Add(third);
        viewModel.VideoTabs.Add(third);
        viewModel.SelectedTab = first;

        Assert.True(viewModel.SelectAdjacentTab(1));
        Assert.Equal(second, viewModel.SelectedTab);
        Assert.True(viewModel.SelectAdjacentTab(-1));
        Assert.Equal(first, viewModel.SelectedTab);
        Assert.True(viewModel.SelectAdjacentTab(-1));
        Assert.Equal(third, viewModel.SelectedTab);
        Assert.True(viewModel.SelectAdjacentTab(1));
        Assert.Equal(first, viewModel.SelectedTab);
        Assert.Equal(false, viewModel.SelectAdjacentTab(0));
        return Task.CompletedTask;
    }),
    ("tab navigation key policy allows left and right in windowed viewer mode", () =>
    {
        Assert.True(TabNavigationKeyPolicy.ShouldHandle(
            Key.Left,
            ModifierKeys.None,
            isFullscreen: false,
            isFullscreenModeActive: false,
            isSettingsOpen: false,
            focusedElement: null));
        Assert.True(TabNavigationKeyPolicy.ShouldHandle(
            Key.Right,
            ModifierKeys.None,
            isFullscreen: false,
            isFullscreenModeActive: false,
            isSettingsOpen: false,
            focusedElement: null));
        Assert.Equal(-1, TabNavigationKeyPolicy.DirectionFor(Key.Left));
        Assert.Equal(1, TabNavigationKeyPolicy.DirectionFor(Key.Right));
        Assert.Equal(0, TabNavigationKeyPolicy.DirectionFor(Key.Up));
        return Task.CompletedTask;
    }),
    ("tab navigation key policy preserves fullscreen, editing, modifier, and settings gates", async () =>
    {
        await TestSta.RunAsync(() =>
        {
            Assert.True(TabNavigationKeyPolicy.ShouldHandle(
                Key.Right,
                ModifierKeys.None,
                isFullscreen: true,
                isFullscreenModeActive: true,
                isSettingsOpen: false,
                focusedElement: null));
            Assert.Equal(false, TabNavigationKeyPolicy.ShouldHandle(
                Key.Right,
                ModifierKeys.None,
                isFullscreen: true,
                isFullscreenModeActive: false,
                isSettingsOpen: false,
                focusedElement: null));
            Assert.Equal(false, TabNavigationKeyPolicy.ShouldHandle(
                Key.Up,
                ModifierKeys.None,
                isFullscreen: false,
                isFullscreenModeActive: false,
                isSettingsOpen: false,
                focusedElement: null));
            Assert.Equal(false, TabNavigationKeyPolicy.ShouldHandle(
                Key.Right,
                ModifierKeys.Control,
                isFullscreen: false,
                isFullscreenModeActive: false,
                isSettingsOpen: false,
                focusedElement: null));
            Assert.Equal(false, TabNavigationKeyPolicy.ShouldHandle(
                Key.Right,
                ModifierKeys.None,
                isFullscreen: false,
                isFullscreenModeActive: false,
                isSettingsOpen: true,
                focusedElement: null));
            Assert.Equal(false, TabNavigationKeyPolicy.ShouldHandle(
                Key.Left,
                ModifierKeys.None,
                isFullscreen: false,
                isFullscreenModeActive: false,
                isSettingsOpen: false,
                focusedElement: new TextBox()));
        });
    }),
    ("main window shortcuts policy accepts only exact ctrl s for replay seekbar", () =>
    {
        Assert.True(ReplaySeekBarShortcutKeyPolicy.ShouldHandle(
            Key.S,
            ModifierKeys.Control));
        Assert.Equal(false, ReplaySeekBarShortcutKeyPolicy.ShouldHandle(
            Key.S,
            ModifierKeys.None));
        Assert.Equal(false, ReplaySeekBarShortcutKeyPolicy.ShouldHandle(
            Key.S,
            ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Equal(false, ReplaySeekBarShortcutKeyPolicy.ShouldHandle(
            Key.D,
            ModifierKeys.Control));
        return Task.CompletedTask;
    }),
    ("main window shortcuts replay seekbar toggle uses command path", () =>
    {
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action());

        var initialVisibility = viewModel.IsReplaySeekBarUiVisible;
        Assert.True(MainWindow.TryExecuteReplaySeekBarShortcut(viewModel));
        Assert.Equal(!initialVisibility, viewModel.IsReplaySeekBarUiVisible);
        Assert.Equal(
            viewModel.IsReplaySeekBarUiVisible ? "Replay seekbar shown" : "Replay seekbar hidden",
            viewModel.StatusMessage);
        Assert.True(MainWindow.TryExecuteReplaySeekBarShortcut(viewModel));
        Assert.Equal(initialVisibility, viewModel.IsReplaySeekBarUiVisible);
        Assert.Equal(
            viewModel.IsReplaySeekBarUiVisible ? "Replay seekbar shown" : "Replay seekbar hidden",
            viewModel.StatusMessage);
        Assert.Equal(false, MainWindow.TryExecuteReplaySeekBarShortcut(null));
        return Task.CompletedTask;
    }),
    ("closing inactive tab does not select it first", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var selected = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var inactive = TestViewModels.CreateTab(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        viewModel.Tabs.Add(selected);
        viewModel.VideoTabs.Add(selected);
        viewModel.Tabs.Add(inactive);
        viewModel.VideoTabs.Add(inactive);
        viewModel.SelectedTab = selected;

        Assert.True(viewModel.CloseTab(inactive));

        Assert.Equal(selected, viewModel.SelectedTab);
        Assert.Equal(1, viewModel.Tabs.Count);
        Assert.Equal(selected, viewModel.Tabs[0]);
        return Task.CompletedTask;
    }),
    ("removing selected tab clears its visual selection", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        viewModel.Tabs.Add(tab);
        viewModel.VideoTabs.Add(tab);
        viewModel.SelectedTab = tab;

        Assert.Equal(true, tab.IsSelected);

        viewModel.Tabs.Remove(tab);

        Assert.Equal<StreamTabViewModel?>(null, viewModel.SelectedTab);
        Assert.Equal(false, tab.IsSelected);
        return Task.CompletedTask;
    }),
    ("title-bar exit button closes tabs and hides to tray", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings
            {
                // This test covers the tray path; the default close behavior is Exit.
                CloseBehavior = WindowCloseBehavior.MinimizeToTray
            };
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var first = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var second = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());

            viewModel.Tabs.Add(first);
            viewModel.VideoTabs.Add(first);
            viewModel.Tabs.Add(second);
            viewModel.VideoTabs.Add(second);
            viewModel.SelectedTab = first;

            var window = new MainWindow
            {
                Width = 900,
                Height = 560,
                DataContext = viewModel
            };
            RemoveMainWindowHandler<System.Windows.RoutedEventHandler>(window, nameof(System.Windows.Window.Loaded), "MainWindowLoaded");
            RemoveMainWindowHandler<EventHandler>(window, nameof(System.Windows.Window.SourceInitialized), "MainWindowSourceInitialized");
            RemoveMainWindowHandler<EventHandler>(window, nameof(System.Windows.Window.Closed), "MainWindowClosed");
            SetMainWindowViewModel(window, viewModel);

            var closeButtonClick = typeof(MainWindow).GetMethod(
                "CloseWindowButton_Click",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var exitRequestedField = typeof(MainWindow).GetField(
                "exitRequested",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(closeButtonClick);
            Assert.NotNull(exitRequestedField);
            Assert.Equal(false, (bool)exitRequestedField!.GetValue(window)!);

            try
            {
                window.Show();
                window.UpdateLayout();

                closeButtonClick!.Invoke(window, [window, new System.Windows.RoutedEventArgs()]);

                Assert.Equal(false, (bool)exitRequestedField.GetValue(window)!);
                Assert.Equal(0, viewModel.Tabs.Count);
                Assert.Equal(0, viewModel.VideoTabs.Count);
                Assert.Equal<StreamTabViewModel?>(null, viewModel.SelectedTab);
                Assert.Equal(false, window.IsVisible);
                Assert.Equal(false, window.ShowInTaskbar);
            }
            finally
            {
                RemoveMainWindowHandler<System.ComponentModel.CancelEventHandler>(
                    window,
                    nameof(System.Windows.Window.Closing),
                    "MainWindowClosing");
                window.Close();
            }

            return Task.CompletedTask;
        });
    })
    ];
}

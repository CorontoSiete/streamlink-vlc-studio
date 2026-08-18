internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> HomeAndBrowseUi { get; } =
    [
    ("ctrl tab strip drop of a merged group over another merged group merges both groups", () =>
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
            var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
            var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
            var third = TestViewModels.CreateTab(StreamInputParser.Parse("xqc", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
            var fourth = TestViewModels.CreateTab(StreamInputParser.Parse("shroud", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());

            viewModel.Tabs.Add(first);
            viewModel.Tabs.Add(second);
            viewModel.Tabs.Add(third);
            viewModel.Tabs.Add(fourth);
            viewModel.SelectedTab = first;
            Assert.True(viewModel.TryMergeTabsIntoMultiView([second], first, second));
            Assert.True(viewModel.TryMergeTabsIntoMultiView([fourth], third, fourth));
            viewModel.SelectedTab = second;

            var window = new MainWindow
            {
                Width = 1400,
                Height = 560,
                Topmost = true,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);
            SetMainWindowControlModifier(window, pressed: true);

            var nativePointType = typeof(MainWindow).GetNestedType("NativePoint", BindingFlags.NonPublic);
            var continueDrag = typeof(MainWindow).GetMethod(
                "TryContinueTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                [nativePointType!, typeof(bool)],
                null);
            var completeDrag = typeof(MainWindow).GetMethod(
                "TryCompleteTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var hasExceededDragDistance = typeof(MainWindow).GetMethod(
                "HasExceededTabDetachDragDistance",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tryGetTabAtScreenPoint = typeof(MainWindow).GetMethod(
                "TryGetTabAtTabStripScreenPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var leftButtonField = typeof(MainWindow).GetField(
                "isLeftMouseButtonPressed",
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
            Assert.NotNull(nativePointType);
            Assert.NotNull(continueDrag);
            Assert.NotNull(completeDrag);
            Assert.NotNull(hasExceededDragDistance);
            Assert.NotNull(tryGetTabAtScreenPoint);
            Assert.NotNull(leftButtonField);
            Assert.NotNull(tabDetachDragTabField);
            Assert.NotNull(tabDetachDragSourceField);
            Assert.NotNull(tabDetachDragStartPointField);
            Assert.NotNull(tabDetachDragStartScreenPointField);

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                leftButtonField!.SetValue(window, (Func<bool>)(() => true));

                var sourceGroupItem = FindTabStripListBoxItem(window, second);
                var targetGroupItem = FindTabStripListBoxItem(window, third);
                Assert.True(sourceGroupItem.DataContext is TabStripItemViewModel { IsGroup: true } sourceTabStripItem &&
                    sourceTabStripItem.Contains(first) &&
                    sourceTabStripItem.Contains(second));
                Assert.True(targetGroupItem.DataContext is TabStripItemViewModel { IsGroup: true } targetTabStripItem &&
                    targetTabStripItem.Contains(third) &&
                    targetTabStripItem.Contains(fourth));

                var startPoint = sourceGroupItem.TransformToAncestor(window).Transform(new System.Windows.Point(
                    sourceGroupItem.ActualWidth / 2,
                    sourceGroupItem.ActualHeight / 2));
                var startScreenPoint = sourceGroupItem.PointToScreen(new System.Windows.Point(
                    sourceGroupItem.ActualWidth / 2,
                    sourceGroupItem.ActualHeight / 2));
                var nativeStartPoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(startScreenPoint.X), (int)Math.Round(startScreenPoint.Y)]);
                Assert.NotNull(nativeStartPoint);
                tabDetachDragSourceField!.SetValue(window, sourceGroupItem);
                tabDetachDragTabField!.SetValue(window, second);
                tabDetachDragStartPointField!.SetValue(window, startPoint);
                tabDetachDragStartScreenPointField!.SetValue(window, nativeStartPoint);

                var targetPoint = targetGroupItem.PointToScreen(new System.Windows.Point(
                    targetGroupItem.ActualWidth / 2,
                    targetGroupItem.ActualHeight / 2));
                var targetNativePoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(targetPoint.X), (int)Math.Round(targetPoint.Y)]);
                Assert.NotNull(targetNativePoint);

                Assert.True(
                    (bool)hasExceededDragDistance!.Invoke(window, [targetNativePoint])!,
                    "Expected grouped tab-to-grouped tab drag to exceed the system drag threshold.");
                var targetArgs = new object?[] { targetNativePoint, null };
                Assert.True(
                    (bool)tryGetTabAtScreenPoint!.Invoke(window, targetArgs)!,
                    "Expected the target merged tab strip item under the simulated drag point.");
                Assert.Equal(third, targetArgs[1]);

                Assert.True(
                    (bool)continueDrag!.Invoke(window, [targetNativePoint, true])!,
                    "Expected grouped tab drag to stay active over the target group until mouse-up.");
                Assert.SequenceEqual(new[] { "albralelie", "summit1g", "xqc", "shroud" }, TabChannels(viewModel.Tabs));
                Assert.Equal(2, viewModel.TabStripItems.Count);

                Assert.True(
                    (bool)completeDrag!.Invoke(window, [targetNativePoint])!,
                    "Expected mouse-up over the target group to merge both multiview groups.");

                Assert.SequenceEqual(new[] { "xqc", "shroud", "albralelie", "summit1g" }, TabChannels(viewModel.Tabs));
                Assert.Equal(1, viewModel.TabStripItems.Count(item =>
                    item.Contains(first) &&
                    item.Contains(second) &&
                    item.Contains(third) &&
                    item.Contains(fourth)));
                Assert.Equal(second, viewModel.SelectedTab);
                Assert.Equal(0, third.VideoGridRow);
                Assert.Equal(0, third.VideoGridColumn);
                Assert.Equal(0, fourth.VideoGridRow);
                Assert.Equal(1, fourth.VideoGridColumn);
                Assert.Equal(1, first.VideoGridRow);
                Assert.Equal(0, first.VideoGridColumn);
                Assert.Equal(1, second.VideoGridRow);
                Assert.Equal(1, second.VideoGridColumn);
                Assert.True(third.IsMergedTabGroupMember);
                Assert.True(third.IsFirstMergedTabGroupMember);
                Assert.True(second.IsMergedTabGroupMember);
                Assert.True(second.IsLastMergedTabGroupMember);
                Assert.Equal(false, settings.MultiStreamEnabled);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }),
    ("ctrl tab strip drag merges with the release tab after crossing intermediate tabs", () =>
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
            var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
            var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
            var third = TestViewModels.CreateTab(StreamInputParser.Parse("xqc", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
            var fourth = TestViewModels.CreateTab(StreamInputParser.Parse("shroud", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());

            viewModel.Tabs.Add(first);
            viewModel.Tabs.Add(second);
            viewModel.Tabs.Add(third);
            viewModel.Tabs.Add(fourth);
            viewModel.SelectedTab = fourth;

            var window = new MainWindow
            {
                Width = 1400,
                Height = 560,
                Topmost = true,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);
            SetMainWindowControlModifier(window, pressed: true);

            var nativePointType = typeof(MainWindow).GetNestedType("NativePoint", BindingFlags.NonPublic);
            var continueDrag = typeof(MainWindow).GetMethod(
                "TryContinueTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                [nativePointType!, typeof(bool)],
                null);
            var completeDrag = typeof(MainWindow).GetMethod(
                "TryCompleteTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var hasExceededDragDistance = typeof(MainWindow).GetMethod(
                "HasExceededTabDetachDragDistance",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tryGetTabAtScreenPoint = typeof(MainWindow).GetMethod(
                "TryGetTabAtTabStripScreenPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var leftButtonField = typeof(MainWindow).GetField(
                "isLeftMouseButtonPressed",
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
            Assert.NotNull(nativePointType);
            Assert.NotNull(continueDrag);
            Assert.NotNull(completeDrag);
            Assert.NotNull(hasExceededDragDistance);
            Assert.NotNull(tryGetTabAtScreenPoint);
            Assert.NotNull(leftButtonField);
            Assert.NotNull(tabDetachDragTabField);
            Assert.NotNull(tabDetachDragSourceField);
            Assert.NotNull(tabDetachDragStartPointField);
            Assert.NotNull(tabDetachDragStartScreenPointField);

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                leftButtonField!.SetValue(window, (Func<bool>)(() => true));

                var firstItem = FindTabStripListBoxItem(window, first);
                var thirdItem = FindTabStripListBoxItem(window, third);
                var fourthItem = FindTabStripListBoxItem(window, fourth);
                var startPoint = fourthItem.TransformToAncestor(window).Transform(new System.Windows.Point(
                    fourthItem.ActualWidth / 2,
                    fourthItem.ActualHeight / 2));
                var startScreenPoint = fourthItem.PointToScreen(new System.Windows.Point(
                    fourthItem.ActualWidth / 2,
                    fourthItem.ActualHeight / 2));
                var nativeStartPoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(startScreenPoint.X), (int)Math.Round(startScreenPoint.Y)]);
                Assert.NotNull(nativeStartPoint);
                tabDetachDragSourceField!.SetValue(window, fourthItem);
                tabDetachDragTabField!.SetValue(window, fourth);
                tabDetachDragStartPointField!.SetValue(window, startPoint);
                tabDetachDragStartScreenPointField!.SetValue(window, nativeStartPoint);

                var thirdPoint = thirdItem.PointToScreen(new System.Windows.Point(
                    thirdItem.ActualWidth / 2,
                    thirdItem.ActualHeight / 2));
                var thirdNativePoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(thirdPoint.X), (int)Math.Round(thirdPoint.Y)]);
                Assert.NotNull(thirdNativePoint);
                var thirdTargetArgs = new object?[] { thirdNativePoint, null };
                Assert.True(
                    (bool)hasExceededDragDistance!.Invoke(window, [thirdNativePoint])!,
                    "Expected drag from the fourth tab to the third tab to exceed the system drag threshold.");
                Assert.True(
                    (bool)tryGetTabAtScreenPoint!.Invoke(window, thirdTargetArgs)!,
                    "Expected the third tab strip item under the intermediate drag point.");
                Assert.Equal(third, thirdTargetArgs[1]);

                Assert.True(
                    (bool)continueDrag!.Invoke(window, [thirdNativePoint, true])!,
                    "Expected tab drag to stay active while crossing the third tab.");
                Assert.SequenceEqual(new[] { first, second, third, fourth }, viewModel.Tabs.ToArray());
                Assert.Equal(false, third.IsMergedTabGroupMember);
                Assert.Equal(false, fourth.IsMergedTabGroupMember);
                Assert.Equal(fourth, tabDetachDragTabField!.GetValue(window));

                var firstPoint = firstItem.PointToScreen(new System.Windows.Point(
                    firstItem.ActualWidth / 2,
                    firstItem.ActualHeight / 2));
                var firstNativePoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(firstPoint.X), (int)Math.Round(firstPoint.Y)]);
                Assert.NotNull(firstNativePoint);
                var firstTargetArgs = new object?[] { firstNativePoint, null };
                Assert.True(
                    (bool)tryGetTabAtScreenPoint.Invoke(window, firstTargetArgs)!,
                    "Expected the first tab strip item under the release point.");
                Assert.Equal(first, firstTargetArgs[1]);

                Assert.True(
                    (bool)completeDrag!.Invoke(window, [firstNativePoint])!,
                    "Expected releasing the fourth tab over the first tab to merge those two tabs.");

                Assert.SequenceEqual(new[] { first, fourth, second, third }, viewModel.Tabs.ToArray());
                Assert.Equal(1, viewModel.TabStripItems.Count(item => item.Contains(first) && item.Contains(fourth)));
                Assert.SequenceEqual(new[] { fourth, first }, viewModel.VideoTabs.ToArray());
                Assert.Equal(fourth, viewModel.SelectedTab);
                Assert.Equal(0, first.VideoGridColumn);
                Assert.Equal(1, fourth.VideoGridColumn);
                Assert.True(first.IsMergedTabGroupMember);
                Assert.True(first.IsFirstMergedTabGroupMember);
                Assert.Equal(false, first.IsLastMergedTabGroupMember);
                Assert.True(fourth.IsMergedTabGroupMember);
                Assert.Equal(false, fourth.IsFirstMergedTabGroupMember);
                Assert.True(fourth.IsLastMergedTabGroupMember);
                Assert.Equal(false, third.IsMergedTabGroupMember);
                Assert.Equal(false, settings.MultiStreamEnabled);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }),
    ("detached selected tab leaves main video host until it is reattached", () =>
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

        var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());

        viewModel.Tabs.Add(first);
        viewModel.Tabs.Add(second);
        viewModel.SelectedTab = first;

        Assert.True(first.IsVideoVisible);
        Assert.True(viewModel.VideoTabs.Contains(first));
        Assert.Equal(false, viewModel.IsSelectedTabDetached);

        Assert.True(viewModel.SetTabsDetached([first], detached: true));

        Assert.True(first.IsDetached);
        Assert.True(viewModel.IsSelectedTabDetached);
        Assert.Equal(false, first.IsVideoVisible);
        Assert.Equal(false, viewModel.VideoTabs.Contains(first));
        Assert.Equal(0, viewModel.VideoTabs.Count);

        Assert.True(viewModel.SetTabsDetached([first], detached: false));

        Assert.Equal(false, first.IsDetached);
        Assert.Equal(false, viewModel.IsSelectedTabDetached);
        Assert.True(first.IsVideoVisible);
        Assert.True(viewModel.VideoTabs.Contains(first));
        Assert.Equal(first, viewModel.SelectedTab);
        return Task.CompletedTask;
    }),
    ("multi-stream layout skips detached tabs on the selected page", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            MultiStreamEnabled = true
        };
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var third = TestViewModels.CreateTab(StreamInputParser.Parse("xqc", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var fourth = TestViewModels.CreateTab(StreamInputParser.Parse("forsen", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var fifth = TestViewModels.CreateTab(StreamInputParser.Parse("eslcs", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());

        foreach (var tab in new[] { first, second, third, fourth, fifth })
        {
            viewModel.Tabs.Add(tab);
        }

        viewModel.SelectedTab = second;
        Assert.True(viewModel.SetTabsDetached([third], detached: true));

        Assert.True(first.IsVideoVisible);
        Assert.True(second.IsVideoVisible);
        Assert.Equal(false, third.IsVideoVisible);
        Assert.True(fourth.IsVideoVisible);
        Assert.True(fifth.IsVideoVisible);
        Assert.Equal(false, viewModel.VideoTabs.Contains(third));
        Assert.SequenceEqual(new[] { first, second, fourth, fifth }, viewModel.VideoTabs.ToArray());
        return Task.CompletedTask;
    }),
    ("multi-stream tab strip merges current video page without blocking tab switching", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            MultiStreamEnabled = true
        };
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var tabs = Enumerable.Range(1, 18)
            .Select(index => TestViewModels.CreateTab(
                StreamInputParser.Parse($"streamer{index}", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()))
            .ToArray();

        foreach (var tab in tabs)
        {
            viewModel.Tabs.Add(tab);
        }

        viewModel.SelectedTab = tabs[14];

        foreach (var tab in tabs.Take(16))
        {
            Assert.True(tab.IsMergedTabGroupMember);
        }

        Assert.True(tabs[0].IsFirstMergedTabGroupMember);
        Assert.Equal(false, tabs[0].IsLastMergedTabGroupMember);
        Assert.Equal(false, tabs[14].IsFirstMergedTabGroupMember);
        Assert.Equal(false, tabs[14].IsLastMergedTabGroupMember);
        Assert.True(tabs[15].IsLastMergedTabGroupMember);
        Assert.Equal(false, tabs[16].IsMergedTabGroupMember);
        Assert.Equal(false, tabs[17].IsMergedTabGroupMember);

        Assert.True(viewModel.SelectAdjacentTab(1));

        Assert.Equal(tabs[15], viewModel.SelectedTab);
        Assert.True(tabs[0].IsMergedTabGroupMember);
        Assert.True(tabs[15].IsLastMergedTabGroupMember);

        Assert.True(viewModel.SelectAdjacentTab(1));

        Assert.Equal(tabs[16], viewModel.SelectedTab);
        foreach (var tab in tabs.Take(16))
        {
            Assert.Equal(false, tab.IsMergedTabGroupMember);
        }

        Assert.True(tabs[16].IsMergedTabGroupMember);
        Assert.True(tabs[16].IsFirstMergedTabGroupMember);
        Assert.True(tabs[17].IsMergedTabGroupMember);
        Assert.True(tabs[17].IsLastMergedTabGroupMember);

        viewModel.IsMultiStreamEnabled = false;

        foreach (var tab in tabs)
        {
            Assert.Equal(false, tab.IsMergedTabGroupMember);
            Assert.Equal(false, tab.IsFirstMergedTabGroupMember);
            Assert.Equal(false, tab.IsLastMergedTabGroupMember);
        }

        return Task.CompletedTask;
    }),
    ("ordinary multi-stream tab drag targets one tab unless explicitly grouped", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            MultiStreamEnabled = true
        };
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var tabs = Enumerable.Range(1, 18)
            .Select(index => TestViewModels.CreateTab(
                StreamInputParser.Parse($"streamer{index}", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()))
            .ToArray();

        foreach (var tab in tabs)
        {
            viewModel.Tabs.Add(tab);
        }

        viewModel.SelectedTab = tabs[14];

        Assert.SequenceEqual(new[] { tabs[4] }, viewModel.GetPictureInPictureDragTabs(tabs[4]).ToArray());
        Assert.True(viewModel.TabStripItems.All(item => !item.IsGroup));

        viewModel.IsMultiStreamEnabled = false;
        Assert.True(viewModel.TryMergeTabsIntoMultiView([tabs[5]], tabs[4], tabs[5]));
        Assert.SequenceEqual(new[] { tabs[4], tabs[5] }, viewModel.GetPictureInPictureDragTabs(tabs[5]).ToArray());
        Assert.True(viewModel.TabStripItems.Any(item => item.IsGroup && item.Contains(tabs[4]) && item.Contains(tabs[5])));
        viewModel.IsMultiStreamEnabled = true;

        viewModel.SelectedTab = tabs[16];

        Assert.SequenceEqual(new[] { tabs[17] }, viewModel.GetPictureInPictureDragTabs(tabs[17]).ToArray());

        viewModel.IsMultiStreamEnabled = false;

        Assert.SequenceEqual(new[] { tabs[17] }, viewModel.GetPictureInPictureDragTabs(tabs[17]).ToArray());
        return Task.CompletedTask;
    }),
    ("detaching ordinary multi-stream tab leaves other page tabs docked", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            MultiStreamEnabled = true
        };
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var third = TestViewModels.CreateTab(StreamInputParser.Parse("xqc", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var fourth = TestViewModels.CreateTab(StreamInputParser.Parse("forsen", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var fifth = TestViewModels.CreateTab(StreamInputParser.Parse("eslcs", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var tabs = new[] { first, second, third, fourth, fifth };

        foreach (var tab in tabs)
        {
            viewModel.Tabs.Add(tab);
        }

        viewModel.SelectedTab = second;
        var detachedTabs = viewModel.GetPictureInPictureDragTabs(second).ToArray();

        Assert.SequenceEqual(new[] { second }, detachedTabs);
        Assert.True(viewModel.SetTabsDetached(detachedTabs, detached: true));

        Assert.True(second.IsDetached);
        Assert.Equal(false, second.IsVideoVisible);
        foreach (var tab in new[] { first, third, fourth, fifth })
        {
            Assert.Equal(false, tab.IsDetached);
        }

        Assert.True(viewModel.IsSelectedTabDetached);
        viewModel.SelectedTab = first;
        Assert.True(first.IsVideoVisible);
        Assert.True(viewModel.VideoTabs.Contains(first));
        Assert.Equal(false, second.IsVideoVisible);
        Assert.Equal(false, viewModel.VideoTabs.Contains(second));

        Assert.True(viewModel.SetTabsDetached(detachedTabs, detached: false));

        foreach (var tab in tabs)
        {
            Assert.Equal(false, tab.IsDetached);
        }

        Assert.Equal(false, viewModel.IsSelectedTabDetached);
        Assert.Equal(first, viewModel.SelectedTab);
        return Task.CompletedTask;
    }),
    ("registered picture-in-picture group stays merged after multi-stream detach", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            MultiStreamEnabled = true
        };
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var third = TestViewModels.CreateTab(StreamInputParser.Parse("xqc", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var tabs = new[] { first, second, third };

        foreach (var tab in tabs)
        {
            viewModel.Tabs.Add(tab);
        }

        viewModel.SelectedTab = second;
        var mergedTabs = tabs;

        viewModel.SetPictureInPictureTabGroup(mergedTabs);
        Assert.SequenceEqual(tabs, viewModel.GetPictureInPictureDragTabs(second).ToArray());
        Assert.True(viewModel.SetTabsDetached(mergedTabs, detached: true));

        Assert.True(first.IsMergedTabGroupMember);
        Assert.True(first.IsFirstMergedTabGroupMember);
        Assert.Equal(false, first.IsLastMergedTabGroupMember);
        Assert.True(second.IsMergedTabGroupMember);
        Assert.Equal(false, second.IsFirstMergedTabGroupMember);
        Assert.Equal(false, second.IsLastMergedTabGroupMember);
        Assert.True(third.IsMergedTabGroupMember);
        Assert.Equal(false, third.IsFirstMergedTabGroupMember);
        Assert.True(third.IsLastMergedTabGroupMember);
        Assert.SequenceEqual(tabs, viewModel.GetPictureInPictureDragTabs(third).ToArray());

        viewModel.SelectedTab = third;

        Assert.SequenceEqual(tabs, viewModel.GetPictureInPictureDragTabs(first).ToArray());
        Assert.True(first.IsFirstMergedTabGroupMember);
        Assert.True(third.IsLastMergedTabGroupMember);
        return Task.CompletedTask;
    }),
    ("home view hides main video surface without clearing playback handle", () =>
    {
        return TestSta.RunAsync(async () =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackEngine = new FakePlaybackEngine();
            var playbackFactory = new FakePlaybackEngineFactory(() => playbackEngine);
            var logger = new MemoryLogger();
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
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

            viewModel.Tabs.Add(tab);
            viewModel.SelectedTab = tab;

            var window = new MainWindow
            {
                Width = 900,
                Height = 560,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var initialSurfaces = FindVisualDescendants<VideoSurface>(window).ToArray();
                Assert.Equal(1, initialSurfaces.Length);
                Assert.True(ReferenceEquals(tab, initialSurfaces[0].Tag));
                Assert.Equal<object?>(null, window.FindName("VideoStatusOverlay"));

                await tab.StartAsync(settings);
                var initialVideoHandle = playbackEngine.VideoHandle;
                Assert.True(initialVideoHandle != IntPtr.Zero);
                viewModel.SelectHomeCommand.Execute(null);
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.Equal<StreamTabViewModel?>(null, viewModel.SelectedTab);
                Assert.True(viewModel.IsHomeVisible);
                Assert.Equal(false, tab.IsVideoVisible);
                Assert.SequenceEqual(new[] { tab }, viewModel.VideoTabs.ToArray());
                var hiddenSurfaces = FindVisualDescendants<VideoSurface>(window).ToArray();
                Assert.Equal(1, hiddenSurfaces.Length);
                Assert.Equal(false, hiddenSurfaces[0].IsVisible);
                Assert.Equal(initialVideoHandle, hiddenSurfaces[0].Handle);
                Assert.Equal(initialVideoHandle, playbackEngine.VideoHandle);
                Assert.Equal(false, playbackEngine.VideoHandleHistory.Contains(IntPtr.Zero));

                viewModel.SelectedTab = tab;
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.True(tab.IsVideoVisible);
                Assert.True(viewModel.VideoTabs.Contains(tab));
                Assert.Equal(initialVideoHandle, playbackEngine.VideoHandle);
            }
            finally
            {
                window.Close();
                await tab.DisposeAsync();
            }
        });
    }),
    ("home selected during unresolved stream start keeps playback on hidden main surface", () =>
    {
        return TestSta.RunAsync(async () =>
        {
            var streamlink = new FakeStreamlinkService();
            var streamlinkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var streamlinkReady = new TaskCompletionSource<IStreamTransportSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            streamlink.StartExternalHttpOverride = (_, cancellationToken) =>
            {
                streamlinkStarted.TrySetResult();
                return streamlinkReady.Task.WaitAsync(cancellationToken);
            };
            var playbackEngine = new FakePlaybackEngine();
            var playbackFactory = new FakePlaybackEngineFactory(() => playbackEngine);
            var logger = new MemoryLogger();
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
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
                StreamInputParser.Parse("linny", PlatformKind.Twitch),
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
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var initialSurfaces = FindVisualDescendants<VideoSurface>(window).ToArray();
                Assert.Equal(1, initialSurfaces.Length);
                Assert.True(ReferenceEquals(tab, initialSurfaces[0].Tag));

                var startTask = tab.StartAsync(settings);
                await streamlinkStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
                await TestWait.UntilAsync(
                    () => playbackEngine.VideoHandle != IntPtr.Zero,
                    TimeSpan.FromMilliseconds(500));
                var initialVideoHandle = playbackEngine.VideoHandle;

                viewModel.SelectHomeCommand.Execute(null);
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.Equal<StreamTabViewModel?>(null, viewModel.SelectedTab);
                Assert.True(viewModel.IsHomeVisible);
                Assert.SequenceEqual(new[] { tab }, viewModel.VideoTabs.ToArray());
                var hiddenSurfaces = FindVisualDescendants<VideoSurface>(window).ToArray();
                Assert.Equal(1, hiddenSurfaces.Length);
                Assert.Equal(false, hiddenSurfaces[0].IsVisible);
                Assert.Equal(initialVideoHandle, hiddenSurfaces[0].Handle);
                Assert.Equal(initialVideoHandle, playbackEngine.VideoHandle);

                streamlinkReady.SetResult(new FakeTransportSession());
                await startTask.WaitAsync(TimeSpan.FromMilliseconds(500));

                Assert.Equal(PlaybackStatus.Playing, tab.Status);
                Assert.Equal("", tab.ErrorMessage);
                Assert.Equal(true, playbackEngine.Played);
                Assert.Equal(initialVideoHandle, playbackEngine.VideoHandle);
                Assert.Equal(false, playbackEngine.VideoHandleHistory.Contains(IntPtr.Zero));

                viewModel.SelectedTab = tab;
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.Equal(PlaybackStatus.Playing, tab.Status);
                Assert.Equal(initialVideoHandle, playbackEngine.VideoHandle);
                Assert.True(FindVisualDescendants<VideoSurface>(window).Any(surface =>
                    ReferenceEquals(surface.Tag, tab) &&
                    surface.Handle == playbackEngine.VideoHandle));
            }
            finally
            {
                window.Close();
                await tab.DisposeAsync();
            }
        });
    }),
    ("multi-stream mode toggle keeps live video surfaces mounted and updates current layout", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            MultiStreamEnabled = true
        };
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var third = TestViewModels.CreateTab(StreamInputParser.Parse("xqc", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());

        viewModel.Tabs.Add(first);
        viewModel.Tabs.Add(second);
        viewModel.Tabs.Add(third);
        viewModel.SelectedTab = second;

        Assert.Equal(3, viewModel.VideoTabs.Count);
        Assert.True(viewModel.VideoTabs.Contains(first));
        Assert.True(viewModel.VideoTabs.Contains(second));
        Assert.True(viewModel.VideoTabs.Contains(third));

        var removedSurfaceTabs = new List<StreamTabViewModel>();
        viewModel.VideoTabs.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is null)
            {
                return;
            }

            foreach (StreamTabViewModel tab in e.OldItems)
            {
                removedSurfaceTabs.Add(tab);
            }
        };

        viewModel.IsMultiStreamEnabled = false;

        Assert.Equal(3, viewModel.VideoTabs.Count);
        Assert.Equal(0, removedSurfaceTabs.Count);
        Assert.True(viewModel.VideoTabs.Contains(first));
        Assert.True(viewModel.VideoTabs.Contains(second));
        Assert.True(viewModel.VideoTabs.Contains(third));
        Assert.Equal(false, first.IsVideoVisible);
        Assert.True(second.IsVideoVisible);
        Assert.Equal(false, third.IsVideoVisible);
        Assert.Equal(0, second.VideoGridRow);
        Assert.Equal(0, second.VideoGridColumn);
        Assert.Equal(2, second.VideoGridRowSpan);
        Assert.Equal(2, second.VideoGridColumnSpan);

        viewModel.IsMultiStreamEnabled = true;

        Assert.Equal(3, viewModel.VideoTabs.Count);
        Assert.Equal(0, removedSurfaceTabs.Count);
        Assert.True(viewModel.VideoTabs.Contains(first));
        Assert.True(viewModel.VideoTabs.Contains(second));
        Assert.True(viewModel.VideoTabs.Contains(third));
        Assert.True(first.IsVideoVisible);
        Assert.True(second.IsVideoVisible);
        Assert.True(third.IsVideoVisible);
        Assert.Equal(0, first.VideoGridRow);
        Assert.Equal(0, first.VideoGridColumn);
        Assert.Equal(0, second.VideoGridRow);
        Assert.Equal(1, second.VideoGridColumn);
        Assert.Equal(1, third.VideoGridRow);
        Assert.Equal(0, third.VideoGridColumn);
        return Task.CompletedTask;
    }),
    ("multi-stream visible tabs keep playing when inactive tab pausing is enabled", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var engines = new Queue<FakePlaybackEngine>();
        var firstEngine = new FakePlaybackEngine();
        var secondEngine = new FakePlaybackEngine();
        engines.Enqueue(firstEngine);
        engines.Enqueue(secondEngine);
        var playbackFactory = new FakePlaybackEngineFactory(() => engines.Dequeue());
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC",
            KeepInactiveTabsRunning = false,
            MultiStreamEnabled = true
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
        var firstTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var secondTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        firstTab.SetVideoHandle(new IntPtr(1234));
        secondTab.SetVideoHandle(new IntPtr(5678));
        viewModel.Tabs.Add(firstTab);
        viewModel.VideoTabs.Add(firstTab);
        viewModel.Tabs.Add(secondTab);
        viewModel.VideoTabs.Add(secondTab);

        viewModel.SelectedTab = firstTab;
        await firstTab.StartAsync(settings);
        await secondTab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => firstTab.Status == PlaybackStatus.Playing &&
                secondTab.Status == PlaybackStatus.Playing &&
                !firstTab.IsBusy &&
                !secondTab.IsBusy,
            TimeSpan.FromSeconds(1));

        Assert.Equal(PlaybackStatus.Playing, firstTab.Status);
        Assert.Equal(PlaybackStatus.Playing, secondTab.Status);
        Assert.True(firstTab.IsVideoVisible);
        Assert.True(secondTab.IsVideoVisible);

        viewModel.SelectedTab = secondTab;
        await TestWait.UntilAsync(
            () => firstTab.Status == PlaybackStatus.Playing &&
                secondTab.Status == PlaybackStatus.Playing,
            TimeSpan.FromSeconds(1));

        Assert.Equal(PlaybackStatus.Playing, firstTab.Status);
        Assert.Equal(false, firstTab.PausedByTabSwitch);
        Assert.Equal(PlaybackStatus.Playing, secondTab.Status);

        viewModel.IsMultiStreamEnabled = false;
        await TestWait.UntilAsync(
            () => firstTab.Status == PlaybackStatus.Paused &&
                firstTab.PausedByTabSwitch &&
                secondTab.Status == PlaybackStatus.Playing,
            TimeSpan.FromMilliseconds(500));

        Assert.Equal(PlaybackStatus.Playing, secondTab.Status);
        await firstTab.DisposeAsync();
        await secondTab.DisposeAsync();
    }),
    ("multi-stream off-grid policy pauses and resumes without changing requested quality", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC",
            KeepInactiveTabsRunning = false,
            MultiStreamEnabled = true
        };
        settings.Chat.ConnectAutomatically = false;
        var logger = new MemoryLogger();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var tabs = Enumerable.Range(1, VideoGridLayoutCalculator.TileLimit + 1)
            .Select(index => TestViewModels.CreateTab(
                StreamInputParser.Parse($"streamer{index}", PlatformKind.Twitch),
                "1080p60",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()))
            .ToArray();

        foreach (var tab in tabs)
        {
            viewModel.Tabs.Add(tab);
        }

        var firstTab = tabs[0];
        firstTab.SetVideoHandle(new IntPtr(1234));
        viewModel.SelectedTab = firstTab;
        await firstTab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => firstTab.Status == PlaybackStatus.Playing && !firstTab.PausedByTabSwitch,
            TimeSpan.FromSeconds(1));

        Assert.Equal("1080p60", streamlink.StartExternalHttpRequests.Single().Quality);
        viewModel.SelectedTab = tabs[^1];
        await TestWait.UntilAsync(
            () => firstTab.Status == PlaybackStatus.Paused &&
                firstTab.PausedByTabSwitch &&
                firstTab.IsBackgroundResourceServicesSuspended,
            TimeSpan.FromSeconds(1));

        viewModel.SelectedTab = firstTab;
        await TestWait.UntilAsync(
            () => firstTab.Status == PlaybackStatus.Playing &&
                !firstTab.PausedByTabSwitch &&
                !firstTab.IsBackgroundResourceServicesSuspended,
            TimeSpan.FromSeconds(1));

        settings.KeepInactiveTabsRunning = true;
        viewModel.SelectedTab = tabs[^1];
        await TestWait.UntilAsync(
            () => firstTab.Status == PlaybackStatus.Playing && !firstTab.PausedByTabSwitch,
            TimeSpan.FromSeconds(1));
        Assert.Equal("1080p60", streamlink.StartExternalHttpRequests.Single().Quality);

        await viewModel.DisposeAsync();
    }),
    ("manual mute stays per tab across selection changes", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var firstEngine = new FakePlaybackEngine();
        var secondEngine = new FakePlaybackEngine();
        var engines = new Queue<FakePlaybackEngine>();
        engines.Enqueue(firstEngine);
        engines.Enqueue(secondEngine);
        var playbackFactory = new FakePlaybackEngineFactory(() => engines.Dequeue());
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
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
        var firstTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var secondTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        firstTab.SetVideoHandle(new IntPtr(1234));
        secondTab.SetVideoHandle(new IntPtr(5678));
        viewModel.Tabs.Add(firstTab);
        viewModel.VideoTabs.Add(firstTab);
        viewModel.Tabs.Add(secondTab);
        viewModel.VideoTabs.Add(secondTab);

        viewModel.SelectedTab = firstTab;
        await firstTab.StartAsync(settings);
        firstTab.IsMuted = true;
        viewModel.SelectedTab = secondTab;
        await secondTab.StartAsync(settings);

        Assert.Equal(true, firstEngine.Muted);
        Assert.Equal(0, firstEngine.Volume);
        Assert.Equal(PlaybackAudioState.HardMuted, firstEngine.AudioState);
        Assert.Equal(false, firstEngine.AudioTrackEnabled);
        Assert.Equal(false, secondEngine.Muted);
        Assert.Equal(80, secondEngine.Volume);
        Assert.Equal(PlaybackAudioState.Audible, secondEngine.AudioState);
        Assert.Equal(true, secondEngine.AudioTrackEnabled);

        viewModel.SelectedTab = firstTab;

        Assert.Equal(true, firstTab.IsSelected);
        Assert.Equal(false, secondTab.IsSelected);
        Assert.Equal(true, firstEngine.Muted);
        Assert.Equal(0, firstEngine.Volume);
        Assert.Equal(PlaybackAudioState.HardMuted, firstEngine.AudioState);
        Assert.Equal(false, firstEngine.AudioTrackEnabled);
        Assert.Equal(true, secondEngine.Muted);
        Assert.Equal(0, secondEngine.Volume);
        Assert.Equal(PlaybackAudioState.Muted, secondEngine.AudioState);
        Assert.Equal(false, secondEngine.AudioTrackEnabled);
        await firstTab.DisposeAsync();
        await secondTab.DisposeAsync();
    }),
    ("volume changes are remembered per stream", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var firstEngine = new FakePlaybackEngine();
        var secondEngine = new FakePlaybackEngine();
        var engines = new Queue<FakePlaybackEngine>();
        engines.Enqueue(firstEngine);
        engines.Enqueue(secondEngine);
        var playbackFactory = new FakePlaybackEngineFactory(() => engines.Dequeue());
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var target = StreamInputParser.Parse("albralelie", PlatformKind.Twitch);
        settings.StreamVolumes[target.StateKey] = 35;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        await viewModel.OpenDetectedStreamAsync(target);
        Assert.Equal(1, viewModel.Tabs.Count);
        var firstTab = viewModel.Tabs[0];
        Assert.Equal(35, firstTab.Volume);
        firstTab.SetVideoHandle(new IntPtr(1234));
        await TestWait.UntilAsync(() => firstEngine.Played, TimeSpan.FromMilliseconds(500));

        firstTab.Volume = 62;
        Assert.Equal(62, settings.StreamVolumes[target.StateKey]);

        Assert.True(viewModel.CloseTab(firstTab));
        await viewModel.OpenDetectedStreamAsync(target);
        Assert.Equal(1, viewModel.Tabs.Count);
        var reopenedTab = viewModel.Tabs[0];
        Assert.True(!ReferenceEquals(firstTab, reopenedTab));
        Assert.Equal(62, reopenedTab.Volume);
        reopenedTab.SetVideoHandle(new IntPtr(5678));
        await TestWait.UntilAsync(() => secondEngine.Played, TimeSpan.FromMilliseconds(500));
        await viewModel.DisposeAsync();
    }),
    ("successful playback is remembered as a recent stream", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var settingsService = new FakeSettingsService(settings);
        var metadataService = new FakeStreamMetadataService(new StreamMetadataResult(
            StreamMetadataState.Available,
            "https://static-cdn.jtvnw.net/previews-ttv/live_user_albralelie-440x248.jpg",
            "Albralelie",
            "metadata updated",
            "Apex Legends"));
        var viewModel = TestViewModels.CreateMain(
            settings,
            settingsService,
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action(),
            streamMetadataService: metadataService);
        var target = StreamInputParser.Parse("albralelie", PlatformKind.Twitch);

        await viewModel.OpenDetectedStreamAsync(target);
        Assert.Equal(1, viewModel.Tabs.Count);
        viewModel.Tabs[0].SetVideoHandle(new IntPtr(1234));

        await TestWait.UntilAsync(
            () => playbackFactory.Engine?.Played == true &&
                settings.RecentStreams.Count == 1 &&
                viewModel.RecentStreams.Count == 1 &&
                settings.RecentStreams[0].ThumbnailUrl == "https://static-cdn.jtvnw.net/previews-ttv/live_user_albralelie-440x248.jpg" &&
                settingsService.SaveCount > 0,
            TimeSpan.FromMilliseconds(500));

        Assert.Equal(PlatformKind.Twitch, settings.RecentStreams[0].Platform);
        Assert.Equal("albralelie", settings.RecentStreams[0].Channel);
        Assert.Equal("https://www.twitch.tv/albralelie", settings.RecentStreams[0].Url);
        Assert.Equal("Albralelie", settings.RecentStreams[0].DisplayName);
        Assert.Equal("Apex Legends", settings.RecentStreams[0].CategoryName);
        Assert.Equal("https://static-cdn.jtvnw.net/previews-ttv/live_user_albralelie-440x248.jpg", settings.RecentStreams[0].ThumbnailUrl);
        Assert.Equal("best", settings.RecentStreams[0].LastQuality);
        Assert.True(settings.RecentStreams[0].LastWatchedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal("Albralelie", viewModel.RecentStreams[0].DisplayName);
        Assert.Equal("Apex Legends", viewModel.RecentStreams[0].CategoryName);
        Assert.Equal("https://static-cdn.jtvnw.net/previews-ttv/live_user_albralelie-440x248.jpg", viewModel.RecentStreams[0].ThumbnailUrl);
        Assert.Equal("Live", viewModel.RecentStreams[0].LiveStatusText);
        Assert.Equal(1, metadataService.CallCount);
        await viewModel.DisposeAsync();
    }),
    ("recent stream thumbnails refresh while recent page is selected", async () =>
    {
        var settings = new AppSettings();
        settings.RecentStreams =
        [
            new RecentStreamSettings
            {
                Platform = PlatformKind.Twitch,
                Channel = "albralelie",
                ThumbnailUrl = "https://static-cdn.jtvnw.net/previews-ttv/live_user_albralelie-old.jpg",
                LastQuality = "best",
                LastWatchedAtUtc = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero)
            }
        ];
        var settingsService = new FakeSettingsService(settings);
        var metadataService = new FakeStreamMetadataService(
            new StreamMetadataResult(
                StreamMetadataState.Available,
                "https://static-cdn.jtvnw.net/previews-ttv/live_user_albralelie-first.jpg",
                "Albralelie",
                "metadata updated",
                "Apex Legends"),
            new StreamMetadataResult(
                StreamMetadataState.Available,
                "https://static-cdn.jtvnw.net/previews-ttv/live_user_albralelie-second.jpg",
                "Albralelie",
                "metadata updated",
                "Apex Legends"));
        var viewModel = TestViewModels.CreateMain(
            settings,
            settingsService,
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            streamMetadataService: metadataService,
            recentThumbnailRefreshInterval: TimeSpan.FromMilliseconds(20));

        viewModel.ShowRecentHomePageCommand.Execute(null);

        await TestWait.UntilAsync(
            () => metadataService.CallCount >= 2 &&
                viewModel.RecentStreams.Count > 0 &&
                settings.RecentStreams[0].ThumbnailUrl == "https://static-cdn.jtvnw.net/previews-ttv/live_user_albralelie-second.jpg" &&
                viewModel.RecentStreams[0].ThumbnailUrl == "https://static-cdn.jtvnw.net/previews-ttv/live_user_albralelie-second.jpg" &&
                viewModel.RecentStreams[0].LiveStatusText == "Live",
            TimeSpan.FromSeconds(1));

        Assert.Equal("Albralelie", settings.RecentStreams[0].DisplayName);
        Assert.Equal("Apex Legends", settings.RecentStreams[0].CategoryName);
        Assert.True(settingsService.SaveCount >= 2);
        await viewModel.DisposeAsync();
    }),
    ("recent stream live indicator uses platform offline metadata", async () =>
    {
        var settings = new AppSettings();
        settings.RecentStreams =
        [
            new RecentStreamSettings
            {
                Platform = PlatformKind.Twitch,
                Channel = "albralelie",
                ThumbnailUrl = "https://static-cdn.jtvnw.net/previews-ttv/live_user_albralelie-old.jpg",
                LastQuality = "best",
                LastWatchedAtUtc = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero)
            },
            new RecentStreamSettings
            {
                Platform = PlatformKind.Kick,
                Channel = "xqc",
                ThumbnailUrl = "https://files.kick.com/xqc-old.jpg",
                LastQuality = "720p",
                LastWatchedAtUtc = new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero)
            }
        ];
        var metadataService = new FakeStreamMetadataService(
            new StreamMetadataResult(
                StreamMetadataState.Available,
                "https://static-cdn.jtvnw.net/previews-ttv/live_user_albralelie-new.jpg",
                "Albralelie",
                "Twitch stream metadata updated."),
            new StreamMetadataResult(
                StreamMetadataState.Offline,
                "",
                "",
                "Kick stream is offline."));
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            streamMetadataService: metadataService,
            recentThumbnailRefreshInterval: TimeSpan.FromHours(1));

        viewModel.ShowRecentHomePageCommand.Execute(null);

        await TestWait.UntilAsync(
            () => metadataService.CallCount >= 2 &&
                viewModel.RecentStreams.Count == 2 &&
                viewModel.RecentStreams[0].LiveStatusText == "Live" &&
                viewModel.RecentStreams[1].LiveStatusText == "Offline",
            TimeSpan.FromSeconds(1));

        Assert.Equal("https://static-cdn.jtvnw.net/previews-ttv/live_user_albralelie-new.jpg", viewModel.RecentStreams[0].ThumbnailUrl);
        Assert.Equal("https://files.kick.com/xqc-old.jpg", viewModel.RecentStreams[1].ThumbnailUrl);
        Assert.True(viewModel.RecentStreams[1].LiveStatusToolTip.Contains("offline", StringComparison.OrdinalIgnoreCase));
        await viewModel.DisposeAsync();
    }),
    ("recent stream delete removes channel from settings and view model", async () =>
    {
        var settings = new AppSettings();
        settings.RecentStreams =
        [
            new RecentStreamSettings
            {
                Platform = PlatformKind.Twitch,
                Channel = "albralelie",
                DisplayName = "Albralelie",
                CategoryName = "Apex Legends",
                ThumbnailUrl = "https://static-cdn.jtvnw.net/previews-ttv/live_user_albralelie.jpg",
                LastQuality = "best",
                LastWatchedAtUtc = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero)
            },
            new RecentStreamSettings
            {
                Platform = PlatformKind.Kick,
                Channel = "xqc",
                DisplayName = "xqc",
                ThumbnailUrl = "https://files.kick.com/xqc.jpg",
                LastQuality = "720p",
                LastWatchedAtUtc = new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero)
            }
        ];
        var settingsService = new FakeSettingsService(settings);
        var viewModel = TestViewModels.CreateMain(
            settings,
            settingsService,
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action());

        Assert.Equal(2, viewModel.RecentStreams.Count);
        Assert.Contains("Albralelie", viewModel.RecentStreams[0].DeleteToolTip);

        await viewModel.RecentStreams[0].DeleteCommand.ExecuteAsync();

        Assert.Equal(1, settings.RecentStreams.Count);
        Assert.Equal(1, viewModel.RecentStreams.Count);
        Assert.Equal(PlatformKind.Kick, settings.RecentStreams[0].Platform);
        Assert.Equal("xqc", settings.RecentStreams[0].Channel);
        Assert.Equal("xqc", viewModel.RecentStreams[0].Channel);
        Assert.True(viewModel.HasRecentStreams);
        Assert.Equal(false, viewModel.IsRecentStreamsEmptyVisible);
        Assert.Contains("removed from recent streams", viewModel.StatusMessage);
        Assert.Equal(1, settingsService.SaveCount);

        await viewModel.RecentStreams[0].DeleteCommand.ExecuteAsync();

        Assert.Equal(0, settings.RecentStreams.Count);
        Assert.Equal(0, viewModel.RecentStreams.Count);
        Assert.Equal(false, viewModel.HasRecentStreams);
        Assert.True(viewModel.IsRecentStreamsEmptyVisible);
        Assert.Equal(2, settingsService.SaveCount);
        await viewModel.DisposeAsync();
    }),
    ("home recent stream background open keeps home selected", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        settings.RecentStreams =
        [
            new RecentStreamSettings
            {
                Platform = PlatformKind.Twitch,
                Channel = "albralelie",
                DisplayName = "Albralelie",
                CategoryName = "Apex Legends",
                ThumbnailUrl = "https://static-cdn.jtvnw.net/previews-ttv/live_user_albralelie.jpg",
                LastQuality = "best",
                LastWatchedAtUtc = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero)
            }
        ];
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action());

        await viewModel.RecentStreams[0].OpenAndStayOnHomeCommand.ExecuteAsync();

        Assert.True(viewModel.IsHomeSelected);
        Assert.True(viewModel.IsHomeVisible);
        Assert.Equal<StreamTabViewModel?>(null, viewModel.SelectedTab);
        Assert.Equal(1, viewModel.Tabs.Count);
        var tab = viewModel.Tabs[0];
        Assert.Equal("albralelie", tab.Target.Channel);
        Assert.Equal("Apex Legends", tab.Target.CategoryName);
        Assert.Equal("Apex Legends", viewModel.TabStripItems.Single().SubtitleText);
        Assert.Equal(false, tab.IsSelected);
        Assert.Equal(false, tab.IsVideoVisible);
        Assert.SequenceEqual(new[] { tab }, viewModel.VideoTabs.ToArray());
        await TestWait.UntilAsync(() => streamlink.Started, TimeSpan.FromMilliseconds(500));
        await viewModel.DisposeAsync();
    }),
    ("home followed stream background open keeps home selected", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var followedStream = new FollowedLiveStream(
            PlatformKind.Twitch,
            "summit1g",
            "summit1g",
            "live now",
            "Just Chatting",
            12000,
            "https://static-cdn.jtvnw.net/previews-ttv/live_user_summit1g.jpg",
            DateTimeOffset.UtcNow.AddMinutes(-30),
            false,
            "en",
            "https://www.twitch.tv/summit1g");
        var followedService = new FakeFollowedStreamsService(followedStream);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            followedStreamsService: followedService);

        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        await viewModel.LiveFollowedChannels[0].OpenAndStayOnHomeCommand.ExecuteAsync();

        Assert.True(viewModel.IsHomeSelected);
        Assert.True(viewModel.IsHomeVisible);
        Assert.Equal<StreamTabViewModel?>(null, viewModel.SelectedTab);
        Assert.Equal(1, viewModel.Tabs.Count);
        var tab = viewModel.Tabs[0];
        Assert.Equal("summit1g", tab.Target.Channel);
        Assert.Equal("Just Chatting", tab.Target.CategoryName);
        Assert.Equal("Just Chatting", viewModel.TabStripItems.Single().SubtitleText);
        Assert.Equal(false, tab.IsSelected);
        Assert.Equal(false, tab.IsVideoVisible);
        Assert.SequenceEqual(new[] { tab }, viewModel.VideoTabs.ToArray());
        await TestWait.UntilAsync(() => streamlink.Started, TimeSpan.FromMilliseconds(500));
        await viewModel.DisposeAsync();
    }),
    ("home followed settings discard invalid Kick slugs before refresh", async () =>
    {
        var followedStream = new FollowedLiveStream(
            PlatformKind.Kick,
            "xqc",
            "xqc",
            "live now",
            "IRL",
            9876,
            "https://files.kick.com/live.jpg",
            DateTimeOffset.UtcNow.AddMinutes(-30),
            false,
            "en",
            "https://kick.com/xqc");
        var followedService = new FakeFollowedStreamsService(followedStream);
        var settings = new AppSettings();
        settings.FollowedChannels.KickChannelSlugs = ["xqc", "bad slug"];

        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            followedStreamsService: followedService);

        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();

        Assert.Equal(1, followedService.CallCount);
        Assert.SequenceEqual(new[] { "xqc" }, settings.FollowedChannels.KickChannelSlugs);
        Assert.Equal(1, viewModel.LiveFollowedChannels.Count);
        Assert.Equal("1 followed channel is live.", viewModel.FollowedChannelsStatus);
        await viewModel.DisposeAsync();
    }),
    ("home followed refresh advances thumbnail cache version for an unchanged live channel", async () =>
    {
        var followedStream = CreateTestFollowedStream(PlatformKind.Twitch, "summit1g") with
        {
            ThumbnailUrl = "https://static-cdn.jtvnw.net/previews-ttv/live_user_summit1g-440x248.jpg?source=followed"
        };
        var followedService = new FakeFollowedStreamsService(followedStream);
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            followedStreamsService: followedService);

        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        var firstItem = viewModel.LiveFollowedChannels.Single();
        var firstCacheVersion = firstItem.ThumbnailCacheVersion;

        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        var secondItem = viewModel.LiveFollowedChannels.Single();
        var secondCacheVersion = secondItem.ThumbnailCacheVersion;

        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        var thirdItem = viewModel.LiveFollowedChannels.Single();
        var thirdCacheVersion = thirdItem.ThumbnailCacheVersion;

        Assert.Equal(3, followedService.CallCount);
        Assert.Equal(followedStream.ThumbnailUrl, firstItem.ThumbnailUrl);
        Assert.Equal(followedStream.ThumbnailUrl, secondItem.ThumbnailUrl);
        Assert.Equal(followedStream.ThumbnailUrl, thirdItem.ThumbnailUrl);
        Assert.True(firstCacheVersion > 0);
        Assert.True(secondCacheVersion > firstCacheVersion);
        Assert.True(thirdCacheVersion > secondCacheVersion);
        Assert.Equal(firstCacheVersion, firstItem.ThumbnailCacheVersion);
        await viewModel.DisposeAsync();
    }),
    ("home followed failed refresh preserves the displayed thumbnail cache version", async () =>
    {
        var followedStream = CreateTestFollowedStream(PlatformKind.Kick, "xqc");
        var followedService = new FakeFollowedStreamsService();
        followedService.EnqueueResult(followedStream);
        followedService.EnqueueResult(_ => Task.FromException<FollowedLiveStreamsResult>(
            new InvalidOperationException("followed refresh failed")));
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            followedStreamsService: followedService);

        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        var displayedItem = viewModel.LiveFollowedChannels.Single();
        var displayedCacheVersion = displayedItem.ThumbnailCacheVersion;

        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();

        Assert.Equal(2, followedService.CallCount);
        Assert.Equal(1, viewModel.LiveFollowedChannels.Count);
        Assert.True(ReferenceEquals(displayedItem, viewModel.LiveFollowedChannels[0]));
        Assert.Equal(displayedCacheVersion, viewModel.LiveFollowedChannels[0].ThumbnailCacheVersion);
        Assert.Contains("followed refresh failed", viewModel.FollowedChannelsStatus);
        await viewModel.DisposeAsync();
    }),
    ("home followed live toast baselines each platform's first successful refresh", async () =>
    {
        var kickStream = CreateTestFollowedStream(PlatformKind.Kick, "xqc", viewerCount: 25000);
        var twitchStream = CreateTestFollowedStream(PlatformKind.Twitch, "summit1g", viewerCount: 12000);
        var newTwitchStream = CreateTestFollowedStream(PlatformKind.Twitch, "albralelie", viewerCount: 9000);
        var followedService = new FakeFollowedStreamsService();
        // Round 1: Twitch fails (e.g. network not ready at app start); only Kick reports.
        followedService.EnqueueResult(_ => Task.FromResult(new FollowedLiveStreamsResult(
            [kickStream], [], [PlatformKind.Kick])));
        // Round 2: Twitch recovers. summit1g was live the whole time and must not toast.
        followedService.EnqueueResult(_ => Task.FromResult(new FollowedLiveStreamsResult(
            [kickStream, twitchStream], [], [PlatformKind.Twitch, PlatformKind.Kick])));
        // Round 3: a genuinely new Twitch channel goes live and must toast.
        followedService.EnqueueResult(_ => Task.FromResult(new FollowedLiveStreamsResult(
            [kickStream, twitchStream, newTwitchStream], [], [PlatformKind.Twitch, PlatformKind.Kick])));
        var notifications = new FakeLiveNotificationService();
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            followedStreamsService: followedService,
            liveNotificationService: notifications);

        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.Equal(0, notifications.Notifications.Count);

        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.Equal(0, notifications.Notifications.Count);

        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.Equal(1, notifications.Notifications.Count);
        Assert.Equal(PlatformKind.Twitch, notifications.Notifications[0].Platform);
        Assert.Equal("albralelie", notifications.Notifications[0].Channel);
        await viewModel.DisposeAsync();
    }),
    ("home followed live toast announces each offline-to-live transition only when enabled", async () =>
    {
        var firstStream = CreateTestFollowedStream(
            PlatformKind.Twitch,
            "summit1g",
            displayName: "Summit1G",
            viewerCount: 12000);
        var secondStream = CreateTestFollowedStream(PlatformKind.Twitch, "albralelie", viewerCount: 9000);
        var followedService = new FakeFollowedStreamsService();
        followedService.EnqueueResult(_ => Task.FromResult(new FollowedLiveStreamsResult(
            [], [], [PlatformKind.Twitch])));
        followedService.EnqueueResult(_ => Task.FromResult(new FollowedLiveStreamsResult(
            [firstStream], [], [PlatformKind.Twitch])));
        followedService.EnqueueResult(_ => Task.FromResult(new FollowedLiveStreamsResult(
            [firstStream], [], [PlatformKind.Twitch])));
        followedService.EnqueueResult(_ => Task.FromResult(new FollowedLiveStreamsResult(
            [], [], [PlatformKind.Twitch])));
        followedService.EnqueueResult(_ => Task.FromResult(new FollowedLiveStreamsResult(
            [firstStream], [], [PlatformKind.Twitch])));
        followedService.EnqueueResult(_ => Task.FromResult(new FollowedLiveStreamsResult(
            [firstStream, secondStream], [], [PlatformKind.Twitch])));
        followedService.EnqueueResult(_ => Task.FromResult(new FollowedLiveStreamsResult(
            [firstStream], [], [PlatformKind.Twitch])));
        followedService.EnqueueResult(_ => Task.FromResult(new FollowedLiveStreamsResult(
            [firstStream, secondStream], [], [PlatformKind.Twitch])));
        var notifications = new FakeLiveNotificationService();
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            followedStreamsService: followedService,
            liveNotificationService: notifications);

        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.Equal(1, notifications.Notifications.Count);
        Assert.Equal("Summit1G", notifications.Notifications[0].DisplayName);
        Assert.Equal("live now", notifications.Notifications[0].Title);
        Assert.Equal("Just Chatting", notifications.Notifications[0].CategoryName);
        Assert.Equal(12000, notifications.Notifications[0].ViewerCount);

        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.Equal(1, notifications.Notifications.Count);
        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.Equal(2, notifications.Notifications.Count);

        settings.FollowedChannels.NotifyWhenLive = false;
        Assert.Equal(false, notifications.IsEnabled);
        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.Equal(2, notifications.Notifications.Count);
        settings.FollowedChannels.NotifyWhenLive = true;
        Assert.True(notifications.IsEnabled);
        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.Equal(3, notifications.Notifications.Count);
        Assert.Equal("albralelie", notifications.Notifications[2].Channel);
        await viewModel.DisposeAsync();
    }),
    ("home followed refresh loads on initialize and repeats automatically", async () =>
    {
        var firstStream = CreateTestFollowedStream(PlatformKind.Twitch, "summit1g", viewerCount: 12000);
        var secondStream = CreateTestFollowedStream(PlatformKind.Kick, "xqc", viewerCount: 25000);
        var followedService = new FakeFollowedStreamsService(secondStream);
        followedService.EnqueueResult(firstStream);
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            followedStreamsService: followedService,
            followedChannelsRefreshInterval: TimeSpan.FromMilliseconds(40));

        viewModel.Initialize();

        await TestWait.UntilAsync(
            () => followedService.CallCount >= 1 &&
                viewModel.LiveFollowedChannels.Count == 1 &&
                viewModel.LiveFollowedChannels[0].Channel == "summit1g",
            TimeSpan.FromMilliseconds(500));
        await TestWait.UntilAsync(
            () => followedService.CallCount >= 2 &&
                viewModel.LiveFollowedChannels.Count == 1 &&
                viewModel.LiveFollowedChannels[0].Channel == "xqc",
            TimeSpan.FromSeconds(1));

        Assert.True(followedService.CancellationTokens.All(token => token.CanBeCanceled));
        await viewModel.DisposeAsync();
    }),
    ("home followed automatic refresh advances thumbnail cache version for an unchanged live channel", async () =>
    {
        var followedStream = CreateTestFollowedStream(PlatformKind.Twitch, "summit1g", viewerCount: 12000);
        var followedService = new FakeFollowedStreamsService(followedStream);
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            followedStreamsService: followedService,
            followedChannelsRefreshInterval: TimeSpan.FromMilliseconds(80));

        viewModel.Initialize();
        await TestWait.UntilAsync(
            () => followedService.CallCount >= 1 && viewModel.LiveFollowedChannels.Count == 1,
            TimeSpan.FromMilliseconds(500));
        var firstCacheVersion = viewModel.LiveFollowedChannels[0].ThumbnailCacheVersion;

        await TestWait.UntilAsync(
            () => followedService.CallCount >= 2 &&
                viewModel.LiveFollowedChannels.Count == 1 &&
                viewModel.LiveFollowedChannels[0].ThumbnailCacheVersion > firstCacheVersion,
            TimeSpan.FromSeconds(1));

        Assert.Equal(followedStream.ThumbnailUrl, viewModel.LiveFollowedChannels[0].ThumbnailUrl);
        await viewModel.DisposeAsync();
    }),
    ("home followed automatic refresh continues when a stream tab is selected", async () =>
    {
        var firstStream = CreateTestFollowedStream(PlatformKind.Twitch, "summit1g", viewerCount: 12000);
        var secondStream = CreateTestFollowedStream(PlatformKind.Twitch, "albralelie", viewerCount: 9000);
        var followedService = new FakeFollowedStreamsService(secondStream);
        followedService.EnqueueResult(firstStream);
        var streamlink = new FakeStreamlinkService();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            followedStreamsService: followedService,
            followedChannelsRefreshInterval: TimeSpan.FromMilliseconds(40));

        viewModel.Initialize();
        await TestWait.UntilAsync(
            () => followedService.CallCount >= 1 &&
                viewModel.LiveFollowedChannels.Count == 1 &&
                viewModel.LiveFollowedChannels[0].Channel == "summit1g",
            TimeSpan.FromMilliseconds(500));

        await viewModel.OpenDetectedStreamAsync(new StreamTarget(
            PlatformKind.Twitch,
            "otherchannel",
            "https://www.twitch.tv/otherchannel"));

        Assert.Equal(false, viewModel.IsHomeSelected);
        await TestWait.UntilAsync(
            () => followedService.CallCount >= 2 &&
                viewModel.LiveFollowedChannels.Count == 1 &&
                viewModel.LiveFollowedChannels[0].Channel == "albralelie",
            TimeSpan.FromSeconds(1));
        Assert.Equal(false, viewModel.IsHomeSelected);
        await viewModel.DisposeAsync();
    }),
    ("home followed automatic refresh skips overlapping slow ticks", async () =>
    {
        var slowRefreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlowRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var followedService = new FakeFollowedStreamsService(
            CreateTestFollowedStream(PlatformKind.Twitch, "defaultlive"));
        followedService.EnqueueResult(CreateTestFollowedStream(PlatformKind.Twitch, "firstlive"));
        followedService.EnqueueResult(async cancellationToken =>
        {
            slowRefreshStarted.SetResult();
            await releaseSlowRefresh.Task.WaitAsync(cancellationToken);
            return new FollowedLiveStreamsResult(
                [CreateTestFollowedStream(PlatformKind.Twitch, "slowlive")],
                []);
        });
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            followedStreamsService: followedService,
            followedChannelsRefreshInterval: TimeSpan.FromMilliseconds(20));

        viewModel.Initialize();
        await TestWait.UntilAsync(
            () => followedService.CallCount >= 1 &&
                viewModel.LiveFollowedChannels.Count == 1 &&
                viewModel.LiveFollowedChannels[0].Channel == "firstlive",
            TimeSpan.FromMilliseconds(500));
        await slowRefreshStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));

        await Task.Delay(120);

        Assert.Equal(2, followedService.CallCount);
        Assert.Equal(1, followedService.MaxConcurrentCalls);

        releaseSlowRefresh.SetResult();
        await TestWait.UntilAsync(
            () => followedService.CallCount >= 3,
            TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, followedService.MaxConcurrentCalls);
        await viewModel.DisposeAsync();
    }),
    ("home followed automatic refresh stops after dispose", async () =>
    {
        var followedService = new FakeFollowedStreamsService(
            CreateTestFollowedStream(PlatformKind.Twitch, "summit1g"));
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            followedStreamsService: followedService,
            followedChannelsRefreshInterval: TimeSpan.FromMilliseconds(80));

        viewModel.Initialize();
        await TestWait.UntilAsync(
            () => followedService.CallCount >= 1 && viewModel.LiveFollowedChannels.Count == 1,
            TimeSpan.FromMilliseconds(500));

        await viewModel.DisposeAsync();
        var callCountAfterDispose = followedService.CallCount;

        await Task.Delay(180);

        Assert.Equal(callCountAfterDispose, followedService.CallCount);
    }),
    ("home Twitch VOD search runs automatically and filter changes reset results", async () =>
    {
        var broadcaster = new TwitchVodBroadcaster("26490481", "summit1g", "summit1g");
        var firstVod = new TwitchVodItem(
            "vod-1",
            "stream-1",
            broadcaster.Id,
            broadcaster.Login,
            broadcaster.DisplayName,
            "first VOD",
            "",
            "https://www.twitch.tv/videos/vod-1",
            "https://static-cdn.jtvnw.net/vod-1-320x180.jpg",
            new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 1, 12, 5, 0, TimeSpan.Zero),
            TimeSpan.FromMinutes(30),
            100,
            TwitchVodTypeFilter.Archive);
        var secondVod = firstVod with
        {
            Id = "vod-2",
            StreamId = "stream-2",
            Title = "second VOD",
            Url = "https://www.twitch.tv/videos/vod-2",
            ViewCount = 200
        };
        var highlightVod = firstVod with
        {
            Id = "vod-3",
            StreamId = "stream-3",
            Title = "highlight VOD",
            Url = "https://www.twitch.tv/videos/vod-3",
            Type = TwitchVodTypeFilter.Highlight
        };
        var vodService = new FakeTwitchVodService(
            new TwitchVodSearchResult(TwitchVodSearchStatus.Available, broadcaster, [firstVod], "cursor-1", "first page"),
            new TwitchVodSearchResult(TwitchVodSearchStatus.Available, broadcaster, [secondVod], "", "second page"),
            new TwitchVodSearchResult(TwitchVodSearchStatus.Available, broadcaster, [highlightVod], "", "highlight page"));
        var streamlink = new FakeStreamlinkService();
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            twitchVodService: vodService,
            twitchVodSearchDebounceInterval: TimeSpan.Zero);

        viewModel.ShowTwitchVodsHomePageCommand.Execute(null);
        Assert.True(viewModel.IsTwitchVodsHomePageSelected);
        Assert.True(viewModel.IsTwitchVodsHomePageVisible);

        viewModel.TwitchVodSearchText = "summit1g";
        await TestWait.UntilAsync(
            () => vodService.CallCount >= 1 && viewModel.TwitchVods.Count == 1,
            TimeSpan.FromMilliseconds(500));

        Assert.Equal(1, viewModel.TwitchVods.Count);
        Assert.Equal("vod-1", viewModel.TwitchVods[0].Id);
        Assert.Equal(0, streamlink.ProbeRequests.Count);
        Assert.Equal(TwitchVodTypeFilter.Archive, vodService.Requests[0].Type);
        Assert.Equal("", vodService.Requests[0].Cursor);
        Assert.True(viewModel.IsTwitchVodLoadMoreVisible);

        await viewModel.LoadMoreTwitchVodsCommand.ExecuteAsync();

        Assert.Equal(2, viewModel.TwitchVods.Count);
        Assert.Equal("cursor-1", vodService.Requests[1].Cursor);

        viewModel.ShowHighlightsVodFilterCommand.Execute(null);

        await TestWait.UntilAsync(
            () => vodService.CallCount >= 3 &&
                viewModel.TwitchVods.Count == 1 &&
                viewModel.TwitchVods[0].Id == "vod-3",
            TimeSpan.FromMilliseconds(500));
        Assert.Equal(TwitchVodTypeFilter.Highlight, vodService.Requests[2].Type);
        Assert.Equal("", vodService.Requests[2].Cursor);
        Assert.Equal(0, streamlink.ProbeRequests.Count);
        await viewModel.DisposeAsync();
    }),
    ("automatic Twitch VOD search debounces typing to the latest query", async () =>
    {
        var broadcaster = new TwitchVodBroadcaster("26490481", "summit1g", "summit1g");
        var vod = new TwitchVodItem(
            "vod-1",
            "stream-1",
            broadcaster.Id,
            broadcaster.Login,
            broadcaster.DisplayName,
            "first VOD",
            "",
            "https://www.twitch.tv/videos/vod-1",
            "https://static-cdn.jtvnw.net/vod-1-320x180.jpg",
            null,
            null,
            TimeSpan.FromMinutes(45),
            100,
            TwitchVodTypeFilter.Archive);
        var vodService = new FakeTwitchVodService(
            new TwitchVodSearchResult(TwitchVodSearchStatus.Available, broadcaster, [vod], "", "one VOD"));
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            twitchVodService: vodService,
            twitchVodSearchDebounceInterval: TimeSpan.FromMilliseconds(50));

        viewModel.TwitchVodSearchText = "sum";
        viewModel.TwitchVodSearchText = "summit1g";

        await TestWait.UntilAsync(
            () => vodService.CallCount == 1 && viewModel.TwitchVods.Count == 1,
            TimeSpan.FromSeconds(1));

        Assert.Equal("summit1g", vodService.Requests[0].Streamer);
        Assert.Equal("vod-1", viewModel.TwitchVods[0].Id);
        await viewModel.DisposeAsync();
    }),
    ("opening two Twitch VODs from the same streamer creates distinct tabs without recents", async () =>
    {
        var broadcaster = new TwitchVodBroadcaster("26490481", "summit1g", "summit1g");
        var firstVod = new TwitchVodItem(
            "vod-1",
            "stream-1",
            broadcaster.Id,
            broadcaster.Login,
            broadcaster.DisplayName,
            "first VOD",
            "",
            "https://www.twitch.tv/videos/vod-1",
            "https://static-cdn.jtvnw.net/vod-1-320x180.jpg",
            null,
            null,
            TimeSpan.FromMinutes(45),
            100,
            TwitchVodTypeFilter.Archive);
        var secondVod = firstVod with
        {
            Id = "vod-2",
            StreamId = "stream-2",
            Title = "second VOD",
            Url = "https://www.twitch.tv/videos/vod-2"
        };
        var vodService = new FakeTwitchVodService(
            new TwitchVodSearchResult(TwitchVodSearchStatus.Available, broadcaster, [firstVod, secondVod], "", "two VODs"));
        var firstEngine = new FakePlaybackEngine();
        var secondEngine = new FakePlaybackEngine();
        var engines = new Queue<FakePlaybackEngine>();
        engines.Enqueue(firstEngine);
        engines.Enqueue(secondEngine);
        var playbackFactory = new FakePlaybackEngineFactory(() => engines.Dequeue());
        var streamlink = new FakeStreamlinkService();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            twitchVodService: vodService,
            twitchVodSearchDebounceInterval: TimeSpan.Zero);

        viewModel.TwitchVodSearchText = "summit1g";
        await TestWait.UntilAsync(
            () => vodService.CallCount >= 1 && viewModel.TwitchVods.Count == 2,
            TimeSpan.FromMilliseconds(500));
        var openVod = typeof(MainViewModel).GetMethod(
            "OpenTwitchVodAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(openVod);

        await ((Task)openVod!.Invoke(viewModel, [viewModel.TwitchVods[0], false])!);
        Assert.Equal(1, viewModel.Tabs.Count);
        viewModel.Tabs[0].SetVideoHandle(new IntPtr(1234));
        await TestWait.UntilAsync(() => firstEngine.Played, TimeSpan.FromMilliseconds(500));

        await ((Task)openVod.Invoke(viewModel, [viewModel.TwitchVods[1], false])!);
        Assert.Equal(2, viewModel.Tabs.Count);
        viewModel.Tabs[1].SetVideoHandle(new IntPtr(5678));
        await TestWait.UntilAsync(() => secondEngine.Played, TimeSpan.FromMilliseconds(500));

        Assert.Equal("summit1g", viewModel.Tabs[0].Target.Channel);
        Assert.Equal("summit1g", viewModel.Tabs[1].Target.Channel);
        Assert.Equal("vod-1", viewModel.Tabs[0].Target.MediaId);
        Assert.Equal("vod-2", viewModel.Tabs[1].Target.MediaId);
        Assert.Equal(false, viewModel.Tabs[0].Target.TabIdentityKey == viewModel.Tabs[1].Target.TabIdentityKey);
        Assert.Equal(0, streamlink.StartCount);
        Assert.Equal(2, streamlink.ResolveStreamUrlCount);
        Assert.Equal(0, streamlink.ProbeRequests.Count);
        Assert.Equal(0, settings.RecentStreams.Count);
        await viewModel.DisposeAsync();
    }),
    ("explicit Twitch VOD tab initializes replay metadata and disables live-only behavior", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var directVodUri = new Uri("https://d1g1f25tn8m2e6.cloudfront.net/vod/2786354640/index-dvr.m3u8");
        streamlink.ResolveStreamUrlOverride = (request, _) =>
            Task.FromResult(new StreamlinkResolvedUrl(directVodUri, $"Resolved {request.Target.MediaId}."));
        var playbackFactory = new FakePlaybackEngineFactory();
        var chatFactory = new FakeChatClientFactory();
        var viewerCountService = new FakeViewerCountService();
        var replayResolver = new FakeReplayResolver(ReplaySessionInfo.Unavailable(
            PlatformKind.Twitch,
            "summit1g",
            "not used"));
        var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available(
            [
                new ReplayChatMessage(
                    TimeSpan.FromMinutes(10),
                    new ChatMessage(PlatformKind.Twitch, "summit1g", "viewer", "vod chat", DateTimeOffset.UtcNow))
            ],
            TimeSpan.Zero,
            TimeSpan.FromMinutes(20)));
        var target = new StreamTarget(
            PlatformKind.Twitch,
            "summit1g",
            "https://www.twitch.tv/videos/2786354640",
            StreamTargetKind.TwitchVod,
            "2786354640",
            "vod title",
            "26490481",
            TimeSpan.FromMinutes(90));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        var tab = TestViewModels.CreateTab(
            target,
            "best",
            streamlink,
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            viewerCountService: viewerCountService,
            replayResolver: replayResolver,
            replayChatProvider: replayChatProvider);

        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);
        await TestWait.UntilAsync(() => replayChatProvider.CallCount > 0, TimeSpan.FromMilliseconds(500));

        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        Assert.Equal("vod title", tab.Title);
        Assert.Equal(0, streamlink.StartCount);
        Assert.Equal(0, streamlink.StartExternalHttpRequests.Count);
        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.Equal(StreamTargetKind.TwitchVod, streamlink.ResolveStreamUrlRequests[0].Target.Kind);
        Assert.Equal("https://www.twitch.tv/videos/2786354640", streamlink.ResolveStreamUrlRequests[0].Target.Url);
        Assert.Equal(false, streamlink.ResolveStreamUrlRequests[0].LowLatency);
        Assert.Equal(1, playbackFactory.Engine!.PlayCount);
        Assert.Equal(directVodUri, playbackFactory.Engine.LastPlayedUri);
        Assert.Equal(0, viewerCountService.CallCount);
        Assert.Equal(0, replayResolver.CallCount);
        Assert.Equal(0, chatFactory.Client.ConnectCount);
        Assert.True(tab.IsReplaySeekEnabled);
        Assert.Equal(TimeSpan.FromMinutes(90).TotalSeconds, tab.ReplaySeekMaximum);
        Assert.Equal("VOD", tab.ReplayLiveStateText);
        Assert.Equal("2786354640", replayChatProvider.Requests[0].ReplayId);
        Assert.Equal("26490481", replayChatProvider.Requests[0].ChatRoomId);
        Assert.Equal(false, tab.CanSendChatMessages);
        Assert.Equal(false, tab.CanReturnToLive);
        Assert.Equal(false, tab.ReturnToLiveCommand.CanExecute(null));

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.Equal(0, streamlink.StartCount);
        Assert.Equal(1, playbackFactory.Engine.PlayCount);
        Assert.Equal(directVodUri, playbackFactory.Engine.LastPlayedUri);
        Assert.Equal(TimeSpan.FromMinutes(10), playbackFactory.Engine.Position);
        Assert.Equal(false, tab.IsBehindLive);
        Assert.Equal("VOD", tab.ReplayLiveStateText);
        await TestWait.UntilAsync(
            () => replayChatProvider.Offsets.LastOrDefault() == TimeSpan.FromMinutes(10),
            TimeSpan.FromMilliseconds(500));
        Assert.Equal(TimeSpan.FromMinutes(10), replayChatProvider.Offsets.Last());
        await TestWait.UntilAsync(
            () => tab.DockedChatMessages.Any(message => message.Message == "vod chat"),
            TimeSpan.FromMilliseconds(500));
        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "vod chat"));

        tab.OutgoingChatText = "should not send";
        await tab.SendChatMessageAsync();

        Assert.Equal(0, chatFactory.Client.SentMessages.Count);
        await tab.DisposeAsync();
    }),
    ("explicit Kick VOD tab without webhook cache reports replay chat unavailable after startup", async () =>
    {
        var sourceVodUri = new Uri("https://vod.kick.com/xqc/master.m3u8");
        var resolvedVodUri = new Uri("https://vod.kick.com/xqc/720p/index.m3u8");
        var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
        const string unavailableReason = "No official Kick webhook chat cache was found for xqc. Enable the Kick webhook listener, subscribe to chat.message.sent, and capture chat before opening the VOD.";
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (request, _) =>
                Task.FromResult(new StreamlinkResolvedUrl(resolvedVodUri, $"Resolved {request.Target.MediaId}.")),
            StartExternalHttpOverride = (_, _) => throw new InvalidOperationException("Kick VOD should not start an external Streamlink transport.")
        };
        var playbackFactory = new FakePlaybackEngineFactory();
        var chatFactory = new FakeChatClientFactory();
        var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable(unavailableReason));
        var target = new StreamTarget(
            PlatformKind.Kick,
            "xqc",
            sourceVodUri.ToString(),
            StreamTargetKind.KickVod,
            "uuid-123",
            "Kick VOD without cached chat",
            "",
            TimeSpan.FromMinutes(30),
            startedAt,
            ChatRoomId: "668");
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        var tab = TestViewModels.CreateTab(
            target,
            "best",
            streamlink,
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayChatProvider: replayChatProvider);

        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => replayChatProvider.CallCount >= 1 &&
                DockedChatMessagesContainText(tab, "webhook chat cache"),
            TimeSpan.FromMilliseconds(500));

        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.Equal(StreamTargetKind.KickVod, streamlink.ResolveStreamUrlRequests[0].Target.Kind);
        Assert.Equal(sourceVodUri.ToString(), streamlink.ResolveStreamUrlRequests[0].Target.Url);
        Assert.Equal("best", streamlink.ResolveStreamUrlRequests[0].Quality);
        Assert.Equal(false, streamlink.ResolveStreamUrlRequests[0].LowLatency);
        Assert.Equal(0, streamlink.StartCount);
        Assert.Equal(1, playbackFactory.Engine!.PlayCount);
        Assert.Equal(resolvedVodUri, playbackFactory.Engine.LastPlayedUri);
        Assert.Equal(0, chatFactory.Client.ConnectCount);
        Assert.Equal(false, tab.CanSendChatMessages);
        Assert.Equal("uuid-123", replayChatProvider.Requests[0].ReplayId);
        Assert.Equal(startedAt, replayChatProvider.Requests[0].StreamStartedAtUtc);
        Assert.Equal("668", replayChatProvider.Requests[0].ChatRoomId);
        Assert.Equal(TimeSpan.Zero, replayChatProvider.Offsets[0]);
        Assert.True(DockedChatMessagesContainText(tab, unavailableReason));

        await tab.DisposeAsync();
    }),
    ("explicit Kick VOD tab without start time reports replay chat unavailable", async () =>
    {
        var sourceVodUri = new Uri("https://vod.kick.com/xqc/master.m3u8");
        var resolvedVodUri = new Uri("https://vod.kick.com/xqc/720p/index.m3u8");
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (request, _) =>
                Task.FromResult(new StreamlinkResolvedUrl(resolvedVodUri, $"Resolved {request.Target.MediaId}.")),
            StartExternalHttpOverride = (_, _) => throw new InvalidOperationException("Kick VOD should not start an external Streamlink transport.")
        };
        var playbackFactory = new FakePlaybackEngineFactory();
        var chatFactory = new FakeChatClientFactory();
        var viewerCountService = new FakeViewerCountService();
        var replayResolver = new FakeReplayResolver(ReplaySessionInfo.Unavailable(
            PlatformKind.Kick,
            "xqc",
            "not used"));
        var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable(
            "Official Kick VOD chat needs the VOD start time so webhook messages can be aligned to playback."));
        var target = new StreamTarget(
            PlatformKind.Kick,
            "xqc",
            sourceVodUri.ToString(),
            StreamTargetKind.KickVod,
            "uuid-123",
            "Kick VOD",
            "",
            TimeSpan.FromMinutes(30));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        var tab = TestViewModels.CreateTab(
            target,
            "best",
            streamlink,
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            viewerCountService: viewerCountService,
            replayResolver: replayResolver,
            replayChatProvider: replayChatProvider);

        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => replayChatProvider.CallCount >= 1 &&
                DockedChatMessagesContainText(tab, "VOD start time") &&
                tab.CanSeekReplay,
            TimeSpan.FromMilliseconds(500));
        await tab.ReplayChatLoadIdleTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.Equal(StreamTargetKind.KickVod, streamlink.ResolveStreamUrlRequests[0].Target.Kind);
        Assert.Equal(sourceVodUri.ToString(), streamlink.ResolveStreamUrlRequests[0].Target.Url);
        Assert.Equal("best", streamlink.ResolveStreamUrlRequests[0].Quality);
        Assert.Equal(false, streamlink.ResolveStreamUrlRequests[0].LowLatency);
        Assert.Equal(0, streamlink.StartCount);
        Assert.Equal(0, streamlink.StartExternalHttpRequests.Count);
        Assert.Equal(1, playbackFactory.Engine!.PlayCount);
        Assert.Equal(resolvedVodUri, playbackFactory.Engine.LastPlayedUri);
        Assert.Equal(0, viewerCountService.CallCount);
        Assert.Equal(0, replayResolver.CallCount);
        Assert.Equal(0, chatFactory.Client.ConnectCount);
        Assert.True(replayChatProvider.CallCount >= 1);
        Assert.True(tab.IsReplaySeekEnabled);
        Assert.True(tab.CanSeekReplay);
        Assert.Equal(TimeSpan.FromMinutes(30).TotalSeconds, tab.ReplaySeekMaximum);
        Assert.Equal("VOD", tab.ReplayLiveStateText);
        Assert.Equal("Kick VOD replay available: uuid-123", tab.ReplaySeekToolTip);
        Assert.Equal(false, tab.CanSendChatMessages);
        Assert.Equal(false, tab.CanReturnToLive);
        Assert.True(DockedChatMessagesContainText(tab, "VOD start time"));

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.Equal(0, streamlink.StartCount);
        Assert.Equal(1, playbackFactory.Engine.PlayCount);
        Assert.Equal(resolvedVodUri, playbackFactory.Engine.LastPlayedUri);
        Assert.Equal(TimeSpan.FromMinutes(10), playbackFactory.Engine.Position);
        Assert.Equal(false, tab.IsBehindLive);
        Assert.Equal("VOD", tab.ReplayLiveStateText);
        await TestWait.UntilAsync(
            () => replayChatProvider.CallCount >= 2 &&
                DockedChatMessagesContainText(tab, "VOD start time"),
            TimeSpan.FromMilliseconds(500));
        await tab.ReplayChatLoadIdleTask.WaitAsync(TimeSpan.FromSeconds(1));

        await tab.SeekReplayAsync(TimeSpan.FromSeconds(-30));
        await tab.ReplayChatLoadIdleTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.Zero, playbackFactory.Engine.Position);

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(45));
        await tab.ReplayChatLoadIdleTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromMinutes(30), playbackFactory.Engine.Position);
        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.True(replayChatProvider.CallCount >= 2);

        await tab.DisposeAsync();
    }),
    ("explicit Kick VOD tab loads replay chat when start time is available", async () =>
    {
        var directVodUri = new Uri("https://vod.kick.com/xqc/index.m3u8");
        var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
        var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available(
        [
            new ReplayChatMessage(
                TimeSpan.Zero,
                new ChatMessage(
                    PlatformKind.Kick,
                    "xqc",
                    "viewer",
                    "official Kick VOD replay chat",
                    startedAt,
                    MessageId: "official-kick-vod-replay-chat"))
        ], TimeSpan.Zero, TimeSpan.FromMinutes(4)));
        var eventSubscriptionService = new FakeKickEventSubscriptionService(
            new KickEventSubscriptionEnsureResult(
                KickEventSubscriptionEnsureStatus.Subscribed,
                "Official Kick chat webhook subscription created for xqc.",
                "sub-123",
                456));
        var chatFactory = new FakeChatClientFactory();
        var target = new StreamTarget(
            PlatformKind.Kick,
            "xqc",
            directVodUri.ToString(),
            StreamTargetKind.KickVod,
            "uuid-123",
            "Kick VOD with chat",
            "",
            TimeSpan.FromMinutes(30),
            startedAt,
            ChatRoomId: "668");
        var tab = TestViewModels.CreateTab(
            target,
            "best",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayChatProvider: replayChatProvider,
            kickEventSubscriptionService: eventSubscriptionService);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.KickWebhookListenerEnabled = true;

        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);

        await TestWait.UntilAsync(
            () => replayChatProvider.CallCount >= 1 &&
                eventSubscriptionService.CallCount >= 1 &&
                DockedChatMessagesContain(tab, "official Kick VOD replay chat"),
            TimeSpan.FromSeconds(2));

        Assert.Equal("uuid-123", replayChatProvider.Requests[0].ReplayId);
        Assert.Equal(startedAt, replayChatProvider.Requests[0].StreamStartedAtUtc);
        Assert.Equal("668", replayChatProvider.Requests[0].ChatRoomId);
        Assert.Equal(TimeSpan.Zero, replayChatProvider.Offsets[0]);
        Assert.Equal("xqc", eventSubscriptionService.Requests[0].Channel);
        Assert.Equal(0, chatFactory.Client.ConnectCount);
        Assert.Equal(false, tab.CanSendChatMessages);
        Assert.Equal(0, tab.DockedChatMessages.Count(message =>
            message.Message.Contains("Kick seekback chat", StringComparison.Ordinal)));

        tab.OutgoingChatText = "should not send";
        await tab.SendChatMessageAsync();

        Assert.Equal(0, chatFactory.Client.ConnectCount);
        Assert.Equal(0, chatFactory.Client.SentMessages.Count);

        await tab.DisposeAsync();
    }),
    ("Kick VOD with missing duration leaves replay seekbar disabled", async () =>
    {
        var directVodUri = new Uri("https://vod.kick.com/xqc/index.m3u8");
        var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("Kick replay chat is unsupported."));
        var tab = TestViewModels.CreateTab(
            new StreamTarget(
                PlatformKind.Kick,
                "xqc",
                directVodUri.ToString(),
                StreamTargetKind.KickVod,
                "uuid-123",
                "Kick VOD",
                "",
                TimeSpan.Zero),
            "best",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayChatProvider: replayChatProvider);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(1234));

        await tab.StartAsync(settings);

        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        Assert.Equal(false, tab.IsReplaySeekEnabled);
        Assert.Equal(false, tab.CanSeekReplay);
        Assert.Contains("usable duration", tab.ReplaySeekToolTip);
        Assert.Equal(0, replayChatProvider.CallCount);

        await tab.DisposeAsync();
    }),
    ("home search service partial query shows live and offline channel matches", async () =>
    {
        var searchService = new FakeStreamSearchService(new StreamSearchResult(
            StreamSearchResultStatus.Available,
            [
                new StreamSearchChannel(
                    PlatformKind.Twitch,
                    "summit1g",
                    "summit1g",
                    "https://www.twitch.tv/summit1g",
                    "https://static-cdn.jtvnw.net/summit1g.jpg",
                    "live title",
                    "Just Chatting",
                    StreamSearchChannelState.Live,
                    StreamSearchSourceStatus.Available,
                    "Playable stream found.",
                    true),
                new StreamSearchChannel(
                    PlatformKind.Kick,
                    "summit",
                    "Summit",
                    "https://kick.com/summit",
                    "https://files.kick.com/summit.jpg",
                    "",
                    "",
                    StreamSearchChannelState.Offline,
                    StreamSearchSourceStatus.Available,
                    "Offline. Open VODs.",
                    false)
            ],
            "2 channel results."));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            streamSearchService: searchService,
            streamSearchDebounceInterval: TimeSpan.Zero);

        viewModel.NewStreamText = "sum";

        await TestWait.UntilAsync(
            () => searchService.CallCount == 1 && viewModel.StreamSearchResults.Count == 2,
            TimeSpan.FromMilliseconds(500));
        Assert.Equal("sum", searchService.Requests[0].Query);
        Assert.Equal("summit1g", viewModel.StreamSearchResults[0].Channel);
        Assert.Equal("Live", viewModel.StreamSearchResults[0].StateText);
        Assert.True(viewModel.StreamSearchResults[0].OpenAndStayOnHomeCommand.CanExecute(null));
        Assert.Equal("summit", viewModel.StreamSearchResults[1].Channel);
        Assert.Equal("Offline", viewModel.StreamSearchResults[1].StateText);
        Assert.True(viewModel.StreamSearchResults[1].OpenAndStayOnHomeCommand.CanExecute(null));
        await viewModel.DisposeAsync();
    }),
    ("home search ignores stale discovery after the query changes", async () =>
    {
        static StreamSearchResult CreateLiveSearchResult(string channel)
        {
            return new StreamSearchResult(
                StreamSearchResultStatus.Available,
                [
                    new StreamSearchChannel(
                        PlatformKind.Twitch,
                        channel,
                        channel,
                        $"https://www.twitch.tv/{channel}",
                        "",
                        "",
                        "",
                        StreamSearchChannelState.Live,
                        StreamSearchSourceStatus.Available,
                        "Live.",
                        true)
                ],
                "1 live channel result.");
        }

        var oldSearchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldSearchRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldSearchFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var searchService = new FakeStreamSearchService(CreateLiveSearchResult("unused"))
        {
            ResponderAsync = async (request, cancellationToken) =>
            {
                if (request.Query == "new")
                {
                    return CreateLiveSearchResult("new-channel");
                }

                using var registration = cancellationToken.Register(() => oldCancellationObserved.TrySetResult());
                oldSearchStarted.TrySetResult();
                await oldSearchRelease.Task;
                oldSearchFinished.TrySetResult();
                return CreateLiveSearchResult("old-channel");
            }
        };
        var settings = new AppSettings();
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            streamSearchService: searchService,
            streamSearchDebounceInterval: TimeSpan.Zero);

        viewModel.NewStreamText = "old";
        await oldSearchStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.Equal(0, viewModel.StreamSearchResults.Count);

        viewModel.NewStreamText = "new";

        await oldCancellationObserved.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        await TestWait.UntilAsync(
            () => viewModel.StreamSearchResults.Count == 1 &&
                viewModel.StreamSearchResults[0].Channel == "new-channel",
            TimeSpan.FromMilliseconds(500));

        oldSearchRelease.SetResult();
        await oldSearchFinished.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        await Task.Delay(50);

        Assert.Equal("new-channel", viewModel.StreamSearchResults.Single().Channel);
        await viewModel.DisposeAsync();
    }),
    ("home search shows results before viewer counts finish then refreshes their order", async () =>
    {
        var searchService = new FakeStreamSearchService(new StreamSearchResult(
            StreamSearchResultStatus.Available,
            [
                new StreamSearchChannel(
                    PlatformKind.Twitch,
                    "offline-channel",
                    "Offline Channel",
                    "https://www.twitch.tv/offline-channel",
                    "",
                    "",
                    "",
                    StreamSearchChannelState.Offline,
                    StreamSearchSourceStatus.Available,
                    "Offline. Open VODs.",
                    false),
                new StreamSearchChannel(
                    PlatformKind.Kick,
                    "lower-live",
                    "Lower Live",
                    "https://kick.com/lower-live",
                    "",
                    "",
                    "IRL",
                    StreamSearchChannelState.Live,
                    StreamSearchSourceStatus.Available,
                    "Playable stream found.",
                    true),
                new StreamSearchChannel(
                    PlatformKind.Kick,
                    "unavailable-channel",
                    "Unavailable Channel",
                    "https://kick.com/unavailable-channel",
                    "",
                    "",
                    "",
                    StreamSearchChannelState.Unavailable,
                    StreamSearchSourceStatus.Unavailable,
                    "Stream unavailable.",
                    false,
                    ReportedLive: true),
                new StreamSearchChannel(
                    PlatformKind.Twitch,
                    "higher-live",
                    "Higher Live",
                    "https://www.twitch.tv/higher-live",
                    "",
                    "",
                    "Just Chatting",
                    StreamSearchChannelState.Live,
                    StreamSearchSourceStatus.Available,
                    "Playable stream found.",
                    true,
                    ViewerCount: 100)
            ],
            "4 channel results."));
        var viewerCountsRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewerCountService = new FakeViewerCountService
        {
            ResponderAsync = async (target, cancellationToken) =>
            {
                await viewerCountsRelease.Task.WaitAsync(cancellationToken);
                return target.Channel switch
                {
                    "lower-live" => new ViewerCountResult(ViewerCountState.Available, 250, "updated"),
                    "unavailable-channel" => new ViewerCountResult(ViewerCountState.Unavailable, null, "unavailable"),
                    _ => throw new InvalidOperationException($"Viewer count should not load for {target.Channel}.")
                };
            }
        };
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            viewerCountService: viewerCountService,
            streamSearchService: searchService,
            streamSearchDebounceInterval: TimeSpan.Zero);

        viewModel.NewStreamText = "channel";

        await TestWait.UntilAsync(
            () => viewModel.StreamSearchResults.Count == 4 && viewerCountService.CallCount == 2,
            TimeSpan.FromMilliseconds(500));
        Assert.SequenceEqual(
            ["higher-live", "lower-live", "unavailable-channel", "offline-channel"],
            viewModel.StreamSearchResults.Select(result => result.Channel).ToArray());
        Assert.True(viewModel.StreamSearchResults[0].IsLive);
        Assert.Equal(100, viewModel.StreamSearchResults[0].ViewerCount);
        Assert.Equal("100 viewers", viewModel.StreamSearchResults[0].ViewerCountText);
        Assert.True(viewModel.StreamSearchResults[0].HasViewerCount);
        Assert.True(viewModel.StreamSearchResults[2].IsLive);
        Assert.Equal("Live", viewModel.StreamSearchResults[2].StateText);
        Assert.Equal(false, viewModel.StreamSearchResults[2].CanOpen);
        Assert.Equal<int?>(null, viewModel.StreamSearchResults[2].ViewerCount);
        Assert.Equal(false, viewModel.StreamSearchResults[3].IsLive);
        Assert.Equal(false, viewModel.IsStreamSearchRunning);
        Assert.Equal(2, viewerCountService.CallCount);
        Assert.Equal(false, viewerCountService.Requests.Any(target => target.Channel == "higher-live"));
        Assert.Equal(false, viewerCountService.Requests.Any(target => target.Channel == "offline-channel"));

        viewerCountsRelease.SetResult();

        await TestWait.UntilAsync(
            () => viewModel.StreamSearchResults[0].Channel == "lower-live" &&
                viewModel.StreamSearchResults[0].ViewerCount == 250,
            TimeSpan.FromMilliseconds(500));
        Assert.SequenceEqual(
            ["lower-live", "higher-live", "unavailable-channel", "offline-channel"],
            viewModel.StreamSearchResults.Select(result => result.Channel).ToArray());
        Assert.Equal("250 viewers", viewModel.StreamSearchResults[0].ViewerCountText);
        await viewModel.DisposeAsync();
    }),
    ("home search ignores stale viewer-count enrichment after the query changes", async () =>
    {
        static StreamSearchResult CreateLiveSearchResult(string channel)
        {
            return new StreamSearchResult(
                StreamSearchResultStatus.Available,
                [
                    new StreamSearchChannel(
                        PlatformKind.Twitch,
                        channel,
                        channel,
                        $"https://www.twitch.tv/{channel}",
                        "",
                        "",
                        "",
                        StreamSearchChannelState.Live,
                        StreamSearchSourceStatus.Available,
                        "Live.",
                        true)
                ],
                "1 live channel result.");
        }

        var searchService = new FakeStreamSearchService(
            CreateLiveSearchResult("old-channel"),
            CreateLiveSearchResult("new-channel"));
        var oldViewerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldViewerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldViewerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewerCountService = new FakeViewerCountService
        {
            ResponderAsync = async (target, cancellationToken) =>
            {
                if (target.Channel == "new-channel")
                {
                    return new ViewerCountResult(ViewerCountState.Available, 10, "updated");
                }

                using var registration = cancellationToken.Register(() => oldCancellationObserved.TrySetResult());
                oldViewerStarted.TrySetResult();
                await oldViewerRelease.Task;
                oldViewerFinished.TrySetResult();
                return new ViewerCountResult(ViewerCountState.Available, 999, "stale");
            }
        };
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            viewerCountService: viewerCountService,
            streamSearchService: searchService,
            streamSearchDebounceInterval: TimeSpan.Zero);

        viewModel.NewStreamText = "old";
        await oldViewerStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.Equal("old-channel", viewModel.StreamSearchResults.Single().Channel);
        Assert.Equal<int?>(null, viewModel.StreamSearchResults.Single().ViewerCount);

        viewModel.NewStreamText = "new";

        await oldCancellationObserved.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        await TestWait.UntilAsync(
            () => viewModel.StreamSearchResults.Count == 1 &&
                viewModel.StreamSearchResults[0].Channel == "new-channel" &&
                viewModel.StreamSearchResults[0].ViewerCount == 10,
            TimeSpan.FromMilliseconds(500));

        oldViewerRelease.SetResult();
        await oldViewerFinished.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        await Task.Delay(50);

        Assert.Equal("new-channel", viewModel.StreamSearchResults.Single().Channel);
        Assert.Equal(10, viewModel.StreamSearchResults.Single().ViewerCount);
        await viewModel.DisposeAsync();
    }),
    ("home search service live result opens playback", async () =>
    {
        var searchService = new FakeStreamSearchService(new StreamSearchResult(
            StreamSearchResultStatus.Available,
            [
                new StreamSearchChannel(
                    PlatformKind.Kick,
                    "xqc",
                    "xQc",
                    "https://kick.com/xqc",
                    "https://files.kick.com/xqc.jpg",
                    "live title",
                    "IRL",
                    StreamSearchChannelState.Live,
                    StreamSearchSourceStatus.Available,
                    "Playable stream found.",
                    true)
            ],
            "1 live channel result."));
        var streamlink = new FakeStreamlinkService();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            streamSearchService: searchService);

        viewModel.NewStreamText = "xq";
        await viewModel.AddAndPlayCommand.ExecuteAsync();

        Assert.Equal(1, viewModel.StreamSearchResults.Count);
        await viewModel.StreamSearchResults[0].OpenAndStayOnHomeCommand.ExecuteAsync();

        await TestWait.UntilAsync(() => viewModel.Tabs.Count == 1, TimeSpan.FromMilliseconds(500));
        viewModel.Tabs[0].SetVideoHandle(new IntPtr(1234));
        await TestWait.UntilAsync(() => streamlink.Started, TimeSpan.FromMilliseconds(500));
        Assert.Equal(PlatformKind.Kick, viewModel.Tabs[0].Target.Platform);
        Assert.Equal("xqc", viewModel.Tabs[0].Target.Channel);
        Assert.Equal("IRL", viewModel.Tabs[0].Target.CategoryName);
        Assert.Equal("IRL", viewModel.TabStripItems.Single().SubtitleText);
        Assert.True(viewModel.IsHomeSelected);
        await viewModel.DisposeAsync();
    }),
    ("background Home playback clears search after inactive policy pauses the tab", async () =>
    {
        var searchService = new FakeStreamSearchService(new StreamSearchResult(
            StreamSearchResultStatus.Available,
            [
                new StreamSearchChannel(
                    PlatformKind.Twitch,
                    "albralelie",
                    "Albralelie",
                    "https://www.twitch.tv/albralelie",
                    "",
                    "live title",
                    "Apex Legends",
                    StreamSearchChannelState.Live,
                    StreamSearchSourceStatus.Available,
                    "Playable stream found.",
                    true)
            ],
            "1 live channel result."));
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            streamSearchService: searchService,
            streamSearchDebounceInterval: TimeSpan.Zero);

        viewModel.NewStreamText = "albr";
        await TestWait.UntilAsync(
            () => viewModel.StreamSearchResults.Count == 1,
            TimeSpan.FromMilliseconds(500));
        await viewModel.StreamSearchResults[0].OpenAndStayOnHomeCommand.ExecuteAsync();

        var tab = viewModel.Tabs.Single();
        tab.SetVideoHandle(new IntPtr(1234));
        await TestWait.UntilAsync(
            () => playbackFactory.Engine?.Played == true &&
                viewModel.NewStreamText.Length == 0 &&
                tab.Status == PlaybackStatus.Paused,
            TimeSpan.FromSeconds(1));

        Assert.True(viewModel.IsHomeSelected);
        Assert.Equal(false, tab.IsSelected);
        await viewModel.DisposeAsync();
    }),
    ("offline Twitch search result opens Twitch VOD search", async () =>
    {
        var broadcaster = new TwitchVodBroadcaster("26490481", "summit1g", "summit1g");
        var vod = new TwitchVodItem(
            "vod-1",
            "stream-1",
            broadcaster.Id,
            broadcaster.Login,
            broadcaster.DisplayName,
            "first VOD",
            "",
            "https://www.twitch.tv/videos/vod-1",
            "",
            null,
            null,
            TimeSpan.FromMinutes(45),
            100,
            TwitchVodTypeFilter.Archive);
        var twitchVodService = new FakeTwitchVodService(
            new TwitchVodSearchResult(TwitchVodSearchStatus.Available, broadcaster, [vod], "", "1 VOD"));
        var searchService = new FakeStreamSearchService(new StreamSearchResult(
            StreamSearchResultStatus.Available,
            [
                new StreamSearchChannel(
                    PlatformKind.Twitch,
                    "summit1g",
                    "summit1g",
                    "https://www.twitch.tv/summit1g",
                    "",
                    "",
                    "",
                    StreamSearchChannelState.Offline,
                    StreamSearchSourceStatus.Available,
                    "Offline. Open VODs.",
                    false)
            ],
            "1 offline channel result."));
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            twitchVodService: twitchVodService,
            streamSearchService: searchService);

        viewModel.NewStreamText = "summ";
        await viewModel.AddAndPlayCommand.ExecuteAsync();
        await viewModel.StreamSearchResults[0].OpenAndStayOnHomeCommand.ExecuteAsync();

        Assert.True(viewModel.IsTwitchVodsHomePageSelected);
        Assert.True(viewModel.IsTwitchVodPlatformSelected);
        Assert.Equal("summit1g", viewModel.TwitchVodSearchText);
        Assert.Equal(1, twitchVodService.CallCount);
        Assert.Equal("summit1g", twitchVodService.Requests[0].Streamer);
        Assert.Equal(1, viewModel.TwitchVods.Count);
        Assert.Equal("vod-1", viewModel.TwitchVods[0].Id);
        await viewModel.DisposeAsync();
    }),
    ("offline Kick search result opens Kick VOD search", async () =>
    {
        var kickVod = new KickVodItem(
            "123",
            "456",
            "uuid-123",
            "xqc",
            "xQc",
            "Kick VOD",
            "https://kick.com/xqc/videos/uuid-123",
            "https://vod.kick.com/xqc/index.m3u8",
            "",
            "Just Chatting",
            null,
            null,
            TimeSpan.FromMinutes(30),
            1234);
        var kickVodService = new FakeKickVodService(
            new KickVodSearchResult(KickVodSearchStatus.Available, [kickVod], "", "1 VOD"));
        var searchService = new FakeStreamSearchService(new StreamSearchResult(
            StreamSearchResultStatus.Available,
            [
                new StreamSearchChannel(
                    PlatformKind.Kick,
                    "xqc",
                    "xQc",
                    "https://kick.com/xqc",
                    "",
                    "",
                    "",
                    StreamSearchChannelState.Offline,
                    StreamSearchSourceStatus.Available,
                    "Offline. Open VODs.",
                    false)
            ],
            "1 offline channel result."));
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            streamSearchService: searchService,
            kickVodService: kickVodService);

        viewModel.NewStreamText = "xqc";
        await viewModel.AddAndPlayCommand.ExecuteAsync();
        await viewModel.StreamSearchResults[0].OpenAndStayOnHomeCommand.ExecuteAsync();

        Assert.True(viewModel.IsTwitchVodsHomePageSelected);
        Assert.True(viewModel.IsKickVodPlatformSelected);
        Assert.Equal("xqc", viewModel.TwitchVodSearchText);
        Assert.Equal(1, kickVodService.CallCount);
        Assert.Equal("xqc", kickVodService.Requests[0].Channel);
        Assert.Equal(1, viewModel.TwitchVods.Count);
        Assert.Equal("uuid-123", viewModel.TwitchVods[0].Id);
        await viewModel.DisposeAsync();
    }),
    ("opening Kick VOD resolves HLS tab without recents", async () =>
    {
        var sourceVodUri = new Uri("https://vod.kick.com/xqc/master.m3u8");
        var resolvedVodUri = new Uri("https://vod.kick.com/xqc/720p/index.m3u8");
        var kickVod = new KickVodItem(
            "123",
            "456",
            "uuid-123",
            "xqc",
            "xQc",
            "Kick VOD",
            "https://kick.com/xqc/videos/uuid-123",
            sourceVodUri.ToString(),
            "https://files.kick.com/vod.jpg",
            "Just Chatting",
            new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero),
            TimeSpan.FromMinutes(30),
            1234,
            "668",
            "https://files.kick.com/xqc-profile.jpg");
        var kickVodService = new FakeKickVodService(
            new KickVodSearchResult(KickVodSearchStatus.Available, [kickVod], "", "1 VOD"));
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (request, _) =>
                Task.FromResult(new StreamlinkResolvedUrl(resolvedVodUri, $"Resolved {request.Target.MediaId}."))
        };
        var playbackFactory = new FakePlaybackEngineFactory();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        var chatFactory = new FakeChatClientFactory();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            kickVodService: kickVodService,
            twitchVodSearchDebounceInterval: TimeSpan.Zero);

        viewModel.ShowTwitchVodsHomePageCommand.Execute(null);
        viewModel.SelectKickVodPlatformCommand.Execute(null);
        viewModel.TwitchVodSearchText = "xqc";
        await TestWait.UntilAsync(
            () => kickVodService.CallCount == 1 && viewModel.TwitchVods.Count == 1,
            TimeSpan.FromMilliseconds(500));

        await viewModel.TwitchVods[0].OpenAndStayOnHomeCommand.ExecuteAsync();

        await TestWait.UntilAsync(() => viewModel.Tabs.Count == 1, TimeSpan.FromMilliseconds(500));
        viewModel.Tabs[0].SetVideoHandle(new IntPtr(1234));
        await TestWait.UntilAsync(() => playbackFactory.Engine?.Played == true, TimeSpan.FromMilliseconds(500));
        Assert.Equal(StreamTargetKind.KickVod, viewModel.Tabs[0].Target.Kind);
        Assert.Equal("uuid-123", viewModel.Tabs[0].Target.MediaId);
        Assert.Equal("", viewModel.Tabs[0].Target.BroadcasterId);
        Assert.Equal("668", viewModel.Tabs[0].Target.ChatRoomId);
        Assert.Equal("Just Chatting", viewModel.Tabs[0].Target.CategoryName);
        Assert.Equal("https://files.kick.com/xqc-profile.jpg", viewModel.Tabs[0].ProfileImageUrl);
        Assert.Equal("https://files.kick.com/xqc-profile.jpg", viewModel.TabStripItems.Single().ProfileImageUrl);
        Assert.Equal("Just Chatting", viewModel.TabStripItems.Single().SubtitleText);
        Assert.Equal(resolvedVodUri, playbackFactory.Engine!.LastPlayedUri);
        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.Equal(StreamTargetKind.KickVod, streamlink.ResolveStreamUrlRequests[0].Target.Kind);
        Assert.Equal(sourceVodUri.ToString(), streamlink.ResolveStreamUrlRequests[0].Target.Url);
        Assert.Equal("best", streamlink.ResolveStreamUrlRequests[0].Quality);
        Assert.Equal(false, streamlink.ResolveStreamUrlRequests[0].LowLatency);
        Assert.True(viewModel.Tabs[0].IsReplaySeekEnabled);
        Assert.True(viewModel.Tabs[0].CanSeekReplay);
        Assert.Equal(TimeSpan.FromMinutes(30).TotalSeconds, viewModel.Tabs[0].ReplaySeekMaximum);
        Assert.Equal("VOD", viewModel.Tabs[0].ReplayLiveStateText);
        Assert.Equal("Kick VOD replay available: uuid-123", viewModel.Tabs[0].ReplaySeekToolTip);

        await viewModel.Tabs[0].SeekReplayAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(1, playbackFactory.Engine.PlayCount);
        Assert.Equal(TimeSpan.FromMinutes(10), playbackFactory.Engine.Position);
        Assert.Equal(0, streamlink.StartCount);
        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.Equal(0, settings.RecentStreams.Count);
        Assert.Equal(0, chatFactory.Client.ConnectCount);
        await viewModel.DisposeAsync();
    }),
    ("home search command requires a nonblank streamer query", async () =>
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action());

        Assert.Equal(false, viewModel.AddAndPlayCommand.CanExecute(null));
        Assert.True(viewModel.IsNewStreamSearchPlaceholderVisible);

        viewModel.NewStreamText = "   ";

        Assert.Equal(false, viewModel.AddAndPlayCommand.CanExecute(null));
        Assert.True(viewModel.IsNewStreamSearchPlaceholderVisible);

        viewModel.NewStreamText = "summit1g";

        Assert.True(viewModel.AddAndPlayCommand.CanExecute(null));
        Assert.Equal(false, viewModel.IsNewStreamSearchPlaceholderVisible);
        await viewModel.DisposeAsync();
    }),
    ("home search automatically probes after streamer query changes", async () =>
    {
        var streamlink = new FakeStreamlinkService
        {
            ProbeStreamsOverride = (request, cancellationToken) =>
                Task.FromResult(request.Target.Platform == PlatformKind.Kick
                    ? new StreamlinkProbeResult(true, "Playable stream found.")
                    : new StreamlinkProbeResult(false, "No streams found."))
        };
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            streamSearchDebounceInterval: TimeSpan.Zero);

        viewModel.NewStreamText = "xqc";

        await TestWait.UntilAsync(
            () => streamlink.ProbeRequests.Count == 2 && viewModel.StreamSearchResults.Count == 2,
            TimeSpan.FromMilliseconds(500));
        Assert.Equal(0, streamlink.StartCount);
        Assert.Equal(PlatformKind.Kick, viewModel.StreamSearchResults[0].Target.Platform);
        Assert.Equal("Live", viewModel.StreamSearchResults[0].StateText);
        Assert.Equal(PlatformKind.Twitch, viewModel.StreamSearchResults[1].Target.Platform);
        Assert.Equal("Unavailable", viewModel.StreamSearchResults[1].StateText);
        Assert.Equal(false, viewModel.StreamSearchResults[1].OpenAndStayOnHomeCommand.CanExecute(null));
        Assert.True(viewModel.IsStreamSearchPanelVisible);
        await viewModel.DisposeAsync();
    }),
    ("home search result uses platform metadata thumbnail", async () =>
    {
        var streamlink = new FakeStreamlinkService
        {
            ProbeStreamsOverride = (request, cancellationToken) =>
                Task.FromResult(request.Target.Platform == PlatformKind.Kick
                    ? new StreamlinkProbeResult(true, "Playable stream found.")
                    : new StreamlinkProbeResult(false, "No streams found."))
        };
        var metadataService = new FakeStreamMetadataService(new StreamMetadataResult(
            StreamMetadataState.Available,
            "https://files.kick.com/xqc.jpg",
            "xQc",
            "Kick stream metadata updated.",
            "Just Chatting"));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            streamMetadataService: metadataService);

        viewModel.NewStreamText = "xqc";
        await viewModel.AddAndPlayCommand.ExecuteAsync();

        Assert.Equal(2, viewModel.StreamSearchResults.Count);
        Assert.Equal(2, metadataService.CallCount);
        Assert.True(metadataService.Requests.Any(request => request.Platform == PlatformKind.Kick));
        Assert.True(metadataService.Requests.Any(request => request.Platform == PlatformKind.Twitch));
        Assert.Equal("https://files.kick.com/xqc.jpg", viewModel.StreamSearchResults[0].ThumbnailUrl);
        Assert.True(viewModel.StreamSearchResults[0].HasThumbnail);
        Assert.Equal("xQc", viewModel.StreamSearchResults[0].DisplayName);
        Assert.Equal("Just Chatting", viewModel.StreamSearchResults[0].CategoryName);
        Assert.True(viewModel.StreamSearchResults[0].HasCategory);
        await viewModel.DisposeAsync();
    }),
    ("home search result leaves thumbnail empty when metadata is unavailable", async () =>
    {
        var metadataService = new FakeStreamMetadataService(new StreamMetadataResult(
            StreamMetadataState.NotConfigured,
            "",
            "",
            "Twitch stream thumbnails require a Twitch OAuth token."));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            streamMetadataService: metadataService);

        viewModel.NewStreamText = "https://www.twitch.tv/summit1g";
        await viewModel.AddAndPlayCommand.ExecuteAsync();

        Assert.Equal(1, viewModel.StreamSearchResults.Count);
        Assert.Equal(1, metadataService.CallCount);
        Assert.Equal("", viewModel.StreamSearchResults[0].ThumbnailUrl);
        Assert.Equal(false, viewModel.StreamSearchResults[0].HasThumbnail);
        Assert.Equal("summit1g", viewModel.StreamSearchResults[0].DisplayName);
        Assert.Equal("", viewModel.StreamSearchResults[0].CategoryName);
        Assert.Equal(false, viewModel.StreamSearchResults[0].HasCategory);
        await viewModel.DisposeAsync();
    }),
    ("home search dropdown can be dismissed and reopened without losing verified results", async () =>
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            streamSearchDebounceInterval: TimeSpan.Zero);

        viewModel.NewStreamText = "xqc";

        await TestWait.UntilAsync(
            () => viewModel.StreamSearchResults.Count == 2,
            TimeSpan.FromMilliseconds(500));
        Assert.True(viewModel.IsStreamSearchPanelVisible);

        viewModel.DismissStreamSearchDropdown();

        Assert.Equal(2, viewModel.StreamSearchResults.Count);
        Assert.Equal(false, viewModel.IsStreamSearchPanelVisible);

        viewModel.ShowStreamSearchDropdown();

        Assert.True(viewModel.IsStreamSearchPanelVisible);

        viewModel.DismissStreamSearchDropdown();

        Assert.Equal(2, viewModel.StreamSearchResults.Count);
        Assert.Equal(false, viewModel.IsStreamSearchPanelVisible);
        await viewModel.DisposeAsync();
    }),
    ("home search shows both playable bare streamer results without guessing a platform", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action());

        viewModel.NewStreamText = "xqc";
        await viewModel.AddAndPlayCommand.ExecuteAsync();

        Assert.Equal(0, viewModel.Tabs.Count);
        Assert.Equal(0, streamlink.StartCount);
        Assert.Equal(2, streamlink.ProbeRequests.Count);
        Assert.Equal(2, viewModel.StreamSearchResults.Count);
        Assert.True(viewModel.HasStreamSearchResults);
        Assert.True(viewModel.IsStreamSearchResultsVisible);
        Assert.Equal(false, viewModel.IsStreamSearchEmptyVisible);
        Assert.Equal(PlatformKind.Twitch, viewModel.StreamSearchResults[0].Target.Platform);
        Assert.Equal(PlatformKind.Kick, viewModel.StreamSearchResults[1].Target.Platform);
        Assert.Contains("2 live channel results", viewModel.StatusMessage);
    }),
    ("home search probes Twitch and Kick before showing a bare streamer result", async () =>
    {
        var streamlink = new FakeStreamlinkService
        {
            ProbeStreamsOverride = async (request, cancellationToken) =>
            {
                await Task.Yield();
                if (request.Target.Platform == PlatformKind.Twitch)
                {
                    return new StreamlinkProbeResult(false, "No streams found.");
                }

                return new StreamlinkProbeResult(true, "Playable stream found.");
            }
        };
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action());

        viewModel.NewStreamText = "xqc";
        await viewModel.AddAndPlayCommand.ExecuteAsync();

        var probeRequests = streamlink.ProbeRequests;
        Assert.Equal(2, probeRequests.Count);
        Assert.True(
            probeRequests.Any(request => request.Target.Platform == PlatformKind.Twitch && request.Target.Channel == "xqc"),
            "Expected home search to probe Twitch for the bare streamer name.");
        Assert.True(
            probeRequests.Any(request => request.Target.Platform == PlatformKind.Kick && request.Target.Channel == "xqc"),
            "Expected home search to probe Kick for the bare streamer name.");
        Assert.Equal(0, viewModel.Tabs.Count);
        Assert.Equal(0, streamlink.StartCount);
        Assert.Equal(2, viewModel.StreamSearchResults.Count);
        Assert.Equal(PlatformKind.Kick, viewModel.StreamSearchResults[0].Target.Platform);
        Assert.Equal("xqc", viewModel.StreamSearchResults[0].Target.Channel);
        Assert.Equal("Live", viewModel.StreamSearchResults[0].StateText);
        Assert.Equal(PlatformKind.Twitch, viewModel.StreamSearchResults[1].Target.Platform);
        Assert.Equal("Unavailable", viewModel.StreamSearchResults[1].StateText);
        Assert.Contains("1 live", viewModel.StreamSearchStatus);
        Assert.Contains("1 unavailable", viewModel.StreamSearchStatus);

        await viewModel.StreamSearchResults[0].OpenAndStayOnHomeCommand.ExecuteAsync();

        await TestWait.UntilAsync(() => viewModel.Tabs.Count == 1, TimeSpan.FromMilliseconds(500));
        viewModel.Tabs[0].SetVideoHandle(new IntPtr(1234));
        await TestWait.UntilAsync(() => streamlink.Started, TimeSpan.FromMilliseconds(500));
        Assert.Equal(PlatformKind.Kick, viewModel.Tabs[0].Target.Platform);
        Assert.Equal("xqc", viewModel.Tabs[0].Target.Channel);
        Assert.True(viewModel.IsHomeSelected);
        await TestWait.UntilAsync(() => viewModel.NewStreamText == "", TimeSpan.FromMilliseconds(500));
        await viewModel.DisposeAsync();
    }),
    ("home search probes a platform URL before showing it as a result", async () =>
    {
        var streamlink = new FakeStreamlinkService
        {
            ProbeStreamsOverride = (request, cancellationToken) =>
                Task.FromResult(new StreamlinkProbeResult(false, "No streams found."))
        };
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action());

        viewModel.NewStreamText = "https://kick.com/xqc";
        await viewModel.AddAndPlayCommand.ExecuteAsync();

        Assert.Equal(0, viewModel.Tabs.Count);
        Assert.Equal(0, streamlink.StartCount);
        Assert.Equal(1, streamlink.ProbeRequests.Count);
        Assert.Equal(PlatformKind.Kick, streamlink.ProbeRequests[0].Target.Platform);
        Assert.Equal("xqc", streamlink.ProbeRequests[0].Target.Channel);
        Assert.Equal(1, viewModel.StreamSearchResults.Count);
        Assert.True(viewModel.HasStreamSearchResults);
        Assert.Equal(false, viewModel.IsStreamSearchEmptyVisible);
        Assert.Equal("Unavailable", viewModel.StreamSearchResults[0].StateText);
        Assert.Equal(false, viewModel.StreamSearchResults[0].OpenAndStayOnHomeCommand.CanExecute(null));
        Assert.Contains("1 unavailable", viewModel.StreamSearchStatus);
        await viewModel.DisposeAsync();
    }),
    ("late-starting inactive tab pauses and stays muted without muting selected stream", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedAudio = new FakeSharedAudioState();
        var firstEngine = new FakePlaybackEngine
        {
            PlayCompletion = playCompletion.Task,
            SharedAudioState = sharedAudio
        };
        var secondEngine = new FakePlaybackEngine { SharedAudioState = sharedAudio };
        var engines = new Queue<FakePlaybackEngine>();
        engines.Enqueue(firstEngine);
        engines.Enqueue(secondEngine);
        var playbackFactory = new FakePlaybackEngineFactory(() => engines.Dequeue());
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
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
        var firstTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var secondTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        firstTab.SetVideoHandle(new IntPtr(1234));
        secondTab.SetVideoHandle(new IntPtr(5678));
        viewModel.Tabs.Add(firstTab);
        viewModel.VideoTabs.Add(firstTab);
        viewModel.Tabs.Add(secondTab);
        viewModel.VideoTabs.Add(secondTab);

        viewModel.SelectedTab = firstTab;
        await viewModel.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(2));
        var firstStart = firstTab.StartAsync(settings);
        await firstEngine.PlayStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));

        var policyPassCount = viewModel.InactivePlaybackPolicyApplyPassCount;
        viewModel.SelectedTab = secondTab;
        await secondTab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => viewModel.InactivePlaybackPolicyApplyPassCount > policyPassCount,
            TimeSpan.FromSeconds(1));
        Assert.Equal(true, firstEngine.Muted);
        Assert.Equal(0, firstEngine.Volume);
        Assert.Equal(false, firstEngine.AudioTrackEnabled);
        Assert.Equal(false, secondEngine.Muted);
        Assert.Equal(80, secondEngine.Volume);
        Assert.Equal(true, secondEngine.AudioTrackEnabled);
        Assert.Equal(false, sharedAudio.Muted);
        Assert.Equal(80, sharedAudio.Volume);

        playCompletion.SetResult();
        await firstStart;
        await viewModel.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(PlaybackStatus.Paused, firstTab.Status);
        Assert.Equal(true, firstTab.PausedByTabSwitch);
        Assert.Equal(true, firstTab.IsBackgroundResourceServicesSuspended);
        Assert.Equal(1, firstEngine.StopCount);
        Assert.Equal(true, firstEngine.Muted);
        Assert.Equal(0, firstEngine.Volume);
        Assert.Equal(false, firstEngine.AudioTrackEnabled);
        Assert.Equal(false, secondEngine.Muted);
        Assert.Equal(80, secondEngine.Volume);
        Assert.Equal(true, secondEngine.AudioTrackEnabled);
        Assert.Equal(false, sharedAudio.Muted);
        Assert.Equal(80, sharedAudio.Volume);
        await firstTab.DisposeAsync();
        await secondTab.DisposeAsync();
    }),
    ("detected stream open loads live category metadata before creating tab", async () =>
    {
        var metadataService = new FakeStreamMetadataService(new StreamMetadataResult(
            StreamMetadataState.Available,
            "",
            "Albralelie",
            "metadata updated",
            "Apex Legends"));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            streamMetadataService: metadataService);

        await viewModel.OpenDetectedStreamAsync(StreamInputParser.Parse("albralelie", PlatformKind.Twitch));

        Assert.Equal(1, metadataService.CallCount);
        Assert.Equal(1, viewModel.Tabs.Count);
        Assert.Equal("Apex Legends", viewModel.Tabs[0].Target.CategoryName);
        Assert.Equal("Apex Legends", viewModel.TabStripItems.Single().SubtitleText);
        await viewModel.DisposeAsync();
    }),
    ("detected streams open another tab while first playback is still starting", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var firstPlaybackRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEngine = new FakePlaybackEngine { PlayCompletion = firstPlaybackRelease.Task };
        var secondEngine = new FakePlaybackEngine();
        var engines = new Queue<FakePlaybackEngine>();
        engines.Enqueue(firstEngine);
        engines.Enqueue(secondEngine);
        var playbackFactory = new FakePlaybackEngineFactory(() => engines.Dequeue());
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
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

        var firstOpen = viewModel.OpenDetectedStreamAsync(StreamInputParser.Parse("albralelie", PlatformKind.Twitch));
        await TestWait.UntilAsync(() => viewModel.Tabs.Count == 1, TimeSpan.FromMilliseconds(500));
        viewModel.Tabs[0].SetVideoHandle(new IntPtr(1234));
        await firstEngine.PlayStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));

        var secondOpen = viewModel.OpenDetectedStreamAsync(StreamInputParser.Parse("summit1g", PlatformKind.Twitch));
        await secondOpen.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.Equal(2, viewModel.Tabs.Count);
        viewModel.Tabs[1].SetVideoHandle(new IntPtr(5678));
        await secondEngine.PlayStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        await TestWait.UntilAsync(() => secondEngine.Played, TimeSpan.FromMilliseconds(500));

        firstPlaybackRelease.SetResult();
        await firstOpen.WaitAsync(TimeSpan.FromMilliseconds(500));
        await TestWait.UntilAsync(() => firstEngine.Played, TimeSpan.FromMilliseconds(500));
        await viewModel.DisposeAsync();
    }),
    ("detected streams start independently while first stream transport is unresolved", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var firstTransportStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondTransportStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTransportRelease = new TaskCompletionSource<IStreamTransportSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        streamlink.StartExternalHttpOverride = (_, _) =>
        {
            var call = Interlocked.Increment(ref callCount);
            if (call == 1)
            {
                firstTransportStarted.TrySetResult();
                return firstTransportRelease.Task;
            }

            secondTransportStarted.TrySetResult();
            return Task.FromResult<IStreamTransportSession>(new FakeTransportSession());
        };

        var createdEngines = new List<FakePlaybackEngine>();
        var createdEnginesGate = new object();
        var playbackFactory = new FakePlaybackEngineFactory(() =>
        {
            var engine = new FakePlaybackEngine();
            lock (createdEnginesGate)
            {
                createdEngines.Add(engine);
            }

            return engine;
        });
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
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

        var firstOpen = viewModel.OpenDetectedStreamAsync(StreamInputParser.Parse("albralelie", PlatformKind.Twitch));
        await TestWait.UntilAsync(() => viewModel.Tabs.Count == 1, TimeSpan.FromMilliseconds(500));
        viewModel.Tabs[0].SetVideoHandle(new IntPtr(1234));
        await firstTransportStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));

        await viewModel.OpenDetectedStreamAsync(StreamInputParser.Parse("summit1g", PlatformKind.Twitch)).WaitAsync(TimeSpan.FromMilliseconds(500));
        await TestWait.UntilAsync(() => viewModel.Tabs.Count == 2, TimeSpan.FromMilliseconds(500));
        viewModel.Tabs[1].SetVideoHandle(new IntPtr(5678));
        await secondTransportStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        await TestWait.UntilAsync(() => IsEnginePlayedForHandle(new IntPtr(5678)), TimeSpan.FromMilliseconds(500));

        Assert.Equal(2, callCount);
        Assert.Equal(false, IsEnginePlayedForHandle(new IntPtr(1234)));
        firstTransportRelease.SetResult(new FakeTransportSession());
        await firstOpen.WaitAsync(TimeSpan.FromMilliseconds(500));
        await TestWait.UntilAsync(() => IsEnginePlayedForHandle(new IntPtr(1234)), TimeSpan.FromMilliseconds(500));
        await viewModel.DisposeAsync();

        bool IsEnginePlayedForHandle(IntPtr handle)
        {
            lock (createdEnginesGate)
            {
                return createdEngines.Any(engine => engine.VideoHandle == handle && engine.Played);
            }
        }
    }),
    ("detected stream starts are throttled after two concurrent startups", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEngine = new FakePlaybackEngine { PlayCompletion = playbackRelease.Task };
        var secondEngine = new FakePlaybackEngine { PlayCompletion = playbackRelease.Task };
        var thirdEngine = new FakePlaybackEngine { PlayCompletion = playbackRelease.Task };
        var engines = new Queue<FakePlaybackEngine>();
        engines.Enqueue(firstEngine);
        engines.Enqueue(secondEngine);
        engines.Enqueue(thirdEngine);
        var playbackFactory = new FakePlaybackEngineFactory(() => engines.Dequeue());
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
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

        await viewModel.OpenDetectedStreamAsync(StreamInputParser.Parse("albralelie", PlatformKind.Twitch));
        await TestWait.UntilAsync(() => viewModel.Tabs.Count == 1, TimeSpan.FromMilliseconds(500));
        viewModel.Tabs[0].SetVideoHandle(new IntPtr(1234));
        await firstEngine.PlayStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));

        await viewModel.OpenDetectedStreamAsync(StreamInputParser.Parse("summit1g", PlatformKind.Twitch));
        await TestWait.UntilAsync(() => viewModel.Tabs.Count == 2, TimeSpan.FromMilliseconds(500));
        viewModel.Tabs[1].SetVideoHandle(new IntPtr(5678));
        await secondEngine.PlayStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));

        await viewModel.OpenDetectedStreamAsync(StreamInputParser.Parse("xqc", PlatformKind.Twitch));
        await TestWait.UntilAsync(() => viewModel.Tabs.Count == 3, TimeSpan.FromMilliseconds(500));
        viewModel.Tabs[2].SetVideoHandle(new IntPtr(9012));

        await Task.Delay(75);

        Assert.Equal(2, streamlink.StartCount);
        Assert.Equal(false, thirdEngine.PlayStarted.Task.IsCompleted);

        playbackRelease.SetResult();
        await thirdEngine.PlayStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        await TestWait.UntilAsync(() => streamlink.StartCount == 3, TimeSpan.FromMilliseconds(500));
        await viewModel.DisposeAsync();
    }),
    ("duplicate detected stream reuses tab while first playback is still starting", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var firstPlaybackRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEngine = new FakePlaybackEngine { PlayCompletion = firstPlaybackRelease.Task };
        var playbackFactory = new FakePlaybackEngineFactory(() => firstEngine);
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
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
        var target = StreamInputParser.Parse("albralelie", PlatformKind.Twitch);

        var firstOpen = viewModel.OpenDetectedStreamAsync(target);
        await TestWait.UntilAsync(() => viewModel.Tabs.Count == 1, TimeSpan.FromMilliseconds(500));
        viewModel.Tabs[0].SetVideoHandle(new IntPtr(1234));
        await firstEngine.PlayStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));

        await viewModel.OpenDetectedStreamAsync(target).WaitAsync(TimeSpan.FromMilliseconds(500));

        Assert.Equal(1, viewModel.Tabs.Count);
        firstPlaybackRelease.SetResult();
        await firstOpen.WaitAsync(TimeSpan.FromMilliseconds(500));
        await TestWait.UntilAsync(() => firstEngine.Played, TimeSpan.FromMilliseconds(500));
        await viewModel.DisposeAsync();
    }),
    ("docked chat keeps native VLC overlay hidden and uses tab chat", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var chatFactory = new FakeChatClientFactory();
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
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
        settings.Chat.Layout = ChatLayout.Docked;

        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);

        Assert.Equal(false, playbackFactory.LastEnableNativeOverlay);
        Assert.Equal(true, chatFactory.Client.Connected);        await tab.DisposeAsync();
    }),
    ("docked chat toggle hides panel without disconnecting chat", async () =>
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

        viewModel.Tabs.Add(tab);
        viewModel.VideoTabs.Add(tab);
        viewModel.SelectedTab = tab;
        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);
        await TestWait.UntilAsync(() => chatFactory.Client.Connected, TimeSpan.FromMilliseconds(500));

        Assert.Equal(true, viewModel.IsDockedChatVisible);
        Assert.Equal(true, viewModel.IsSelectedChatShowing);

        await viewModel.ToggleChatCommand.ExecuteAsync();

        Assert.Equal(true, tab.IsChatVisible);
        Assert.Equal(false, tab.IsDockedChatPanelVisible);
        Assert.Equal(false, viewModel.IsDockedChatVisible);
        Assert.Equal(false, viewModel.IsSelectedChatShowing);
        Assert.Equal(true, chatFactory.Client.Connected);

        await viewModel.ToggleChatCommand.ExecuteAsync();

        Assert.Equal(true, tab.IsChatVisible);
        Assert.Equal(true, tab.IsDockedChatPanelVisible);
        Assert.Equal(true, viewModel.IsDockedChatVisible);
        Assert.Equal(true, viewModel.IsSelectedChatShowing);
        Assert.Equal(true, chatFactory.Client.Connected);
        await viewModel.DisposeAsync();
    }),
    ("dock width drag updates docked chat setting without chat or playback restart", () =>
    {
        return TestSta.RunAsync(async () =>
        {
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
            };
            settings.Chat.ConnectAutomatically = true;
            settings.Chat.Layout = ChatLayout.Docked;
            settings.Chat.DockWidth = ChatSettings.DefaultDockWidth;

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

            viewModel.Tabs.Add(tab);
            viewModel.VideoTabs.Add(tab);
            viewModel.SelectedTab = tab;
            tab.SetVideoHandle(new IntPtr(1234));
            await tab.StartAsync(settings);
            await TestWait.UntilAsync(() => chatFactory.Client.Connected, TimeSpan.FromMilliseconds(500));

            var playbackCreateCount = playbackFactory.CreateCount;
            var streamStartCount = streamlink.StartCount;
            var chatConnectCount = chatFactory.Client.ConnectCount;

            var window = new MainWindow
            {
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);

            try
            {
                window.Show();
                window.UpdateLayout();

                Assert.Equal(true, viewModel.IsDockedChatVisible);
                var panel = (Border)window.FindName("DockedChatPanel");
                var thumb = (Thumb)window.FindName("DockedChatResizeThumb");
                Assert.NotNull(panel);
                Assert.NotNull(thumb);
                Assert.Equal(true, panel.IsVisible);
                Assert.Equal(ChatSettings.MinimumDockWidth, panel.MinWidth);
                Assert.Equal(ChatSettings.MaximumDockWidth, panel.MaxWidth);

                var resize = typeof(MainWindow).GetMethod(
                    "DockedChatResizeThumb_DragDelta",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(resize);

                void InvokeDrag(double horizontalChange)
                {
                    var args = new DragDeltaEventArgs(horizontalChange, 0);
                    resize!.Invoke(window, [thumb, args]);
                    Assert.Equal(true, args.Handled);
                    window.Dispatcher.Invoke(
                        () => { },
                        System.Windows.Threading.DispatcherPriority.DataBind);
                }

                InvokeDrag(-50);
                Assert.Equal(ChatSettings.DefaultDockWidth + 50, settings.Chat.DockWidth);

                InvokeDrag(30);
                Assert.Equal(ChatSettings.DefaultDockWidth + 20, settings.Chat.DockWidth);

                InvokeDrag(-10000);
                Assert.Equal(ChatSettings.MaximumDockWidth, settings.Chat.DockWidth);

                InvokeDrag(10000);
                Assert.Equal(ChatSettings.MinimumDockWidth, settings.Chat.DockWidth);

                await Task.Delay(100);
                Assert.Equal(playbackCreateCount, playbackFactory.CreateCount);
                Assert.Equal(streamStartCount, streamlink.StartCount);
                Assert.Equal(chatConnectCount, chatFactory.Client.ConnectCount);
            }
            finally
            {
                window.Close();
                await viewModel.DisposeAsync();
            }
        });
    }),
    ("multi-stream docked chat toggle only affects selected docked panel", async () =>
    {
        var settings = new AppSettings
        {
            MultiStreamEnabled = true
        };
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

        await viewModel.ToggleChatCommand.ExecuteAsync();

        Assert.Equal(true, first.IsChatVisible);
        Assert.Equal(false, first.IsDockedChatPanelVisible);
        Assert.Equal(true, second.IsChatVisible);
        Assert.Equal(true, second.IsDockedChatPanelVisible);
        await viewModel.DisposeAsync();
    }),
    ("docked chat toggle restores chat disabled from overlay layout", async () =>
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Overlay;

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

        viewModel.Tabs.Add(tab);
        viewModel.VideoTabs.Add(tab);
        viewModel.SelectedTab = tab;
        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);
        await TestWait.UntilAsync(() => chatFactory.Client.Connected, TimeSpan.FromMilliseconds(500));

        await viewModel.ToggleChatCommand.ExecuteAsync();

        Assert.Equal(false, tab.IsChatVisible);
        Assert.Equal(1, playbackFactory.CreateCount);
        Assert.Equal(1, streamlink.StartCount);
        Assert.Equal(false, viewModel.IsSelectedChatShowing);
        Assert.Equal(true, tab.IsDockedChatPanelVisible);

        settings.Chat.Layout = ChatLayout.Docked;
        await TestWait.UntilAsync(
            () => playbackFactory.CreateCount == 2 &&
                playbackFactory.LastEnableNativeOverlay == false &&
                streamlink.StartCount == 2,
            TimeSpan.FromSeconds(2));

        Assert.Equal(false, tab.IsChatVisible);
        Assert.Equal(false, viewModel.IsDockedChatVisible);
        Assert.Equal(false, viewModel.IsSelectedChatShowing);

        await viewModel.ToggleChatCommand.ExecuteAsync();

        Assert.Equal(true, tab.IsChatVisible);
        Assert.Equal(true, tab.IsDockedChatPanelVisible);
        Assert.Equal(true, viewModel.IsDockedChatVisible);
        Assert.Equal(true, viewModel.IsSelectedChatShowing);
        Assert.True(chatFactory.Client.ConnectCount >= 2);
        await viewModel.DisposeAsync();
    }),
    ("chat layout hidden flag follows settings layout", async () =>
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
        var notified = false;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsChatLayoutHidden))
            {
                notified = true;
            }
        };

        Assert.Equal(false, viewModel.IsChatLayoutHidden);

        settings.Chat.Layout = ChatLayout.Hidden;

        Assert.Equal(true, viewModel.IsChatLayoutHidden);
        Assert.Equal(true, notified);
        await viewModel.DisposeAsync();
    }),
    ("home held middle-button autoscroll tracks cursor distance and clamps", () =>
    {
        Assert.Equal(true, MainWindow.ShouldContinueHomeAutoScroll(MouseButtonState.Pressed));
        Assert.Equal(false, MainWindow.ShouldContinueHomeAutoScroll(MouseButtonState.Released));
        AssertNear(0, MainWindow.GetHomeAutoScrollVelocity(100, 100));
        AssertNear(216, MainWindow.GetHomeAutoScrollVelocity(100, 120));
        AssertNear(-756, MainWindow.GetHomeAutoScrollVelocity(100, 50));
        AssertNear(2600, MainWindow.GetHomeAutoScrollVelocity(100, 1000));
        AssertNear(208, MainWindow.GetHomeAutoScrollVerticalOffset(100, 100, 120, 500, 0.5));
        AssertNear(0, MainWindow.GetHomeAutoScrollVerticalOffset(100, 100, 0, 500, 1));
        AssertNear(500, MainWindow.GetHomeAutoScrollVerticalOffset(480, 40, 160, 500, 1));
        AssertNear(0, MainWindow.GetHomeAutoScrollVerticalOffset(double.NaN, 40, 160, 500, 1));
        return Task.CompletedTask;
    }),
    ("middle-click home stream item button resolves and executes stay-on-home command", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var openCalls = 0;
            bool? stayOnHome = null;
            var item = new StreamSearchResultViewModel(
                new StreamTarget(PlatformKind.Twitch, "xqc", "https://www.twitch.tv/xqc"),
                new StreamlinkProbeResult(true, "Playable stream found."),
                null,
                (_, shouldStayOnHome) =>
                {
                    openCalls++;
                    stayOnHome = shouldStayOnHome;
                    return Task.CompletedTask;
                });
            var button = new Button
            {
                DataContext = item
            };
            button.SetBinding(Button.CommandProperty, new Binding("OpenCommand"));

            Assert.Equal(
                true,
                MainWindow.TryResolveHomeStreamOpenAndStayOnHomeCommand(button, out var command));
            Assert.True(ReferenceEquals(item.OpenAndStayOnHomeCommand, command));

            Assert.Equal(true, MainWindow.TryHandleHomeStreamOpenAndStayOnHomeCommand(button));

            Assert.Equal(1, openCalls);
            Assert.Equal(true, stayOnHome);
            return Task.CompletedTask;
        });
    }),
    ("middle-click home stream resolver ignores non-stream and non-open buttons", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var category = new BrowseCategoryViewModel(
                new BrowseCategory(PlatformKind.Twitch, "509658", "Just Chatting", "", []),
                _ => Task.CompletedTask);
            var categoryButton = new Button
            {
                DataContext = category
            };
            categoryButton.SetBinding(Button.CommandProperty, new Binding("SelectCommand"));

            Assert.Equal(
                false,
                MainWindow.TryResolveHomeStreamOpenAndStayOnHomeCommand(categoryButton, out _));

            var recent = new RecentStreamViewModel(
                new RecentStreamSettings
                {
                    Platform = PlatformKind.Twitch,
                    Channel = "summit1g",
                    Url = "https://www.twitch.tv/summit1g"
                },
                (_, _) => throw new InvalidOperationException("Recent stream open should not be resolved from delete button."),
                _ => Task.CompletedTask);
            var deleteButton = new Button
            {
                DataContext = recent
            };
            deleteButton.SetBinding(Button.CommandProperty, new Binding("DeleteCommand"));

            Assert.Equal(
                false,
                MainWindow.TryResolveHomeStreamOpenAndStayOnHomeCommand(deleteButton, out _));
            return Task.CompletedTask;
        });
    }),
    ("middle-click home stream resolver does not execute running command", () =>
    {
        return TestSta.RunAsync(async () =>
        {
            var releaseOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var openCalls = 0;
            var item = new StreamSearchResultViewModel(
                new StreamTarget(PlatformKind.Kick, "some-channel", "https://kick.com/some-channel"),
                new StreamlinkProbeResult(true, "Playable stream found."),
                null,
                async (_, _) =>
                {
                    openCalls++;
                    await releaseOpen.Task;
                });
            var button = new Button
            {
                DataContext = item
            };
            button.SetBinding(Button.CommandProperty, new Binding("OpenCommand"));

            var runningOpen = item.OpenAndStayOnHomeCommand.ExecuteAsync();
            Assert.Equal(1, openCalls);
            Assert.Equal(false, item.OpenAndStayOnHomeCommand.CanExecute(null));

            Assert.Equal(true, MainWindow.TryHandleHomeStreamOpenAndStayOnHomeCommand(button));
            Assert.Equal(1, openCalls);

            releaseOpen.SetResult();
            await runningOpen.WaitAsync(TimeSpan.FromSeconds(1));
        });
    }),
    ("mouse button four is browse back and other mouse buttons are not", () =>
    {
        Assert.Equal(true, MainWindow.IsBrowseBackMouseButton(MouseButton.XButton1));
        Assert.Equal(false, MainWindow.IsBrowseBackMouseButton(MouseButton.XButton2));
        Assert.Equal(false, MainWindow.IsBrowseBackMouseButton(MouseButton.Left));
        Assert.Equal(false, MainWindow.IsBrowseBackMouseButton(MouseButton.Middle));
        return Task.CompletedTask;
    }),
    ("Twitch and Kick category clicks open stream pages at the top", () =>
    {
        return TestSta.RunAsync(async () =>
        {
            foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
            {
                var browseService = new FakeBrowseService();
                var categories = Enumerable.Range(1, 24)
                    .Select(index => new BrowseCategory(
                        platform,
                        index.ToString(CultureInfo.InvariantCulture),
                        $"{platform} Category {index}",
                        "",
                        []))
                    .ToArray();
                var streams = Enumerable.Range(1, 24)
                    .Select(index => new BrowseLiveStream(
                        platform,
                        $"channel-{index}",
                        $"Channel {index}",
                        $"Live stream {index}",
                        categories[^1].Id,
                        categories[^1].Name,
                        1000 - index,
                        "",
                        null,
                        false,
                        "en",
                        platform == PlatformKind.Twitch
                            ? $"https://www.twitch.tv/channel-{index}"
                            : $"https://kick.com/channel-{index}"))
                    .ToArray();
                browseService.EnqueueCategories(new BrowseResult<BrowseCategory>(
                    BrowseResultStatus.Available,
                    categories,
                    "",
                    $"Loaded {platform} categories"));
                browseService.EnqueueStreams(new BrowseResult<BrowseLiveStream>(
                    BrowseResultStatus.Available,
                    streams,
                    "",
                    $"Loaded {platform} streams"));

                var viewModel = TestViewModels.CreateMain(
                    new AppSettings(),
                    new FakeSettingsService(new AppSettings()),
                    new FakeStreamlinkService(),
                    new FakePlaybackEngineFactory(),
                    new FakeChatClientFactory(),
                    new MemoryLogger(),
                    action => action(),
                    browseService: browseService);
                if (platform == PlatformKind.Kick)
                {
                    viewModel.SelectKickBrowsePlatformCommand.Execute(null);
                }

                viewModel.ShowBrowseHomePageCommand.Execute(null);
                var window = new MainWindow
                {
                    Width = 980,
                    Height = 620,
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

                    var scrollViewer = window.FindName("HomeContentScrollViewer") as ScrollViewer;
                    Assert.NotNull(scrollViewer);
                    Assert.True(
                        scrollViewer!.ScrollableHeight > 0,
                        $"Expected the {platform} category page to be scrollable for this regression test.");
                    scrollViewer.ScrollToEnd();
                    window.UpdateLayout();
                    Assert.True(
                        scrollViewer.VerticalOffset > 0,
                        $"Expected to begin the {platform} category click below the top of the page.");

                    var categoryButton = FindVisualDescendants<Button>(window)
                        .Single(button => ReferenceEquals(button.DataContext, viewModel.BrowseCategories[^1]));
                    var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(categoryButton);
                    var invokeProvider = (System.Windows.Automation.Provider.IInvokeProvider?)peer.GetPattern(
                        System.Windows.Automation.Peers.PatternInterface.Invoke);
                    Assert.NotNull(invokeProvider);
                    invokeProvider!.Invoke();

                    await TestWait.UntilAsync(
                        () => viewModel.IsBrowseStreamsPageVisible,
                        TimeSpan.FromMilliseconds(500));
                    window.Dispatcher.Invoke(
                        () => { },
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();

                    Assert.True(
                        scrollViewer.ScrollableHeight > 0,
                        $"Expected the {platform} stream page to remain scrollable after navigation.");
                    Assert.True(
                        Math.Abs(scrollViewer.VerticalOffset) < 0.01,
                        $"Expected the {platform} stream page at offset 0, got {scrollViewer.VerticalOffset.ToString(CultureInfo.InvariantCulture)}.");
                }
                finally
                {
                    window.Close();
                    await viewModel.DisposeAsync();
                }
            }
        });
    }),
    ("home content scroll bottom helper treats bottom and threshold as loadable", () =>
    {
        Assert.Equal(true, MainWindow.IsHomeContentScrollNearBottom(1000, 1000, 120));
        Assert.Equal(true, MainWindow.IsHomeContentScrollNearBottom(890, 1000, 120));
        return Task.CompletedTask;
    }),
    ("home content scroll bottom helper ignores positions far from bottom", () =>
    {
        Assert.Equal(false, MainWindow.IsHomeContentScrollNearBottom(700, 1000, 120));
        return Task.CompletedTask;
    }),
    ("home content scroll bottom helper treats non-scrollable content as loadable", () =>
    {
        Assert.Equal(true, MainWindow.IsHomeContentScrollNearBottom(0, 0, 120));
        Assert.Equal(true, MainWindow.IsHomeContentScrollNearBottom(0, -12, 120));
        return Task.CompletedTask;
    }),
    ("home content scroll bottom helper rejects invalid numeric input", () =>
    {
        Assert.Equal(false, MainWindow.IsHomeContentScrollNearBottom(double.NaN, 1000, 120));
        Assert.Equal(false, MainWindow.IsHomeContentScrollNearBottom(0, double.PositiveInfinity, 120));
        Assert.Equal(false, MainWindow.IsHomeContentScrollNearBottom(0, 1000, double.NaN));
        Assert.Equal(false, MainWindow.IsHomeContentScrollNearBottom(0, 1000, -1));
        return Task.CompletedTask;
    }),
    ("home content padding converter keeps a compact side gutter when card gap is disabled", () =>
    {
        var converter = new StreamlinkVlcStudio.App.Wpf.Converters.HomeContentPaddingConverter();

        var compactPadding = (System.Windows.Thickness)converter.Convert(
            false,
            typeof(System.Windows.Thickness),
            null!,
            CultureInfo.InvariantCulture);
        Assert.Equal(16d, compactPadding.Left);
        Assert.Equal(16d, compactPadding.Right);
        Assert.Equal(18d, compactPadding.Top);
        Assert.Equal(24d, compactPadding.Bottom);

        var gappedPadding = (System.Windows.Thickness)converter.Convert(
            true,
            typeof(System.Windows.Thickness),
            null!,
            CultureInfo.InvariantCulture);
        Assert.Equal(24d, gappedPadding.Left);
        Assert.Equal(24d, gappedPadding.Right);
        Assert.Equal(18d, gappedPadding.Top);
        Assert.Equal(24d, gappedPadding.Bottom);

        return Task.CompletedTask;
    }),
    ("rounded clip border masks configured corners and follows resize", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var clipBorder = new RoundedClipBorder
            {
                CornerRadius = new CornerRadius(11.5, 11.5, 0, 0),
                Child = new Border { Background = Brushes.Red }
            };

            clipBorder.Measure(new Size(100, 50));
            clipBorder.Arrange(new Rect(0, 0, 100, 50));
            clipBorder.UpdateLayout();

            Assert.NotNull(clipBorder.Clip);
            Assert.Equal(false, clipBorder.Clip.FillContains(new Point(1, 1)));
            Assert.Equal(false, clipBorder.Clip.FillContains(new Point(99, 1)));
            Assert.True(clipBorder.Clip.FillContains(new Point(1, 49)));
            Assert.True(clipBorder.Clip.FillContains(new Point(99, 49)));

            var rendered = WpfVisualTest.Render(clipBorder);
            Assert.Equal((byte)0, WpfVisualTest.PixelAlpha(rendered, 0, 0));
            Assert.Equal((byte)255, WpfVisualTest.PixelAlpha(rendered, 50, 1));
            Assert.Equal((byte)255, WpfVisualTest.PixelAlpha(rendered, 1, 48));

            clipBorder.Arrange(new Rect(0, 0, 137.5, 63.25));
            clipBorder.UpdateLayout();

            Assert.Equal(137.5, clipBorder.Clip.Bounds.Width);
            Assert.Equal(63.25, clipBorder.Clip.Bounds.Height);
            Assert.Equal(false, clipBorder.Clip.FillContains(new Point(136.5, 1)));
            Assert.True(clipBorder.Clip.FillContains(new Point(136.5, 62.25)));
        });
    }),
    ];
}

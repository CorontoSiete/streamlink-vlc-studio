using System.Windows.Automation;

internal static partial class OfflineFollowedChannelsTestCatalog
{
    private static Task TransitionsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var followed = new FakeFollowedStreamsService();
        var notifications = new FakeLiveNotificationService();
        Enqueue(followed, Result([Live("alpha")], [Offline("beta")]));
        await using var model = CreateModel(followed, notifications: notifications);
        var changed = new List<string?>();
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        await model.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.Equal("alpha", model.LiveFollowedChannels.Single().Channel);
        Assert.Equal("beta", model.OfflineFollowedChannels.Single().Channel);
        Assert.Equal(0, notifications.Notifications.Count);

        Enqueue(followed, Result([Live("beta")], [Offline("alpha"), Offline("BETA")]));
        await model.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.Equal("beta", model.LiveFollowedChannels.Single().Channel);
        Assert.Equal("alpha", model.OfflineFollowedChannels.Single().Channel);
        Assert.Equal("1 offline", model.OfflineFollowedChannelsCountText);
        Assert.Equal("1 followed channel is offline.", model.OfflineFollowedChannelsStatus);
        Assert.Equal("beta", notifications.Notifications.Single().Channel);

        Enqueue(followed, Result([], [Offline("beta")]));
        await model.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.Equal(0, model.LiveFollowedChannels.Count);
        Assert.Equal("beta", model.OfflineFollowedChannels.Single().Channel);
        Assert.True(model.HasOfflineFollowedChannels);
        Assert.Equal(false, model.IsOfflineFollowedChannelsEmptyVisible);
        Assert.True(model.IsFollowedChannelsEmptyVisible);
        Assert.Equal(1, notifications.Notifications.Count);
        Assert.True(changed.Contains(nameof(MainViewModel.OfflineFollowedChannelsCountText)));
        Assert.True(changed.Contains(nameof(MainViewModel.HasOfflineFollowedChannels)));
        Assert.True(changed.Contains(nameof(MainViewModel.IsOfflineFollowedChannelsEmptyVisible)));
    });

    private static Task CardReuseAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var followed = new FakeFollowedStreamsService();
        var twitch = Offline("same");
        var kick = Offline("same", PlatformKind.Kick);
        Enqueue(followed, Result([], [twitch, kick]));
        await using var model = CreateModel(followed);
        await model.RefreshFollowedChannelsCommand.ExecuteAsync();
        var cards = model.OfflineFollowedChannels.ToArray();
        var commands = cards.Select(card => card.OpenCommand).ToArray();
        var changes = new List<NotifyCollectionChangedAction>();
        model.OfflineFollowedChannels.CollectionChanged += (_, args) => changes.Add(args.Action);
        for (var index = 0; index < 4; index++)
        {
            Enqueue(followed, Result([], [twitch, kick]));
            await model.RefreshFollowedChannelsCommand.ExecuteAsync();
        }
        Assert.SequenceEqual(cards, model.OfflineFollowedChannels);
        Assert.SequenceEqual(commands, model.OfflineFollowedChannels.Select(card => card.OpenCommand));
        Assert.Equal(0, changes.Count);

        var properties = new List<string?>();
        cards[0].PropertyChanged += (_, args) => properties.Add(args.PropertyName);
        Enqueue(followed, Result([], [kick, twitch with { DisplayName = "Updated name", ProfileImageUrl = "https://images.example/updated.png" }]));
        await model.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.True(ReferenceEquals(cards[1], model.OfflineFollowedChannels[0]));
        Assert.True(ReferenceEquals(cards[0], model.OfflineFollowedChannels[1]));
        Assert.True(ReferenceEquals(commands[0], model.OfflineFollowedChannels[1].OpenCommand));
        Assert.Equal("Updated name", cards[0].DisplayName);
        Assert.True(cards[0].HasProfileImage);
        Assert.True(properties.Contains(nameof(OfflineFollowedChannelViewModel.DisplayName)));
        Assert.True(properties.Contains(nameof(OfflineFollowedChannelViewModel.ProfileImageUrl)));
        Assert.Equal(false, changes.Contains(NotifyCollectionChangedAction.Reset));
        Assert.Equal("2 offline", model.OfflineFollowedChannelsCountText);
    });

    private static Task RefreshStatesAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var followed = new FakeFollowedStreamsService();
        var completion = new TaskCompletionSource<FollowedLiveStreamsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        followed.EnqueueResult(_ => completion.Task);
        await using var model = CreateModel(followed);
        Assert.True(model.IsOfflineFollowedChannelsEmptyVisible);
        var refresh = model.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.True(model.IsFollowedChannelsRefreshing);
        Assert.Equal(false, model.IsOfflineFollowedChannelsEmptyVisible);
        Assert.Equal("Refreshing offline followed channels", model.OfflineFollowedChannelsStatus);
        completion.SetResult(Result([Live("liveone")], []) with { OfflineMessages = ["Twitch: follow-list request failed (401)."] });
        await refresh;
        Assert.Equal(false, model.IsFollowedChannelsRefreshing);
        Assert.True(model.IsOfflineFollowedChannelsEmptyVisible);
        Assert.True(model.OfflineFollowedChannelsStatus.StartsWith("Offline followed channels could not be fully loaded.", StringComparison.Ordinal));
        Assert.True(model.OfflineFollowedChannelsStatus.Contains("401", StringComparison.Ordinal));
        Assert.Equal("1 followed channel is live.", model.FollowedChannelsStatus);

        followed.EnqueueResult(_ => Task.FromException<FollowedLiveStreamsResult>(new InvalidOperationException("Refresh failed")));
        await model.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.Equal("Refresh failed", model.OfflineFollowedChannelsStatus);
        Assert.Equal(false, model.IsFollowedChannelsRefreshing);

        Enqueue(followed, Result([], []));
        await model.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.Equal("No offline followed channels found.", model.OfflineFollowedChannelsStatus);
        Assert.Equal("0 offline", model.OfflineFollowedChannelsCountText);
        Assert.True(model.IsOfflineFollowedChannelsEmptyVisible);
    });

    private static Task SupersededRefreshAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = new AppSettings();
        settings.Chat.TwitchOAuthToken = "first";
        var followed = new FakeFollowedStreamsService();
        var oldResult = new TaskCompletionSource<FollowedLiveStreamsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        followed.EnqueueResult(_ => oldResult.Task);
        Enqueue(followed, Result([], [Offline("current")]));
        await using var feature = CreateFeature(followed, settings);
        var original = feature.RefreshFollowedChannelsAsync();
        Assert.True(ReferenceEquals(original, feature.RefreshFollowedChannelsAsync()));
        settings.Chat.TwitchOAuthToken = "second";
        var replacement = feature.RefreshFollowedChannelsAsync();
        oldResult.SetResult(Result([], [Offline("stale")]));
        await Task.WhenAll(original, replacement);
        Assert.Equal("current", feature.OfflineFollowedChannels.Single().Channel);
        Assert.Equal(2, followed.CallCount);
        Assert.Equal(1, followed.MaxConcurrentCalls);
    });

    private static Task OpenVideosAsync(PlatformKind platform) => TestSta.RunOffscreenAsync(async () =>
    {
        var followed = new FakeFollowedStreamsService();
        Enqueue(followed, Result([], [Offline("channel", platform)]));
        var twitch = new FakeTwitchVodService(new TwitchVodSearchResult(TwitchVodSearchStatus.Available,
            new TwitchVodBroadcaster("42", "channel", "Channel"), [], "", ""));
        var kick = new FakeKickVodService(new KickVodSearchResult(KickVodSearchStatus.Available, [], "", ""));
        await using var model = CreateModel(followed, twitch, kick);
        await model.RefreshFollowedChannelsCommand.ExecuteAsync();
        await model.OfflineFollowedChannels.Single().OpenCommand.ExecuteAsync();
        Assert.True(model.IsHomeSelected);
        Assert.True(model.IsTwitchVodsHomePageSelected);
        Assert.Equal(platform, model.SelectedVodPlatform);
        Assert.Equal("channel", model.TwitchVodSearchText);
        Assert.Equal(0, model.Tabs.Count);
        if (platform == PlatformKind.Twitch)
        {
            Assert.Equal("channel", twitch.Requests.Single().Streamer);
            Assert.Equal(0, kick.CallCount);
        }
        else
        {
            Assert.Equal("channel", kick.Requests.Single().Channel);
            Assert.Equal(0, twitch.CallCount);
        }
    });

    private static Task DisposedCommandAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var followed = new FakeFollowedStreamsService();
        Enqueue(followed, Result([], [Offline("channel")]));
        var twitch = new FakeTwitchVodService(new TwitchVodSearchResult(TwitchVodSearchStatus.Available, null, [], "", ""));
        var model = CreateModel(followed, twitch);
        await model.RefreshFollowedChannelsCommand.ExecuteAsync();
        var card = model.OfflineFollowedChannels.Single();
        await model.DisposeAsync();
        Assert.Equal(false, card.OpenCommand.CanExecute(null));
        await card.OpenCommand.ExecuteAsync();
        Assert.Equal(0, twitch.CallCount);
    });

    private static Task LateShutdownAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var followed = new FakeFollowedStreamsService();
        var completion = new TaskCompletionSource<FollowedLiveStreamsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        followed.EnqueueResult(_ => completion.Task);
        var feature = CreateFeature(followed, new AppSettings());
        var refresh = feature.RefreshFollowedChannelsAsync();
        var shutdown = feature.DisposeAsync().AsTask();
        completion.SetResult(Result([], [Offline("late")]));
        await Task.WhenAll(refresh, shutdown);
        Assert.True(followed.CancellationTokens.Single().IsCancellationRequested);
        Assert.Equal(0, feature.OfflineFollowedChannels.Count);
    });

    private static Task LayoutAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var followed = new FakeFollowedStreamsService();
        Enqueue(followed, Result([Live("liveone")],
            [Offline("twitch_offline") with { DisplayName = "A favorite streamer with a long display name" },
             Offline("kick-offline", PlatformKind.Kick)]));
        await using var model = CreateModel(followed);
        await model.RefreshFollowedChannelsCommand.ExecuteAsync();
        model.OfflineFollowedChannels.Last().TogglePinCommand.Execute(null);
        var window = new MainWindow { DataContext = model };
        ApplicationTestCatalog.RemoveMainWindowAutomaticStartup(window);
        var root = (FrameworkElement)window.Content;
        var section = (Expander)window.FindName("OfflineFollowedChannelsSection");
        var offlineList = (ItemsControl)window.FindName("OfflineFollowedChannelsList");
        Assert.NotNull(section);
        Assert.NotNull(offlineList);
        try
        {
            foreach (var width in new[] { 1320.0, 360.0, 320.0 })
            {
                section.IsExpanded = true;
                Layout(root, width);
                Assert.True(ReferenceEquals(model.OfflineFollowedChannels, offlineList.ItemsSource));
                var liveList = Descendants<ItemsControl>(root).Single(control => ReferenceEquals(control.ItemsSource, model.LiveFollowedChannels));
                Assert.True(Bounds(liveList, root).Bottom <= Bounds(section, root).Top + 0.5);
                var panel = Descendants<HomeCardWrapPanel>(offlineList).Single();
                Assert.Equal(2, panel.Children.Count);
                Assert.Equal(0, Descendants<StreamHoverPreview>(offlineList).Count());
                Assert.Equal(false, Descendants<TextBlock>(offlineList).Any(text => text.Text == "LIVE"));
                foreach (var card in model.OfflineFollowedChannels)
                {
                    var button = Descendants<Button>(offlineList).Single(candidate => ReferenceEquals(candidate.Command, card.OpenCommand));
                    Assert.True(button.ActualHeight >= 100 && button.ActualHeight < 180);
                    var bounds = Bounds(button, offlineList);
                    Assert.True(bounds.Left >= -0.5 && bounds.Right <= offlineList.ActualWidth + 0.5);
                    var pin = Descendants<Button>(offlineList).Single(candidate => ReferenceEquals(candidate.Command, card.TogglePinCommand));
                    Assert.True(pin.IsEnabled && pin.Focusable && pin.IsTabStop);
                    Assert.Equal(card.PinActionText, AutomationProperties.GetName(pin));
                    Assert.Equal(card.PinStatusText, AutomationProperties.GetItemStatus(pin));
                    Assert.Equal(false, Descendants<Button>(button).Contains(pin));
                    var pinBounds = Bounds(pin, offlineList);
                    Assert.True(pinBounds.Width >= 32 && pinBounds.Height >= 32);
                    Assert.True(pinBounds.Left >= bounds.Left && pinBounds.Right <= bounds.Right + 0.5);
                    Assert.True(pinBounds.Top >= bounds.Top && pinBounds.Bottom <= bounds.Bottom + 0.5);
                    var name = Descendants<TextBlock>(button).Single(text => text.Text == card.DisplayName);
                    Assert.True(Bounds(name, offlineList).Right <= pinBounds.Left + 0.5);
                    var badge = Descendants<Border>(button).Single(border => Equals(border.ToolTip, card.PlatformText));
                    Assert.True(Bounds(badge, offlineList).Top >= pinBounds.Bottom - 0.5);
                }
                var title = Descendants<TextBlock>(section).Single(text => text.Text == "Offline followed channels");
                Assert.True(Bounds(title, section).Right <= section.ActualWidth + 0.5);
                SaveImage(root, $"offline-following-{width:0}");
                var scroll = (ScrollViewer)window.FindName("HomeContentScrollViewer");
                scroll.ScrollToEnd();
                Layout(root, width);
                var lastCommand = model.OfflineFollowedChannels.Last().OpenCommand;
                var lastButton = Descendants<Button>(offlineList).Single(button => ReferenceEquals(button.Command, lastCommand));
                var lastBounds = Bounds(lastButton, scroll);
                Assert.True(lastBounds.Top >= -0.5 && lastBounds.Bottom <= scroll.ActualHeight + 0.5);
                SaveImage(root, $"offline-following-{width:0}-scrolled");
                scroll.ScrollToTop();
                var expandedHeight = section.ActualHeight;
                section.IsExpanded = false;
                Layout(root, width);
                Assert.True(section.ActualHeight < expandedHeight);
            }

            Enqueue(followed, Result([], [Offline("still_offline")]));
            await model.RefreshFollowedChannelsCommand.ExecuteAsync();
            section.IsExpanded = true;
            Layout(root, 1320);
            Assert.Equal(1, Descendants<HomeCardWrapPanel>(offlineList).Single().Children.Count);
            Assert.Equal(Visibility.Visible, offlineList.Visibility);
            SaveImage(root, "offline-following-no-live");
        }
        finally
        {
            window.Close();
        }
    });

    private static void Layout(FrameworkElement root, double width)
    {
        root.Measure(new Size(width, 900));
        root.Arrange(new Rect(0, 0, width, 900));
        root.UpdateLayout();
        root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        root.UpdateLayout();
    }

    private static Rect Bounds(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static IEnumerable<TElement> Descendants<TElement>(DependencyObject root) where TElement : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is TElement element) yield return element;
            foreach (var descendant in Descendants<TElement>(child)) yield return descendant;
        }
    }

    private static void SaveImage(FrameworkElement root, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SVS_RESPONSIVE_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        System.IO.Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(WpfVisualTest.Render(root)));
        using var output = System.IO.File.Create(System.IO.Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }

    private static FollowedChannel Offline(string channel, PlatformKind platform = PlatformKind.Twitch)
    {
        Assert.True(StreamInputParser.TryFromChannel(platform, channel, out var target));
        var parsedTarget = target!;
        return new(platform, parsedTarget.Channel, channel, parsedTarget.Url);
    }

    private static FollowedLiveStream Live(string channel) =>
        new(PlatformKind.Twitch, channel, channel, "Live stream", "", 42, "", null, false, "en", $"https://www.twitch.tv/{channel}");

    private static FollowedLiveStreamsResult Result(IReadOnlyList<FollowedLiveStream> streams, IReadOnlyList<FollowedChannel> offline) =>
        new(streams, [], [PlatformKind.Twitch, PlatformKind.Kick], offline, []);

    private static void Enqueue(FakeFollowedStreamsService service, FollowedLiveStreamsResult result) =>
        service.EnqueueResult(_ => Task.FromResult(result));

    private static MainViewModel CreateModel(IFollowedStreamsService followed,
        ITwitchVodService? twitch = null, IKickVodService? kick = null, ILiveNotificationService? notifications = null)
    {
        var settings = new AppSettings();
        settings.Chat.ConnectAutomatically = false;
        settings.FollowedChannels.NotifyWhenLive = true;
        return TestViewModels.CreateMain(settings, new FakeSettingsService(settings), new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(), action => action(),
            followedStreamsService: followed, twitchVodService: twitch, kickVodService: kick, liveNotificationService: notifications);
    }

    private static FollowedChannelsViewModel CreateFeature(IFollowedStreamsService followed, AppSettings settings) =>
        new(new MainViewModelDependencies
        {
            Settings = settings,
            SettingsService = new FakeSettingsService(settings),
            StreamlinkService = new FakeStreamlinkService(),
            PlaybackFactory = new FakePlaybackEngineFactory(),
            ChatFactory = new FakeChatClientFactory(),
            Logger = new MemoryLogger(),
            Dispatch = action => action(),
            FollowedStreamsService = followed
        }, _ => { }, (_, _) => Task.CompletedTask, _ => Task.CompletedTask);
}

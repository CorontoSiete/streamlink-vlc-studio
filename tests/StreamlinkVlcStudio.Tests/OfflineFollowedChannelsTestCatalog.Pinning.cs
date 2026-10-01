using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using System.Windows.Threading;

internal static partial class OfflineFollowedChannelsTestCatalog
{
    private static Task PinSettingsAsync()
    {
        var settings = new FollowedChannelsSettings();
        Assert.Equal(0, settings.PinnedOfflineChannelKeys.Count);
        var source = new List<string>
        {
            " twitch : @Favorite ", "TWITCH:FAVORITE", "Kick:favorite", "Kick:other-channel",
            null!, "", "Other:favorite", "Twitch:bad-channel", "Kick:../escape", "Twitch:",
            "Twitch:directory", "Twitch:favorite:other"
        };
        settings.PinnedOfflineChannelKeys = source;
        source.Clear();
        Assert.SequenceEqual(new[] { "Twitch:favorite", "Kick:favorite", "Kick:other-channel" }, settings.PinnedOfflineChannelKeys);
        settings.PinnedOfflineChannelKeys = null!;
        Assert.Equal(0, settings.PinnedOfflineChannelKeys.Count);
        Assert.Equal(0, JsonSerializer.Deserialize<FollowedChannelsSettings>("{}")!.PinnedOfflineChannelKeys.Count);
        Assert.Equal(0, JsonSerializer.Deserialize<FollowedChannelsSettings>("""{"PinnedOfflineChannelKeys":null}""")!.PinnedOfflineChannelKeys.Count);
        return Task.CompletedTask;
    }

    private static Task PinOrderingAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = new AppSettings();
        var followed = new FakeFollowedStreamsService();
        Enqueue(followed, Result([Live("liveone")], [Offline("beta"), Offline("alpha"), Offline("gamma")]));
        await using var feature = CreateFeature(followed, settings);
        await feature.RefreshFollowedChannelsAsync();
        var cards = feature.OfflineFollowedChannels.ToArray();
        var liveCard = feature.LiveFollowedChannels.Single();
        var changes = new List<NotifyCollectionChangedAction>();
        feature.OfflineFollowedChannels.CollectionChanged += (_, args) => changes.Add(args.Action);

        cards[2].TogglePinCommand.Execute(null);
        Assert.SequenceEqual(new[] { "gamma", "beta", "alpha" }, feature.OfflineFollowedChannels.Select(card => card.Channel));
        Assert.True(cards[2].IsPinned);
        Assert.Equal("Unpin gamma", cards[2].PinActionText);
        cards[1].TogglePinCommand.Execute(null);
        Assert.SequenceEqual(new[] { "alpha", "gamma", "beta" }, feature.OfflineFollowedChannels.Select(card => card.Channel));
        cards[2].TogglePinCommand.Execute(null);
        Assert.SequenceEqual(new[] { "alpha", "beta", "gamma" }, feature.OfflineFollowedChannels.Select(card => card.Channel));
        Assert.Equal(false, cards[2].IsPinned);
        cards[1].TogglePinCommand.Execute(null);
        Assert.SequenceEqual(cards, feature.OfflineFollowedChannels);
        Assert.Equal(0, settings.FollowedChannels.PinnedOfflineChannelKeys.Count);
        Assert.True(changes.Count > 0 && changes.All(change => change == NotifyCollectionChangedAction.Move));
        Assert.True(ReferenceEquals(liveCard, feature.LiveFollowedChannels.Single()));
        Assert.Equal("3 offline", feature.OfflineFollowedChannelsCountText);
        Assert.Equal(1, followed.CallCount);
    });

    private static Task PinRefreshAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = new AppSettings();
        var followed = new FakeFollowedStreamsService();
        var alpha = Offline("alpha");
        var twitch = Offline("same");
        var kick = Offline("same", PlatformKind.Kick);
        Enqueue(followed, Result([], [alpha, twitch, kick]));
        await using var feature = CreateFeature(followed, settings);
        await feature.RefreshFollowedChannelsAsync();
        var cards = feature.OfflineFollowedChannels.ToArray();
        var pinCommands = cards.Select(card => card.TogglePinCommand).ToArray();
        cards[2].TogglePinCommand.Execute(null);
        Assert.True(cards[2].IsPinned);
        Assert.Equal(false, cards[1].IsPinned);
        Assert.SequenceEqual(new[] { "Kick:same", "Twitch:alpha", "Twitch:same" }, feature.OfflineFollowedChannels.Select(card => card.Target.StateKey));
        cards[1].TogglePinCommand.Execute(null);
        var pinnedOrder = feature.OfflineFollowedChannels.ToArray();
        var changes = new List<NotifyCollectionChangedAction>();
        feature.OfflineFollowedChannels.CollectionChanged += (_, args) => changes.Add(args.Action);
        for (var index = 0; index < 4; index++)
        {
            Enqueue(followed, Result([], [alpha, twitch, kick]));
            await feature.RefreshFollowedChannelsAsync();
        }
        Assert.SequenceEqual(pinnedOrder, feature.OfflineFollowedChannels);
        Assert.Equal(0, changes.Count);
        Assert.SequenceEqual(pinCommands, cards.Select(card => card.TogglePinCommand));

        var properties = new List<string?>();
        cards[1].PropertyChanged += (_, args) => properties.Add(args.PropertyName);
        Enqueue(followed, Result([], [kick, alpha, twitch with { Channel = "SAME", DisplayName = "Updated favorite" }]));
        await feature.RefreshFollowedChannelsAsync();
        Assert.SequenceEqual(new[] { cards[2], cards[1], cards[0] }, feature.OfflineFollowedChannels);
        Assert.Equal("Unpin Updated favorite", cards[1].PinActionText);
        Assert.True(properties.Contains(nameof(OfflineFollowedChannelViewModel.PinActionText)));
        cards[1].TogglePinCommand.Execute(null);
        cards[2].TogglePinCommand.Execute(null);
        Assert.SequenceEqual(new[] { cards[2], cards[0], cards[1] }, feature.OfflineFollowedChannels);
        Assert.Equal(false, changes.Contains(NotifyCollectionChangedAction.Reset));
    });

    private static Task PinTransitionsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = new AppSettings();
        var followed = new FakeFollowedStreamsService();
        Enqueue(followed, Result([], [Offline("alpha"), Offline("zeta")]));
        await using var feature = CreateFeature(followed, settings);
        await feature.RefreshFollowedChannelsAsync();
        var favorite = feature.OfflineFollowedChannels.Last();
        favorite.TogglePinCommand.Execute(null);
        Enqueue(followed, Result([Live("zeta")], [Offline("alpha"), Offline("ZETA")]));
        await feature.RefreshFollowedChannelsAsync();
        Assert.Equal("alpha", feature.OfflineFollowedChannels.Single().Channel);
        Assert.Equal("zeta", feature.LiveFollowedChannels.Single().Channel);
        favorite.TogglePinCommand.Execute(null);
        Assert.SequenceEqual(new[] { "Twitch:zeta" }, settings.FollowedChannels.PinnedOfflineChannelKeys);
        Enqueue(followed, Result([], [Offline("alpha")]));
        await feature.RefreshFollowedChannelsAsync();
        Assert.Equal(1, feature.OfflineFollowedChannels.Count);
        Enqueue(followed, Result([], [Offline("alpha"), Offline("ZETA")]));
        await feature.RefreshFollowedChannelsAsync();
        var restored = feature.OfflineFollowedChannels.First();
        Assert.Equal("ZETA", restored.Channel);
        Assert.True(restored.IsPinned);
        await feature.DisposeAsync();
        Assert.Equal(false, restored.TogglePinCommand.CanExecute(null));
        restored.TogglePinCommand.Execute(null);
        Assert.SequenceEqual(new[] { "Twitch:zeta" }, settings.FollowedChannels.PinnedOfflineChannelKeys);
    });

    private static Task PinDuringRefreshAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = new AppSettings();
        var followed = new FakeFollowedStreamsService();
        var channels = new[] { Offline("alpha"), Offline("zeta") };
        Enqueue(followed, Result([], channels));
        await using var feature = CreateFeature(followed, settings);
        await feature.RefreshFollowedChannelsAsync();
        var favorite = feature.OfflineFollowedChannels.Last();
        foreach (var pin in new[] { true, false })
        {
            var completion = new TaskCompletionSource<FollowedLiveStreamsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            followed.EnqueueResult(_ => completion.Task);
            var refresh = feature.RefreshFollowedChannelsAsync();
            Assert.True(feature.IsFollowedChannelsRefreshing);
            favorite.TogglePinCommand.Execute(null);
            completion.SetResult(Result([], channels));
            await refresh;
            Assert.Equal(pin, favorite.IsPinned);
            Assert.Equal(pin ? "zeta" : "alpha", feature.OfflineFollowedChannels.First().Channel);
            Assert.True(feature.OfflineFollowedChannels.Contains(favorite));
        }
        Assert.Equal(3, followed.CallCount);
    });

    private static Task PinSettingsReplacementAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = new AppSettings();
        var original = settings.FollowedChannels;
        var followed = new FakeFollowedStreamsService();
        Enqueue(followed, Result([], [Offline("alpha"), Offline("beta"), Offline("gamma")]));
        await using var feature = CreateFeature(followed, settings);
        await feature.RefreshFollowedChannelsAsync();
        var cards = feature.OfflineFollowedChannels.ToArray();
        original.PinnedOfflineChannelKeys = ["Twitch:gamma"];
        Assert.True(ReferenceEquals(cards[2], feature.OfflineFollowedChannels.First()));
        settings.FollowedChannels = new FollowedChannelsSettings { PinnedOfflineChannelKeys = ["Twitch:beta"] };
        Assert.True(ReferenceEquals(cards[1], feature.OfflineFollowedChannels.First()));
        Assert.Equal(false, cards[2].IsPinned);
        original.PinnedOfflineChannelKeys = ["Twitch:alpha"];
        Assert.True(ReferenceEquals(cards[1], feature.OfflineFollowedChannels.First()));
        settings.FollowedChannels.PinnedOfflineChannelKeys = [];
        Assert.SequenceEqual(cards, feature.OfflineFollowedChannels);
        await feature.DisposeAsync();
        settings.FollowedChannels.PinnedOfflineChannelKeys = ["Twitch:gamma"];
        Assert.SequenceEqual(cards, feature.OfflineFollowedChannels);
        Assert.Equal(false, cards[2].IsPinned);
        Assert.Equal(1, followed.CallCount);
    });

    private static Task PinPersistenceAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "StreamStudioTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(path, """{"FollowedChannels":{"NotifyWhenLive":false}}""");
            var service = new JsonSettingsService(path);
            var settings = await service.LoadAsync();
            Assert.Equal(0, settings.FollowedChannels.PinnedOfflineChannelKeys.Count);
            settings.Chat.ConnectAutomatically = false;
            var followed = new FakeFollowedStreamsService();
            var channels = new[] { Offline("alpha"), Offline("same"), Offline("same", PlatformKind.Kick) };
            Enqueue(followed, Result([], channels));
            await using (var model = CreatePinningModel(settings, service, followed))
            {
                await model.RefreshFollowedChannelsCommand.ExecuteAsync();
                model.OfflineFollowedChannels[1].TogglePinCommand.Execute(null);
                model.OfflineFollowedChannels.Last().TogglePinCommand.Execute(null);
                await WaitForSavedPinsAsync(service, ["Twitch:same", "Kick:same"]);
            }

            var restartedService = new JsonSettingsService(path);
            var restored = await restartedService.LoadAsync();
            Assert.Equal<string?>(null, restartedService.LastLoadWarning);
            Assert.Equal(false, restored.FollowedChannels.NotifyWhenLive);
            Enqueue(followed, Result([], channels));
            await using (var restarted = CreatePinningModel(restored, restartedService, followed))
            {
                await restarted.RefreshFollowedChannelsCommand.ExecuteAsync();
                Assert.SequenceEqual(new[] { "Twitch:same", "Kick:same", "Twitch:alpha" }, restarted.OfflineFollowedChannels.Select(card => card.Target.StateKey));
                Assert.True(restarted.OfflineFollowedChannels.Take(2).All(card => card.IsPinned));
                restarted.OfflineFollowedChannels[0].TogglePinCommand.Execute(null);
                Assert.SequenceEqual(new[] { "Kick:same", "Twitch:alpha", "Twitch:same" }, restarted.OfflineFollowedChannels.Select(card => card.Target.StateKey));
                restarted.OfflineFollowedChannels[0].TogglePinCommand.Execute(null);
                // Closing immediately must flush these edits without waiting for the autosave delay.
            }
            Assert.Equal(0, (await new JsonSettingsService(path).LoadAsync()).FollowedChannels.PinnedOfflineChannelKeys.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    });

    private static async Task WaitForSavedPinsAsync(JsonSettingsService service, IReadOnlyList<string> expected)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var saved = (await service.LoadAsync()).FollowedChannels.PinnedOfflineChannelKeys;
            if (saved.SequenceEqual(expected)) return;
            if (timer.Elapsed >= TimeSpan.FromSeconds(5))
            {
                Assert.SequenceEqual(expected, saved);
                return;
            }
            await Task.Delay(25);
        }
    }

    private static Task PinButtonActivationAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = new AppSettings();
        settings.Chat.ConnectAutomatically = false;
        var followed = new FakeFollowedStreamsService();
        Enqueue(followed, Result([], [Offline("alpha"), Offline("zeta")]));
        var twitch = new FakeTwitchVodService(new TwitchVodSearchResult(TwitchVodSearchStatus.Available, null, [], "", ""));
        var kick = new FakeKickVodService(new KickVodSearchResult(KickVodSearchStatus.Available, [], "", ""));
        await using var model = CreatePinningModel(settings, new FakeSettingsService(settings), followed, twitch, kick);
        await model.RefreshFollowedChannelsCommand.ExecuteAsync();
        var favorite = model.OfflineFollowedChannels.Last();
        var window = new MainWindow { DataContext = model };
        ApplicationTestCatalog.RemoveMainWindowAutomaticStartup(window);
        var root = (FrameworkElement)window.Content;
        var list = (ItemsControl)window.FindName("OfflineFollowedChannelsList");
        // Attach the real controls to a hidden presentation source so input hit testing is active.
        window.Content = null;
        root.DataContext = model;
        root.Resources = window.Resources;
        TextElement.SetFontFamily(root, window.FontFamily);
        TextElement.SetFontSize(root, window.FontSize);
        TextElement.SetForeground(root, window.Foreground);
        using var host = new HwndSource(new HwndSourceParameters("Offline pin test")
        {
            Width = 1320,
            Height = 900,
            WindowStyle = 0
        });
        host.RootVisual = root;
        try
        {
            foreach (var pinned in new[] { true, false })
            {
                Layout(root, 1320);
                var button = Descendants<Button>(list).Single(candidate => ReferenceEquals(candidate.Command, favorite.TogglePinCommand));
                Assert.True(button.IsVisible && button.IsEnabled && button.Focusable && button.IsTabStop);
                var bounds = Bounds(button, root);
                var point = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                var hit = root.InputHitTest(point) as DependencyObject;
                while (hit is not null && hit is not Button) hit = VisualTreeHelper.GetParent(hit);
                Assert.True(ReferenceEquals(button, hit),
                    $"The pin's click target resolved to {hit?.GetType().Name ?? "nothing"}; " +
                    $"bounds={bounds}, root={root.RenderSize}, window-visible={window.IsVisible}, " +
                    $"pin-visible={button.IsVisible}, pin-hit-test-visible={button.IsHitTestVisible}.");
                var provider = (IInvokeProvider?)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke);
                Assert.NotNull(provider);
                provider!.Invoke();
                Layout(root, 1320);
                Assert.Equal(pinned, favorite.IsPinned);
                Assert.Equal(pinned ? "zeta" : "alpha", model.OfflineFollowedChannels.First().Channel);
                button = Descendants<Button>(list).Single(candidate => ReferenceEquals(candidate.Command, favorite.TogglePinCommand));
                Assert.Equal(favorite.PinActionText, AutomationProperties.GetName(button));
                Assert.Equal(pinned ? "Pinned" : "Not pinned", AutomationProperties.GetItemStatus(button));
                var icon = Descendants<System.Windows.Shapes.Path>(button).Single();
                Assert.Equal(pinned, icon.Fill is not null);
                SaveImage(root, pinned ? "offline-following-pinned" : "offline-following-unpinned");
            }
            Assert.Equal(0, twitch.Requests.Count);
            Assert.Equal(0, kick.CallCount);
            Assert.Equal(0, model.Tabs.Count);
            Assert.True(model.IsFollowedHomePageSelected);
            Assert.Equal(1, followed.CallCount);
        }
        finally
        {
            window.Close();
        }
    });

    private static MainViewModel CreatePinningModel(AppSettings settings, ISettingsService service,
        IFollowedStreamsService followed, ITwitchVodService? twitch = null, IKickVodService? kick = null)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        return TestViewModels.CreateMain(settings, service, new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(),
            action => dispatcher.BeginInvoke(action), followedStreamsService: followed,
            twitchVodService: twitch, kickVodService: kick);
    }
}

using StreamlinkVlcStudio.App.Wpf.Twitch;

internal static partial class TwitchChannelPointsTestCatalog
{
    private static LiveStreamCardData ChannelData(string channel, PlatformKind platform = PlatformKind.Twitch,
        StreamTargetKind kind = StreamTargetKind.Live, LiveStreamCardSource source = LiveStreamCardSource.Followed) =>
        new(source, new StreamTarget(platform, channel, $"https://www.{(platform == PlatformKind.Twitch ? "twitch.tv" : "kick.com")}/{channel}", kind),
            platform, channel, channel, "Live title", "Category", 100, "", "", null, null, "en");

    private static FollowedLiveStream FollowedStream(string channel, PlatformKind platform = PlatformKind.Twitch) =>
        new(platform, channel, channel, "Live title", "Category", 100, "", null, null, "en", ChannelData(channel, platform).Target.Url);

    private static Task AllFollowedAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var channels = Enumerable.Range(1, 125).Select(index => $"followed{index:000}").ToArray();
        await using var fixture = new Fixture(initialChannels: channels.Select(channel => ChannelData(channel)));
        await Eventually(() => fixture.Browser.Pages.Count == channels.Length && fixture.Browser.Pages.All(page => page.Checks > 0));
        Assert.Equal(0, fixture.Tabs.Count);
        Assert.SequenceEqual(channels, fixture.Browser.Pages.Select(page => page.Channel).Order(StringComparer.Ordinal));
        Assert.Equal(channels.Length, fixture.Controller.ChannelClaims.Count);
        foreach (var page in fixture.Browser.Pages) page.Confirm($"{page.Channel}-claim");
        Assert.True(fixture.Controller.ChannelClaims.All(row => row.Count == 1));
        fixture.LiveFollowedChannels.Clear();
        Assert.True(fixture.Browser.Pages.All(page => page.Disposed));
        Assert.Equal(channels.Length, fixture.Controller.ChannelClaims.Count);
    });

    private static Task FollowedCardsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new Fixture();
        var alpha = fixture.Add("alpha");
        var bravo = fixture.Add("bravo");
        await Eventually(() => fixture.Browser.Pages.Count == 2);
        var alphaPage = fixture.Browser.Pages.Single(page => page.Channel == "alpha");
        var bravoPage = fixture.Browser.Pages.Single(page => page.Channel == "bravo");
        var updated = ChannelData("alpha") with { Title = "Updated title", ViewerCount = 200 };
        updated = updated with { Target = updated.Target with { CategoryName = "Updated category" } };
        alpha.Update(updated, 2);
        fixture.LiveFollowedChannels.Move(0, 1);
        Assert.Equal(2, fixture.Browser.OpenAttempts);
        Assert.True(fixture.Browser.Pages.All(page => !page.Disposed));

        var charlie = new LiveStreamCardViewModel(ChannelData("charlie"), (_, _) => Task.CompletedTask);
        fixture.LiveFollowedChannels[fixture.LiveFollowedChannels.IndexOf(bravo)] = charlie;
        await Eventually(() => fixture.Browser.Pages.Count == 3);
        Assert.True(bravoPage.Disposed);
        Assert.Equal(false, alphaPage.Disposed);
        bravo.Update(ChannelData("removed"), 3);
        Assert.Equal(3, fixture.Browser.OpenAttempts);

        alpha.Update(ChannelData("alpha", source: LiveStreamCardSource.Browse), 4);
        Assert.True(alphaPage.Disposed);
        Assert.SequenceEqual(new[] { "charlie" }, fixture.Controller.ChannelClaims.Select(row => row.Channel));
        alpha.Update(ChannelData("alpha"), 5);
        await Eventually(() => fixture.Browser.Pages.Count == 4);
        var replacementAlphaPage = fixture.Browser.Pages.Last();
        alpha.Update(ChannelData("alpha", platform: PlatformKind.Kick), 6);
        Assert.True(replacementAlphaPage.Disposed);
        alpha.Update(ChannelData("delta"), 7);
        await Eventually(() => fixture.Browser.Pages.Count == 5);
        Assert.SequenceEqual(new[] { "charlie", "delta" }, fixture.Controller.ChannelClaims.Select(row => row.Channel));

        fixture.LiveFollowedChannels.Clear();
        alpha.Update(ChannelData("detached"), 8);
        charlie.Update(ChannelData("detachedtoo"), 8);
        Assert.True(fixture.Controller.HasNoChannelClaims);
        Assert.True(fixture.Browser.Pages.All(page => page.Disposed));
        Assert.Equal(5, fixture.Browser.OpenAttempts);
    });

    private static Task PendingSessionAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(sessionGate: release.Task,
            initialChannels: [ChannelData("alpha"), ChannelData("bravo")]);
        Assert.Equal(0, fixture.Browser.OpenAttempts);
        fixture.LiveFollowedChannels.RemoveAt(0);
        fixture.Add("charlie");
        release.SetResult();
        await Eventually(() => fixture.Browser.Pages.Count == 2 && fixture.Browser.Pages.All(page => page.Checks > 0));
        Assert.SequenceEqual(new[] { "bravo", "charlie" }, fixture.Browser.Pages.Select(page => page.Channel).Order(StringComparer.Ordinal));
        Assert.Equal(0, fixture.Tabs.Count);
    });

    private static Task FollowedRefreshAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = new AppSettings { StreamlinkPath = "streamlink.exe", VlcDirectory = @"C:\Program Files\VideoLAN\VLC" };
        settings.Chat.ConnectAutomatically = false;
        var service = new FakeFollowedStreamsService(FollowedStream("bravo"), FollowedStream("charlie"));
        service.EnqueueResult(FollowedStream("alpha"), FollowedStream("bravo"), FollowedStream("kickone", PlatformKind.Kick));
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource<FollowedLiveStreamsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.EnqueueResult(token =>
        {
            refreshStarted.TrySetResult();
            return releaseRefresh.Task.WaitAsync(token);
        });
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        await using var viewModel = TestViewModels.CreateMain(settings, new FakeSettingsService(settings),
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(),
            action => { if (dispatcher.CheckAccess()) action(); else dispatcher.BeginInvoke(action); },
            followedStreamsService: service, followedChannelsRefreshInterval: TimeSpan.FromMilliseconds(100));
        viewModel.Initialize();
        using var browser = new FakeBrowser();
        using var controller = new TwitchChannelPointsController(settings, viewModel.LiveFollowedChannels,
            () => viewModel.SelectedTab, browser, new MemoryLogger(), TimeSpan.FromMilliseconds(10));
        await Eventually(() => browser.Pages.Count == 2 && browser.Pages.All(page => page.Checks > 0));
        Assert.Equal(0, viewModel.Tabs.Count);
        var alphaPage = browser.Pages.Single(page => page.Channel == "alpha");
        var bravoPage = browser.Pages.Single(page => page.Channel == "bravo");
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var checksWhileRefreshing = alphaPage.Checks;
        await Eventually(() => alphaPage.Checks > checksWhileRefreshing);
        Assert.Equal(false, alphaPage.Disposed);

        await viewModel.OpenStreamAsync(ChannelData("notfollowed").Target);
        Assert.Equal(false, viewModel.IsHomeSelected);
        Assert.Equal("notfollowed", viewModel.SelectedTab!.Target.Channel);
        Assert.Equal(2, browser.OpenAttempts);
        releaseRefresh.SetResult(new([FollowedStream("bravo"), FollowedStream("charlie")], []));
        await Eventually(() => browser.Pages.Count == 3 && browser.Pages.Last().Checks > 0 && alphaPage.Disposed);
        Assert.Equal(false, bravoPage.Disposed);
        Assert.Equal("charlie", browser.Pages.Last().Channel);
        Assert.Equal(false, viewModel.IsHomeSelected);

        service.EnqueueResult(_ => Task.FromException<FollowedLiveStreamsResult>(new InvalidOperationException("refresh failed")));
        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.Equal(false, bravoPage.Disposed);
        Assert.Equal(false, browser.Pages.Last().Disposed);
        Assert.Equal(3, browser.OpenAttempts);
        service.EnqueueResult();
        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.True(browser.Pages.All(page => page.Disposed));
        Assert.True(controller.HasNoChannelClaims);
        Assert.Equal(3, browser.OpenAttempts);
    });

    private static Task SelectedPageAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new Fixture();
        fixture.Add("alpha");
        await Eventually(() => fixture.Browser.Pages.Count == 1);
        var page = fixture.Browser.Pages[0];
        fixture.Controller.OpenPageCommand.Execute(null);
        Assert.Equal(0, page.Shows);
        var tab = fixture.AddTab("alpha");
        SetStatus(tab, PlaybackStatus.Stopped);
        typeof(StreamTabViewModel).GetProperty(nameof(StreamTabViewModel.IsBehindLive))!.SetValue(tab, true);
        fixture.Controller.OpenPageCommand.Execute(null);
        Assert.Equal(1, page.Shows);
        fixture.Tabs.Clear();
        fixture.AddTab("alpha", kind: StreamTargetKind.TwitchVod);
        fixture.Controller.OpenPageCommand.Execute(null);
        Assert.Equal(1, page.Shows);
        fixture.Tabs.Clear();
        fixture.AddTab("alpha", platform: PlatformKind.Kick);
        fixture.Controller.OpenPageCommand.Execute(null);
        Assert.Equal(1, page.Shows);
        fixture.Tabs.Clear();
        fixture.AddTab("alpha");
        fixture.LiveFollowedChannels.Clear();
        fixture.Controller.OpenPageCommand.Execute(null);
        Assert.Equal(1, page.Shows);
        Assert.True(page.Disposed);
    });
}

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> NavigationTests { get; } =
    [
        ("back navigation restores the same playing stream from settings without restarting", BackFromSettingsPreservesStreamAsync),
        ("back navigation follows Home pages and stream tabs in visit order", BackNavigationVisitOrderAsync),
        ("back navigation ignores repeated selections and disables when history is exhausted", BackNavigationRepeatedSelectionAsync),
        ("back navigation skips closed streams and survives closing all tabs in settings", BackNavigationClosedTabsAsync),
        ("back navigation restores browse categories after platform changes", BackNavigationBrowseCategoryAsync),
        ("back navigation can reload categories after canceling an asynchronous platform restore", BackNavigationAsyncBrowseCategoryAsync)
    ];

    private static async Task BackFromSettingsPreservesStreamAsync()
    {
        await using var fixture = new NavigationFixture();
        var tab = fixture.AddTab("albralelie");
        fixture.Main.SelectedTab = tab;
        // Tabs.Add queues the Home visibility policy. Let the selected-tab pass
        // finish before startup so its pause/resume work is outside this navigation test.
        await fixture.Main.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(2));
        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(fixture.Settings);
        await fixture.Main.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(true, tab.IsVideoVisible);
        Assert.Equal(false, tab.PausedByTabSwitch);
        var engine = fixture.Playback.Engine!;
        var plays = engine.PlayCount;
        var starts = fixture.Streamlink.StartCount;
        var creates = fixture.Playback.CreateCount;

        fixture.Main.ToggleSettingsCommand.Execute(null);
        fixture.Main.ShowHotkeysSettingsCommand.Execute(null);
        fixture.Main.ShowPlaybackSettingsCommand.Execute(null);
        Assert.Equal(true, fixture.Main.IsSettingsOpen);
        Assert.Equal(true, fixture.Main.GoBackCommand.CanExecute(null));

        fixture.Main.GoBackCommand.Execute(null);
        await fixture.Main.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(false, fixture.Main.IsSettingsOpen);
        Assert.Equal(false, fixture.Main.IsHomeSelected);
        Assert.Equal(tab, fixture.Main.SelectedTab);
        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        Assert.Equal(plays, engine.PlayCount);
        Assert.Equal(starts, fixture.Streamlink.StartCount);
        Assert.Equal(creates, fixture.Playback.CreateCount);
        Assert.Equal(1, fixture.Main.Tabs.Count);
    }

    private static async Task BackNavigationVisitOrderAsync()
    {
        await using var fixture = new NavigationFixture();
        var main = fixture.Main;
        var first = fixture.AddTab("albralelie");
        var second = fixture.AddTab("summit1g");
        main.ShowRecentHomePageCommand.Execute(null);
        main.ShowTwitchVodsHomePageCommand.Execute(null);
        main.SelectedTab = first;
        main.SelectedTab = second;
        main.SelectHomeCommand.Execute(null);
        main.ShowFollowedHomePageCommand.Execute(null);
        main.ToggleSettingsCommand.Execute(null);

        main.GoBackCommand.Execute(null);
        Assert.Equal(false, main.IsSettingsOpen);
        Assert.Equal(true, main.IsHomeSelected);
        Assert.Equal(true, main.IsFollowedHomePageSelected);
        main.GoBackCommand.Execute(null);
        Assert.Equal(true, main.IsTwitchVodsHomePageSelected);
        Assert.Equal(true, main.IsHomeSelected);
        main.GoBackCommand.Execute(null);
        Assert.Equal(second, main.SelectedTab);
        Assert.Equal(false, main.IsHomeSelected);
        main.GoBackCommand.Execute(null);
        Assert.Equal(first, main.SelectedTab);
        main.GoBackCommand.Execute(null);
        Assert.Equal(true, main.IsHomeSelected);
        Assert.Equal(true, main.IsTwitchVodsHomePageSelected);
        main.GoBackCommand.Execute(null);
        Assert.Equal(true, main.IsRecentHomePageSelected);
        main.GoBackCommand.Execute(null);
        Assert.Equal(true, main.IsFollowedHomePageSelected);
        Assert.Equal(false, main.CanGoBack);
    }

    private static async Task BackNavigationRepeatedSelectionAsync()
    {
        await using var fixture = new NavigationFixture();
        var main = fixture.Main;
        var canExecuteChanges = 0;
        main.GoBackCommand.CanExecuteChanged += (_, _) => canExecuteChanges++;
        Assert.Equal(false, main.CanGoBack);
        main.GoBackCommand.Execute(null);
        main.SelectHomeCommand.Execute(null);
        main.ShowFollowedHomePageCommand.Execute(null);
        Assert.Equal(false, main.GoBackCommand.CanExecute(null));

        main.ShowRecentHomePageCommand.Execute(null);
        main.ShowRecentHomePageCommand.Execute(null);
        main.IsSettingsOpen = true;
        main.IsSettingsOpen = true;
        main.ShowHotkeysSettingsCommand.Execute(null);
        main.GoBackCommand.Execute(null);
        Assert.Equal(false, main.IsSettingsOpen);
        Assert.Equal(true, main.IsRecentHomePageSelected);
        main.GoBackCommand.Execute(null);
        Assert.Equal(true, main.IsFollowedHomePageSelected);
        Assert.Equal(false, main.CanGoBack);
        Assert.Equal(false, main.GoBackCommand.CanExecute(null));
        main.GoBackCommand.Execute(null);
        Assert.Equal(true, main.IsFollowedHomePageSelected);
        Assert.True(canExecuteChanges > 0);
    }

    private static async Task BackNavigationClosedTabsAsync()
    {
        await using var fixture = new NavigationFixture();
        var main = fixture.Main;
        var first = fixture.AddTab("albralelie");
        var second = fixture.AddTab("summit1g");
        main.ShowRecentHomePageCommand.Execute(null);
        main.SelectedTab = first;
        main.SelectedTab = second;
        main.IsSettingsOpen = true;
        Assert.Equal(true, main.CloseTab(second));
        main.GoBackCommand.Execute(null);
        Assert.Equal(false, main.IsSettingsOpen);
        Assert.Equal(first, main.SelectedTab);
        Assert.Equal(1, main.Tabs.Count);

        main.IsSettingsOpen = true;
        Assert.Equal(true, main.CloseAllTabs());
        main.GoBackCommand.Execute(null);
        Assert.Equal(false, main.IsSettingsOpen);
        Assert.Equal(true, main.IsHomeSelected);
        Assert.Equal(true, main.IsRecentHomePageSelected);
        Assert.Equal(0, main.Tabs.Count);
        main.GoBackCommand.Execute(null);
        Assert.Equal(true, main.IsFollowedHomePageSelected);
        Assert.Equal(false, main.CanGoBack);
    }

    private static async Task BackNavigationBrowseCategoryAsync()
    {
        var browse = new FakeBrowseService
        {
            CategoryResponder = request => new BrowseResult<BrowseCategory>(
                BrowseResultStatus.Available,
                [new BrowseCategory(request.Platform, "games", "Games", "", [])],
                "",
                "Categories loaded"),
            StreamResponder = _ => new BrowseResult<BrowseLiveStream>(
                BrowseResultStatus.Available, [], "", "Streams loaded")
        };
        await using var fixture = new NavigationFixture(browse);
        var main = fixture.Main;
        main.ShowBrowseHomePageCommand.Execute(null);
        var category = main.BrowseCategories[0];
        await category.SelectCommand.ExecuteAsync();
        var streamLoads = browse.StreamRequests.Count;
        main.IsSettingsOpen = true;
        main.GoBackCommand.Execute(null);
        Assert.Equal(true, main.IsBrowseStreamsPageVisible);
        Assert.Equal(category, main.SelectedBrowseCategory);
        Assert.Equal(streamLoads, browse.StreamRequests.Count);

        main.SelectKickBrowsePlatformCommand.Execute(null);
        Assert.Equal(PlatformKind.Kick, main.SelectedBrowsePlatform);
        main.GoBackCommand.Execute(null);
        Assert.Equal(PlatformKind.Twitch, main.SelectedBrowsePlatform);
        Assert.Equal(true, main.IsBrowseStreamsPageVisible);
        Assert.Equal(category, main.SelectedBrowseCategory);
        Assert.Equal(streamLoads + 1, browse.StreamRequests.Count);
        main.GoBackCommand.Execute(null);
        Assert.Equal(true, main.IsBrowseCategoriesPageVisible);
        main.GoBackCommand.Execute(null);
        Assert.Equal(true, main.IsFollowedHomePageSelected);
        Assert.Equal(false, main.CanGoBack);
    }

    private static async Task BackNavigationAsyncBrowseCategoryAsync()
    {
        var canceledLoad = new TaskCompletionSource<BrowseResult<BrowseCategory>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var returningLoad = new TaskCompletionSource<BrowseResult<BrowseCategory>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var twitchRequests = 0;
        var canceledToken = CancellationToken.None;
        static BrowseResult<BrowseCategory> Categories(PlatformKind platform, string id) => new(
            BrowseResultStatus.Available,
            [new BrowseCategory(platform, id, id, "", [])],
            "",
            "Categories loaded");
        var browse = new NavigationAsyncBrowseService
        {
            CategoryResponder = (request, cancellationToken) =>
            {
                if (request.Platform == PlatformKind.Kick)
                {
                    return Task.FromResult(Categories(request.Platform, "kick"));
                }

                twitchRequests++;
                if (twitchRequests == 1)
                {
                    return Task.FromResult(Categories(request.Platform, "original"));
                }

                if (twitchRequests == 2)
                {
                    canceledToken = cancellationToken;
                    return canceledLoad.Task;
                }

                return returningLoad.Task;
            }
        };
        await using var fixture = new NavigationFixture(browse);
        var main = fixture.Main;
        try
        {
            main.ShowBrowseHomePageCommand.Execute(null);
            var originalCategory = main.BrowseCategories[0];
            await originalCategory.SelectCommand.ExecuteAsync();
            main.SelectKickBrowsePlatformCommand.Execute(null);

            // Restoring Twitch details starts, then cancels, a real pending category
            // load. The stale generation's finally block cannot clear the loading flag.
            main.GoBackCommand.Execute(null);
            Assert.Equal(2, twitchRequests);
            Assert.Equal(true, canceledToken.IsCancellationRequested);
            Assert.Equal(true, main.IsBrowseStreamsPageVisible);
            Assert.Equal(originalCategory, main.SelectedBrowseCategory);
            Assert.Equal(false, main.IsBrowseCategoriesLoading);

            main.GoBackCommand.Execute(null);
            Assert.Equal(true, main.IsBrowseCategoriesPageVisible);
            Assert.Equal(3, twitchRequests);
            Assert.Equal(true, main.IsBrowseCategoriesLoading);
            canceledLoad.SetResult(Categories(PlatformKind.Twitch, "stale"));
            returningLoad.SetResult(Categories(PlatformKind.Twitch, "reloaded"));
            await TestWait.UntilAsync(
                () => !main.IsBrowseCategoriesLoading && main.BrowseCategories.Count == 1,
                TimeSpan.FromSeconds(1));
            Assert.Equal("reloaded", main.BrowseCategories[0].Id);
            Assert.Equal(true, main.IsBrowseCategoriesPageVisible);
            main.GoBackCommand.Execute(null);
            Assert.Equal(true, main.IsFollowedHomePageSelected);
            Assert.Equal(false, main.CanGoBack);
        }
        finally
        {
            canceledLoad.TrySetResult(Categories(PlatformKind.Twitch, "canceled"));
            returningLoad.TrySetResult(Categories(PlatformKind.Twitch, "cleanup"));
        }
    }

    private sealed class NavigationAsyncBrowseService : IBrowseService
    {
        private readonly FakeBrowseService remainingRequests = new();

        public required Func<BrowseCategoryRequest, CancellationToken, Task<BrowseResult<BrowseCategory>>> CategoryResponder { get; init; }

        public Task<BrowseResult<BrowseCategory>> GetCategoriesAsync(
            BrowseCategoryRequest request,
            AppSettings settings,
            CancellationToken cancellationToken = default) => CategoryResponder(request, cancellationToken);

        public Task<BrowseResult<BrowseCategoryViewerCount>> GetCategoryViewerCountsAsync(
            BrowseCategoryViewerCountRequest request,
            AppSettings settings,
            CancellationToken cancellationToken = default) =>
            remainingRequests.GetCategoryViewerCountsAsync(request, settings, cancellationToken);

        public Task<BrowseResult<BrowseLiveStream>> GetStreamsAsync(
            BrowseStreamRequest request,
            AppSettings settings,
            CancellationToken cancellationToken = default) =>
            remainingRequests.GetStreamsAsync(request, settings, cancellationToken);
    }

    private sealed class NavigationFixture : IAsyncDisposable
    {
        private readonly MemoryLogger logger = new();
        private readonly FakeChatClientFactory chat = new();

        public AppSettings Settings { get; } = new()
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };

        public FakeStreamlinkService Streamlink { get; } = new();
        public FakePlaybackEngineFactory Playback { get; } = new();
        public MainViewModel Main { get; }

        public NavigationFixture(IBrowseService? browse = null)
        {
            Settings.Chat.ConnectAutomatically = false;
            Main = TestViewModels.CreateMain(
                Settings,
                new FakeSettingsService(Settings),
                Streamlink,
                Playback,
                chat,
                logger,
                action => action(),
                browseService: browse);
        }

        public StreamTabViewModel AddTab(string channel)
        {
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse(channel, PlatformKind.Twitch),
                "best",
                Streamlink,
                Playback,
                chat,
                logger,
                action => action());
            Main.Tabs.Add(tab);
            return tab;
        }

        public ValueTask DisposeAsync() => Main.DisposeAsync();
    }
}

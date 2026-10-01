using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Threading;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> RecentStreamNavigationTests { get; } =
    [
        ("recent stream navigation: offline Twitch button opens the channel's VODs", () => OfflineRecentNavigationAsync(PlatformKind.Twitch, false)),
        ("recent stream navigation: offline Kick button opens the channel's VODs", () => OfflineRecentNavigationAsync(PlatformKind.Kick, false)),
        ("recent stream navigation: offline Twitch background open opens the channel's VODs", () => OfflineRecentNavigationAsync(PlatformKind.Twitch, true)),
        ("recent stream navigation: offline Kick background open opens the channel's VODs", () => OfflineRecentNavigationAsync(PlatformKind.Kick, true)),
        ("recent stream navigation: live Twitch click still opens live playback", () => RecentPlaybackNavigationAsync(StreamMetadataState.Available, false)),
        ("recent stream navigation: live Kick background open keeps Recently Watched selected", () => RecentPlaybackNavigationAsync(StreamMetadataState.Available, true)),
        ("recent stream navigation: unchecked status keeps the live playback path", () => RecentPlaybackNavigationAsync(null, false)),
        ("recent stream navigation: unavailable metadata is not treated as offline", () => RecentPlaybackNavigationAsync(StreamMetadataState.Unavailable, false)),
        ("recent stream navigation: retained commands use a later offline result", () => RefreshedRecentNavigationAsync(becomesOffline: true)),
        ("recent stream navigation: retained commands use a later live result", () => RefreshedRecentNavigationAsync(becomesOffline: false))
    ];

    private static readonly TimeSpan RecentNavigationTimeout = TimeSpan.FromSeconds(3);

    private static Task OfflineRecentNavigationAsync(PlatformKind platform, bool background) => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new RecentNavigationFixture(platform, RecentNavigationMetadata(StreamMetadataState.Offline));
        var main = fixture.Main;
        var card = main.RecentStreams.Single();
        await fixture.ShowRecentAsync("Offline");
        Assert.True(ReferenceEquals(card, main.RecentStreams.Single()));
        var history = fixture.Settings.RecentStreams.ToArray();
        var saves = fixture.Storage.SaveCount;

        if (background) await card.OpenAndStayOnHomeCommand.ExecuteAsync();
        else await InvokeRecentNavigationButtonAsync(main, card);

        AssertRecentVodNavigation(fixture);
        Assert.SequenceEqual(history, fixture.Settings.RecentStreams);
        Assert.Equal(saves, fixture.Storage.SaveCount);
        Assert.Equal(0, fixture.Streamlink.ProbeRequests.Count);
        Assert.Equal(0, fixture.Streamlink.StartCount);
        Assert.Equal(0, fixture.Streamlink.ResolveStreamUrlCount);
        Assert.Equal(0, fixture.Playback.CreateCount);
        main.GoBackCommand.Execute(null);
        Assert.True(main.IsHomeSelected && main.IsRecentHomePageSelected);
        Assert.True(ReferenceEquals(card, main.RecentStreams.Single()));
    });

    private static Task RecentPlaybackNavigationAsync(StreamMetadataState? state, bool background) => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new RecentNavigationFixture(background ? PlatformKind.Kick : PlatformKind.Twitch,
            state is { } value ? [RecentNavigationMetadata(value)] : []);
        var main = fixture.Main;
        await fixture.ShowRecentAsync(state == StreamMetadataState.Available ? "Live" : "Unknown");
        var card = main.RecentStreams.Single();

        if (background) await card.OpenAndStayOnHomeCommand.ExecuteAsync();
        else await card.OpenCommand.ExecuteAsync();

        AssertRecentPlaybackNavigation(fixture, background);
    });

    private static Task RefreshedRecentNavigationAsync(bool becomesOffline) => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new RecentNavigationFixture(PlatformKind.Twitch,
            RecentNavigationMetadata(becomesOffline ? StreamMetadataState.Available : StreamMetadataState.Offline),
            RecentNavigationMetadata(becomesOffline ? StreamMetadataState.Offline : StreamMetadataState.Available));
        var main = fixture.Main;
        var card = main.RecentStreams.Single();
        var command = card.OpenCommand;
        await fixture.ShowRecentAsync(becomesOffline ? "Live" : "Offline");
        await fixture.ShowRecentAsync(becomesOffline ? "Offline" : "Live");
        Assert.True(ReferenceEquals(card, main.RecentStreams.Single()));
        Assert.True(ReferenceEquals(command, card.OpenCommand));

        await command.ExecuteAsync();

        if (becomesOffline) AssertRecentVodNavigation(fixture);
        else AssertRecentPlaybackNavigation(fixture, false);
    });

    private static void AssertRecentVodNavigation(RecentNavigationFixture fixture)
    {
        var main = fixture.Main;
        Assert.True(main.IsHomeSelected && main.IsHomeVisible && main.IsTwitchVodsHomePageSelected);
        Assert.Equal(false, main.IsRecentHomePageSelected);
        Assert.Equal(fixture.Platform, main.SelectedVodPlatform);
        Assert.Equal(fixture.Channel, main.TwitchVodSearchText);
        Assert.Equal(0, main.Tabs.Count);
        Assert.Equal<StreamTabViewModel?>(null, main.SelectedTab);
        Assert.True(main.HasTwitchVodSearchCompleted && !main.IsTwitchVodSearchRunning);
        var vod = main.TwitchVods.Single();
        Assert.Equal(fixture.Platform, vod.Platform);
        Assert.Equal(fixture.Channel, vod.Target.Channel);
        if (fixture.Platform == PlatformKind.Twitch)
        {
            Assert.Equal(1, fixture.Twitch.CallCount);
            Assert.Equal(fixture.Channel, fixture.Twitch.Requests.Single().Streamer);
            Assert.Equal(0, fixture.Kick.CallCount);
            Assert.Equal("12345", vod.Id);
        }
        else
        {
            Assert.Equal(1, fixture.Kick.CallCount);
            Assert.Equal(fixture.Channel, fixture.Kick.Requests.Single().Channel);
            Assert.Equal(0, fixture.Twitch.CallCount);
            Assert.Equal("uuid-123", vod.Id);
        }
    }

    private static void AssertRecentPlaybackNavigation(RecentNavigationFixture fixture, bool background)
    {
        var main = fixture.Main;
        var tab = main.Tabs.Single();
        Assert.Equal(StreamTargetKind.Live, tab.Target.Kind);
        Assert.Equal(fixture.Platform, tab.Target.Platform);
        Assert.Equal(fixture.Channel, tab.Target.Channel);
        Assert.Equal(background, main.IsHomeSelected);
        Assert.Equal(background ? null : tab, main.SelectedTab);
        Assert.True(main.IsRecentHomePageSelected);
        Assert.Equal(false, main.IsTwitchVodsHomePageSelected);
        Assert.Equal(0, fixture.Twitch.CallCount);
        Assert.Equal(0, fixture.Kick.CallCount);
    }

    private static async Task InvokeRecentNavigationButtonAsync(MainViewModel main, RecentStreamViewModel card)
    {
        var window = new MainWindow { DataContext = main };
        RemoveMainWindowAutomaticStartup(window);
        try
        {
            var root = (FrameworkElement)window.Content;
            var size = new Size(1100, 720);
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
            await root.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            root.UpdateLayout();
            var button = FindVisualDescendants<Button>(root).Single(candidate => ReferenceEquals(candidate.Command, card.OpenCommand));
            Assert.True(button.IsEnabled && button.ActualWidth > 0 && button.ActualHeight > 0);
            var provider = (IInvokeProvider?)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke);
            Assert.NotNull(provider);
            provider!.Invoke();
            await root.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await TestWait.UntilAsync(() => card.OpenCommand.CanExecute(null), RecentNavigationTimeout);
        }
        finally
        {
            window.Close();
        }
    }

    private static StreamMetadataResult RecentNavigationMetadata(StreamMetadataState state) =>
        new(state, "", "", state == StreamMetadataState.Unavailable ? "Offline status could not be checked." : state.ToString());

    private sealed class RecentNavigationFixture : IAsyncDisposable
    {
        public PlatformKind Platform { get; }
        public string Channel { get; }
        public AppSettings Settings { get; }
        public FakeSettingsService Storage { get; }
        public FakeStreamlinkService Streamlink { get; } = new();
        public FakePlaybackEngineFactory Playback { get; } = new();
        public FakeTwitchVodService Twitch { get; }
        public FakeKickVodService Kick { get; }
        public MainViewModel Main { get; }

        public RecentNavigationFixture(PlatformKind platform, params StreamMetadataResult[] metadata)
        {
            Platform = platform;
            Channel = platform == PlatformKind.Twitch ? "summit1g" : "xqc";
            Settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\Program Files\VideoLAN\VLC",
                RecentStreams =
                [
                    new RecentStreamSettings
                    {
                        Platform = platform,
                        Channel = Channel,
                        DisplayName = "Different display name",
                        Url = $"https://{(platform == PlatformKind.Twitch ? "www.twitch.tv" : "kick.com")}/{Channel}",
                        LastQuality = "720p",
                        LastWatchedAtUtc = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero)
                    }
                ]
            };
            Settings.Chat.ConnectAutomatically = false;
            Storage = new FakeSettingsService(Settings);
            var broadcaster = new TwitchVodBroadcaster("26490481", "summit1g", "summit1g");
            var twitchVod = new TwitchVodItem("12345", "stream-1", broadcaster.Id, broadcaster.Login, broadcaster.DisplayName,
                "Past broadcast", "", "https://www.twitch.tv/videos/12345", "", null, null,
                TimeSpan.FromMinutes(45), 100, TwitchVodTypeFilter.Archive);
            Twitch = new FakeTwitchVodService(new TwitchVodSearchResult(TwitchVodSearchStatus.Available, broadcaster, [twitchVod], "", "1 VOD"));
            var kickVod = new KickVodItem("123", "456", "uuid-123", "xqc", "xQc", "Past broadcast",
                "https://kick.com/xqc/videos/uuid-123", "https://vod.kick.com/xqc/index.m3u8", "", "Just Chatting",
                null, null, TimeSpan.FromMinutes(30), 100);
            Kick = new FakeKickVodService(new KickVodSearchResult(KickVodSearchStatus.Available, [kickVod], "", "1 VOD"));
            Main = TestViewModels.CreateMain(Settings, Storage, Streamlink, Playback, new FakeChatClientFactory(), new MemoryLogger(),
                action => action(), streamMetadataService: metadata.Length > 0 ? new FakeStreamMetadataService(metadata) : null,
                recentThumbnailRefreshInterval: TimeSpan.Zero, streamSearchDebounceInterval: TimeSpan.FromHours(1),
                twitchVodService: Twitch, kickVodService: Kick, twitchVodSearchDebounceInterval: TimeSpan.FromHours(1));
        }

        public async Task ShowRecentAsync(string expectedStatus)
        {
            Main.ShowRecentHomePageCommand.Execute(null);
            await TestWait.UntilAsync(() => Main.RecentStreams.Single().LiveStatusText == expectedStatus, RecentNavigationTimeout);
        }

        public ValueTask DisposeAsync() => Main.DisposeAsync();
    }
}

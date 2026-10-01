using System.Windows.Automation;
using System.Windows.Threading;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> SettingsAutoSaveTests { get; } =
    [
        ("settings autosave: edits across every category and stream overlay sizes survive reload", AutoSaveSettingsRoundTripAsync),
        ("settings autosave: rapid edits share one write containing the final values", AutoSaveSettingsDebounceAsync),
        ("settings autosave: replacing settings groups observes the replacements and releases old groups", AutoSaveSettingsReplacementAsync),
        ("settings autosave: edits during a write are serialized and flushed before shutdown", AutoSaveSettingsDuringWriteAsync),
        ("settings autosave: leaving settings and shutting down flush pending edits", AutoSaveSettingsOnCloseAsync)
    ];

    private static Task AutoSaveSettingsRoundTripAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var root = CreateTempTestDirectory();
        var settings = new AppSettings();
        settings.Chat.ConnectAutomatically = false;
        var service = new JsonSettingsService(Path.Combine(root, "settings.json"));
        try
        {
            await using var main = CreateAutoSaveMain(settings, service);
            var window = new MainWindow { DataContext = main };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, main);
            try
            {
                main.IsSettingsOpen = true;
                main.SelectedSettingsCategory = SettingsCategory.Playback;
                LayoutStudioPolishWindow(window, new Size(1320, 820));
                var quality = FindVisualDescendants<ComboBox>((FrameworkElement)window.FindName("PlaybackSettingsPage"))
                    .Single(control => ReferenceEquals(control.ItemsSource, main.QualityOptions));
                quality.SetCurrentValue(ComboBox.SelectedValueProperty, "720p");
                Assert.Equal("720p", settings.DefaultQuality);
                Assert.Equal("720p", main.SelectedQuality);

                settings.CloseBehavior = WindowCloseBehavior.MinimizeToTray;
                settings.Replay.Enabled = false;
                settings.FollowedChannels.NotifyWhenLive = false;
                settings.Hotkeys.SkipBackward = "Ctrl+J";
                settings.Hotkeys.SkipBackwardSeconds = 75;
                settings.Updates.AutomaticDownloadsEnabled = true;
                settings.CustomStreamlinkArguments = "--retry-streams 5";
                main.KickFollowedChannelsText = "fixture\nhttps://kick.com/anotherfixture";
                main.ChatTextSize = 26;

                var tab = TestViewModels.CreateTab(StreamInputParser.Parse("fixture", PlatformKind.Twitch),
                    "480p", new FakeStreamlinkService(), new FakePlaybackEngineFactory(),
                    new FakeChatClientFactory(), new MemoryLogger(), action => action());
                main.Tabs.Add(tab);
                main.SelectedTab = tab;
                main.SelectedVlcOverlayFontSize = 31;
                Assert.Equal("720p", settings.DefaultQuality);

                main.SelectedSettingsCategory = SettingsCategory.Downloads;
                LayoutStudioPolishWindow(window, new Size(1320, 820));
                ((ComboBox)window.FindName("VodDownloadQualityComboBox")).SetCurrentValue(ComboBox.SelectedValueProperty, "480p");
                var bandwidth = (TextBox)window.FindName("VodDownloadBandwidthLimitTextBox");
                bandwidth.SetCurrentValue(TextBox.TextProperty, "1.25");
                bandwidth.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();

                main.SelectedSettingsCategory = SettingsCategory.Accounts;
                LayoutStudioPolishWindow(window, new Size(1320, 820));
                var token = FindVisualDescendants<PasswordBox>((FrameworkElement)window.FindName("AccountsSettingsPage"))
                    .Single(control => AutomationProperties.GetName(control) == "Twitch OAuth token");
                token.Password = "autosave-test-token";
                Assert.Equal("autosave-test-token", settings.Chat.TwitchOAuthToken);

                await TestWait.UntilAsync(() => File.Exists(service.SettingsPath), TimeSpan.FromSeconds(4));
                var reloaded = await new JsonSettingsService(service.SettingsPath).LoadAsync();
                Assert.Equal(WindowCloseBehavior.MinimizeToTray, reloaded.CloseBehavior);
                Assert.Equal("720p", reloaded.DefaultQuality);
                Assert.Equal("480p", reloaded.Downloads.Quality);
                Assert.Equal(1.25, reloaded.Downloads.BandwidthLimitMegabytesPerSecond);
                Assert.Equal(false, reloaded.Replay.Enabled);
                Assert.Equal(false, reloaded.FollowedChannels.NotifyWhenLive);
                Assert.SequenceEqual(new[] { "fixture", "anotherfixture" }, reloaded.FollowedChannels.KickChannelSlugs);
                Assert.Equal("Ctrl+J", reloaded.Hotkeys.SkipBackward);
                Assert.Equal(75, reloaded.Hotkeys.SkipBackwardSeconds);
                Assert.True(reloaded.Updates.AutomaticDownloadsEnabled);
                Assert.Equal("--retry-streams 5", reloaded.CustomStreamlinkArguments);
                Assert.Equal(26d, reloaded.Chat.VlcOverlayFontSize);
                Assert.Equal(31d, reloaded.StreamVlcOverlayFontSizes[tab.Target.StateKey]);
                Assert.Equal("autosave-test-token", reloaded.Chat.TwitchOAuthToken);
                Assert.DoesNotContain("autosave-test-token", await File.ReadAllTextAsync(service.SettingsPath));
            }
            finally { window.Close(); }
        }
        finally { DeleteTempTestDirectory(root); }
    });

    private static Task AutoSaveSettingsDebounceAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = new AppSettings();
        var service = new AutoSaveRecordingSettingsService();
        await using var main = CreateAutoSaveMain(settings, service);
        for (var value = 10; value <= 80; value++)
        {
            settings.Hotkeys.SkipForwardSeconds = value;
            settings.CustomStreamlinkArguments = "--retry-streams " + value;
        }

        Assert.Equal(0, service.Snapshots.Count);
        await TestWait.UntilAsync(() => service.Snapshots.Count == 1, TimeSpan.FromSeconds(3));
        await Task.Delay(550);
        Assert.Equal(1, service.Snapshots.Count);
        Assert.Equal(80, service.Snapshots[0].Hotkeys.SkipForwardSeconds);
        Assert.Equal("--retry-streams 80", service.Snapshots[0].CustomStreamlinkArguments);
    });

    private static Task AutoSaveSettingsReplacementAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = new AppSettings();
        var oldChat = settings.Chat;
        var oldReplay = settings.Replay;
        var oldDownloads = settings.Downloads;
        var oldHotkeys = settings.Hotkeys;
        var oldFollowed = settings.FollowedChannels;
        var oldUpdates = settings.Updates;
        var service = new AutoSaveRecordingSettingsService();
        await using var main = CreateAutoSaveMain(settings, service);
        settings.Chat = new ChatSettings { ConnectAutomatically = false };
        settings.Replay = new ReplaySettings();
        settings.Downloads = new DownloadSettings();
        settings.Hotkeys = new HotkeySettings();
        settings.FollowedChannels = new FollowedChannelsSettings();
        settings.Updates = new UpdateSettings();
        await TestWait.UntilAsync(() => service.Snapshots.Count == 1, TimeSpan.FromSeconds(3));

        oldChat.FontSize = 32;
        oldReplay.Enabled = false;
        oldDownloads.Quality = "worst";
        oldDownloads.BandwidthLimitMegabytesPerSecond = 2;
        oldHotkeys.GoBack = "Ctrl+B";
        oldFollowed.NotifyWhenLive = false;
        oldUpdates.AutomaticChecksEnabled = false;
        await Task.Delay(550);
        Assert.Equal(1, service.Snapshots.Count);

        settings.Chat.FontSize = 24;
        settings.Replay.Enabled = false;
        settings.Downloads.Quality = "720p";
        settings.Downloads.BandwidthLimitMegabytesPerSecond = 0.5;
        settings.Hotkeys.GoBack = "Ctrl+B";
        settings.FollowedChannels.NotifyWhenLive = false;
        settings.Updates.AutomaticChecksEnabled = false;
        await TestWait.UntilAsync(() => service.Snapshots.Count == 2, TimeSpan.FromSeconds(3));
        var saved = service.Snapshots[1];
        Assert.Equal(24d, saved.Chat.FontSize);
        Assert.Equal(false, saved.Replay.Enabled);
        Assert.Equal("720p", saved.Downloads.Quality);
        Assert.Equal(0.5, saved.Downloads.BandwidthLimitMegabytesPerSecond);
        Assert.Equal("Ctrl+B", saved.Hotkeys.GoBack);
        Assert.Equal(false, saved.FollowedChannels.NotifyWhenLive);
        Assert.Equal(false, saved.Updates.AutomaticChecksEnabled);
    });

    private static Task AutoSaveSettingsDuringWriteAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = new AppSettings();
        var service = new AutoSaveRecordingSettingsService { HoldFirstSave = true };
        await using var main = CreateAutoSaveMain(settings, service);
        settings.DefaultQuality = "720p";
        try
        {
            await TestWait.UntilAsync(() => service.Snapshots.Count == 1, TimeSpan.FromSeconds(3));
            settings.DefaultQuality = "480p";
            settings.Hotkeys.SkipForwardSeconds = 90;
            await Task.Delay(550);
            Assert.Equal(1, service.Snapshots.Count);
            var shutdown = main.DisposeAsync().AsTask();
            Assert.Equal(false, shutdown.IsCompleted);
            service.ReleaseFirstSave.TrySetResult();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(2, service.Snapshots.Count);
            Assert.Equal(1, service.MaximumConcurrentSaves);
            Assert.Equal("480p", service.Snapshots[1].DefaultQuality);
            Assert.Equal(90, service.Snapshots[1].Hotkeys.SkipForwardSeconds);
            settings.DefaultQuality = "best";
            await Task.Delay(550);
            Assert.Equal(2, service.Snapshots.Count);
        }
        finally { service.ReleaseFirstSave.TrySetResult(); }
    });

    private static Task AutoSaveSettingsOnCloseAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = new AppSettings();
        var service = new AutoSaveRecordingSettingsService();
        await using var main = CreateAutoSaveMain(settings, service);
        main.IsSettingsOpen = true;
        main.KickFollowedChannelsText = "fixture";
        main.IsSettingsOpen = false;
        Assert.Equal(1, service.Snapshots.Count);
        Assert.SequenceEqual(new[] { "fixture" }, service.Snapshots[0].FollowedChannels.KickChannelSlugs);
        settings.Hotkeys.SkipBackwardSeconds = 120;
        await main.DisposeAsync();
        Assert.Equal(2, service.Snapshots.Count);
        Assert.Equal(120, service.Snapshots[1].Hotkeys.SkipBackwardSeconds);
    });

    private static MainViewModel CreateAutoSaveMain(AppSettings settings, ISettingsService service)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        return TestViewModels.CreateMain(settings, service,
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
            new MemoryLogger(), action => dispatcher.BeginInvoke(action));
    }

    private sealed class AutoSaveRecordingSettingsService : ISettingsService
    {
        private int concurrentSaves;
        public string SettingsPath => "memory";
        public List<AppSettings> Snapshots { get; } = [];
        public bool HoldFirstSave { get; init; }
        public int MaximumConcurrentSaves { get; private set; }
        public TaskCompletionSource ReleaseFirstSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshots.LastOrDefault() ?? new AppSettings());

        public async Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default)
        {
            MaximumConcurrentSaves = Math.Max(MaximumConcurrentSaves, ++concurrentSaves);
            try
            {
                Snapshots.Add(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(value))!);
                if (HoldFirstSave && Snapshots.Count == 1) await ReleaseFirstSave.Task;
            }
            finally { concurrentSaves--; }
        }
    }
}

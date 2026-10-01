using System.Windows.Threading;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> VodDownloadUiTests { get; } =
    [
        ("VOD downloads: download settings persist quality, bandwidth, and folders in wide and compact dark layouts", () => DownloadSettingsUiAsync(AppTheme.Dark)),
        ("VOD downloads: download settings persist quality, bandwidth, and folders in wide and compact light layouts", () => DownloadSettingsUiAsync(AppTheme.Light)),
        ("VOD downloads: download settings shortcut only appears on past broadcasts and downloads pages", DownloadSettingsButtonVisibilityAsync),
        ("VOD downloads: replacing download settings updates the shared limit and releases old preferences", DownloadSettingsReplacementAsync),
        ("VOD downloads: library rail paints only the selected destination's accent indicator across all themes", DownloadLibraryRailIndicatorAsync),
        ("VOD downloads: library navigation, quality, URL entry, offline actions, and compact UI render", DownloadLibraryUiAsync),
        ("VOD downloads: Twitch cards track real progress, cancellation, retry, offline playback, and deletion", () => DownloadCardLifecycleAsync(PlatformKind.Twitch)),
        ("VOD downloads: Kick cards track real progress, cancellation, retry, offline playback, and deletion", () => DownloadCardLifecycleAsync(PlatformKind.Kick)),
        ("VOD downloads: cards restore completed downloads after an offline restart", DownloadCardRestoreAsync),
        ("VOD download profile images: real Twitch tab avatars render after an offline restart", () => DownloadProfileImageUiAsync(PlatformKind.Twitch)),
        ("VOD download profile images: real Kick tab avatars render after an offline restart", () => DownloadProfileImageUiAsync(PlatformKind.Kick)),
        ("VOD downloads: card percentages never imply completion before files are committed", DownloadCardPresentationAsync),
        ("VOD downloads: card status matches platform and selected quality and follows URL downloads", DownloadCardBindingsAsync),
        ("VOD downloads: on-card buttons render all states and preserve playback hit targets in dark theme", () => DownloadCardVisualsAsync(AppTheme.Dark)),
        ("VOD downloads: on-card buttons render all states and preserve playback hit targets in light theme", () => DownloadCardVisualsAsync(AppTheme.Light))
    ];

    internal static IReadOnlyList<(string Name, Func<Task> Run)> VodDownloadNativeTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ? [] :
        [
            ("VOD downloads: real VLC plays, seeks, pauses, and resumes downloaded Twitch video and audio with internet denied", () => NativeOfflineDownloadAsync(PlatformKind.Twitch, false)),
            ("VOD downloads: real VLC plays, seeks, pauses, and resumes downloaded Kick video and audio with internet denied", () => NativeOfflineDownloadAsync(PlatformKind.Kick, false)),
            ("VOD downloads: real VLC plays and seeks AES-encrypted downloads using only local keys", () => NativeOfflineDownloadAsync(PlatformKind.Twitch, true)),
            ("VOD downloads: real VLC plays and seeks audio-only downloads without internet", NativeOfflineAudioDownloadAsync)
        ];

    private static Task DownloadLibraryRailIndicatorAsync() => WithStudioPolishWindowAsync((window, model, _) =>
    {
        var rail = (Border)window.FindName("LibraryRail");
        var root = (FrameworkElement)window.Content;
        var size = new Size(1320, 820);
        var destinations = new (string Name, ICommand Command)[]
        {
            ("following", model.ShowFollowedHomePageCommand),
            ("discover", model.ShowBrowseHomePageCommand),
            ("broadcasts", model.ShowTwitchVodsHomePageCommand),
            ("downloads", model.ShowDownloadsHomePageCommand),
            ("recent", model.ShowRecentHomePageCommand)
        };
        var buttons = destinations.Select(destination => FindVisualDescendants<Button>(rail)
            .Single(button => ReferenceEquals(button.Command, destination.Command))).ToArray();
        try
        {
            foreach (var theme in Enum.GetValues<AppTheme>())
            {
                StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(theme);
                foreach (var destination in destinations)
                {
                    destination.Command.Execute(null);
                    LayoutStudioPolishWindow(window, size);
                    if (theme is AppTheme.Dark or AppTheme.Light)
                        SaveResponsiveWindowImage(window, $"library-rail-{theme}-{destination.Name}");
                    var bitmap = WpfVisualTest.Render(root);
                    foreach (var button in buttons)
                    {
                        var selected = ReferenceEquals(button.Command, destination.Command);
                        var accent = WpfVisualTest.PaletteColor(button, "StudioAccentColor");
                        var expected = selected ? accent : Colors.Transparent.ToString();
                        WpfVisualTest.AssertSolidBrushColor(expected, button.BorderBrush);
                        var indicator = (Border)button.Template.FindName("RailIndicator", button);
                        WpfVisualTest.AssertSolidBrushColor(expected, indicator.Background);
                        Assert.True(indicator.ActualWidth >= 2 && indicator.ActualHeight >= 16,
                            "The rail selection must retain a visible accent indicator.");
                        var point = indicator.TransformToAncestor(root).Transform(
                            new Point(indicator.ActualWidth / 2, indicator.ActualHeight / 2));
                        var pixel = WpfVisualTest.PixelColor(bitmap, (int)Math.Floor(point.X), (int)Math.Floor(point.Y));
                        Assert.True(selected ? pixel == accent : pixel != accent,
                            $"The {button.Content} rail indicator must {(selected ? "paint" : "clear")} its accent when {destination.Name} is selected in {theme}; got {pixel}.");
                        if (selected)
                            WpfVisualTest.AssertSolidBrushColor(WpfVisualTest.PaletteColor(button, "StudioAccentTextColor"), button.Foreground);
                    }
                }
            }
        }
        finally
        {
            StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Dark);
        }
        return Task.CompletedTask;
    });

    private static Task DownloadLibraryUiAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(PlatformKind.Kick);
        var item = await fixture.DownloadAsync();
        var settings = VodResumeTestCatalog.Settings();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var permitDeletion = false;
        await using var model = new MainViewModel(new MainViewModelDependencies
        {
            Settings = settings,
            SettingsService = new FakeSettingsService(settings),
            StreamlinkService = fixture.Resolver,
            PlaybackFactory = new FakePlaybackEngineFactory(),
            ChatFactory = new FakeChatClientFactory(),
            Logger = new MemoryLogger(),
            Dispatch = action => dispatcher.BeginInvoke(action),
            VodDownloadService = fixture.Service,
            ConfirmDeleteVodDownload = _ => permitDeletion
        });
        model.Initialize();
        await TestWait.UntilAsync(() => model.VodDownloads.Count == 1, TimeSpan.FromSeconds(3));
        var window = new MainWindow { DataContext = model };
        RemoveMainWindowAutomaticStartup(window);
        var root = (FrameworkElement)window.Content;
        void Layout(double width)
        {
            root.Measure(new Size(width, 820));
            root.Arrange(new Rect(0, 0, width, 820));
            root.UpdateLayout();
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            root.UpdateLayout();
        }
        try
        {
            model.ShowDownloadsHomePageCommand.Execute(null);
            Layout(1320);
            Assert.True(model.IsDownloadsHomePageSelected);
            Assert.True(!model.IsFollowedHomePageSelected);
            var downloads = FindVisualDescendants<VodDownloadsView>(root).Single();
            Assert.Equal(Visibility.Visible, downloads.Visibility);
            var downloadForm = ((StackPanel)downloads.Content).Children.OfType<Border>().Single();
            var openFolder = FindVisualDescendants<Button>(downloads).Single(button => Equals(button.Content, "Open download folder"));
            var downloadSettings = FindVisualDescendants<Button>(downloads).Single(button => Equals(button.Content, "Download settings"));
            var downloadItems = (ItemsControl)downloads.FindName("DownloadsItemsControl");
            void AssertDownloadFolderButtonLayout()
            {
                var formBounds = downloadForm.TransformToAncestor(downloads).TransformBounds(new Rect(downloadForm.RenderSize));
                var buttonBounds = openFolder.TransformToAncestor(downloads).TransformBounds(new Rect(openFolder.RenderSize));
                var settingsBounds = downloadSettings.TransformToAncestor(downloads).TransformBounds(new Rect(downloadSettings.RenderSize));
                Assert.True(openFolder.ActualWidth > 0 && openFolder.ActualHeight > 0);
                Assert.True(formBounds.Contains(buttonBounds),
                    "The download folder button must render inside the Download a VOD box.");
                Assert.True(buttonBounds.Left >= settingsBounds.Right && Math.Abs(buttonBounds.Top - settingsBounds.Top) < 0.5,
                    "The download folder button must render beside Download settings.");
                Assert.True(openFolder.Parent is WrapPanel && ReferenceEquals(downloadSettings.Parent, openFolder.Parent),
                    "The download folder and settings buttons must share their action row.");
                Assert.True(ReferenceEquals(model.OpenDownloadFolderCommand, openFolder.Command));
                Assert.Equal(model.DownloadDirectory, openFolder.ToolTip as string);
            }
            AssertDownloadFolderButtonLayout();
            Assert.Equal(1, downloadItems.Items.Count);
            var watch = FindVisualDescendants<Button>(downloads).Single(button => Equals(button.Content, "Watch offline"));
            Assert.True(watch.Command.CanExecute(null));
            var originalQuality = model.SelectedQuality;
            model.SelectedVodDownloadQuality = "720p";
            Assert.Equal(originalQuality, model.SelectedQuality);
            SaveResponsiveWindowImage(window, "vod-downloads-wide");
            Layout(440);
            AssertDownloadFolderButtonLayout();
            Assert.Equal(Visibility.Visible, ((ToolBar)window.FindName("HomeNavigation")).Visibility);
            Assert.True(((ToolBar)window.FindName("HomeNavigation")).Items.OfType<Button>()
                .Any(button => Equals(button.Content, "Downloads") && button.Command.CanExecute(null)));
            Assert.True(((TextBox)downloads.FindName("VodDownloadUrlTextBox")).ActualWidth > 0);
            SaveResponsiveWindowImage(window, "vod-downloads-compact");
            model.ShowRecentHomePageCommand.Execute(null);
            Assert.True(!model.IsDownloadsHomePageSelected);
            model.GoBackCommand.Execute(null);
            Assert.True(model.IsDownloadsHomePageSelected);
            model.SelectedVodDownloadQuality = item.Quality;
            model.VodDownloadUrl = item.Target.Url;
            await model.DownloadVodUrlCommand.ExecuteAsync();
            Assert.Equal("", model.VodDownloadUrl);
            Assert.Equal(1, model.VodDownloads.Count);
            await model.VodDownloads[0].DeleteCommand.ExecuteAsync();
            Assert.True(File.Exists(item.LocalMediaPath));
            await model.ChangeDownloadDirectoryAsync(Path.Combine(fixture.Root, "new downloads"));
            await model.VodDownloads[0].PlayCommand.ExecuteAsync();
            Assert.True(model.SelectedTab!.Target.IsOfflineVod);
            Assert.Equal(item.Quality, model.SelectedTab.Quality);
            await Assert.ThrowsAsync<InvalidOperationException>(() => model.VodDownloads[0].DeleteCommand.ExecuteAsync());
            Assert.True(File.Exists(item.LocalMediaPath));
            await File.AppendAllTextAsync(item.LocalMediaPath, "\n#tampered\n");
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.GetOfflineTargetAsync(item.Id));
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(model.VodDownloads[0].CanRetry);
            Assert.Equal("", model.VodDownloads[0].Item.LocalMediaPath);
            await Assert.ThrowsAsync<InvalidOperationException>(() => model.VodDownloads[0].DeleteCommand.ExecuteAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => model.VodDownloads[0].RetryCommand.ExecuteAsync());
            await model.CloseSelectedCommand.ExecuteAsync();
            permitDeletion = true;
            await model.VodDownloads[0].DeleteCommand.ExecuteAsync();
            Assert.True(!File.Exists(item.LocalMediaPath));
            Assert.Equal(0, model.VodDownloads.Count);
        }
        finally { window.Close(); }
    });

    private static Task NativeOfflineDownloadAsync(PlatformKind platform, bool encrypted) => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(platform);
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-audio");
        var playlist = await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, "index.m3u8"));
        var segmentNames = playlist.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#')).ToArray();
        var key = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        for (var index = 0; index < segmentNames.Length; index++)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(fixtureDirectory, segmentNames[index]));
            if (encrypted)
            {
                using var cipher = Aes.Create();
                cipher.Key = key;
                var iv = new byte[16];
                BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8), index);
                bytes = cipher.EncryptCbc(bytes, iv, PaddingMode.PKCS7);
            }
            fixture.Handler.Put("/redirected/" + segmentNames[index], bytes);
        }
        if (encrypted)
        {
            playlist = playlist.Replace("#EXT-X-MEDIA-SEQUENCE:0", "#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-KEY:METHOD=AES-128,URI=\"local-key\"");
            fixture.Handler.Put("/redirected/local-key", key);
        }
        fixture.Handler.Put("/redirected/index.m3u8", Encoding.UTF8.GetBytes(playlist.TrimEnd() + "\n#EXT-X-ENDLIST\n"));
        var downloaded = await fixture.DownloadAsync();
        var target = await fixture.Service.GetOfflineTargetAsync(downloaded.Id);
        Assert.Equal(TimeSpan.FromSeconds(60), downloaded.Duration);
        fixture.Handler.NetworkAvailable = false;
        var requestCount = fixture.Handler.RequestCount;
        fixture.Resolver.ResolveStreamUrlOverride = (_, _) => throw new InvalidOperationException("Internet is disabled.");
        using var historyFiles = new VodResumeTestCatalog.HistoryFiles();
        var settings = VodResumeTestCatalog.Settings();
        settings.VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
        settings.StreamlinkPath = null;
        settings.VideoRendererMode = VideoRendererMode.Gdi;
        await using var gateway = new TwitchMutedVodPlaybackGateway(new MemoryLogger());
        var factory = new OfflineDownloadNativeFactory(settings.Chat, gateway);
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            await using (var tab = VodResumeTestCatalog.Tab(target, historyFiles.Create(), factory, streamlink: fixture.Resolver))
            {
                tab.SetVideoHandle(handle);
                tab.IsMuted = true;
                await tab.StartAsync(settings);
                Assert.Equal(PlaybackStatus.Playing, tab.Status);
                await TestWait.UntilAsync(() => factory.Engine!.TryGetPlaybackHealth(out var health) &&
                    health.DisplayedPictures > 0 && health.DecodedAudio > 0 &&
                    factory.Engine.TryGetPlaybackClock(out var clock) && clock.IsSeekable, TimeSpan.FromSeconds(8));
                await tab.SeekReplayAsync(TimeSpan.FromSeconds(35.25));
                Assert.Equal(PlaybackStatus.Playing, tab.Status);
                Assert.True(factory.Engine!.TryGetPlaybackClock(out var seeked));
                Assert.True(seeked.Position >= TimeSpan.FromSeconds(34) && seeked.Position < TimeSpan.FromSeconds(40));
                await tab.PauseOrResumeAsync();
                Assert.Equal(PlaybackStatus.Paused, tab.Status);
                Assert.True(factory.Engine.TryGetPlaybackClock(out var paused));
                await Task.Delay(300);
                Assert.True(factory.Engine.TryGetPlaybackClock(out var stillPaused));
                Assert.True((stillPaused.Position - paused.Position).Duration() < TimeSpan.FromMilliseconds(150));
                await tab.PauseOrResumeAsync();
                Assert.Equal(PlaybackStatus.Playing, tab.Status);
            }
            var bookmark = (await historyFiles.Create().GetAsync(target))!;
            Assert.True(bookmark.Position >= TimeSpan.FromSeconds(34));
            var reopenedFactory = new OfflineDownloadNativeFactory(settings.Chat, gateway);
            await using var reopened = VodResumeTestCatalog.Tab(target, historyFiles.Create(), reopenedFactory, streamlink: fixture.Resolver);
            reopened.SetVideoHandle(handle);
            reopened.IsMuted = true;
            await reopened.StartAsync(settings);
            Assert.Equal(PlaybackStatus.Playing, reopened.Status);
            await TestWait.UntilAsync(() => reopenedFactory.Engine!.TryGetPlaybackHealth(out var health) &&
                health.DisplayedPictures > 0 && health.DecodedAudio > 0, TimeSpan.FromSeconds(8));
            Assert.True(reopenedFactory.Engine!.TryGetPlaybackClock(out var restored));
            Console.WriteLine($"Offline {platform} encrypted={encrypted}: saved={bookmark.Position}, restored={restored.Position}, HTTP requests after disconnect={fixture.Handler.RequestCount - requestCount}");
            Assert.True(restored.Position >= bookmark.Position - TimeSpan.FromSeconds(1) && restored.Position <= bookmark.Position + TimeSpan.FromSeconds(3));
            Assert.Equal(requestCount, fixture.Handler.RequestCount);
            Assert.Equal(1, fixture.Resolver.ResolveStreamUrlCount);
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });

    private static Task NativeOfflineAudioDownloadAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(PlatformKind.Twitch);
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "playback-rate-tone");
        fixture.Handler.Put("/redirected/index.m3u8", await File.ReadAllBytesAsync(Path.Combine(directory, "index.m3u8")));
        fixture.Handler.Put("/redirected/tone.ts", await File.ReadAllBytesAsync(Path.Combine(directory, "tone.ts")));
        var queued = await fixture.Service.EnqueueAsync(new VodDownloadRequest(fixture.Target, "audio_only", fixture.Options));
        var downloaded = await fixture.WaitAsync(queued.Id, VodDownloadState.Completed);
        var target = await fixture.Service.GetOfflineTargetAsync(downloaded.Id);
        fixture.Handler.NetworkAvailable = false;
        var requestCount = fixture.Handler.RequestCount;
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var settings = VodResumeTestCatalog.Settings();
        settings.VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
        settings.StreamlinkPath = null;
        await using var gateway = new TwitchMutedVodPlaybackGateway(new MemoryLogger());
        var factory = new OfflineDownloadNativeFactory(settings.Chat, gateway);
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            await using var tab = VodResumeTestCatalog.Tab(target, files.Create(), factory, streamlink: fixture.Resolver);
            tab.Quality = "audio_only";
            tab.SetVideoHandle(handle);
            tab.IsMuted = true;
            await tab.StartAsync(settings);
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            await TestWait.UntilAsync(() => factory.Engine!.TryGetPlaybackHealth(out var health) && health.DecodedAudio > 0 &&
                factory.Engine.TryGetPlaybackClock(out var clock) && clock.IsSeekable, TimeSpan.FromSeconds(5));
            Assert.True(factory.Engine!.TryGetPlaybackHealth(out var decoded));
            Assert.Equal(0L, decoded.DisplayedPictures);
            await tab.SeekReplayAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            Assert.True(factory.Engine.TryGetPlaybackClock(out var seeked));
            Assert.True(seeked.Position >= TimeSpan.FromSeconds(2) && seeked.Position < TimeSpan.FromSeconds(5));
            await tab.PauseOrResumeAsync();
            Assert.Equal(PlaybackStatus.Paused, tab.Status);
            await tab.PauseOrResumeAsync();
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            Assert.Equal(requestCount, fixture.Handler.RequestCount);
            Assert.Equal("audio_only", fixture.Resolver.ResolveStreamUrlRequests.Single().Quality);
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });

    private sealed class OfflineDownloadNativeFactory(ChatSettings chat, IPlaybackMediaSourceGateway gateway) : IPlaybackEngineFactory
    {
        internal IPlaybackEngine? Engine { get; private set; }
        public async Task<IPlaybackEngine> CreateAsync(string vlcDirectory, bool enableNativeOverlay = true,
            string? nativeOverlayPositionStatePath = null, CancellationToken cancellationToken = default,
            VideoRendererMode rendererMode = VideoRendererMode.Automatic)
        {
            Engine = await new LibVlcPlaybackEngineFactory(new MemoryLogger(), chat, gateway).CreateAsync(vlcDirectory,
                enableNativeOverlay: false, cancellationToken: cancellationToken, rendererMode: rendererMode);
            return Engine;
        }
    }
}

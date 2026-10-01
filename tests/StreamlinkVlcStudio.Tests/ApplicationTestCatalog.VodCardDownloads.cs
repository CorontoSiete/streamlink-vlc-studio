using System.Windows.Automation;
using System.Windows.Threading;

internal static partial class ApplicationTestCatalog
{
    private static Task DownloadCardLifecycleAsync(PlatformKind platform) => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(platform);
        var entered = fixture.BlockSegment();
        var dispatcher = Dispatcher.CurrentDispatcher;
        await using var model = CreateDownloadCardsMain(fixture.Service, [fixture.Target], action => dispatcher.BeginInvoke(action));
        model.Initialize();
        if (platform == PlatformKind.Kick) model.SelectKickVodPlatformCommand.Execute(null);
        await model.SearchTwitchVodsCommand.ExecuteAsync();
        var card = model.TwitchVods.Single();
        Assert.True(card.Download is null);
        var playbackQuality = model.SelectedQuality;
        await card.DownloadCommand.ExecuteAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await TestWait.UntilAsync(() => card.Download?.ProgressPercentage == 50, TimeSpan.FromSeconds(3));
        Assert.True(ReferenceEquals(card.Download, model.VodDownloads.Single()));
        Assert.Equal("50%", card.Download!.CardButtonText);
        Assert.Equal(false, card.Download.IsIndeterminate);
        Assert.Equal(false, card.DownloadCommand.CanExecute(null));
        Assert.Equal(0, model.Tabs.Count);
        await card.DownloadCommand.ExecuteAsync();
        Assert.Equal(1, model.VodDownloads.Count);
        var id = card.Download.Id;

        await card.Download.CancelCommand.ExecuteAsync();
        await TestWait.UntilAsync(() => card.Download.CanRetry, TimeSpan.FromSeconds(3));
        Assert.Equal("Retry", card.Download.CardButtonText);
        Assert.True(card.DownloadCommand.CanExecute(null));
        fixture.Handler.Override = null;
        await card.DownloadCommand.ExecuteAsync();
        await fixture.WaitAsync(id, VodDownloadState.Completed);
        await TestWait.UntilAsync(() => card.Download.CanPlay, TimeSpan.FromSeconds(3));
        Assert.Equal(id, card.Download.Id);
        Assert.Equal(1, model.VodDownloads.Count);
        Assert.Equal("Downloaded", card.Download.CardButtonText);
        Assert.Contains("Click to watch offline", card.Download.CardToolTip);
        Assert.Equal(playbackQuality, model.SelectedQuality);

        fixture.Handler.NetworkAvailable = false;
        var requestCount = fixture.Handler.RequestCount;
        await card.DownloadCommand.ExecuteAsync();
        Assert.True(model.SelectedTab!.Target.IsOfflineVod);
        Assert.Equal(card.Download.Item.LocalMediaPath, model.SelectedTab.Target.LocalMediaPath);
        Assert.Equal(card.Download.Item.Quality, model.SelectedTab.Quality);
        Assert.Equal(requestCount, fixture.Handler.RequestCount);
        await model.CloseSelectedCommand.ExecuteAsync();
        await card.Download.DeleteCommand.ExecuteAsync();
        Assert.True(card.Download is null);
        Assert.True(card.DownloadCommand.CanExecute(null));
        Assert.Equal(0, model.VodDownloads.Count);
        Assert.Equal(false, Directory.Exists(Path.Combine(fixture.Library, id.ToString("N"))));
    });

    private static Task DownloadCardRestoreAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(PlatformKind.Kick);
        var downloaded = await fixture.DownloadAsync();
        await fixture.RestartAsync();
        fixture.Handler.NetworkAvailable = false;
        var requestCount = fixture.Handler.RequestCount;
        var dispatcher = Dispatcher.CurrentDispatcher;
        await using var model = CreateDownloadCardsMain(fixture.Service, [fixture.Target], action => dispatcher.BeginInvoke(action));
        model.Initialize();
        await TestWait.UntilAsync(() => model.VodDownloads.Count == 1, TimeSpan.FromSeconds(3));
        model.SelectKickVodPlatformCommand.Execute(null);
        await model.SearchTwitchVodsCommand.ExecuteAsync();
        var card = model.TwitchVods.Single();
        Assert.Equal(downloaded.Id, card.Download!.Id);
        Assert.Equal("Downloaded", card.Download.CardButtonText);
        await model.SearchTwitchVodsCommand.ExecuteAsync();
        Assert.Equal(downloaded.Id, model.TwitchVods.Single().Download!.Id);
        Assert.Equal(requestCount, fixture.Handler.RequestCount);
    });

    private static Task DownloadCardPresentationAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var item = new VodDownloadItem(Guid.NewGuid(), DownloadCardTarget(1), "best", DateTimeOffset.UtcNow);
        var download = new VodDownloadViewModel(item, (execute, enabled) => new AsyncRelayCommand(execute, enabled),
            _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask);
        var changed = new HashSet<string>();
        download.PropertyChanged += (_, arguments) => changed.Add(arguments.PropertyName!);
        Assert.Equal("Queued", download.CardButtonText);
        Assert.True(download.IsIndeterminate);
        download.Update(item = item with { State = VodDownloadState.Resolving, Revision = 1 });
        Assert.Equal("Preparing…", download.CardButtonText);
        download.Update(item = item with { State = VodDownloadState.Downloading, Revision = 2 });
        Assert.Equal("Downloading…", download.CardButtonText);
        Assert.True(download.IsIndeterminate);
        download.Update(item = item with { CompletedSegments = 998, TotalSegments = 999, Revision = 3 });
        Assert.Equal("99%", download.CardButtonText);
        Assert.Equal(false, download.IsIndeterminate);
        download.Update(item = item with { CompletedSegments = 999, Revision = 4 });
        Assert.Equal("Finishing…", download.CardButtonText);
        Assert.True(download.IsIndeterminate);
        Assert.Equal(false, download.CanPlay);
        download.Update(item = item with { State = VodDownloadState.Completed, LocalMediaPath = "offline.m3u8", Revision = 5 });
        Assert.Equal("Downloaded", download.CardButtonText);
        Assert.True(download.CanPlay);
        Assert.Equal(false, download.IsIndeterminate);
        download.Update(item with { State = VodDownloadState.Downloading, CompletedSegments = 1, Revision = 2 });
        Assert.Equal("Downloaded", download.CardButtonText);
        foreach (var state in new[] { VodDownloadState.Failed, VodDownloadState.Canceled, VodDownloadState.Interrupted })
        {
            download.Update(item = item with { State = state, Error = "Fixture failure", Revision = item.Revision + 1 });
            Assert.Equal("Retry", download.CardButtonText);
            Assert.Equal("Retry VOD download", download.CardActionName);
            Assert.Contains("Fixture failure", download.CardToolTip);
            Assert.True(download.CanRetry);
        }
        foreach (var property in new[] { nameof(download.CardButtonText), nameof(download.CardActionName), nameof(download.CardToolTip) })
            Assert.True(changed.Contains(property));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new VodDownloadViewModel(item with { State = VodDownloadState.Completed },
            (execute, enabled) => new AsyncRelayCommand(execute, enabled), _ => release.Task,
            _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask);
        var vod = new VodViewModel(new TwitchVodItem("101", "stream", "broadcaster", "streamer", "Streamer", "VOD", "",
            "https://www.twitch.tv/videos/101", "", null, null, TimeSpan.FromHours(2), 100, TwitchVodTypeFilter.Archive),
            (_, _) => Task.CompletedTask, card => new AsyncRelayCommand(() => Task.CompletedTask,
                () => card.Download?.PlayCommand.CanExecute(null) == true));
        vod.UpdateDownload(ready);
        var commandNotifications = 0;
        vod.DownloadCommand.CanExecuteChanged += (_, _) => commandNotifications++;
        var playing = ready.PlayCommand.ExecuteAsync();
        Assert.Equal(false, vod.DownloadCommand.CanExecute(null));
        Assert.True(commandNotifications > 0);
        release.SetResult();
        await playing;
        Assert.True(vod.DownloadCommand.CanExecute(null));
        Assert.True(commandNotifications > 1);
    });

    private static Task DownloadCardBindingsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var target = DownloadCardTarget(1);
        var other = DownloadCardTarget(2);
        var completed = new VodDownloadItem(Guid.NewGuid(), target, "best", DateTimeOffset.UtcNow,
            VodDownloadState.Completed, 100, 100, LocalMediaPath: "offline.m3u8", Revision: 5);
        var active = completed with
        {
            Id = Guid.NewGuid(),
            Quality = "720p",
            State = VodDownloadState.Downloading,
            CompletedSegments = 42,
            LocalMediaPath = ""
        };
        var differentPlatform = completed with
        {
            Id = Guid.NewGuid(),
            Target = target with
            {
                Platform = PlatformKind.Kick,
                Kind = StreamTargetKind.KickVod,
                MediaId = other.MediaId
            }
        };
        var service = new CardDownloadTestService(completed, active, differentPlatform);
        var dispatcher = Dispatcher.CurrentDispatcher;
        await using var model = CreateDownloadCardsMain(service, [target, other], action => dispatcher.BeginInvoke(action));
        model.Initialize();
        await TestWait.UntilAsync(() => model.VodDownloads.Count == 3, TimeSpan.FromSeconds(3));
        await model.SearchTwitchVodsCommand.ExecuteAsync();
        var first = model.TwitchVods[0];
        var second = model.TwitchVods[1];
        Assert.Equal(completed.Id, first.Download!.Id);
        Assert.True(second.Download is null);
        var playbackQuality = model.SelectedQuality;
        model.SelectedVodDownloadQuality = "720p";
        Assert.Equal(active.Id, first.Download!.Id);
        Assert.Equal("42%", first.Download.CardButtonText);
        Assert.Equal(false, first.DownloadCommand.CanExecute(null));
        Assert.Equal(playbackQuality, model.SelectedQuality);
        model.SelectedVodDownloadQuality = "best";
        Assert.Equal(completed.Id, first.Download!.Id);
        await Task.Run(() => service.Publish(completed with { State = VodDownloadState.Downloading, Revision = 1 }));
        dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal("Downloaded", first.Download.CardButtonText);
        model.VodDownloadUrl = other.Url;
        await model.DownloadVodUrlCommand.ExecuteAsync();
        Assert.Equal("Queued", second.Download!.CardButtonText);
        Assert.Equal(false, second.DownloadCommand.CanExecute(null));
        Assert.Equal(1, service.Requests.Count);
        await second.DownloadCommand.ExecuteAsync();
        Assert.Equal(1, service.Requests.Count);
        await second.Download.CancelCommand.ExecuteAsync();
        await TestWait.UntilAsync(() => second.Download.CanRetry, TimeSpan.FromSeconds(3));
        await second.DownloadCommand.ExecuteAsync();
        Assert.Equal(second.Download.Id, service.RetriedIds.Single());
        await TestWait.UntilAsync(() => second.Download.CanCancel, TimeSpan.FromSeconds(3));
        await first.Download.DeleteCommand.ExecuteAsync();
        Assert.True(first.Download is null);
        await Task.Run(() => service.Publish(completed));
        dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.True(first.Download is null);
        await model.DisposeAsync();
        Assert.Equal(0, service.SubscriberCount);
    });

    private static Task DownloadCardVisualsAsync(AppTheme theme) => TestSta.RunOffscreenAsync(async () =>
    {
        var targets = Enumerable.Range(1, 6).Select(DownloadCardTarget).ToArray();
        var states = new[] { VodDownloadState.Downloading, VodDownloadState.Completed, VodDownloadState.Resolving,
            VodDownloadState.Failed, VodDownloadState.Downloading };
        var entries = states.Select((state, index) => new VodDownloadItem(Guid.NewGuid(), targets[index + 1], "best",
            DateTimeOffset.UtcNow, state, index == 4 ? 100 : 42, state == VodDownloadState.Resolving ? 0 : 100,
            BytesDownloaded: 25 * 1024 * 1024, Error: state == VodDownloadState.Failed ? "The connection was interrupted." : "",
            LocalMediaPath: state == VodDownloadState.Completed ? "offline.m3u8" : "", Revision: 1)).ToArray();
        var service = new CardDownloadTestService(entries);
        var dispatcher = Dispatcher.CurrentDispatcher;
        await using var model = CreateDownloadCardsMain(service, targets, action => dispatcher.BeginInvoke(action));
        model.Initialize();
        await TestWait.UntilAsync(() => model.VodDownloads.Count == entries.Length, TimeSpan.FromSeconds(3));
        await model.SearchTwitchVodsCommand.ExecuteAsync();
        model.TwitchVods[2].UpdateWatchProgress(new VodPlaybackBookmark(TimeSpan.FromHours(2), TimeSpan.FromHours(2), DateTimeOffset.UtcNow, true));
        StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(theme);
        var owner = new MainWindow { DataContext = model };
        RemoveMainWindowAutomaticStartup(owner);
        try
        {
            var cards = (ItemsControl)owner.FindName("VodCardsItemsControl");
            ((Panel)cards.Parent).Children.Remove(cards);
            var host = new Border
            {
                DataContext = model,
                Resources = owner.Resources,
                Child = cards,
                Background = WpfVisualTest.PaletteBrush(owner, "StudioSurface0Brush")
            };
            using var renderingSource = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("VOD card offscreen render")
            { Width = 1020, Height = 900, WindowStyle = 0 })
            { RootVisual = host, SizeToContent = SizeToContent.WidthAndHeight };
            void Layout(double width)
            {
                host.Width = width;
                dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                host.Measure(new Size(width, double.PositiveInfinity));
                host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
                host.UpdateLayout();
            }
            Button DownloadButton(VodViewModel card) => FindVisualDescendants<Button>(host)
                .Single(button => ReferenceEquals(button.Command, card.DownloadCommand));
            ProgressBar Progress(Button button) => (ProgressBar)button.Template.FindName("DownloadProgress", button);
            Layout(1020);
            Assert.True(host.IsVisible);
            var expected = new[] { "Download", "42%", "Downloaded", "Preparing…", "Retry", "Finishing…" };
            for (var index = 0; index < expected.Length; index++)
            {
                var card = model.TwitchVods[index];
                var button = DownloadButton(card);
                Assert.Equal(expected[index], button.Content.ToString()!);
                Assert.Equal(card.Download?.CanCancel != true, button.IsEnabled);
                Assert.True(button.Focusable && button.FocusVisualStyle is not null);
                Assert.Equal(button.ToolTip.ToString()!, AutomationProperties.GetHelpText(button));
                Assert.Equal(expected[index], AutomationProperties.GetItemStatus(button));
                Assert.Equal(card.Download?.CardActionName ?? "Download VOD for offline playback", AutomationProperties.GetName(button));
                Assert.Equal(false, MainWindow.TryResolveHomeStreamOpenAndStayOnHomeCommand(button, out _));
            }
            var progress = Progress(DownloadButton(model.TwitchVods[1]));
            Assert.Equal(42d, progress.Value);
            Assert.Equal(false, progress.IsIndeterminate);
            var track = (FrameworkElement)progress.Template.FindName("PART_Track", progress);
            var fill = (FrameworkElement)progress.Template.FindName("PART_Indicator", progress);
            Assert.True(Math.Abs(fill.ActualWidth - track.ActualWidth * 0.42) < 1);
            var preparing = Progress(DownloadButton(model.TwitchVods[3]));
            Assert.True(preparing.IsIndeterminate);
            var glow = (FrameworkElement)preparing.Template.FindName("PART_GlowRect", preparing);
            Assert.Equal(Visibility.Visible, glow.Visibility);
            Assert.True(DependencyPropertyHelper.GetValueSource(glow, FrameworkElement.MarginProperty).IsAnimated);
            Assert.True(Progress(DownloadButton(model.TwitchVods[5])).IsIndeterminate);
            Assert.Equal(Visibility.Collapsed, Progress(DownloadButton(model.TwitchVods[2])).Visibility);
            AssertCardGeometry();
            SaveDownloadCardImage(host, $"vod-card-downloads-{theme}-wide");
            Layout(340);
            AssertCardGeometry();
            SaveDownloadCardImage(host, $"vod-card-downloads-{theme}-compact");

            await Task.Run(() => service.Publish(entries[0] with { CompletedSegments = 73, Revision = 2 }));
            Layout(340);
            Assert.Equal("73%", DownloadButton(model.TwitchVods[1]).Content.ToString()!);
            Assert.Equal(73d, Progress(DownloadButton(model.TwitchVods[1])).Value);
            var first = model.TwitchVods[0];
            service.EnqueueGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.EnqueueFailure = new IOException("The disk is full.");
            var start = first.DownloadCommand.ExecuteAsync();
            Layout(340);
            Assert.True(first.IsDownloadStarting);
            Assert.Equal("Queuing…", DownloadButton(first).Content.ToString()!);
            Assert.True(Progress(DownloadButton(first)).IsIndeterminate);
            Assert.Equal(false, DownloadButton(first).IsEnabled);
            service.EnqueueGate.SetResult();
            await Assert.ThrowsAsync<IOException>(() => start);
            Layout(340);
            Assert.Equal(false, first.IsDownloadStarting);
            Assert.Equal("Download", DownloadButton(first).Content.ToString()!);
            Assert.True(DownloadButton(first).IsEnabled);

            void AssertCardGeometry()
            {
                foreach (var card in model.TwitchVods)
                {
                    var button = DownloadButton(card);
                    var thumbnail = FindVisualDescendants<RoundedClipBorder>(host)
                        .Single(border => ReferenceEquals(border.DataContext, card) && border.Parent is AspectRatioDecorator);
                    var bounds = button.TransformToVisual(thumbnail).TransformBounds(new Rect(button.RenderSize));
                    Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= thumbnail.ActualWidth && bounds.Bottom <= thumbnail.ActualHeight);
                    var hit = VisualTreeHelper.HitTest(host, button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), host));
                    Assert.True(ReferenceEquals(button, DownloadCardHitButton(hit!.VisualHit)));
                    var mediaHit = VisualTreeHelper.HitTest(host, thumbnail.TranslatePoint(new Point(thumbnail.ActualWidth / 2, thumbnail.ActualHeight / 2), host));
                    Assert.True(ReferenceEquals(card.OpenCommand, DownloadCardHitButton(mediaHit!.VisualHit)!.Command));
                    if (card.IsWatched)
                    {
                        var badge = FindVisualDescendants<Border>(thumbnail).Single(border => AutomationProperties.GetName(border) == "Watched VOD");
                        var badgeBounds = badge.TransformToVisual(thumbnail).TransformBounds(new Rect(badge.RenderSize));
                        Assert.True(badgeBounds.Right < bounds.Left);
                    }
                }
            }
        }
        finally
        {
            owner.Close();
            StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Dark);
        }
    });

    private static Button? DownloadCardHitButton(DependencyObject? visual)
    {
        while (visual is not null)
        {
            if (visual is Button button) return button;
            visual = VisualTreeHelper.GetParent(visual);
        }
        return null;
    }

    private static void SaveDownloadCardImage(FrameworkElement host, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SVS_TEST_ARTIFACT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(WpfVisualTest.Render(host)));
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }

    private static StreamTarget DownloadCardTarget(int index) => VodDownloadUrlParser.Parse($"https://www.twitch.tv/videos/{100 + index}") with
    { Channel = "streamer", DisplayTitle = $"Recorded broadcast {index}", MediaDuration = TimeSpan.FromHours(2) };

    private static MainViewModel CreateDownloadCardsMain(IVodDownloadService service, IReadOnlyList<StreamTarget> targets, Action<Action> dispatch)
    {
        var settings = VodResumeTestCatalog.Settings();
        var model = new MainViewModel(new MainViewModelDependencies
        {
            Settings = settings,
            SettingsService = new FakeSettingsService(settings),
            StreamlinkService = new FakeStreamlinkService(),
            PlaybackFactory = new FakePlaybackEngineFactory(),
            ChatFactory = new FakeChatClientFactory(),
            Logger = new MemoryLogger(),
            Dispatch = dispatch,
            VodDownloadService = service,
            ConfirmDeleteVodDownload = _ => true,
            TwitchVodSearchDebounceInterval = TimeSpan.FromHours(1),
            TwitchVodService = new FakeTwitchVodService(new TwitchVodSearchResult(TwitchVodSearchStatus.Available, null,
                targets.Where(target => target.Platform == PlatformKind.Twitch).Select(target => new TwitchVodItem(target.MediaId,
                    "recorded", "broadcaster", target.Channel, "Streamer", target.DisplayTitle, "", target.Url, "", null, null,
                    TimeSpan.FromHours(2), 100, TwitchVodTypeFilter.Archive, TwitchVodAccessKind.Public,
                    target.ProfileImageUrl)).ToArray(), "", "Fixture")),
            KickVodService = new FakeKickVodService(new KickVodSearchResult(KickVodSearchStatus.Available,
                targets.Where(target => target.Platform == PlatformKind.Kick).Select(target => new KickVodItem(target.MediaId,
                    "recorded", target.MediaId, target.Channel, "Streamer", target.DisplayTitle, target.Url, target.Url, "", "",
                    null, null, TimeSpan.FromHours(2), 100, ProfileImageUrl: target.ProfileImageUrl)).ToArray(), "", "Fixture"))
        });
        model.TwitchVodSearchText = "streamer";
        return model;
    }

    private sealed class CardDownloadTestService(params VodDownloadItem[] initial) : IVodDownloadService
    {
        private readonly Dictionary<Guid, VodDownloadItem> entries = initial.ToDictionary(item => item.Id);
        public string DownloadDirectory { get; private set; } = Path.GetTempPath();
        internal long BandwidthLimit { get; private set; }
        public void SetBandwidthLimit(long bytesPerSecond) => BandwidthLimit = bytesPerSecond;
        public Task ChangeDownloadDirectoryAsync(string downloadDirectory, CancellationToken cancellationToken = default)
        {
            DownloadDirectory = Path.GetFullPath(downloadDirectory);
            return Task.CompletedTask;
        }
        public event Action<VodDownloadItem>? DownloadChanged;
        internal int SubscriberCount => DownloadChanged?.GetInvocationList().Length ?? 0;
        internal List<VodDownloadRequest> Requests { get; } = [];
        internal List<Guid> RetriedIds { get; } = [];
        internal TaskCompletionSource? EnqueueGate { get; set; }
        internal Exception? EnqueueFailure { get; set; }
        public Task<IReadOnlyList<VodDownloadItem>> GetDownloadsAsync(CancellationToken cancellationToken = default)
        {
            lock (entries) return Task.FromResult<IReadOnlyList<VodDownloadItem>>(entries.Values.OrderByDescending(item => item.CreatedAtUtc).ToArray());
        }
        internal void Publish(VodDownloadItem item)
        {
            lock (entries)
                if (!entries.TryGetValue(item.Id, out var previous) || item.Revision >= previous.Revision) entries[item.Id] = item;
            DownloadChanged?.Invoke(item);
        }
        public async Task<VodDownloadItem> EnqueueAsync(VodDownloadRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (EnqueueGate is not null) await EnqueueGate.Task.WaitAsync(cancellationToken);
            if (EnqueueFailure is not null) throw EnqueueFailure;
            var item = new VodDownloadItem(Guid.NewGuid(), request.Target, request.Quality, DateTimeOffset.UtcNow);
            Publish(item);
            return item;
        }
        public Task CancelAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var item = entries[id];
            Publish(item with { State = VodDownloadState.Canceled, Revision = item.Revision + 1 });
            return Task.CompletedTask;
        }
        public Task RetryAsync(Guid id, VodDownloadOptions options, CancellationToken cancellationToken = default)
        {
            RetriedIds.Add(id);
            var item = entries[id];
            Publish(item with { State = VodDownloadState.Queued, CompletedSegments = 0, TotalSegments = 0, Revision = item.Revision + 1 });
            return Task.CompletedTask;
        }
        public Task<StreamTarget> GetOfflineTargetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(entries[id].OfflineTarget);
        public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
        {
            lock (entries) entries.Remove(id);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

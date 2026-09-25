internal static class RecentRefreshResourceTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("recent refresh resources: repeated status refreshes retain cards commands and thumbnails", PreserveAsync),
        ("recent refresh resources: fast channels update before slow channels with one settings save", ProgressiveAsync),
        ("recent refresh resources: deletion preserves completed previews while settings wait for the batch", KeepProgressAsync),
        ("recent refresh resources: provider failures retain metadata and status-only refreshes avoid saves", FailureAsync),
        ("recent refresh resources: reconciliation preserves order identity and platform distinctions", ReconcileAsync),
        ("recent refresh resources: changed settings notify retained rows and commands use current metadata", UpdateAsync),
        ("recent refresh resources: deleting during refresh retains survivors and rejects deleted results", DeleteAsync),
        ("recent refresh resources: refresh preserves a newer watch time and user-selected quality", PreserveWatchAsync),
        ("recent refresh resources: shutdown rejects late responses and does not start queued channels", ShutdownAsync)
    ];

    private static Task PreserveAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = Settings(50);
        var storage = new FakeSettingsService(settings);
        var service = new MetadataService((target, _) => Task.FromResult(Metadata(target.Channel)));
        await using var viewModel = Create(settings, service, storage);
        var cards = viewModel.RecentStreams.ToArray();
        var commands = cards.Select(card => (card.OpenCommand, card.OpenAndStayOnHomeCommand, card.DeleteCommand)).ToArray();
        var changes = 0;
        var imageChanges = 0;
        viewModel.RecentStreams.CollectionChanged += (_, _) => changes++;
        foreach (var card in cards)
            card.PropertyChanged += (_, e) => imageChanges += e.PropertyName == nameof(RecentStreamViewModel.ThumbnailUrl) ? 1 : 0;
        for (var iteration = 0; iteration < 5; iteration++) await RefreshAsync(viewModel);
        Console.WriteLine($"Recent fixture: 50 cards, 5 refreshes, {changes} collection notifications, {imageChanges} thumbnail notifications.");
        Assert.SequenceEqual(cards, viewModel.RecentStreams);
        Assert.SequenceEqual(commands, viewModel.RecentStreams.Select(card => (card.OpenCommand, card.OpenAndStayOnHomeCommand, card.DeleteCommand)));
        Assert.Equal(0, changes);
        Assert.Equal(0, imageChanges);
        Assert.Equal(0, storage.SaveCount);
        Assert.Equal(250, service.CallCount);
        Assert.True(cards.All(card => card.LiveStatusText == "Live"));
    });

    private static Task ProgressiveAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = Settings(2);
        var originalSettings = settings.RecentStreams;
        var storage = new FakeSettingsService(settings);
        var slow = new TaskCompletionSource<StreamMetadataResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new MetadataService((target, token) => target.Channel == "channel1"
            ? slow.Task.WaitAsync(token) : Task.FromResult(Metadata("fast", "updated") with { ThumbnailUrl = "//example.invalid/fast.jpg" }));
        await using var viewModel = Create(settings, service, storage);
        var card = viewModel.RecentStreams[0];
        var refresh = RefreshAsync(viewModel);
        try
        {
            await TestWait.UntilAsync(() => viewModel.RecentStreams[0].LiveStatusText == "Live", TimeSpan.FromSeconds(2));
            Assert.Equal(false, refresh.IsCompleted);
            Assert.True(ReferenceEquals(card, viewModel.RecentStreams[0]));
            Assert.Equal("fast", card.DisplayName);
            Assert.Equal("https://example.invalid/fast.jpg", card.ThumbnailUrl);
            Assert.Equal("updated", card.CategoryName);
            Assert.Equal("Checking", viewModel.RecentStreams[1].LiveStatusText);
            Assert.Equal(0, storage.SaveCount);
            Assert.Equal("channel0", settings.RecentStreams[0].DisplayName);
        }
        finally
        {
            slow.TrySetResult(Metadata("slow", "updated"));
            await refresh;
        }
        Assert.Equal(1, storage.SaveCount);
        Assert.Equal("channel0", originalSettings[0].DisplayName);
        Assert.SequenceEqual(new[] { "fast", "slow" }, viewModel.RecentStreams.Select(item => item.DisplayName));
    });

    private static Task KeepProgressAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = Settings(3);
        var storage = new FakeSettingsService(settings);
        var slow = new TaskCompletionSource<StreamMetadataResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new MetadataService((target, token) => target.Channel == "channel0"
            ? Task.FromResult(Metadata("fast")) : slow.Task.WaitAsync(token));
        await using var viewModel = Create(settings, service, storage);
        var refresh = RefreshAsync(viewModel);
        try
        {
            await TestWait.UntilAsync(() => viewModel.RecentStreams[0].DisplayName == "fast", TimeSpan.FromSeconds(2));
            var card = viewModel.RecentStreams[0];
            await viewModel.RecentStreams[1].DeleteCommand.ExecuteAsync();
            Assert.True(ReferenceEquals(card, viewModel.RecentStreams[0]));
            Assert.Equal("fast", card.DisplayName);
            Assert.Equal("https://example.invalid/fast.jpg", card.ThumbnailUrl);
            Assert.Equal("channel0", settings.RecentStreams[0].DisplayName);
            Assert.Equal(1, storage.SaveCount);
        }
        finally
        {
            slow.TrySetResult(Metadata("slow"));
            await refresh;
        }
        Assert.Equal(2, storage.SaveCount); // One removal and one completed metadata batch.
        Assert.SequenceEqual(new[] { "channel0", "channel2" }, viewModel.RecentStreams.Select(card => card.Channel));
        Assert.Equal("fast", settings.RecentStreams[0].DisplayName);
    });

    private static Task FailureAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = Settings(3);
        var storage = new FakeSettingsService(settings);
        var service = new MetadataService((target, _) => target.Channel switch
        {
            "channel0" => Task.FromResult(new StreamMetadataResult(StreamMetadataState.Offline, "", "", "offline")),
            "channel1" => Task.FromResult(new StreamMetadataResult(StreamMetadataState.Unavailable, "", "", "try again")),
            _ => Task.FromException<StreamMetadataResult>(new IOException("provider failed"))
        });
        await using var viewModel = Create(settings, service, storage);
        var cards = viewModel.RecentStreams.ToArray();
        await RefreshAsync(viewModel);
        Assert.SequenceEqual(cards, viewModel.RecentStreams);
        Assert.SequenceEqual(new[] { "Offline", "Unknown", "Unknown" }, cards.Select(card => card.LiveStatusText));
        Assert.True(cards.All(card => card.HasThumbnail && card.CategoryName == "original"));
        Assert.Equal(0, storage.SaveCount);
        viewModel.ShowRecentHomePageCommand.Execute(null);
        Assert.Equal(5, service.CallCount); // Only unavailable channels retry on a fresh revisit.
    });

    private static Task ReconcileAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = Settings(3);
        await using var viewModel = Create(settings);
        var first = viewModel.RecentStreams[0];
        var second = viewModel.RecentStreams[1];
        var changes = new List<NotifyCollectionChangedAction>();
        viewModel.RecentStreams.CollectionChanged += (_, e) => changes.Add(e.Action);
        settings.RecentStreams =
        [
            Recent("CHANNEL1"), Recent("channel0"), Recent("channel0", PlatformKind.Kick),
            Recent("CHANNEL0"), Recent("new")
        ];
        Rebuild(viewModel);
        Assert.Equal(4, viewModel.RecentStreams.Count);
        Assert.True(ReferenceEquals(second, viewModel.RecentStreams[0]));
        Assert.True(ReferenceEquals(first, viewModel.RecentStreams[1]));
        Assert.Equal(PlatformKind.Kick, viewModel.RecentStreams[2].Platform);
        Assert.Equal("new", viewModel.RecentStreams[3].Channel);
        Assert.Equal(false, changes.Contains(NotifyCollectionChangedAction.Reset));
        changes.Clear();
        Rebuild(viewModel);
        Assert.Equal(0, changes.Count);
        settings.RecentStreams.Clear();
        Rebuild(viewModel);
        Assert.Equal(0, viewModel.RecentStreams.Count);
        Assert.Equal(false, changes.Contains(NotifyCollectionChangedAction.Reset));
    });

    private static Task UpdateAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = Settings(1);
        await using var viewModel = Create(settings);
        var card = viewModel.RecentStreams.Single();
        var changes = new List<string?>();
        card.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        // Settings are mutable: a retained row must snapshot its previous values.
        var data = settings.RecentStreams[0];
        data.DisplayName = "Renamed";
        data.CategoryName = "Updated category";
        data.ThumbnailUrl = "";
        data.LastWatchedAtUtc = DateTimeOffset.UtcNow;
        data.LastQuality = "480p";
        Rebuild(viewModel);
        Assert.True(ReferenceEquals(card, viewModel.RecentStreams.Single()));
        foreach (var property in new[] { "DisplayName", "DeleteToolTip", "CategoryName", "ThumbnailUrl", "HasThumbnail", "LastWatchedText", "MetadataText", "Target" })
            Assert.True(changes.Contains(property), $"Missing notification: {property}");
        Assert.Equal("Remove Renamed from recent streams", card.DeleteToolTip);
        Assert.Equal(false, card.HasThumbnail);
        Assert.True(card.MetadataText.Contains("480p", StringComparison.Ordinal));
        changes.Clear();
        Rebuild(viewModel);
        Assert.Equal(0, changes.Count);
        await card.OpenAndStayOnHomeCommand.ExecuteAsync();
        Assert.Equal("Updated category", viewModel.Tabs.Single().Target.CategoryName);
        await card.DeleteCommand.ExecuteAsync();
        Assert.Equal(0, viewModel.RecentStreams.Count);
    });

    private static Task DeleteAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = Settings(2);
        var release = new TaskCompletionSource<StreamMetadataResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new MetadataService((_, token) => release.Task.WaitAsync(token));
        await using var viewModel = Create(settings, service);
        var survivor = viewModel.RecentStreams[1];
        var refresh = RefreshAsync(viewModel);
        try
        {
            await viewModel.RecentStreams[0].DeleteCommand.ExecuteAsync();
            Assert.True(ReferenceEquals(survivor, viewModel.RecentStreams.Single()));
        }
        finally
        {
            release.TrySetResult(Metadata("updated"));
            await refresh;
        }
        Assert.Equal(1, settings.RecentStreams.Count);
        Assert.True(ReferenceEquals(survivor, viewModel.RecentStreams.Single()));
        Assert.Equal("channel1", survivor.Channel);
        Assert.Equal("updated", survivor.DisplayName);
    });

    private static Task PreserveWatchAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = Settings(2);
        var release = new TaskCompletionSource<StreamMetadataResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewModel = Create(settings, new MetadataService((_, token) => release.Task.WaitAsync(token)));
        var card = viewModel.RecentStreams[1];
        var refresh = RefreshAsync(viewModel);
        var watchedAt = DateTimeOffset.UtcNow;
        try
        {
            var watched = settings.RecentStreams[1];
            watched.LastWatchedAtUtc = watchedAt;
            watched.LastQuality = "720p";
            settings.RecentStreams.Reverse();
            Rebuild(viewModel);
        }
        finally
        {
            release.TrySetResult(Metadata("updated"));
            await refresh;
        }
        Assert.True(ReferenceEquals(card, viewModel.RecentStreams[0]));
        Assert.Equal(watchedAt, settings.RecentStreams[0].LastWatchedAtUtc);
        Assert.Equal("720p", settings.RecentStreams[0].LastQuality);
        Assert.True(card.MetadataText.Contains("720p", StringComparison.Ordinal));
    });

    private static Task ShutdownAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = Settings(9);
        var storage = new FakeSettingsService(settings);
        var release = new TaskCompletionSource<StreamMetadataResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new MetadataService((_, _) => release.Task); // Ignore cancellation to exercise late completion.
        await using var viewModel = Create(settings, service, storage);
        viewModel.ShowRecentHomePageCommand.Execute(null);
        Assert.Equal(4, service.CallCount);
        var disposal = viewModel.DisposeAsync().AsTask();
        release.SetResult(Metadata("late"));
        await disposal;
        Assert.Equal(4, service.CallCount);
        Assert.Equal(0, storage.SaveCount);
        Assert.True(viewModel.RecentStreams.All(card => card.DisplayName != "late"));
    });

    private static Task RefreshAsync(MainViewModel viewModel) => (Task)typeof(MainViewModel)
        .GetMethod("RefreshRecentThumbnailsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(viewModel, [((CancellationTokenSource)typeof(MainViewModel)
            .GetField("recentThumbnailRefreshCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel)!).Token, true])!;

    private static void Rebuild(MainViewModel viewModel) => typeof(MainViewModel)
        .GetMethod("RebuildRecentStreams", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, null);

    private static MainViewModel Create(AppSettings settings, IStreamMetadataService? metadata = null, FakeSettingsService? storage = null) =>
        TestViewModels.CreateMain(settings, storage ?? new FakeSettingsService(settings), new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(), action => action(),
            streamMetadataService: metadata, recentThumbnailRefreshInterval: TimeSpan.FromHours(1));

    private static AppSettings Settings(int count) => new()
    {
        RecentStreams = Enumerable.Range(0, count).Select(index => Recent($"channel{index}")).ToList()
    };

    private static RecentStreamSettings Recent(string channel, PlatformKind platform = PlatformKind.Twitch) => new()
    {
        Platform = platform,
        Channel = channel,
        DisplayName = channel,
        Url = $"https://{(platform == PlatformKind.Twitch ? "www.twitch.tv" : "kick.com")}/{channel}",
        ThumbnailUrl = $"https://example.invalid/{channel}.jpg",
        CategoryName = "original"
    };

    private static StreamMetadataResult Metadata(string name, string category = "original") =>
        new(StreamMetadataState.Available, $"https://example.invalid/{name}.jpg", name, "live", category);

    private sealed class MetadataService(Func<StreamTarget, CancellationToken, Task<StreamMetadataResult>> respond) : IStreamMetadataService
    {
        public int CallCount { get; private set; }

        public Task<StreamMetadataResult> GetLiveStreamMetadataAsync(StreamTarget target, AppSettings settings, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return respond(target, cancellationToken);
        }
    }
}

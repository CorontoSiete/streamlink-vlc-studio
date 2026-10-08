internal static partial class CodeCleanupTestCatalog
{
    private static async Task BrowseNavigationCancellationFailureAsync()
    {
        await using var main = CreateCleanupMain();
        var browse = (BrowseViewModel)GetPrivateField(main, "browse")!;
        using var cancellation = new CancellationTokenSource();
        SetPrivateField(browse, "browseCategoryViewerCountCancellation", cancellation);
        SetPrivateField(browse, "browseCategoryViewerCountLoadPending", true);
        browse.BrowseCategories.Add(CreateCleanupCategory());
        using var registration = cancellation.Token.Register(() =>
            throw new IOException("Injected browse cancellation failure."));

        try
        {
            browse.BrowseCategorySearchText = "replacement";

            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(0, browse.BrowseCategories.Count);
            Assert.Equal<object?>(null, GetPrivateField(browse, "browseCategoryViewerCountCancellation"));
            Assert.Equal(false, GetPrivateField(browse, "browseCategoryViewerCountLoadPending"));
        }
        finally
        {
            registration.Dispose();
            browse.CancelActiveBrowseCategoryViewerCountLoad();
        }
    }

    private static async Task BrowseCancellationReentryAsync()
    {
        await using var main = CreateCleanupMain();
        var browse = (BrowseViewModel)GetPrivateField(main, "browse")!;
        using var cancellation = new CancellationTokenSource();
        SetPrivateField(browse, "browseCategoryViewerCountCancellation", cancellation);
        Task? reentry = null;
        var callbackFinished = false;
        using var registration = cancellation.Token.Register(() =>
        {
            reentry = Task.Run(browse.CancelActiveBrowseCategoryViewerCountLoad);
            callbackFinished = reentry.Wait(TimeSpan.FromSeconds(1));
        });

        browse.CancelActiveBrowseCategoryViewerCountLoad();
        await reentry!.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(callbackFinished, "Reentrant browse cancellation was blocked by the callback's state lock.");
        Assert.Equal<object?>(null, GetPrivateField(browse, "browseCategoryViewerCountCancellation"));
    }

    private static async Task BrowseLateViewerCountsAsync()
    {
        var service = new FakeBrowseService();
        await using var main = CreateCleanupMain(service);
        var browse = (BrowseViewModel)GetPrivateField(main, "browse")!;
        browse.BrowseCategories.Add(CreateCleanupCategory());
        await browse.DisposeAsync();

        browse.StartBrowseCategoryViewerCountLoad(PlatformKind.Twitch, "");

        Assert.Equal(0, service.CategoryViewerCountRequests.Count);
        Assert.Equal<object?>(null, GetPrivateField(browse, "browseCategoryViewerCountCancellation"));
    }

    private static async Task FollowedCancellationFailureAsync()
    {
        var main = CreateCleanupMain();
        var followed = (FollowedChannelsViewModel)GetPrivateField(main, "followed")!;
        var cancellation = (CancellationTokenSource)GetPrivateField(followed, "followedChannelsRefreshCancellation")!;
        using var registration = cancellation.Token.Register(() =>
            throw new IOException("Injected followed-channel cancellation failure."));
        try
        {
            await followed.DisposeAsync();

            Assert.Equal<object?>(null, GetPrivateField(followed, "observedFollowedChannelsSettings"));
            Assert.Throws<ObjectDisposedException>(() => _ = cancellation.Token);
        }
        finally
        {
            registration.Dispose();
            try { await main.DisposeAsync(); }
            catch (AggregateException) { }
        }
    }

    private static async Task RecentCancellationFailureAsync()
    {
        var main = CreateCleanupMain(streamMetadataService: new FakeStreamMetadataService(
            new StreamMetadataResult(StreamMetadataState.Unavailable, "", "", "Unavailable")));
        var recent = (RecentStreamsViewModel)GetPrivateField(main, "recent")!;
        var cancellation = (CancellationTokenSource)GetPrivateField(recent, "recentThumbnailRefreshCancellation")!;
        recent.EnsureRecentThumbnailRefreshTimerStarted();
        var timer = (System.Threading.Timer)GetPrivateField(recent, "recentThumbnailRefreshTimer")!;
        using var registration = cancellation.Token.Register(() =>
            throw new IOException("Injected recent-stream cancellation failure."));
        try
        {
            await recent.DisposeAsync();

            Assert.Equal<object?>(null, GetPrivateField(recent, "recentThumbnailRefreshTimer"));
            Assert.Throws<ObjectDisposedException>(() => _ = cancellation.Token);
            var notifications = 0;
            recent.PropertyChanged += (_, _) => notifications++;
            recent.RecentStreams.Clear();
            Assert.Equal(0, notifications);
        }
        finally
        {
            registration.Dispose();
            timer.Dispose();
            try { await main.DisposeAsync(); }
            catch (AggregateException) { }
        }
    }

    private static BrowseCategoryViewModel CreateCleanupCategory() =>
        new(new BrowseCategory(PlatformKind.Twitch, "1", "Category", "", []), _ => Task.CompletedTask);
}

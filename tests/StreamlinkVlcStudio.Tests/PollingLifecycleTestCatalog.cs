internal static class PollingLifecycleTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("polling lifecycle: replacement retains the old token until its request finishes", () => ReplaceViewerPollAsync(checkToken: true)),
        ("polling lifecycle: late replaced requests cannot overwrite current viewers", () => ReplaceViewerPollAsync(checkToken: false)),
        ("polling lifecycle: queued viewer updates are ignored after polling stops", () => QueuedViewerResultAsync(apply: false)),
        ("polling lifecycle: viewer metadata applies in one dispatcher callback", () => QueuedViewerResultAsync(apply: true))
    ];

    private static async Task ReplaceViewerPollAsync(bool checkToken)
    {
        var firstRequest = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ViewerCountResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = new FakeViewerCountService
        {
            ResponderAsync = (_, token) =>
            {
                if (Interlocked.Increment(ref calls) != 1)
                    return Task.FromResult(new ViewerCountResult(ViewerCountState.Available, 222, "current"));
                firstRequest.SetResult(token);
                return release.Task; // Simulate an HTTP handler that completes despite cancellation.
            }
        };
        await using var tab = CreateTab(service, action => action());
        tab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(tab.ViewerCountText) && tab.ViewerCountText == "222")
                currentApplied.TrySetResult();
        };
        Start(tab);
        var oldTask = (Task)typeof(StreamTabViewModel).GetField("viewerCountPollingTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;
        var token = await firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            Start(tab);
            await currentApplied.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(token.IsCancellationRequested);
            if (checkToken)
            {
                Assert.True(token.WaitHandle.WaitOne(0));
                Assert.Equal(false, tab.PlaybackCleanupIdleTask.IsCompleted);
            }
        }
        finally
        {
            release.TrySetResult(new ViewerCountResult(ViewerCountState.Available, 111, "stale"));
            await oldTask.WaitAsync(TimeSpan.FromSeconds(3));
        }
        await tab.PlaybackCleanupIdleTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("222", tab.ViewerCountText);
        if (checkToken) Assert.Throws<ObjectDisposedException>(() => _ = token.WaitHandle);
    }

    private static async Task QueuedViewerResultAsync(bool apply)
    {
        var queued = new ConcurrentQueue<Action>();
        var resultQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeViewerCountService
        {
            Responder = _ => new ViewerCountResult(ViewerCountState.Available, 222, "queued", "stale category", "stale title")
        };
        await using var tab = CreateTab(service, action => { queued.Enqueue(action); resultQueued.TrySetResult(); });
        while (queued.TryDequeue(out var initial)) initial();
        resultQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialViewerText = tab.ViewerCountText;
        Start(tab);
        await resultQueued.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (apply)
        {
            Assert.True(queued.TryDequeue(out var queuedUpdate));
            queuedUpdate!();
            Assert.Equal("222", tab.ViewerCountText);
            Assert.Equal("stale category", tab.CategoryName);
            Assert.Equal("stale title", tab.StreamTitle);
            Assert.True(queued.IsEmpty);
            return;
        }
        await (Task)typeof(StreamTabViewModel).GetMethod("StopViewerCountPollingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tab, null)!;
        while (queued.TryDequeue(out var update)) update();
        Assert.Equal(initialViewerText, tab.ViewerCountText);
        Assert.Equal("", tab.CategoryName);
        Assert.Equal("", tab.StreamTitle);
    }

    private static StreamTabViewModel CreateTab(IViewerCountService service, Action<Action> dispatch) =>
        TestViewModels.CreateTab(new StreamTarget(PlatformKind.Twitch, "streamer", "https://www.twitch.tv/streamer"),
            "best", new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
            new MemoryLogger(), dispatch, viewerCountService: service);

    private static void Start(StreamTabViewModel tab) =>
        typeof(StreamTabViewModel).GetMethod("StartViewerCountPolling", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(tab, [new AppSettings()]);
}

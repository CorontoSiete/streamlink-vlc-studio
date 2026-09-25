internal static class BackgroundWorkflowResourceTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("background workflow resources: manual Followed refresh shares startup work and allows later refresh", ShareFollowedAsync),
        ("background workflow resources: synchronous Followed callbacks share the published operation", ReentrantFollowedAsync),
        ("background workflow resources: changed Followed settings replace queued work and reject old results", ReplaceFollowedAsync),
        ("background workflow resources: account changes require a new Followed request", ChangeAccountAsync),
        ("background workflow resources: failed Followed refresh preserves cards and permits retry", RetryFollowedAsync),
        ("background workflow resources: shutdown cancels shared Followed work and prevents restart", StopFollowedAsync),
        ("background workflow resources: a log burst uses one UI callback and preserves the latest 250 lines", BatchLogsAsync),
        ("background workflow resources: concurrent log producers share one pending UI callback", ConcurrentLogsAsync),
        ("background workflow resources: log arrivals during delivery are drained in order", LogsDuringDeliveryAsync),
        ("background workflow resources: failed log dispatch permits the next delivery", RetryLogDispatchAsync),
        ("background workflow resources: rejected log dispatch permits the next delivery", RetryRejectedLogDispatchAsync),
        ("background workflow resources: queued log callbacks do not update a disposed view model", StopLogsAsync)
    ];

    private static Task ShareFollowedAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new ControlledFollowedService();
        await using var viewModel = Create(followed: service);
        viewModel.Initialize();
        var refresh = viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.Equal(1, service.Requests.Count);
        Assert.Equal(false, refresh.IsCompleted);
        service.Requests[0].Complete("first");
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal(1, service.Requests.Count);
        await refresh;
        Assert.Equal("first", viewModel.LiveFollowedChannels.Single().Channel);
        refresh = viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.Equal(2, service.Requests.Count);
        service.Requests[1].Complete("second");
        await refresh;
        Assert.Equal("second", viewModel.LiveFollowedChannels.Single().Channel);
    });

    private static Task ReplaceFollowedAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new ControlledFollowedService();
        await using var viewModel = Create(followed: service);
        var published = new List<string>();
        viewModel.LiveFollowedChannels.CollectionChanged += (_, _) =>
            published.AddRange(viewModel.LiveFollowedChannels.Select(card => card.Channel));
        viewModel.Initialize();
        viewModel.KickFollowedChannelsText = "intermediate";
        await viewModel.SaveSettingsCommand.ExecuteAsync();
        viewModel.KickFollowedChannelsText = "latest";
        await viewModel.SaveSettingsCommand.ExecuteAsync();
        var refresh = viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.Equal(1, service.Requests.Count);
        service.Requests[0].Complete("obsolete");
        await TestWait.UntilAsync(() => service.Requests.Count == 2, TimeSpan.FromSeconds(2));
        Assert.SequenceEqual(new[] { "latest" }, service.Requests[1].Slugs);
        Assert.Equal(false, published.Contains("obsolete"));
        service.Requests[1].Complete("current");
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal(2, service.Requests.Count);
        await refresh;
        Assert.Equal("current", viewModel.LiveFollowedChannels.Single().Channel);
        Assert.Equal(false, viewModel.IsFollowedChannelsRefreshing);
    });

    private static Task ReentrantFollowedAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new FakeFollowedStreamsService();
        await using var viewModel = Create(followed: service);
        Task? refresh = null;
        service.EnqueueResult(_ =>
        {
            refresh = viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
            return Task.FromResult(new FollowedLiveStreamsResult([], []));
        });
        viewModel.Initialize();
        Assert.True(refresh is not null);
        await refresh!;
        Assert.Equal(1, service.CallCount);
        Assert.Equal(false, viewModel.IsFollowedChannelsRefreshing);
    });

    private static Task ChangeAccountAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new ControlledFollowedService();
        await using var viewModel = Create(followed: service);
        viewModel.Settings.Chat.TwitchOAuthToken = "old-account";
        viewModel.Initialize();
        await viewModel.ClearTwitchTokenCommand.ExecuteAsync();
        var refresh = viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        service.Requests[0].Complete("old-account-stream");
        await TestWait.UntilAsync(() => service.Requests.Count == 2, TimeSpan.FromSeconds(2));
        Assert.Equal("", service.Requests[1].TwitchToken);
        Assert.Equal(0, viewModel.LiveFollowedChannels.Count);
        service.Requests[1].Complete("new-account-stream");
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal(2, service.Requests.Count);
        await refresh;
    });

    private static Task RetryFollowedAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new ControlledFollowedService();
        await using var viewModel = Create(followed: service);
        var refresh = viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        service.Requests[0].Complete("kept");
        await refresh;
        var original = viewModel.LiveFollowedChannels.Single();
        viewModel.Initialize();
        refresh = viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        service.Requests[1].Completion.SetException(new HttpRequestException("temporary failure"));
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal(2, service.Requests.Count);
        await refresh;
        Assert.True(ReferenceEquals(original, viewModel.LiveFollowedChannels.Single()));
        Assert.Equal(false, viewModel.IsFollowedChannelsRefreshing);
        Assert.Equal("temporary failure", viewModel.FollowedChannelsStatus);
        refresh = viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        Assert.Equal(3, service.Requests.Count);
        service.Requests[2].Complete("recovered");
        await refresh;
        Assert.Equal("recovered", viewModel.LiveFollowedChannels.Single().Channel);
    });

    private static Task StopFollowedAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new ControlledFollowedService();
        var viewModel = Create(followed: service);
        viewModel.Initialize();
        var refresh = viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        await viewModel.DisposeAsync();
        await refresh;
        Assert.True(service.Requests[0].Token.IsCancellationRequested);
        await viewModel.RefreshFollowedChannelsCommand.ExecuteAsync();
        viewModel.Initialize();
        Assert.Equal(1, service.Requests.Count);
        Assert.Equal(0, viewModel.LiveFollowedChannels.Count);
    });

    private static async Task BatchLogsAsync()
    {
        var callbacks = new Queue<Action>();
        var logger = new MemoryLogger();
        await using var viewModel = Create(logger: logger, dispatch: callbacks.Enqueue);
        viewModel.Initialize();
        for (var index = 0; index < 10_000; index++) logger.Write(AppLogLevel.Info, "Burst", $"entry-{index}");
        Assert.Equal(1, callbacks.Count);
        Assert.Equal(0, viewModel.AppLogLines.Count);
        callbacks.Dequeue()();
        Assert.Equal(0, callbacks.Count);
        Assert.Equal(10_000, logger.Entries.Count);
        Assert.SequenceEqual(logger.Entries.TakeLast(250).Select(Format), viewModel.AppLogLines);
        logger.Write(AppLogLevel.Warning, "Next", "after burst");
        Assert.Equal(1, callbacks.Count);
        callbacks.Dequeue()();
        Assert.SequenceEqual(logger.Entries.TakeLast(250).Select(Format), viewModel.AppLogLines);
    }

    private static async Task LogsDuringDeliveryAsync()
    {
        var callbacks = new Queue<Action>();
        var logger = new MemoryLogger();
        await using var viewModel = Create(logger: logger, dispatch: callbacks.Enqueue);
        viewModel.Initialize();
        var injected = false;
        viewModel.AppLogLines.CollectionChanged += (_, _) =>
        {
            if (injected) return;
            injected = true;
            logger.Write(AppLogLevel.Warning, "During", "third");
        };
        logger.Write(AppLogLevel.Info, "Before", "first");
        logger.Write(AppLogLevel.Info, "Before", "second");
        Assert.Equal(1, callbacks.Count);
        callbacks.Dequeue()();
        Assert.Equal(1, callbacks.Count);
        callbacks.Dequeue()();
        Assert.SequenceEqual(logger.Entries.Select(Format), viewModel.AppLogLines);
    }

    private static async Task ConcurrentLogsAsync()
    {
        var callbacks = new ConcurrentQueue<Action>();
        var logger = new MemoryLogger();
        await using var viewModel = Create(logger: logger, dispatch: callbacks.Enqueue);
        viewModel.Initialize();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(producer => Task.Run(() =>
        {
            for (var index = 0; index < 1_000; index++)
                logger.Write(AppLogLevel.Info, $"Producer-{producer}", $"entry-{index}");
        })));
        for (var index = 0; index < 250; index++) logger.Write(AppLogLevel.Info, "Tail", $"entry-{index}");
        Assert.Equal(1, callbacks.Count);
        Assert.True(callbacks.TryDequeue(out var callback));
        callback!();
        Assert.Equal(0, callbacks.Count);
        Assert.Equal(8_250, logger.Entries.Count);
        Assert.SequenceEqual(logger.Entries.TakeLast(250).Select(Format), viewModel.AppLogLines);
    }

    private static async Task RetryLogDispatchAsync()
    {
        var callbacks = new Queue<Action>();
        var logger = new MemoryLogger();
        var fail = true;
        await using var viewModel = Create(logger: logger, dispatch: action =>
        {
            if (fail) throw new InvalidOperationException("dispatcher unavailable");
            callbacks.Enqueue(action);
        });
        viewModel.Initialize();
        try { logger.Write(AppLogLevel.Info, "Test", "retained"); }
        catch (InvalidOperationException) { }
        fail = false;
        logger.Write(AppLogLevel.Info, "Test", "retried");
        Assert.Equal(1, callbacks.Count);
        callbacks.Dequeue()();
        Assert.SequenceEqual(logger.Entries.Select(Format), viewModel.AppLogLines);
    }

    private static async Task RetryRejectedLogDispatchAsync()
    {
        var callbacks = new Queue<Action>();
        var logger = new MemoryLogger();
        var accept = false;
        await using var viewModel = Create(logger: logger, dispatch: callbacks.Enqueue,
            tryDispatch: action =>
            {
                if (!accept) return false;
                callbacks.Enqueue(action);
                return true;
            });
        viewModel.Initialize();
        logger.Write(AppLogLevel.Info, "Test", "retained");
        Assert.Equal(0, callbacks.Count);
        accept = true;
        logger.Write(AppLogLevel.Info, "Test", "retried");
        Assert.Equal(1, callbacks.Count);
        callbacks.Dequeue()();
        Assert.SequenceEqual(logger.Entries.Select(Format), viewModel.AppLogLines);
    }

    private static async Task StopLogsAsync()
    {
        var callbacks = new Queue<Action>();
        var logger = new MemoryLogger();
        var viewModel = Create(logger: logger, dispatch: callbacks.Enqueue);
        viewModel.Initialize();
        logger.Write(AppLogLevel.Info, "Test", "queued");
        await viewModel.DisposeAsync();
        while (callbacks.TryDequeue(out var callback)) callback();
        Assert.Equal(0, viewModel.AppLogLines.Count);
        viewModel.Initialize();
        logger.Write(AppLogLevel.Info, "Test", "after disposal");
        Assert.Equal(0, callbacks.Count);
    }

    private static string Format(LogEntry entry) =>
        $"{entry.Timestamp:HH:mm:ss} [{entry.Level}] {entry.Source}: {entry.Message}";

    private static MainViewModel Create(IFollowedStreamsService? followed = null, IAppLogger? logger = null,
        Action<Action>? dispatch = null, Func<Action, bool>? tryDispatch = null)
    {
        var settings = new AppSettings();
        settings.Chat.ConnectAutomatically = false;
        return TestViewModels.CreateMain(settings, new FakeSettingsService(settings), new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory(), logger ?? new MemoryLogger(),
            dispatch ?? (action => action()), followedStreamsService: followed,
            followedChannelsRefreshInterval: TimeSpan.Zero, tryDispatch: tryDispatch);
    }

    private sealed class ControlledFollowedService : IFollowedStreamsService
    {
        internal List<PendingRequest> Requests { get; } = [];

        public Task<FollowedLiveStreamsResult> GetLiveFollowedStreamsAsync(AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            var request = new PendingRequest(settings.FollowedChannels.KickChannelSlugs.ToArray(),
                settings.Chat.TwitchOAuthToken, cancellationToken);
            Requests.Add(request);
            return request.Completion.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed record PendingRequest(string[] Slugs, string TwitchToken, CancellationToken Token)
    {
        internal TaskCompletionSource<FollowedLiveStreamsResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Complete(string channel) => Completion.SetResult(new FollowedLiveStreamsResult(
            [new FollowedLiveStream(PlatformKind.Twitch, channel, channel, "title", "category", 100,
                "", null, null, "en", $"https://www.twitch.tv/{channel}")], []));
    }
}

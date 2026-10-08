
internal static class VodChatLifecycleTestCatalog
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(6);

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("VOD chat lifecycle: a provider timeout is retried", ProviderTimeoutAsync),
        ("VOD chat lifecycle: a stale completed page cannot stop a seek", StaleCompletionAsync),
        ("VOD chat lifecycle: a stale unsupported result cannot undo promotion", StalePromotionAsync),
        ("VOD chat lifecycle: seeking into evicted history refetches messages", RefetchEvictedHistoryAsync),
        ("VOD chat lifecycle: seeking inside retained history reuses messages", ReuseRetainedHistoryAsync),
        ("VOD chat lifecycle: unsupported history preserves captured messages after eviction", PreserveUnsupportedHistoryAsync),
        ("VOD chat lifecycle: disposal drains a stopped fetch", () => DrainRetiredFetchAsync(replace: false)),
        ("VOD chat lifecycle: disposal drains a replaced fetch", () => DrainRetiredFetchAsync(replace: true)),
        ("VOD chat lifecycle: concurrent disposal shares completion", ConcurrentDisposalAsync),
        ("VOD chat reliability: stop drains a fetch after a cancellation callback fails", () => CancellationFailureAsync(CancellationTransition.Stop)),
        ("VOD chat reliability: replacement drains a fetch after a cancellation callback fails", () => CancellationFailureAsync(CancellationTransition.Replace)),
        ("VOD chat reliability: disposal drains a fetch after a cancellation callback fails", () => CancellationFailureAsync(CancellationTransition.Dispose)),
        ("VOD chat reliability: provider exceptions retry when diagnostics fail", () => RetryAfterDiagnosticFailureAsync(() => throw new IOException("Provider unavailable."))),
        ("VOD chat reliability: provider timeouts retry when diagnostics fail", () => RetryAfterDiagnosticFailureAsync(() => throw new TaskCanceledException("Provider deadline elapsed."))),
        ("VOD chat reliability: unsuccessful pages retry when diagnostics fail", () => RetryAfterDiagnosticFailureAsync(() => VodChatFetchResult.Failed("Provider unavailable."))),
        ("VOD chat reliability: growing chat keeps polling when diagnostics fail", GrowingTailDiagnosticFailureAsync),
        ("VOD chat reliability: duration fallback and captured messages survive failed diagnostics", DurationDiagnosticFailureAsync),
        ("VOD chat reliability: same replay metadata refresh releases buffered live messages", RefreshReplayMetadataAsync)
    ];

    private static async Task ProviderTimeoutAsync()
    {
        var retried = Completion();
        var calls = 0;
        var provider = new CallbackProvider((_, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new TaskCanceledException("The provider's HTTP deadline expired.");
            }

            retried.TrySetResult();
            return Task.FromResult(VodChatFetchResult.Completed([], TimeSpan.FromHours(1)));
        });
        await using var controller = CreateController(provider);
        Start(controller, Replay());
        await retried.Task.WaitAsync(TestTimeout);
        await controller.WaitUntilCaughtUpAsync().WaitAsync(TestTimeout);
        Assert.Equal(2, calls);
    }

    private static async Task StaleCompletionAsync()
    {
        var firstStarted = Completion();
        var firstResult = new TaskCompletionSource<VodChatFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondOffset = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var provider = new CallbackProvider((_, offset, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult();
                return firstResult.Task.WaitAsync(token);
            }

            secondOffset.TrySetResult(offset);
            return Task.FromResult(VodChatFetchResult.Completed([], TimeSpan.FromHours(1)));
        });
        await using var controller = CreateController(provider);
        var replay = Replay();
        Start(controller, replay);
        await firstStarted.Task.WaitAsync(TestTimeout);
        Start(controller, replay, TimeSpan.FromMinutes(10));
        firstResult.SetResult(VodChatFetchResult.Completed([], TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromMinutes(9.5), await secondOffset.Task.WaitAsync(TestTimeout));
    }

    private static async Task StalePromotionAsync()
    {
        var firstStarted = Completion();
        var firstResult = new TaskCompletionSource<VodChatFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestedReplay = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new CallbackProvider((replay, _, token) =>
        {
            if (replay.ReplayId == "live-dvr-1")
            {
                firstStarted.TrySetResult();
                return firstResult.Task.WaitAsync(token);
            }

            requestedReplay.TrySetResult(replay.ReplayId);
            return Task.FromResult(VodChatFetchResult.Completed([], TimeSpan.FromHours(1)));
        });
        await using var controller = CreateController(provider);
        Start(controller, Replay("live-dvr-1"));
        await firstStarted.Task.WaitAsync(TestTimeout);
        controller.Promote(Replay("123"));
        firstResult.SetResult(VodChatFetchResult.Unsupported("No published comments yet."));
        Assert.Equal("123", await requestedReplay.Task.WaitAsync(TestTimeout));
        await controller.WaitUntilCaughtUpAsync().WaitAsync(TestTimeout);
        Assert.Equal(false, controller.TryTakeNotice(out _));
    }

    private static async Task RefetchEvictedHistoryAsync()
    {
        var calls = 0;
        var original = CapturedMessage(0);
        await using var controller = CreateController(new CallbackProvider((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(VodChatFetchResult.Completed(
                [new VodChatMessage(TimeSpan.Zero, original)], TimeSpan.FromHours(1)));
        }));
        var replay = Replay();
        Start(controller, replay);
        await controller.WaitUntilCaughtUpAsync().WaitAsync(TestTimeout);
        FillCapturedHistory(controller);

        Start(controller, replay);
        await controller.WaitUntilCaughtUpAsync().WaitAsync(TestTimeout);

        Assert.Equal(2, calls);
        Assert.SequenceEqual(new[] { original.MessageId },
            controller.TakeMessagesDueAt(TimeSpan.Zero, 100).Select(message => message.MessageId));
    }

    private static async Task ReuseRetainedHistoryAsync()
    {
        var calls = 0;
        await using var controller = CreateController(new CallbackProvider((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(VodChatFetchResult.Completed([], TimeSpan.FromHours(1)));
        }));
        var replay = Replay();
        Start(controller, replay);
        await controller.WaitUntilCaughtUpAsync().WaitAsync(TestTimeout);
        FillCapturedHistory(controller);

        Start(controller, replay, TimeSpan.FromSeconds(150));
        await controller.WaitUntilCaughtUpAsync().WaitAsync(TestTimeout);

        Assert.Equal(1, calls);
        Assert.True(controller.TakeMessagesDueAt(TimeSpan.FromSeconds(150), 100).Count > 0);
        Assert.Equal(40_000, controller.TimelineCount);
    }

    private static async Task PreserveUnsupportedHistoryAsync()
    {
        var calls = 0;
        await using var controller = CreateController(new CallbackProvider((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(VodChatFetchResult.Unsupported("Live capture only."));
        }));
        var replay = Replay();
        Start(controller, replay);
        await controller.WaitUntilCaughtUpAsync().WaitAsync(TestTimeout);
        FillCapturedHistory(controller);

        Start(controller, replay);
        await controller.WaitUntilCaughtUpAsync().WaitAsync(TestTimeout);

        Assert.Equal(1, calls);
        Assert.Equal(40_000, controller.TimelineCount);
        Assert.True(controller.TakeMessagesDueAt(TimeSpan.FromSeconds(150), 100).Count > 0);
    }

    private static void FillCapturedHistory(VodChatController controller)
    {
        controller.CaptureLiveMessage(CapturedMessage(0));
        for (var index = 1; index <= 40_000; index++)
        {
            controller.CaptureLiveMessage(CapturedMessage(100 + index / 100d));
        }
        Assert.Equal(40_000, controller.TimelineCount);
    }

    private static ChatMessage CapturedMessage(double seconds) => new(
        PlatformKind.Twitch, "streamer", "viewer", "message", DateTimeOffset.UnixEpoch.AddSeconds(seconds),
        MessageId: seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static async Task DrainRetiredFetchAsync(bool replace)
    {
        var started = Completion();
        var release = new TaskCompletionSource<VodChatFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = Completion();
        var controller = CreateController(new CallbackProvider(async (replay, _, token) =>
        {
            if (replay.ReplayId != "1") return VodChatFetchResult.Unsupported("Unused replacement.");
            using var registration = token.Register(() => canceled.TrySetResult());
            started.TrySetResult();
            // Emulate a provider that needs time to unwind after cancellation.
            return await release.Task;
        }));
        Task? disposal = null;
        try
        {
            Start(controller, Replay());
            await started.Task.WaitAsync(TestTimeout);
            if (replace) Start(controller, Replay("2"));
            controller.Stop();
            await canceled.Task.WaitAsync(TestTimeout);
            disposal = controller.DisposeAsync().AsTask();
            Assert.Equal(false, disposal.IsCompleted);
        }
        finally
        {
            release.TrySetResult(VodChatFetchResult.Completed([], TimeSpan.FromHours(1)));
            await (disposal ?? controller.DisposeAsync().AsTask()).WaitAsync(TestTimeout);
        }

        Assert.Equal(0, controller.TimelineCount);
    }

    private static async Task ConcurrentDisposalAsync()
    {
        var started = Completion();
        var release = new TaskCompletionSource<VodChatFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = CreateController(new CallbackProvider((_, _, _) =>
        {
            started.TrySetResult();
            return release.Task;
        }));
        Start(controller, Replay());
        await started.Task.WaitAsync(TestTimeout);
        var first = controller.DisposeAsync().AsTask();
        var second = controller.DisposeAsync().AsTask();
        try
        {
            Assert.Equal(false, first.IsCompleted);
            Assert.Equal(false, second.IsCompleted);
        }
        finally
        {
            release.TrySetResult(VodChatFetchResult.Completed([], TimeSpan.FromHours(1)));
            await Task.WhenAll(first, second).WaitAsync(TestTimeout);
        }
    }

    private static async Task CancellationFailureAsync(CancellationTransition transition)
    {
        var started = Completion();
        var release = new TaskCompletionSource<VodChatFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken fetchToken = default;
        Task? reenteredDisposal = null;
        VodChatController? controller = null;
        controller = CreateController(new CallbackProvider(async (replay, _, token) =>
        {
            if (replay.ReplayId != "1") return VodChatFetchResult.Completed([], TimeSpan.FromHours(1));
            fetchToken = token;
            using var registration = token.Register(() =>
            {
                if (transition == CancellationTransition.Dispose)
                    reenteredDisposal = controller!.DisposeAsync().AsTask();
                throw new IOException("Provider cancellation callback failed.");
            });
            started.TrySetResult();
            // Cancellation must not skip a provider that needs time to finish cleanup.
            return await release.Task;
        }));
        Task? disposal = null;
        try
        {
            Start(controller, Replay());
            await started.Task.WaitAsync(TestTimeout);
            if (transition == CancellationTransition.Stop) controller.Stop();
            if (transition == CancellationTransition.Replace) Start(controller, Replay("2"), TimeSpan.FromMinutes(2));
            disposal = controller.DisposeAsync().AsTask();
            Assert.True(fetchToken.IsCancellationRequested);
            Assert.True(!disposal.IsCompleted, "Cancellation failure skipped the outstanding fetch.");
            Assert.True(ReferenceEquals(disposal, controller.DisposeAsync().AsTask()));
            if (transition == CancellationTransition.Dispose)
                Assert.True(ReferenceEquals(disposal, reenteredDisposal));
            release.TrySetResult(VodChatFetchResult.Completed([], TimeSpan.FromHours(1)));
            await disposal.WaitAsync(TestTimeout);
            Assert.Throws<ObjectDisposedException>(() => _ = fetchToken.WaitHandle);
            Assert.Equal(0, controller.TimelineCount);
        }
        finally
        {
            release.TrySetResult(VodChatFetchResult.Completed([], TimeSpan.FromHours(1)));
            try { await (disposal ?? controller.DisposeAsync().AsTask()).WaitAsync(TestTimeout); }
            catch (AggregateException) { }
        }
    }

    private static async Task RetryAfterDiagnosticFailureAsync(Func<VodChatFetchResult> firstResult)
    {
        var retried = Completion();
        var calls = 0;
        var provider = new CallbackProvider((_, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) return Task.FromResult(firstResult());
            retried.TrySetResult();
            return Task.FromResult(VodChatFetchResult.Completed(
                [new VodChatMessage(TimeSpan.FromSeconds(10), CapturedMessage(10))], TimeSpan.FromHours(1)));
        });
        await using var controller = new VodChatController(provider, MemoryLogger.WithWriteFailure());
        Start(controller, Replay());
        await retried.Task.WaitAsync(TestTimeout);
        await controller.WaitUntilCaughtUpAsync().WaitAsync(TestTimeout);
        Assert.Equal(2, calls);
        Assert.Equal("10", controller.TakeMessagesDueAt(TimeSpan.FromSeconds(10), 10).Single().MessageId);
    }

    private static async Task GrowingTailDiagnosticFailureAsync()
    {
        var firstFetched = Completion();
        var retried = Completion();
        var calls = 0;
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var provider = new CallbackProvider((_, _, _) =>
        {
            (Interlocked.Increment(ref calls) == 1 ? firstFetched : retried).TrySetResult();
            return Task.FromResult(VodChatFetchResult.Completed([], TimeSpan.Zero));
        });
        await using var controller = new VodChatController(provider, MemoryLogger.WithWriteFailure(), clock);
        controller.Start(Replay(), new AppSettings(), TimeSpan.Zero, () => TimeSpan.FromHours(1), isGrowing: true);
        await firstFetched.Task.WaitAsync(TestTimeout);
        await controller.WaitUntilCaughtUpAsync().WaitAsync(TestTimeout);
        clock.Advance(TimeSpan.FromSeconds(5));
        await retried.Task.WaitAsync(TestTimeout);
        await controller.WaitUntilCaughtUpAsync().WaitAsync(TestTimeout);
        Assert.Equal(2, calls);
    }

    private static async Task DurationDiagnosticFailureAsync()
    {
        var fetched = Completion();
        var provider = new CallbackProvider((_, _, _) =>
        {
            fetched.TrySetResult();
            return Task.FromResult(VodChatFetchResult.Completed([], TimeSpan.FromHours(1)));
        });
        await using var controller = new VodChatController(provider, MemoryLogger.WithWriteFailure());
        controller.Start(Replay(), new AppSettings(), TimeSpan.FromSeconds(30),
            () => throw new IOException("Playback duration is temporarily unavailable."));
        Assert.True(controller.CaptureLiveMessage(CapturedMessage(5)));
        await fetched.Task.WaitAsync(TestTimeout);
        await controller.WaitUntilCaughtUpAsync().WaitAsync(TestTimeout);
        Assert.Equal("5", controller.TakeMessagesDueAt(TimeSpan.FromSeconds(30), 10).Single().MessageId);
    }

    private static async Task RefreshReplayMetadataAsync()
    {
        await using var controller = new VodChatController(null, new MemoryLogger());
        var replay = Replay();
        Start(controller, replay with { StreamStartedAtUtc = null }, TimeSpan.FromSeconds(30));
        Assert.True(!controller.CaptureLiveMessage(CapturedMessage(5)));
        await controller.WaitUntilCaughtUpAsync().WaitAsync(TestTimeout);
        Assert.Equal(0, controller.TimelineCount);

        Start(controller, replay, TimeSpan.FromSeconds(30));

        Assert.Equal("5", controller.TakeMessagesDueAt(TimeSpan.FromSeconds(30), 10).Single().MessageId);
    }

    private enum CancellationTransition { Stop, Replace, Dispose }

    private static TaskCompletionSource Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static VodChatController CreateController(IVodChatProvider provider) => new(provider, new MemoryLogger());

    private static void Start(VodChatController controller, ReplaySessionInfo replay, TimeSpan position = default) =>
        controller.Start(replay, new AppSettings(), position, () => replay.Duration);

    private static ReplaySessionInfo Replay(string id = "1") => new(
        PlatformKind.Twitch, "streamer", "https://example.invalid/replay.m3u8", id,
        DateTimeOffset.UnixEpoch, TimeSpan.FromHours(1), true, "");

    private sealed class CallbackProvider(
        Func<ReplaySessionInfo, TimeSpan, CancellationToken, Task<VodChatFetchResult>> fetch) : IVodChatProvider
    {
        public Task<VodChatFetchResult> FetchAsync(
            ReplaySessionInfo replay, AppSettings settings, TimeSpan fromOffset,
            CancellationToken cancellationToken = default) => fetch(replay, fromOffset, cancellationToken);
    }
}

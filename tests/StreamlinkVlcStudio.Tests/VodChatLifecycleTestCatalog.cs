using StreamlinkVlcStudio.App.Wpf.Chat;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

internal static class VodChatLifecycleTestCatalog
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(6);

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("VOD chat lifecycle: a provider timeout is retried", ProviderTimeoutAsync),
        ("VOD chat lifecycle: a stale completed page cannot stop a seek", StaleCompletionAsync),
        ("VOD chat lifecycle: a stale unsupported result cannot undo promotion", StalePromotionAsync),
        ("VOD chat lifecycle: disposal drains a stopped fetch", () => DrainRetiredFetchAsync(replace: false)),
        ("VOD chat lifecycle: disposal drains a replaced fetch", () => DrainRetiredFetchAsync(replace: true)),
        ("VOD chat lifecycle: concurrent disposal shares completion", ConcurrentDisposalAsync)
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

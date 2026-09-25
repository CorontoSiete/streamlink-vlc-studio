using System.Net;
using StreamlinkVlcStudio.Infrastructure.Http;

internal static class RepositoryReviewTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("repository review: Kick cache keeps changed credentials isolated", KickCacheIsolationAsync),
        ("repository review: Kick refresh preserves credentials edited during acquisition", KickRefreshIsolationAsync),
        ("repository review: Kick refresh reaches every shared waiter", KickRefreshWaitersAsync),
        ("repository review: canceled Kick waiters can recover refreshed credentials", KickCanceledRefreshAsync),
        ("repository review: active tab starts can finish after disposal", TabStartDisposalAsync),
        ("repository review: queued tab starts cannot run after disposal", QueuedTabStartDisposalAsync),
        ("repository review: optional catalogs tolerate unsupported charsets", OptionalCatalogCharsetAsync),
        ("repository review: updater disposal preserves in-flight errors", UpdaterDisposalAsync),
        ("repository review: canceled direct Kick token requests have no side effects", DirectKickCancellationAsync),
        ("repository review: canceled Kick UI callbacks do not mutate credentials", () => KickUiCallbackAsync(cancel: true)),
        ("repository review: delayed Kick UI callbacks preserve edited credentials", () => KickUiCallbackAsync(cancel: false)),
        ("repository review: timed-out Kick UI callbacks do not mutate credentials", () => KickUiCallbackAsync(cancel: false, timeout: true)),
        ("repository review: direct Kick refresh reads a snapshot and preserves account edits", DirectKickRefreshIsolationAsync),
        ("repository review: background credential changes dispatch UI notifications", ChatSettingsDispatchAsync)
    ];

    private static async Task KickCacheIsolationAsync()
    {
        var release = NewCompletion();
        var requests = 0;
        var provider = new KickTokenProvider(async (settings, _, _) =>
        {
            requests++;
            var token = settings.KickOAuthToken;
            await release.Task;
            return token;
        });
        var settings = new ChatSettings { KickOAuthToken = "old-token" };
        var pending = provider.ResolveAsync(settings, new MemoryLogger());
        settings.KickOAuthToken = "new-token";
        release.SetResult();
        Assert.Equal("old-token", await pending);
        Assert.Equal("new-token", await provider.ResolveAsync(settings, new MemoryLogger()));
        Assert.Equal(2, requests);
    }

    private static async Task KickRefreshIsolationAsync()
    {
        var release = NewCompletion();
        var provider = RefreshingProvider(release.Task);
        var settings = ExpiredCredentials();
        var pending = provider.ResolveAsync(settings, new MemoryLogger());
        settings.KickOAuthToken = "other-account";
        settings.KickRefreshToken = "other-refresh";
        release.SetResult();
        await pending;
        Assert.Equal("other-account", settings.KickOAuthToken);
        Assert.Equal("other-refresh", settings.KickRefreshToken);
    }

    private static async Task KickRefreshWaitersAsync()
    {
        var release = NewCompletion();
        var provider = RefreshingProvider(release.Task);
        var first = ExpiredCredentials();
        var second = ExpiredCredentials();
        var firstRequest = provider.ResolveAsync(first, new MemoryLogger());
        var secondRequest = provider.ResolveAsync(second, new MemoryLogger());
        release.SetResult();
        await Task.WhenAll(firstRequest, secondRequest);
        Assert.Equal("refreshed-token", first.KickOAuthToken);
        Assert.Equal("refreshed-token", second.KickOAuthToken);
        Assert.Equal("rotated-refresh", second.KickRefreshToken);
        Assert.Equal(first.KickTokenExpiresAtUtc, second.KickTokenExpiresAtUtc);
    }

    private static async Task KickCanceledRefreshAsync()
    {
        var release = NewCompletion();
        var provider = RefreshingProvider(release.Task);
        var settings = ExpiredCredentials();
        using var cancellation = new CancellationTokenSource();
        var pending = provider.ResolveAsync(settings, new MemoryLogger(), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        release.SetResult();
        await TestWait.UntilAsync(() => provider.InFlightCountForTest == 0, TimeSpan.FromSeconds(2));
        Assert.Equal("expired-token", settings.KickOAuthToken);
        Assert.Equal("refreshed-token", await provider.ResolveAsync(settings, new MemoryLogger()));
        Assert.Equal("rotated-refresh", settings.KickRefreshToken);
    }

    private static KickTokenProvider RefreshingProvider(Task release) => new(async (settings, _, _) =>
    {
        await release;
        settings.KickOAuthToken = "refreshed-token";
        settings.KickRefreshToken = "rotated-refresh";
        settings.KickTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1);
        return settings.KickOAuthToken;
    });

    private static ChatSettings ExpiredCredentials() => new()
    {
        KickOAuthToken = "expired-token",
        KickRefreshToken = "old-refresh",
        KickTokenExpiresAtUtc = DateTimeOffset.UnixEpoch
    };

    private static async Task TabStartDisposalAsync()
    {
        using var controller = new TabStartController(1);
        var release = NewCompletion();
        var tabId = Guid.NewGuid();
        Assert.True(controller.TryBegin(tabId));
        var pending = controller.RunBegunAsync(tabId, _ => release.Task, default);
        controller.Dispose();
        release.SetResult();
        await pending;
        Assert.True(!controller.IsActive(tabId));
        Assert.True(!controller.TryBegin(Guid.NewGuid()));
    }

    private static async Task QueuedTabStartDisposalAsync()
    {
        using var controller = new TabStartController(1);
        var release = NewCompletion();
        var active = Guid.NewGuid();
        var queued = Guid.NewGuid();
        var startedQueued = false;
        Assert.True(controller.TryBegin(active));
        Assert.True(controller.TryBegin(queued));
        var first = controller.RunBegunAsync(active, _ => release.Task, default);
        var second = controller.RunBegunAsync(queued, _ =>
        {
            startedQueued = true;
            return Task.CompletedTask;
        }, default);
        controller.Dispose();
        release.SetResult();
        await first;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(!startedQueued);
    }

    private static async Task OptionalCatalogCharsetAsync()
    {
        using var client = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            var content = new StringContent("{}");
            content.Headers.ContentType!.CharSet = "not-a-supported-charset";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/catalog");
        using var result = await OptionalHttpJsonReader.SendAsync(client, request, 100);
        Assert.True(result is null);
    }

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task UpdaterDisposalAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"StreamStudio-review-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var started = NewCompletion();
            var release = NewCompletion();
            using var client = new HttpClient(new AsyncHttpMessageHandler(async (_, _) =>
            {
                started.SetResult();
                await release.Task;
                throw new HttpRequestException("upstream unavailable");
            }));
            using var service = new StagedAppUpdateService(new MemoryLogger(), client, root, Path.Combine(root, "updates"));
            var pending = service.CheckAsync(UpdateCheckReason.Manual);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            service.Dispose();
            release.SetResult();
            await Assert.ThrowsAsync<HttpRequestException>(() => pending);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.CheckAsync(UpdateCheckReason.Manual));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task DirectKickCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => KickOAuthService.GetUsableAccessTokenAsync(
            new ChatSettings { KickOAuthToken = "valid-token" }, cancellationToken: cancellation.Token));
    }

    private static async Task KickUiCallbackAsync(bool cancel, bool timeout = false)
    {
        Action? queued = null;
        var defer = false;
        await using var tab = TestViewModels.CreateTab(
            StreamInputParser.FromChannel(PlatformKind.Kick, "streamer"), "best",
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
            new MemoryLogger(), action => { if (defer) queued = action; else action(); });
        using var cancellation = new CancellationTokenSource();
        var settings = ExpiredCredentials();
        var method = typeof(StreamTabViewModel).GetMethod("ApplyKickTokenResultOnUiThreadAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        defer = true;
        var pending = (Task)method.Invoke(tab,
            [settings, new KickOAuthTokenResult("refreshed", "rotated", DateTimeOffset.UtcNow.AddHours(1), "Bearer", []), cancellation.Token])!;
        defer = false;
        Assert.True(queued is not null);
        if (timeout)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => pending);
            queued!();
            Assert.Equal("expired-token", settings.KickOAuthToken);
            return;
        }

        if (cancel) cancellation.Cancel();
        else settings.KickOAuthToken = "edited-token";
        queued!();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancel ? "expired-token" : "edited-token", settings.KickOAuthToken);
    }

    private static async Task DirectKickRefreshIsolationAsync()
    {
        var release = NewCompletion();
        var settings = ExpiredCredentials();
        settings.KickClientId = "client";
        settings.KickClientSecret = "secret";
        var applied = false;
        var pending = KickOAuthService.GetUsableAccessTokenAsync(settings,
            (_, _, _) => { applied = true; return Task.CompletedTask; },
            async (snapshot, _) =>
            {
                await release.Task;
                Assert.Equal("expired-token", snapshot.KickOAuthToken);
                return new KickOAuthTokenResult("refreshed", "rotated", DateTimeOffset.UtcNow.AddHours(1), "Bearer", []);
            }, null, default);
        settings.KickOAuthToken = "new-account";
        release.SetResult();
        Assert.Equal<string?>(null, await pending);
        Assert.True(!applied);
        Assert.Equal("new-account", settings.KickOAuthToken);
    }

    private static async Task ChatSettingsDispatchAsync()
    {
        var settings = new AppSettings();
        var queued = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var defer = false;
        await using var viewModel = TestViewModels.CreateMain(settings, new FakeSettingsService(settings),
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
            new MemoryLogger(), action => { if (defer) queued.Enqueue(action); else action(); });
        var notifications = 0;
        viewModel.ClearKickTokenCommand.CanExecuteChanged += (_, _) => notifications++;
        defer = true;
        await Task.Run(() => settings.Chat.KickOAuthToken = "background-token");
        Assert.Equal(0, notifications);
        defer = false;
        while (queued.TryDequeue(out var action)) action();
        Assert.True(notifications > 0);
    }
}

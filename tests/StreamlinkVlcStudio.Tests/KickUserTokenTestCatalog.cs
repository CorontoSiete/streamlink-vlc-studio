internal static class KickUserTokenTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Kick user refresh: ordinary and forced requests share rotation", () => SharedRefreshAsync(false)),
        ("Kick user refresh: one canceled waiter leaves other tabs active", () => SharedRefreshAsync(true)),
        ("Kick user refresh: disconnected accounts reject delayed credentials", () => AccountChangedAsync(false)),
        ("Kick user refresh: replacement accounts reject delayed credentials", () => AccountChangedAsync(true)),
        ("Kick user refresh: inactive unexpired tokens force rotation", ForceInactiveAsync),
        ("Kick user refresh: a canceled sole waiter can recover the rotated refresh token", CanceledSoleWaiterAsync)
    ];

    private static ChatSettings Credentials() => new()
    {
        KickOAuthToken = "expired",
        KickRefreshToken = "refresh",
        KickClientId = "client",
        KickClientSecret = "secret",
        KickTokenExpiresAtUtc = DateTimeOffset.UnixEpoch
    };

    private static KickOAuthTokenResult Rotation() => new("fresh", "rotated", DateTimeOffset.UtcNow.AddHours(1), "Bearer", ["chat:write"]);

    private static Task ApplyAsync(ChatSettings settings, KickOAuthTokenResult result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        KickOAuthService.ApplyTokenResult(settings, result);
        return Task.CompletedTask;
    }

    private static async Task SharedRefreshAsync(bool cancelFirst)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var coordinator = new KickUserTokenCoordinator(async (snapshot, cancellationToken) =>
        {
            Interlocked.Increment(ref requests);
            Assert.Equal(false, cancellationToken.CanBeCanceled);
            await release.Task;
            Assert.Equal("refresh", snapshot.KickRefreshToken);
            return Rotation();
        });
        var first = Credentials();
        var second = Credentials();
        using var cancellation = new CancellationTokenSource();
        var normal = coordinator.ResolveAsync(first, ApplyAsync, null, cancellation.Token);
        var forced = coordinator.ResolveAsync(second, ApplyAsync, null, default, "expired");
        if (cancelFirst)
        {
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await normal);
        }
        release.SetResult();
        Assert.Equal("fresh", await forced);
        if (!cancelFirst) Assert.Equal("fresh", await normal);
        Assert.Equal(1, requests);
        Assert.Equal(cancelFirst ? "refresh" : "rotated", first.KickRefreshToken);
        Assert.Equal("rotated", second.KickRefreshToken);
    }

    private static async Task AccountChangedAsync(bool replace)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new KickUserTokenCoordinator(async (_, _) => { await release.Task; return Rotation(); });
        var settings = Credentials();
        var pending = coordinator.ResolveAsync(settings, ApplyAsync, null, default, "expired");
        KickOAuthService.ClearToken(settings);
        if (replace) KickOAuthService.ApplyTokenResult(settings, new("replacement", "replacement-refresh", null, "Bearer", []));
        release.SetResult();
        Assert.Equal<string?>(null, await pending);
        Assert.Equal(replace ? "replacement" : "", settings.KickOAuthToken);
        Assert.Equal(replace ? "replacement-refresh" : "", settings.KickRefreshToken);
    }

    private static async Task ForceInactiveAsync()
    {
        var requests = 0;
        var coordinator = new KickUserTokenCoordinator((_, _) => { requests++; return Task.FromResult(Rotation()); });
        var settings = Credentials();
        settings.KickTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1);
        Assert.Equal("expired", await coordinator.ResolveAsync(settings, ApplyAsync, null, default));
        Assert.Equal(0, requests);
        Assert.Equal("fresh", await coordinator.ResolveAsync(settings, ApplyAsync, null, default, "expired"));
        Assert.Equal("fresh", await coordinator.ResolveAsync(settings, ApplyAsync, null, default, "expired"));
        Assert.Equal(1, requests);
    }

    private static async Task CanceledSoleWaiterAsync()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var coordinator = new KickUserTokenCoordinator(async (_, _) => { requests++; await release.Task; return Rotation(); });
        var settings = Credentials();
        using var cancellation = new CancellationTokenSource();
        var pending = coordinator.ResolveAsync(settings, ApplyAsync, null, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        release.SetResult();
        Assert.Equal("fresh", await coordinator.ResolveAsync(settings, ApplyAsync, null, default));
        Assert.Equal("rotated", settings.KickRefreshToken);
        Assert.Equal(1, requests);
    }
}

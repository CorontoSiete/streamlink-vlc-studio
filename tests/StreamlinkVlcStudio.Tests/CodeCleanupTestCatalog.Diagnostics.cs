internal static partial class CodeCleanupTestCatalog
{
    private static async Task KickRotationDiagnosticsAsync()
    {
        var requests = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshed = new KickOAuthTokenResult("fresh-token", "rotated-refresh", DateTimeOffset.UtcNow.AddHours(1), "Bearer", []);
        var coordinator = new KickUserTokenCoordinator(async (_, _) =>
        {
            Interlocked.Increment(ref requests);
            await release.Task;
            return refreshed;
        });
        var firstSettings = ExpiredDiagnosticCredentials();
        var secondSettings = KickCredentialSnapshot.Capture(firstSettings).ToSettings();
        var logger = MemoryLogger.WithWriteFailure();
        var first = coordinator.ResolveAsync(firstSettings, ApplyDiagnosticRotationAsync, logger, default);
        var second = coordinator.ResolveAsync(secondSettings, ApplyDiagnosticRotationAsync, logger, default);
        release.SetResult();

        Assert.Equal("fresh-token", await first);
        Assert.Equal("fresh-token", await second);
        Assert.Equal("rotated-refresh", firstSettings.KickRefreshToken);
        Assert.Equal("rotated-refresh", secondSettings.KickRefreshToken);
        Assert.Equal(1, requests);
    }

    private static async Task KickRotationFailureDiagnosticsAsync()
    {
        var requests = 0;
        var coordinator = new KickUserTokenCoordinator((_, _) =>
        {
            if (++requests == 1) throw new IOException("Token endpoint unavailable.");
            return Task.FromResult(new KickOAuthTokenResult("fresh-token", "rotated-refresh", DateTimeOffset.UtcNow.AddHours(1), "Bearer", []));
        });
        var settings = ExpiredDiagnosticCredentials();
        var logger = MemoryLogger.WithWriteFailure();

        Assert.Equal("expired-token", await coordinator.ResolveAsync(settings, ApplyDiagnosticRotationAsync, logger, default));
        Assert.Equal("original-refresh", settings.KickRefreshToken);
        Assert.Equal("fresh-token", await coordinator.ResolveAsync(settings, ApplyDiagnosticRotationAsync, logger, default));
        Assert.Equal("rotated-refresh", settings.KickRefreshToken);
        Assert.Equal(2, requests);
    }

    private static async Task KickLookupDiagnosticsAsync()
    {
        var requests = 0;
        var provider = new KickTokenProvider((_, _, _) =>
        {
            requests++;
            throw new OperationCanceledException("The HTTP deadline elapsed.");
        });
        var settings = ExpiredDiagnosticCredentials();
        var logger = MemoryLogger.WithWriteFailure();

        Assert.Equal<string?>(null, await provider.ResolveAsync(settings, logger));
        Assert.Equal<string?>(null, await provider.ResolveAsync(settings, logger));
        Assert.Equal(1, requests);
        Assert.Equal(0, provider.InFlightCountForTest);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.ResolveAsync(settings, logger, cancellation.Token));
        Assert.Equal(1, requests);
    }

    private static async Task TwitchValidationDiagnosticsAsync()
    {
        var requests = 0;
        using var client = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            if (++requests == 1) throw new OperationCanceledException("The HTTP deadline elapsed.");
            return ValidatedDiagnosticTokenResponse();
        }));
        var logger = MemoryLogger.WithWriteFailure();
        var token = Guid.NewGuid().ToString("N");

        Assert.Equal<string?>(null, await TwitchClientIdCache.GetOrResolveAsync(client, token, logger, "Test", "Validation failed.", default));
        Assert.Equal("validated-client", await TwitchClientIdCache.GetOrResolveAsync(client, token, logger, "Test", "Validation failed.", default));
        Assert.Equal(2, requests);
    }

    private static async Task TwitchClientMismatchDiagnosticsAsync()
    {
        using var client = new HttpClient(new FakeHttpMessageHandler(_ => ValidatedDiagnosticTokenResponse()));
        var settings = new ChatSettings { TwitchClientId = "configured-client" };
        var result = await TwitchClientIdResolver.ResolveAsync(settings, client, Guid.NewGuid().ToString("N"),
            MemoryLogger.WithWriteFailure(), "Test", "Validation failed.", default);

        Assert.Equal("validated-client", result);
        Assert.Equal("configured-client", settings.TwitchClientId);
    }

    private static async Task DownloadQueueDiagnosticsAsync()
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(PlatformKind.Twitch);
        fixture.Logger.FailWrites();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        fixture.Resolver.ResolveStreamUrlOverride = async (_, token) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
                throw new IOException("The first VOD is unavailable.");
            }
            return new StreamlinkResolvedUrl(fixture.PlaylistUri, "Fixture");
        };
        var failedNotification = new TaskCompletionSource<VodDownloadItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.DownloadChanged += item =>
        {
            if (item.State == VodDownloadState.Failed) failedNotification.TrySetResult(item);
        };

        var first = await fixture.EnqueueAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await fixture.EnqueueAsync(fixture.Target with { MediaId = "99999", Url = "https://www.twitch.tv/videos/99999" });
        release.SetResult();

        var failed = await failedNotification.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(first.Id, failed.Id);
        Assert.Contains("unavailable", failed.Error);
        var completed = await fixture.WaitAsync(second.Id, VodDownloadState.Completed);
        Assert.Equal(20L, completed.BytesDownloaded);
        await fixture.Service.RetryAsync(first.Id, fixture.Options);
        var retried = await fixture.WaitAsync(first.Id, VodDownloadState.Completed);
        Assert.Equal(20L, retried.BytesDownloaded);
        Assert.Equal(3, requests);
    }

    private static async Task DownloadAvatarDiagnosticsAsync()
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(PlatformKind.Twitch);
        fixture.Logger.FailWrites();
        var target = fixture.Target with { ProfileImageUrl = "https://static-cdn.jtvnw.net/unavailable-avatar.png" };

        var completed = await fixture.WaitAsync((await fixture.EnqueueAsync(target)).Id, VodDownloadState.Completed);
        var offline = await fixture.Service.GetOfflineTargetAsync(completed.Id);
        Assert.Equal(20L, completed.BytesDownloaded);
        Assert.Equal(target.ProfileImageUrl, offline.ProfileImageUrl);
        Assert.True(File.Exists(offline.LocalMediaPath));
    }

    private static async Task PlaybackCleanupDiagnosticsAsync()
    {
        foreach (var timesOut in new[] { false, true })
        {
            var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pendingStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var engine = new FakePlaybackEngine
            {
                StopCompletion = timesOut ? pendingStop.Task : Task.FromException(new IOException("Playback stop failed.")),
                DisposeAction = () => disposed.TrySetResult()
            };
            try
            {
                await new PlaybackResourceCoordinator(MemoryLogger.WithWriteFailure(), () => "Test playback").StopAsync(
                    engine, timesOut ? TimeSpan.FromMilliseconds(20) : null, null, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                pendingStop.TrySetResult();
                await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static ChatSettings ExpiredDiagnosticCredentials() => new()
    {
        KickOAuthToken = "expired-token",
        KickRefreshToken = "original-refresh",
        KickClientId = "client",
        KickClientSecret = "secret",
        KickTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
    };

    private static Task ApplyDiagnosticRotationAsync(ChatSettings settings, KickOAuthTokenResult result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        KickOAuthService.ApplyTokenResult(settings, result);
        return Task.CompletedTask;
    }

    private static HttpResponseMessage ValidatedDiagnosticTokenResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"client_id":"validated-client","login":"streamer","user_id":"42","scopes":[],"expires_in":3600}""", Encoding.UTF8, "application/json")
    };

}

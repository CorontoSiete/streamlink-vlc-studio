using StreamlinkVlcStudio.App.Wpf.Twitch;

internal static partial class TwitchChannelPointsTestCatalog
{
    private static Task BonusDiagnosticsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new BonusHistorySettingsService();
        var logger = new MemoryLogger();
        logger.EntryWritten += (_, _) => throw new IOException("Diagnostic output unavailable.");
        await using var fixture = new Fixture(settingsService: service, logger: logger);
        fixture.Add("alpha");
        await Eventually(() => fixture.Browser.Pages.Count == 1 && fixture.Browser.Pages[0].Checks > 0);
        var page = fixture.Browser.Pages[0];

        page.Confirm("claim-1");
        Assert.Equal(1L, (await service.LoadAsync()).TwitchBonusClaims["alpha"].Count);

        page.Result = "Bonus claim clicked; waiting for Twitch.";
        var previousChecks = page.Checks;
        await Eventually(() => page.Checks >= previousChecks + 3);
        Assert.Equal(1, fixture.Browser.OpenAttempts);
        Assert.Equal(false, page.Disposed);

        service.FailSave = true;
        page.Confirm("claim-2");
        Assert.Contains("could not be saved", fixture.Controller.HistorySaveStatus);
        service.FailSave = false;
        await fixture.Controller.RetryCommand.ExecuteAsync();
        Assert.Equal("", fixture.Controller.HistorySaveStatus);
        Assert.Equal(2L, (await service.LoadAsync()).TwitchBonusClaims["alpha"].Count);
    });

    private static Task BonusWorkerCancellationFailureAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture();
        fixture.Browser.CheckGate = release.Task;
        fixture.Add("alpha");
        fixture.Add("bravo");
        await Eventually(() => fixture.Browser.Pages.Count == 2 && fixture.Browser.Pages.All(page => page.Checks > 0));
        var pages = fixture.Browser.Pages.ToArray();
        var registrations = pages.Select(page => page.LastCheckToken.Register(() =>
            throw new IOException("Injected bonus worker cancellation failure."))).ToArray();
        try
        {
            fixture.Controller.Enabled = false;

            Assert.True(pages.All(page => page.Disposed && page.LastCheckToken.IsCancellationRequested));
            release.TrySetResult();
            fixture.Controller.Enabled = true;
            await Eventually(() => fixture.Browser.Pages.Count == 4 && fixture.Browser.Pages[3].Checks > 0);
        }
        finally
        {
            foreach (var registration in registrations) registration.Dispose();
            release.TrySetResult();
        }
    });

    private static Task BonusPageDisposalFailureAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new Fixture();
        fixture.Add("alpha");
        fixture.Add("bravo");
        await Eventually(() => fixture.Browser.Pages.Count == 2 && fixture.Browser.Pages.All(page => page.Checks > 0));
        fixture.Browser.Pages[0].FailDispose = true;

        fixture.Controller.Enabled = false;

        Assert.True(fixture.Browser.Pages.All(page => page.Disposed && page.LastCheckToken.IsCancellationRequested));
        fixture.Controller.Dispose();
        Assert.True(fixture.Browser.Disposed);
    });

    private static Task BonusShutdownCancellationFailureAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(sessionGate: release.Task);
        var cancellation = (CancellationTokenSource)typeof(TwitchChannelPointsController)
            .GetField("lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Controller)!;
        using var registration = fixture.Browser.LastSessionToken.Register(() =>
            throw new IOException("Injected bonus lifetime cancellation failure."));
        try
        {
            fixture.Controller.Dispose();

            Assert.True(fixture.Browser.Disposed);
            Assert.True(fixture.Browser.LastSessionToken.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => _ = cancellation.Token);
        }
        finally
        {
            registration.Dispose();
            release.TrySetResult();
            fixture.Browser.Dispose();
            cancellation.Dispose();
        }
    });
}

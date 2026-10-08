internal static partial class UpdateModernizationTestCatalog
{
    private static async Task AutomaticUpdateDiagnosticsAsync(bool failCompletion)
    {
        using var cancellation = new CancellationTokenSource();
        var service = new ScheduledUpdateService { FailCompletion = failCompletion, FailuresRemaining = 1 };
        var logger = new MemoryLogger();
        logger.EntryWritten += (_, _) => throw new IOException("Diagnostic output unavailable.");
        var delays = new List<TimeSpan>();
        var successes = 0;
        var startupResults = 0;
        await new AutomaticUpdateController(service, () => true, _ => successes++, _ => { }, logger,
            (delay, _) =>
            {
                delays.Add(delay);
                if (delays.Count == 4) cancellation.Cancel();
                return Task.CompletedTask;
            }, onStartupCompleted: _ => startupResults++).RunAsync(cancellation.Token);

        Assert.Equal(1, startupResults);
        Assert.Equal(3, service.Checks);
        Assert.Equal(2, successes);
        Assert.SequenceEqual(new[] { TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(1), TimeSpan.FromHours(1) }, delays);
        Assert.Equal(failCompletion ? 2 : 1, logger.Entries.Count);
    }
}

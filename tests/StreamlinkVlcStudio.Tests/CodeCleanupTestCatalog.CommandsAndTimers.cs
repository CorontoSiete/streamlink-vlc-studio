extern alias BootstrapperAssembly;

using StreamlinkVlcStudio.Core.Commands;
using SetupRelayCommand = BootstrapperAssembly::StreamlinkVlcStudio.Core.Commands.RelayCommand;

internal static partial class CodeCleanupTestCatalog
{
    internal const string HomeRefreshTimerFailureTestName = "review timers: Home refresh dispatch failures are reported and later ticks continue";
    internal const string PredictionClockTimerFailureTestName = "review timers: prediction clock dispatch failures are reported and later ticks continue";

    private static Task DisabledRelayCommandsAsync()
    {
        var enabled = false;
        var executions = 0;
        var applicationCommand = new RelayCommand(() => executions++, () => enabled);
        var setupCommand = new SetupRelayCommand(() => executions++, () => enabled);
        ICommand[] commands = [applicationCommand, setupCommand];
        foreach (var command in commands)
        {
            Assert.True(!command.CanExecute(null));
            command.Execute(null);
        }
        Assert.Equal(0, executions);

        var notifications = 0;
        foreach (var command in commands) command.CanExecuteChanged += (_, _) => notifications++;
        enabled = true;
        applicationCommand.RaiseCanExecuteChanged();
        setupCommand.RaiseCanExecuteChanged();
        Assert.Equal(2, notifications);
        foreach (var command in commands)
        {
            Assert.True(command.CanExecute(null));
            command.Execute(null);
        }
        Assert.Equal(2, executions);
        enabled = false;
        foreach (var command in commands) command.Execute(null);
        Assert.Equal(2, executions);
        return Task.CompletedTask;
    }

    private static async Task HomeRefreshTimerFailureAsync()
    {
        var logger = MemoryLogger.WithWriteFailure();
        await using var feature = new HomeCleanupProbe(logger);
        feature.AllowDrain.TrySetResult();
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("The UI dispatcher is unavailable.");
        var calls = 0;
        feature.StartRefreshTimer(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw failure;
            resumed.TrySetResult();
        });

        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(logger.Entries.Any(entry => ReferenceEquals(entry.Exception, failure)));
    }

    private static async Task PredictionClockTimerFailureAsync()
    {
        var logger = MemoryLogger.WithWriteFailure();
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("The UI dispatcher is unavailable.");
        var clockStarted = false;
        var calls = 0;
        await using var tab = TestViewModels.CreateTab(StreamInputParser.FromChannel(PlatformKind.Twitch, "streamer"), "best",
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(), logger, action =>
            {
                if (clockStarted)
                {
                    if (Interlocked.Increment(ref calls) == 1) throw failure;
                    resumed.TrySetResult();
                }
                action();
            });
        clockStarted = true;
        typeof(StreamTabViewModel).GetMethod("StartTwitchPredictionClock", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tab, null);
        var timer = (Timer)GetPrivateField(tab, "twitchPredictionClockTimer")!;
        timer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(50));

        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(logger.Entries.Any(entry => ReferenceEquals(entry.Exception, failure)));
    }

    private static async Task CompletedDebounceTimerAsync()
    {
        using var coordinator = new CancellationDebounceCoordinator();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Schedule(TimeSpan.FromHours(1), () => completed.TrySetResult());
        var timer = (Timer)GetPrivateField(coordinator, "timer")!;
        timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var reactivated = false;
        try { reactivated = timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
        Assert.True(!reactivated, "A completed debounce timer can still be reactivated.");
        Assert.Equal<object?>(null, GetPrivateField(coordinator, "timer"));
    }
}

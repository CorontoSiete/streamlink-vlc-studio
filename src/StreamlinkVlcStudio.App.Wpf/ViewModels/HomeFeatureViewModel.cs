using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Threading;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

/// <summary>Owns a Home feature's command, dispatch, and shutdown lifetime.</summary>
internal abstract class HomeFeatureViewModel : ObservableObject, IAsyncDisposable
{
    protected readonly AppSettings Settings;
    protected readonly IAppLogger logger;
    protected readonly Action<Action> dispatch;
    protected readonly CancellationTokenSource lifetimeCancellation = new();
    protected readonly BackgroundOperationController backgroundOperationController;
    protected bool disposed;
    private readonly Action<string> setStatus;
    private readonly object disposalGate = new();
    private Task? disposal;

    protected HomeFeatureViewModel(MainViewModelDependencies dependencies, Action<string> setStatus)
    {
        Settings = dependencies.Settings;
        logger = dependencies.Logger;
        dispatch = dependencies.Dispatch;
        this.setStatus = setStatus;
        backgroundOperationController = new BackgroundOperationController(logger);
    }

    protected string StatusMessage { set { if (!disposed) setStatus(value); } }

    protected AsyncRelayCommand CreateCommand(Func<Task> execute, Func<bool>? canExecute = null) =>
        new(() => Track(execute()), () => !disposed && (canExecute?.Invoke() ?? true), exception =>
        {
            if (disposed || exception is OperationCanceledException) return;
            StatusMessage = $"Command failed. {exception.Message}";
            logger.Write(AppLogLevel.Error, "Command", "An application command failed.", exception);
        });

    protected Task Track(Task task)
    {
        backgroundOperationController.Track(task);
        return task;
    }

    protected void ReportDebouncedCallbackFailure(Exception exception) =>
        logger.Write(AppLogLevel.Warning, "UI", "A debounced UI operation could not be dispatched.", exception);

    protected void CancelOperation(CancellationTokenSource? cancellation) =>
        CancellationSourceCleanup.Cancel(cancellation,
            exception => logger.Write(AppLogLevel.Warning, "UI", "A Home feature cancellation callback failed.", exception));

    protected void EnsureRefreshTimerStarted(object timerGate, ref System.Threading.Timer? timer,
        TimeSpan interval, Action refresh)
    {
        if (interval <= TimeSpan.Zero || disposed) return;
        lock (timerGate)
        {
            if (timer is not null || disposed) return;
            timer = new System.Threading.Timer(
                _ => SafeEventDispatcher.Invoke(refresh, logger, "UI", "Home refresh timer"), null, interval, interval);
        }
    }

    protected static void StopRefreshTimer(object timerGate, ref System.Threading.Timer? timer)
    {
        lock (timerGate)
        {
            timer?.Dispose();
            timer = null;
        }
    }

    public ValueTask DisposeAsync() =>
        new(AsyncDisposal.Begin(disposalGate, ref disposed, ref disposal, DisposeCoreAsync));

    protected virtual void StopOperations() { }
    protected virtual Task WaitForOperationsAsync() => Task.CompletedTask;
    protected virtual void ReleaseResources() { }

    private async Task DisposeCoreAsync()
    {
        var cleanup = CleanupAsync();
        try { await cleanup.WaitAsync(TimeSpan.FromSeconds(4)); }
        catch (TimeoutException)
        {
            // Late providers still own their semaphores and cancellation sources.
            // Cleanup releases them after those providers have actually returned.
            logger.WriteSafely(AppLogLevel.Warning, "UI", "A Home feature is finishing cleanup in the background.");
            _ = cleanup.ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
        }
    }

    private async Task CleanupAsync()
    {
        CancelOperation(lifetimeCancellation);
        try
        {
            try { StopOperations(); }
            finally { await WaitForOperationsAsync(); }
        }
        finally
        {
            try { await backgroundOperationController.WaitForIdleAsync(); }
            finally
            {
                try { ReleaseResources(); }
                finally { lifetimeCancellation.Dispose(); }
            }
        }
    }
}

using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

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

    public ValueTask DisposeAsync()
    {
        if (disposal is not null) return new(disposal);
        disposed = true;
        lifetimeCancellation.Cancel();
        StopOperations();
        disposal = DisposeCoreAsync();
        return new(disposal);
    }

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
            logger.Write(AppLogLevel.Warning, "UI", "A Home feature is finishing cleanup in the background.");
            _ = cleanup.ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
        }
    }

    private async Task CleanupAsync()
    {
        await WaitForOperationsAsync();
        await backgroundOperationController.WaitForIdleAsync();
        ReleaseResources();
        lifetimeCancellation.Dispose();
    }
}

using System.Windows.Input;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> execute;
    private readonly Func<bool>? canExecute;
    private readonly Action<Exception>? errorHandler;
    private readonly bool allowConcurrentExecution;
    // ICommand can be invoked from both WPF's dispatcher and input/event callbacks. A plain bool
    // leaves a small but real race between CanExecute and ExecuteAsync, allowing two callers to
    // enter the same operation. Keep the gate as an integer so entry is one atomic operation.
    // Commands which coalesce their own requests can explicitly opt out of this gate.
    private int isRunning;

    public AsyncRelayCommand(
        Func<Task> execute,
        Func<bool>? canExecute = null,
        Action<Exception>? errorHandler = null,
        bool allowConcurrentExecution = false)
    {
        this.execute = execute;
        this.canExecute = canExecute;
        this.errorHandler = errorHandler;
        this.allowConcurrentExecution = allowConcurrentExecution;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        (allowConcurrentExecution || Volatile.Read(ref isRunning) == 0) && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync(parameter);
        }
        catch (Exception exception)
        {
            try
            {
                errorHandler?.Invoke(exception);
            }
            catch
            {
                // An optional UI error handler must not reintroduce an async-void failure.
            }
        }
    }

    public async Task ExecuteAsync(object? parameter = null)
    {
        if ((!allowConcurrentExecution && Volatile.Read(ref isRunning) != 0) ||
            (canExecute is not null && !canExecute()))
        {
            return;
        }

        if (!allowConcurrentExecution && Interlocked.CompareExchange(ref isRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            // CanExecute may depend on mutable view-model state. Re-check after taking the gate so a
            // caller that observed the command just before another state change cannot run stale work.
            // Keep predicates and notifications inside the try: user callbacks may throw, but the
            // atomic running gate must still be released.
            if (canExecute is not null && !canExecute())
            {
                return;
            }

            RaiseCanExecuteChanged();
            await execute();
        }
        finally
        {
            if (!allowConcurrentExecution) Volatile.Write(ref isRunning, 0);
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

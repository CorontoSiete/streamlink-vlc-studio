namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

/// <summary>
/// Coordinates a replaceable debounced callback and the cancellation tokens for
/// the work started by that callback.  A token source is retained until the
/// corresponding operation calls <see cref="Complete"/>, so cancellation never
/// disposes a source while its consumer is still using it.
/// </summary>
internal sealed class CancellationDebounceCoordinator : IDisposable
{
    private readonly object gate = new();
    private readonly HashSet<CancellationTokenSource> operations = [];
    private CancellationTokenSource? currentOperation;
    private Timer? timer;
    private long scheduleVersion;
    private TaskCompletionSource? drained;
    private bool disposed;

    public bool Schedule(
        TimeSpan delay,
        Action callback,
        Action<Exception>? callbackErrorHandler = null)
    {
        ArgumentNullException.ThrowIfNull(callback);

        Timer? previousTimer;
        lock (gate)
        {
            if (disposed)
            {
                return false;
            }

            previousTimer = timer;
            var scheduledVersion = ++scheduleVersion;
            timer = new Timer(
                _ => RunScheduledCallback(scheduledVersion, callback, callbackErrorHandler),
                null,
                delay,
                Timeout.InfiniteTimeSpan);
        }

        previousTimer?.Dispose();
        return true;
    }

    public void CancelScheduled()
    {
        Timer? timerToDispose;
        lock (gate)
        {
            timerToDispose = timer;
            timer = null;
            scheduleVersion++;
        }

        timerToDispose?.Dispose();
    }

    public CancellationTokenSource BeginOperation(CancellationToken lifetimeToken)
    {
        CancellationTokenSource? previousOperation;
        CancellationTokenSource nextOperation;

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            previousOperation = currentOperation;
            nextOperation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            currentOperation = nextOperation;
            operations.Add(nextOperation);
        }

        TryCancel(previousOperation);
        return nextOperation;
    }

    public void CancelActive()
    {
        CancellationTokenSource? operation;
        lock (gate)
        {
            operation = currentOperation;
            currentOperation = null;
        }

        TryCancel(operation);
    }

    public void Complete(CancellationTokenSource operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        TaskCompletionSource? completion = null;
        lock (gate)
        {
            if (!operations.Remove(operation))
            {
                return;
            }

            if (ReferenceEquals(currentOperation, operation))
            {
                currentOperation = null;
            }

            if (operations.Count == 0)
            {
                completion = drained;
                drained = null;
            }
        }

        operation.Dispose();
        completion?.TrySetResult();
    }

    public async Task DrainAsync(TimeSpan timeout)
    {
        Task? completionTask = null;
        lock (gate)
        {
            if (operations.Count > 0)
            {
                drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                completionTask = drained.Task;
            }
        }

        if (completionTask is not null)
        {
            await completionTask.WaitAsync(timeout);
        }
    }

    public void Dispose()
    {
        Timer? timerToDispose;
        CancellationTokenSource[] operationsToCancel;
        TaskCompletionSource? completion = null;

        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            timerToDispose = timer;
            timer = null;
            currentOperation = null;
            operationsToCancel = operations.ToArray();
            scheduleVersion++;
            if (operationsToCancel.Length == 0)
            {
                completion = drained;
                drained = null;
            }
        }

        timerToDispose?.Dispose();
        foreach (var operation in operationsToCancel)
        {
            TryCancel(operation);
        }

        completion?.TrySetResult();
    }

    private void RunScheduledCallback(
        long scheduledVersion,
        Action callback,
        Action<Exception>? callbackErrorHandler)
    {
        lock (gate)
        {
            if (disposed || scheduledVersion != scheduleVersion)
            {
                return;
            }

            timer = null;
        }

        try
        {
            callback();
        }
        catch (Exception exception)
        {
            // Timer callbacks run on a ThreadPool thread. Letting a caller's callback
            // exception escape here terminates the process instead of reporting the
            // failed debounced operation through the caller's normal error path.
            if (callbackErrorHandler is null)
            {
                System.Diagnostics.Debug.WriteLine($"Debounced callback failed: {exception}");
                return;
            }

            try
            {
                callbackErrorHandler(exception);
            }
            catch (Exception handlerException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Debounced callback error handler failed: {handlerException}");
            }
        }
    }

    private static void TryCancel(CancellationTokenSource? operation)
    {
        try
        {
            operation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion and cancellation may race. A disposed source is already terminal.
        }
    }
}

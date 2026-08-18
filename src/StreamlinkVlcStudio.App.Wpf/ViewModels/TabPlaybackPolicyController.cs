namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

/// <summary>
/// Serializes and coalesces generation-aware inactive-tab playback policy requests.
/// </summary>
internal sealed class TabPlaybackPolicyController : IDisposable
{
    private readonly object gate = new();
    private readonly Action<Action> dispatch;
    private readonly Func<bool> isDisposed;
    private readonly Func<long, Task> applyPassAsync;
    private readonly Action<Task> trackOperation;
    private readonly Action<Exception> reportFailure;
    private TaskCompletionSource idleCompletion = CreateCompletedCompletion();
    private long generation;
    private bool loopQueued;
    private bool runRequested;
    private bool disposed;

    public TabPlaybackPolicyController(
        Action<Action> dispatch,
        Func<bool> isDisposed,
        Func<Task> applyPassAsync,
        Action<Task> trackOperation,
        Action<Exception> reportFailure)
        : this(
            dispatch,
            isDisposed,
            _ => applyPassAsync(),
            trackOperation,
            reportFailure)
    {
        ArgumentNullException.ThrowIfNull(applyPassAsync);
    }

    public TabPlaybackPolicyController(
        Action<Action> dispatch,
        Func<bool> isDisposed,
        Func<long, Task> applyPassAsync,
        Action<Task> trackOperation,
        Action<Exception> reportFailure)
    {
        this.dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        this.isDisposed = isDisposed ?? throw new ArgumentNullException(nameof(isDisposed));
        this.applyPassAsync = applyPassAsync ?? throw new ArgumentNullException(nameof(applyPassAsync));
        this.trackOperation = trackOperation ?? throw new ArgumentNullException(nameof(trackOperation));
        this.reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
    }

    internal Task IdleTask
    {
        get
        {
            lock (gate)
            {
                return idleCompletion.Task;
            }
        }
    }

    public long Request()
    {
        var shouldDispatch = false;
        long requestedGeneration;
        lock (gate)
        {
            if (disposed || isDisposed())
            {
                return generation;
            }

            requestedGeneration = checked(++generation);
            runRequested = true;
            if (!loopQueued)
            {
                loopQueued = true;
                idleCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                shouldDispatch = true;
            }
        }

        if (shouldDispatch)
        {
            DispatchLoop();
        }

        return requestedGeneration;
    }

    public bool IsCurrent(long candidateGeneration)
    {
        lock (gate)
        {
            return !disposed && !isDisposed() && candidateGeneration == generation;
        }
    }

    public void Dispose()
    {
        TaskCompletionSource? completion = null;
        lock (gate)
        {
            disposed = true;
            runRequested = false;
            if (!loopQueued)
            {
                completion = idleCompletion;
            }
        }

        completion?.TrySetResult();
    }

    private void DispatchLoop()
    {
        try
        {
            dispatch(StartLoop);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FinishLoopAndMaybeRequeue(allowImmediateRequeue: false);
            reportFailure(ex);
        }
        catch (OperationCanceledException)
        {
            FinishLoopAndMaybeRequeue(allowImmediateRequeue: false);
        }
    }

    private void StartLoop()
    {
        if (disposed || isDisposed())
        {
            FinishLoopAndMaybeRequeue();
            return;
        }

        try
        {
            trackOperation(RunLoopAsync());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FinishLoopAndMaybeRequeue(allowImmediateRequeue: false);
            reportFailure(ex);
        }
        catch (OperationCanceledException)
        {
            FinishLoopAndMaybeRequeue(allowImmediateRequeue: false);
        }
    }

    private async Task RunLoopAsync()
    {
        try
        {
            while (true)
            {
                long passGeneration;
                lock (gate)
                {
                    if (disposed || isDisposed() || !runRequested)
                    {
                        return;
                    }

                    runRequested = false;
                    passGeneration = generation;
                }

                // Coalesce UI changes queued in the same dispatcher turn while retaining the
                // newest generation so stale async continuations can identify themselves.
                await Task.Yield();
                lock (gate)
                {
                    if (runRequested)
                    {
                        runRequested = false;
                        passGeneration = generation;
                    }
                }

                await applyPassAsync(passGeneration);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            reportFailure(ex);
        }
        finally
        {
            FinishLoopAndMaybeRequeue();
        }
    }

    private void FinishLoopAndMaybeRequeue(bool allowImmediateRequeue = true)
    {
        var shouldDispatch = false;
        TaskCompletionSource? completion = null;
        lock (gate)
        {
            loopQueued = false;
            if (allowImmediateRequeue && !disposed && !isDisposed() && runRequested)
            {
                loopQueued = true;
                shouldDispatch = true;
            }
            else
            {
                completion = idleCompletion;
            }
        }

        completion?.TrySetResult();
        if (shouldDispatch)
        {
            DispatchLoop();
        }
    }

    private static TaskCompletionSource CreateCompletedCompletion()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }
}

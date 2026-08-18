namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

/// <summary>
/// Owns the lifecycle mechanics shared by Home stream searches: debouncing,
/// replaceable cancellation, generation checks, and shutdown draining. The
/// view model remains responsible for service calls and WPF collection updates.
/// </summary>
internal sealed class StreamSearchController : IDisposable
{
    private readonly CancellationDebounceCoordinator coordinator = new();
    private int generation;

    public int CurrentGeneration => Volatile.Read(ref generation);

    public int AdvanceGeneration()
    {
        return Interlocked.Increment(ref generation);
    }

    public bool IsCurrent(
        int expectedGeneration,
        string expectedQuery,
        Func<string> currentQuery,
        Func<bool> isDisposed)
    {
        ArgumentNullException.ThrowIfNull(currentQuery);
        ArgumentNullException.ThrowIfNull(isDisposed);

        return !isDisposed() &&
            CurrentGeneration == expectedGeneration &&
            string.Equals(currentQuery().Trim(), expectedQuery, StringComparison.Ordinal);
    }

    public bool Schedule(
        TimeSpan delay,
        Action callback,
        Action<Exception>? callbackErrorHandler = null)
    {
        return coordinator.Schedule(delay, callback, callbackErrorHandler);
    }

    public void CancelScheduled()
    {
        coordinator.CancelScheduled();
    }

    public CancellationTokenSource BeginOperation(CancellationToken lifetimeToken)
    {
        return coordinator.BeginOperation(lifetimeToken);
    }

    public void CancelActive()
    {
        coordinator.CancelActive();
    }

    public void Complete(CancellationTokenSource operation)
    {
        coordinator.Complete(operation);
    }

    public Task DrainAsync(TimeSpan timeout)
    {
        return coordinator.DrainAsync(timeout);
    }

    public void Dispose()
    {
        coordinator.Dispose();
    }
}

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal sealed class TabStartController(int maximumConcurrency) : IDisposable
{
    private readonly object gate = new();
    private readonly HashSet<Guid> activeStarts = [];
    private readonly SemaphoreSlim startSlots = new(maximumConcurrency, maximumConcurrency);
    private bool disposed;

    public bool IsActive(Guid tabId)
    {
        lock (gate)
        {
            return activeStarts.Contains(tabId);
        }
    }

    public bool TryBegin(Guid tabId)
    {
        lock (gate)
        {
            return !disposed && activeStarts.Add(tabId);
        }
    }

    public void End(Guid tabId)
    {
        lock (gate)
        {
            activeStarts.Remove(tabId);
        }
    }

    public async Task RunBegunAsync(
        Guid tabId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var acquired = false;
        try
        {
            await startSlots.WaitAsync(cancellationToken);
            acquired = true;
            await operation(cancellationToken);
        }
        finally
        {
            if (acquired)
            {
                startSlots.Release();
            }

            End(tabId);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            activeStarts.Clear();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            activeStarts.Clear();
        }

        startSlots.Dispose();
    }
}

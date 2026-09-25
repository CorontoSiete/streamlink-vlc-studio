namespace StreamlinkVlcStudio.Infrastructure.Threading;

/// <summary>
/// Bounds concurrent operations and closes admission on disposal. Active leases can finish;
/// queued callers are rejected, and the semaphore survives until every caller has released it.
/// </summary>
internal sealed class AsyncOperationGate(int maximumConcurrency = 1) : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim slots = new(maximumConcurrency, maximumConcurrency);
    private int users;
    private bool disposed;

    internal async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            users++;
        }

        var acquired = false;
        try
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
            }

            return new Lease(this);
        }
        catch
        {
            Release(acquired);
            throw;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (users == 0) slots.Dispose();
        }
    }

    private void Release(bool acquired)
    {
        lock (gate)
        {
            if (acquired) slots.Release();
            users--;
            if (disposed && users == 0) slots.Dispose();
        }
    }

    private sealed class Lease(AsyncOperationGate owner) : IDisposable
    {
        private AsyncOperationGate? owner = owner;

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(acquired: true);
    }
}

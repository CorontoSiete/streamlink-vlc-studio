using StreamlinkVlcStudio.Infrastructure.Threading;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal sealed class TabStartController(int maximumConcurrency) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, StartRegistration> activeStarts = [];
    private readonly AsyncOperationGate startSlots = new(maximumConcurrency);
    private bool disposed;

    public bool IsActive(Guid tabId)
    {
        lock (gate)
        {
            return activeStarts.TryGetValue(tabId, out var start) && !start.Token.IsCancellationRequested;
        }
    }

    public StartRegistration? TryBegin(Guid tabId)
    {
        lock (gate)
        {
            if (disposed || (activeStarts.TryGetValue(tabId, out var existing) && !existing.Token.IsCancellationRequested))
            {
                return null;
            }

            var start = new StartRegistration(tabId);
            activeStarts[tabId] = start;
            return start;
        }
    }

    public void Cancel(Guid tabId)
    {
        StartRegistration? start;
        lock (gate)
        {
            activeStarts.TryGetValue(tabId, out start);
        }

        start?.Cancel();
    }

    public void End(StartRegistration start)
    {
        lock (gate)
        {
            // A cancelled request may already have been replaced by Play. Its delayed
            // dispatcher callback or cleanup must not remove that replacement.
            if (activeStarts.TryGetValue(start.TabId, out var current) && ReferenceEquals(current, start))
                activeStarts.Remove(start.TabId);
        }

        start.Dispose();
    }

    public async Task RunBegunAsync(
        StartRegistration start,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(start.Token, cancellationToken);
            IDisposable lease;
            try
            {
                lease = await startSlots.EnterAsync(linked.Token);
            }
            catch (ObjectDisposedException ex) when (Volatile.Read(ref disposed))
            {
                // Admission closes before Clear cancels each registration. A released
                // slot in that interval still represents a canceled start, not a UI error.
                throw new OperationCanceledException("Tab startup was canceled during shutdown.", ex, linked.Token);
            }

            using (lease)
            {
                linked.Token.ThrowIfCancellationRequested();
                await operation(linked.Token);
            }
        }
        finally
        {
            End(start);
        }
    }

    public void Clear()
    {
        StartRegistration[] starts;
        lock (gate)
        {
            starts = activeStarts.Values.ToArray();
            activeStarts.Clear();
        }

        foreach (var start in starts) start.Cancel();
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
            startSlots.Dispose();
        }

        Clear();
    }

    internal sealed class StartRegistration : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        public Guid TabId { get; }
        public CancellationToken Token { get; }

        public StartRegistration(Guid tabId)
        {
            TabId = tabId;
            Token = cancellation.Token;
        }

        public void Cancel() => CancellationSourceCleanup.Cancel(cancellation);

        public void Dispose() => cancellation.Dispose();
    }
}

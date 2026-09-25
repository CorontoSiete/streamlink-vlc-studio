namespace StreamlinkVlcStudio.App.Wpf.Chat;

/// <summary>
/// Coalesces changes off the caller's thread without losing any affected lookup scopes.
/// </summary>
internal sealed class CatalogChangeNotifier(object sender, Action<Action>? schedule = null)
{
    private const int MaximumPendingScopes = 256;
    private readonly object gate = new();
    private readonly HashSet<CatalogChangeScope> pendingScopes = [];
    private bool allScopes;
    private bool queued;

    internal void Queue(Func<EventHandler?> handlers, CatalogChangeScope? scope = null)
    {
        lock (gate)
        {
            if (!allScopes)
            {
                if (scope is null || (pendingScopes.Add(scope.Value) && pendingScopes.Count > MaximumPendingScopes))
                {
                    // Fall back to a complete refresh rather than retaining an unbounded burst
                    // or omitting decorations when many different channels change together.
                    allScopes = true;
                    pendingScopes.Clear();
                }
            }

            if (queued) return;
            queued = true;
        }

        void Deliver()
        {
            EventArgs changes;
            lock (gate)
            {
                changes = allScopes ? EventArgs.Empty : new CatalogChangedEventArgs(pendingScopes.ToArray());
                pendingScopes.Clear();
                allScopes = false;
                queued = false;
            }

            CatalogLoadCoordinator.RaiseSafely(handlers(), sender, changes);
        }

        if (schedule is null) _ = Task.Run(Deliver);
        else schedule(Deliver);
    }
}

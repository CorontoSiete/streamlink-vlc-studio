namespace StreamlinkVlcStudio.App.Wpf.Chat;

/// <summary>
/// Raises a catalog-changed event off the caller's thread and coalesces bursts: while a
/// notification is pending, further requests are dropped instead of queueing another pass.
/// The badge and emote catalogs both mutate in tight loops while loading, so this keeps a
/// single load from fanning out into hundreds of UI refreshes.
/// </summary>
internal sealed class CatalogChangeNotifier(object sender)
{
    private int queued;

    internal void Queue(Func<EventHandler?> handlers)
    {
        if (Interlocked.Exchange(ref queued, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            Interlocked.Exchange(ref queued, 0);
            CatalogLoadCoordinator.RaiseSafely(handlers(), sender);
        });
    }
}

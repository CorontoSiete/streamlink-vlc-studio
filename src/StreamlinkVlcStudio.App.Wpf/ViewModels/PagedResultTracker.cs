using System.Collections.ObjectModel;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

/// <summary>Appends unique rows and stops pagination when a provider revisits a completed page.</summary>
internal sealed class PagedResultTracker
{
    private readonly HashSet<string> completedCursors = new(StringComparer.Ordinal);

    /// <summary>Reuses cards on refresh and creates only new identities, including on overlapping pages.</summary>
    internal string ApplyPage<TCard, TItem>(
        ObservableCollection<TCard> destination,
        IEnumerable<TItem> items,
        Func<TCard, string> cardIdentity,
        Func<TItem, string> itemIdentity,
        Func<TItem, TCard> create,
        Action<TCard, TItem> update,
        string requestedCursor,
        string nextCursor) where TCard : class
    {
        ApplyItems(destination, items, cardIdentity, itemIdentity, create, update,
            reset: string.IsNullOrWhiteSpace(requestedCursor));
        return RecordPage(requestedCursor, nextCursor);
    }

    /// <summary>Shares card reconciliation with refreshes that do not use pagination.</summary>
    internal static void ApplyItems<TCard, TItem>(
        ObservableCollection<TCard> destination,
        IEnumerable<TItem> items,
        Func<TCard, string> cardIdentity,
        Func<TItem, string> itemIdentity,
        Func<TItem, TCard> create,
        Action<TCard, TItem> update,
        bool reset) where TCard : class
    {
        var existing = destination.ToDictionary(cardIdentity, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var desired = reset ? new List<TCard>() : null;
        foreach (var item in items)
        {
            var key = itemIdentity(item);
            if (!seen.Add(key)) continue;
            if (existing.TryGetValue(key, out var card))
            {
                // Overlap on a later page must not discard metadata already enriched
                // on the earlier page (such as separately loaded category viewer counts).
                if (reset) update(card, item);
            }
            else
            {
                card = create(item);
                if (!reset) destination.Add(card);
            }
            desired?.Add(card);
        }

        if (desired is not null)
        {
            for (var index = destination.Count - 1; index >= 0; index--)
            {
                if (!seen.Contains(cardIdentity(destination[index]))) destination.RemoveAt(index);
            }
            for (var index = 0; index < desired.Count; index++)
            {
                var card = desired[index];
                if (index < destination.Count && ReferenceEquals(destination[index], card)) continue;
                var currentIndex = destination.IndexOf(card);
                if (currentIndex < 0) destination.Insert(index, card);
                else destination.Move(currentIndex, index);
            }
        }
    }

    private string RecordPage(string requestedCursor, string nextCursor)
    {
        // The empty cursor starts a fresh search, even when the query itself is unchanged.
        if (string.IsNullOrWhiteSpace(requestedCursor))
        {
            completedCursors.Clear();
        }
        else
        {
            completedCursors.Add(requestedCursor.Trim());
        }

        var normalizedNext = (nextCursor ?? "").Trim();
        return completedCursors.Contains(normalizedNext) ? "" : normalizedNext;
    }
}

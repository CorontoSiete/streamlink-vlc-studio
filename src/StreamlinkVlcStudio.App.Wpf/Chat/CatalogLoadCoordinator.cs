namespace StreamlinkVlcStudio.App.Wpf.Chat;

internal readonly record struct CatalogLoadResult(bool Succeeded, bool Changed)
{
    internal static CatalogLoadResult Successful(bool changed = false) => new(true, changed);

    internal static CatalogLoadResult Failed(bool changed = false) => new(false, changed);
}

/// <summary>Coordinates bounded, retryable, per-scope catalog loads.</summary>
internal sealed class CatalogLoadCoordinator
{
    private const int DefaultMaximumEntries = 256;
    private static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(5);

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider timeProvider;
    private readonly int maximumEntries;
    private readonly TimeSpan timeToLive;
    private readonly TimeSpan retryDelay;
    private readonly Action<string>? scopeEvicted;

    internal CatalogLoadCoordinator(
        TimeProvider? timeProvider = null,
        int maximumEntries = DefaultMaximumEntries,
        TimeSpan? timeToLive = null,
        TimeSpan? retryDelay = null,
        Action<string>? scopeEvicted = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.maximumEntries = maximumEntries;
        this.timeToLive = timeToLive is { } ttl && ttl > TimeSpan.Zero ? ttl : DefaultTimeToLive;
        this.retryDelay = retryDelay is { } retry && retry >= TimeSpan.Zero ? retry : DefaultRetryDelay;
        this.scopeEvicted = scopeEvicted;
    }

    internal bool Ensure(
        string scope,
        Func<Task<CatalogLoadResult>> loadAsync,
        Action? changed = null,
        bool preserveFromEviction = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(loadAsync);
        var now = timeProvider.GetUtcNow();
        string? evictedScope = null;
        Entry entry;
        lock (gate)
        {
            if (entries.TryGetValue(scope, out var existing))
            {
                existing.LastAccessUtc = now;
                if (existing.InFlight || existing.NextAttemptUtc > now)
                {
                    return false;
                }
            }
            else
            {
                if (!TryMakeRoomLocked(out evictedScope))
                {
                    return false;
                }

                existing = new Entry(preserveFromEviction);
                entries[scope] = existing;
            }

            existing.InFlight = true;
            existing.LastAccessUtc = now;
            entry = existing;
        }

        if (evictedScope is not null)
        {
            RaiseEvictedSafely(evictedScope);
        }

        _ = RunLoadAsync(scope, entry, loadAsync, changed);
        return true;
    }

    internal int EntryCount
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    internal int InFlightCount
    {
        get
        {
            lock (gate)
            {
                return entries.Count(pair => pair.Value.InFlight);
            }
        }
    }

    internal void InvalidateScopes(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        lock (gate)
        {
            foreach (var key in entries.Keys
                         .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                entries.Remove(key);
            }
        }
    }

    private async Task RunLoadAsync(
        string scope,
        Entry expectedEntry,
        Func<Task<CatalogLoadResult>> loadAsync,
        Action? changed)
    {
        var result = CatalogLoadResult.Failed();
        try
        {
            result = await loadAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Catalogs are best-effort UI decoration. Failure state is retained for backoff,
            // while the exception is observed here so fire-and-forget loads remain harmless.
        }

        lock (gate)
        {
            if (!entries.TryGetValue(scope, out var entry) ||
                !ReferenceEquals(entry, expectedEntry))
            {
                return;
            }

            var now = timeProvider.GetUtcNow();
            entry.InFlight = false;
            entry.LastAccessUtc = now;
            if (result.Succeeded)
            {
                entry.FailureCount = 0;
                entry.NextAttemptUtc = now.Add(timeToLive);
            }
            else
            {
                entry.FailureCount = Math.Min(entry.FailureCount + 1, 16);
                entry.NextAttemptUtc = now.Add(CalculateRetryDelay(entry.FailureCount));
            }
        }

        if (result.Changed && changed is not null)
        {
            try
            {
                changed();
            }
            catch (Exception)
            {
            }
        }
    }

    private TimeSpan CalculateRetryDelay(int failureCount)
    {
        if (retryDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var multiplier = 1L << Math.Min(Math.Max(0, failureCount - 1), 10);
        return TimeSpan.FromMilliseconds(Math.Min(
            MaximumRetryDelay.TotalMilliseconds,
            retryDelay.TotalMilliseconds * multiplier));
    }

    private bool TryMakeRoomLocked(out string? evictedScope)
    {
        evictedScope = null;
        if (entries.Count < maximumEntries)
        {
            return true;
        }

        var evictionCandidate = entries
            .Where(pair => !pair.Value.InFlight && !pair.Value.PreserveFromEviction)
            .OrderBy(pair => pair.Value.LastAccessUtc)
            .Select(pair => pair.Key)
            .FirstOrDefault();
        if (evictionCandidate is null)
        {
            // Do not exceed the bound when every retained scope is still loading. A later
            // Ensure call can admit this scope after one of those loads completes.
            return false;
        }

        entries.Remove(evictionCandidate);
        evictedScope = evictionCandidate;
        return true;
    }

    private void RaiseEvictedSafely(string scope)
    {
        try
        {
            scopeEvicted?.Invoke(scope);
        }
        catch (Exception)
        {
        }
    }

    internal static void RaiseSafely(EventHandler? handlers, object sender)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(sender, EventArgs.Empty);
            }
            catch (Exception)
            {
            }
        }
    }

    private sealed class Entry(bool preserveFromEviction)
    {
        internal bool PreserveFromEviction { get; } = preserveFromEviction;
        internal bool InFlight { get; set; }
        internal int FailureCount { get; set; }
        internal DateTimeOffset NextAttemptUtc { get; set; }
        internal DateTimeOffset LastAccessUtc { get; set; }
    }
}

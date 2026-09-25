using System.Collections.ObjectModel;
using StreamlinkVlcStudio.Core.Logging;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

/// <summary>Bounds pending diagnostics to the same history the UI retains.</summary>
internal sealed class AppLogBuffer(
    ObservableCollection<string> lines,
    Action<Action> dispatch,
    Func<Action, bool>? tryDispatch) : IDisposable
{
    private const int MaximumLines = 250;
    private readonly object gate = new();
    private readonly Queue<LogEntry> pending = new(MaximumLines);
    private bool dispatchQueued;
    private volatile bool disposed;

    internal void Enqueue(LogEntry entry)
    {
        lock (gate)
        {
            if (disposed) return;
            if (pending.Count == MaximumLines) pending.Dequeue();
            pending.Enqueue(entry);
            if (dispatchQueued) return;
            dispatchQueued = true;
        }

        ScheduleDelivery();
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            pending.Clear();
        }
    }

    private void ScheduleDelivery()
    {
        try
        {
            if (tryDispatch is not null)
            {
                if (tryDispatch(Deliver)) return;
            }
            else
            {
                dispatch(Deliver);
                return;
            }
        }
        catch (Exception)
        {
            // Logging must not fail because its optional UI subscriber is unavailable.
            // Keep the bounded history so the next entry can retry dispatching it.
        }

        lock (gate) dispatchQueued = false;
    }

    private void Deliver()
    {
        LogEntry[] batch;
        lock (gate)
        {
            if (disposed)
            {
                dispatchQueued = false;
                return;
            }

            batch = pending.ToArray();
            pending.Clear();
        }

        try
        {
            while (!disposed && lines.Count + batch.Length > MaximumLines) lines.RemoveAt(0);
            foreach (var entry in batch)
            {
                if (disposed) return;
                lines.Add($"{entry.Timestamp:HH:mm:ss} [{entry.Level}] {entry.Source}: {entry.Message}");
            }
        }
        finally
        {
            bool needsDelivery;
            lock (gate)
            {
                needsDelivery = !disposed && pending.Count > 0;
                dispatchQueued = needsDelivery;
            }

            // Yield to other UI work between batches, even under continuous logging.
            if (needsDelivery) ScheduleDelivery();
        }
    }
}

using System.Diagnostics;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal sealed class BackgroundOperationController(IAppLogger logger)
{
    private readonly object gate = new();
    private readonly HashSet<Task> operations = [];

    public void Track(Task operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (gate)
        {
            operations.Add(operation);
        }

        _ = operation.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lock (gate)
                {
                    operations.Remove(operation);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal Task WaitForCurrentAsync()
    {
        Task[] pending;
        lock (gate) pending = operations.ToArray();
        return WaitForCompletionAsync(pending);
    }

    internal async Task WaitForIdleAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (gate) pending = operations.ToArray();
            if (pending.Length == 0) return;
            await WaitForCompletionAsync(pending).ConfigureAwait(false);
            lock (gate)
                foreach (var task in pending) operations.Remove(task);
        }
    }

    private static async Task WaitForCompletionAsync(Task[] pending)
    {
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch { /* Track observes failures; shutdown still waits for every operation. */ }
    }

    public async Task DrainAsync(TimeSpan timeout)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            Task[] pending;
            lock (gate)
            {
                pending = operations.ToArray();
            }

            if (pending.Length == 0)
            {
                return;
            }

            var remaining = timeout - Stopwatch.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                logger.WriteSafely(AppLogLevel.Warning, "UI", "Timed out waiting for background UI operations during shutdown.");
                return;
            }

            try
            {
                await Task.WhenAll(pending).WaitAsync(remaining);
            }
            catch (TimeoutException)
            {
                logger.WriteSafely(AppLogLevel.Warning, "UI", "Timed out waiting for background UI operations during shutdown.");
                return;
            }
            catch (OperationCanceledException)
            {
                // A canceled background operation is already finished for shutdown purposes.
            }
            catch (Exception ex)
            {
                logger.WriteSafely(AppLogLevel.Warning, "UI", "A background UI operation failed during shutdown.", ex);
            }
        }
    }
}

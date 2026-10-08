using System.Diagnostics;

namespace StreamlinkVlcStudio.Infrastructure.Threading;

/// <summary>Requests cancellation without letting failed callbacks interrupt the remaining cleanup.</summary>
internal static class CancellationSourceCleanup
{
    internal static void Cancel(CancellationTokenSource? source, Action<AggregateException>? reportFailure = null)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion can dispose the source before a captured cancellation request runs.
        }
        catch (AggregateException exception)
        {
            // Cancel has notified every callback and marked the token canceled.
            // Continue draining its consumers before releasing their resources.
            if (reportFailure is null)
            {
                Debug.WriteLine($"Cancellation callback failed: {exception}");
                return;
            }

            try { reportFailure(exception); }
            catch (Exception reportingException)
            {
                Debug.WriteLine($"Could not report cancellation failure: {reportingException}");
            }
        }
    }
}

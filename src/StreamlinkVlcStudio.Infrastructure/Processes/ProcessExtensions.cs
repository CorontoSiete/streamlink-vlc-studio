using System.ComponentModel;
using System.Diagnostics;

namespace StreamlinkVlcStudio.Infrastructure.Processes;

/// <summary>
/// Shared <see cref="Process"/> helpers. Consolidates the process-tree termination logic that was
/// previously duplicated across the streamlink, replay, and viewer service integrations.
/// </summary>
internal static class ProcessExtensions
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Kills the process and its entire child tree (when still running) and waits for exit, swallowing
    /// the benign races that occur when the process has already exited.
    /// </summary>
    internal static async Task KillProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(CleanupTimeout);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception or OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Observes redirected output reads after a process is terminated. Cancellation and stream
    /// teardown races are expected during forced process shutdown.
    /// </summary>
    internal static async Task ObserveOutputReadsAsync(Task standardOutputTask, Task standardErrorTask)
    {
        var readsTask = Task.WhenAll(standardOutputTask, standardErrorTask);
        try
        {
            await readsTask.WaitAsync(CleanupTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = readsTask.ContinueWith(
                static completedTask => _ = completedTask.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }
}

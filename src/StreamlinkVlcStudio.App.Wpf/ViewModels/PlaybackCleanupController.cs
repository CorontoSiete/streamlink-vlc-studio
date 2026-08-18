using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal sealed class PlaybackCleanupController(IAppLogger logger, Func<string> displayName)
{
    private readonly object gate = new();
    private readonly HashSet<Task> operations = [];
    private TaskCompletionSource idle = CreateCompletedTaskCompletion();

    public Task IdleTask
    {
        get
        {
            lock (gate)
            {
                return idle.Task;
            }
        }
    }

    public void Observe(Task operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (gate)
        {
            if (!operations.Add(operation))
            {
                return;
            }

            if (operations.Count == 1)
            {
                idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        _ = operation.ContinueWith(
            completed =>
            {
                if (completed.Exception is not null)
                {
                    logger.Write(
                        AppLogLevel.Warning,
                        "Playback",
                        $"Background playback cleanup failed for {displayName()}.",
                        completed.Exception.GetBaseException());
                }

                TaskCompletionSource? completedIdle = null;
                lock (gate)
                {
                    operations.Remove(completed);
                    if (operations.Count == 0)
                    {
                        completedIdle = idle;
                    }
                }

                completedIdle?.TrySetResult();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static TaskCompletionSource CreateCompletedTaskCompletion()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }
}

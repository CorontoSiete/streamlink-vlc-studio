using System.Runtime.ExceptionServices;

namespace StreamlinkVlcStudio.Infrastructure.Threading;

/// <summary>Publishes one disposal task before cleanup can invoke callbacks or acquire other locks.</summary>
internal static class AsyncDisposal
{
    /// <summary>Finishes independent cleanup steps before reporting any failures.</summary>
    internal static async Task RunAllAsync(params Func<ValueTask>[] cleanup)
    {
        List<Exception>? failures = null;
        foreach (var action in cleanup)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }

        if (failures is { Count: 1 }) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is { Count: > 1 }) throw new AggregateException(failures);
    }

    internal static Task Begin(
        object gate,
        ref bool disposed,
        ref Task? disposalTask,
        Func<Task> disposeAsync)
    {
        TaskCompletionSource completion;
        lock (gate)
        {
            if (disposalTask is not null) return disposalTask;

            disposed = true;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disposalTask = completion.Task;
        }

        // Reentrant callers share the published task. Cleanup runs outside the
        // state lock so cancellation callbacks cannot invert lifecycle locks.
        _ = CompleteAsync(completion, disposeAsync);
        return completion.Task;
    }

    private static async Task CompleteAsync(TaskCompletionSource completion, Func<Task> disposeAsync)
    {
        try
        {
            await disposeAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }
}

namespace StreamlinkVlcStudio.Infrastructure.Threading;

/// <summary>Publishes one disposal task before cleanup can invoke callbacks or acquire other locks.</summary>
internal static class AsyncDisposal
{
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

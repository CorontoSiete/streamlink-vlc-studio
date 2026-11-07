using System.Reflection;
using System.Windows.Threading;
using StreamlinkVlcStudio.App.Wpf.Chat;

internal static class ReviewLifecycleTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("review lifecycle: stopped overlay renderer completes selection requests", () => RendererShutdownAsync(false)),
        ("review lifecycle: overlay shutdown completes queued selection requests", () => RendererShutdownAsync(true))
    ];

    private static async Task RendererShutdownAsync(bool queueBeforeShutdown)
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = await NativeReplayOverlayFrameScheduler.CreateAsync(
            new MemoryLogger(), _ => { }, dispatcherStopped: () => stopped.TrySetResult());
        var dispatcher = (Dispatcher)typeof(NativeReplayOverlayFrameScheduler)
            .GetField("dispatcher", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scheduler)!;
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? selection = null;
        Task<Uri?>? click = null;
        Task<string?>? text = null;
        try
        {
            if (queueBeforeShutdown)
            {
                _ = dispatcher.BeginInvoke(new Action(() =>
                {
                    started.TrySetResult();
                    release.Wait(TimeSpan.FromSeconds(5));
                }), DispatcherPriority.Send);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                RequestSelection();
            }

            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            release.Set();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (!queueBeforeShutdown) RequestSelection();
            await Task.WhenAll(selection!, click!, text!).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(false, await selection!);
            Assert.True(await click! is null);
            Assert.True(await text! is null);
        }
        finally { release.Set(); }

        void RequestSelection()
        {
            selection = scheduler.HandleTextSelectionEventAsync(0, 0);
            click = scheduler.HandleTextClickEventAsync(0);
            text = scheduler.GetSelectedMessageBodyTextAsync();
        }
    }
}

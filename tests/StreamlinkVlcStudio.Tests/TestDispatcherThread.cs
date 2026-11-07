using System.Windows.Threading;

/// <summary>Runs input-routing tests on a dedicated dispatcher with optional initial blocking.</summary>
internal sealed class TestDispatcherThread : IAsyncDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private readonly TaskCompletionSource<Dispatcher> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim release = new();
    private readonly Thread thread;
    private Exception? threadFailure;
    private bool dispatcherGateReleased = true;

    private TestDispatcherThread(bool blockInitially)
    {
        thread = new Thread(() =>
        {
            try
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                if (blockInitially)
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                    {
                        blocked.TrySetResult();
                        dispatcherGateReleased = release.Wait(WaitTimeout);
                    }));
                }

                ready.TrySetResult(dispatcher);
                System.Windows.Threading.Dispatcher.Run();
            }
            catch (Exception exception)
            {
                threadFailure = exception;
                ready.TrySetException(exception);
                blocked.TrySetException(exception);
            }
            finally
            {
                stopped.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = "Input regression dispatcher"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    internal Dispatcher Dispatcher => ready.Task.GetAwaiter().GetResult();

    internal static async Task<TestDispatcherThread> StartAsync(bool blockInitially = false)
    {
        var fixture = new TestDispatcherThread(blockInitially);
        try
        {
            await fixture.ready.Task.WaitAsync(WaitTimeout);
            if (blockInitially)
            {
                await fixture.blocked.Task.WaitAsync(WaitTimeout);
            }

            return fixture;
        }
        catch
        {
            fixture.Release();
            if (fixture.ready.Task.IsCompletedSuccessfully)
            {
                fixture.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }

            _ = fixture.thread.Join(WaitTimeout);
            fixture.release.Dispose();
            throw;
        }
    }

    internal void Release() => release.Set();

    internal async Task FlushAsync()
    {
        // Input is higher priority, so completion observes all previously queued input callbacks.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task.WaitAsync(WaitTimeout);
    }

    internal async Task StopAsync()
    {
        Release();
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }

        await stopped.Task.WaitAsync(WaitTimeout);
        Assert.True(thread.Join(WaitTimeout), "The regression dispatcher did not stop.");
        Assert.True(threadFailure is null, threadFailure?.ToString() ?? "The dispatcher thread failed.");
        Assert.True(dispatcherGateReleased, "The busy dispatcher gate timed out before the test released it.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        finally
        {
            release.Dispose();
        }
    }
}

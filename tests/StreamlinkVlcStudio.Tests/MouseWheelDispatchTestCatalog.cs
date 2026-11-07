using System.Windows.Threading;

internal static class MouseWheelDispatchTestCatalog
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LongerThanHookTimeout = TimeSpan.FromMilliseconds(100);

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Mouse wheel dispatch preserves every queued delta while the UI is busy", BusyDispatcherRetainsWheelBurstAsync),
        ("Mouse wheel dispatch forwards unhandled input exactly once in order", UnhandledWheelFallsBackOnceAsync),
        ("Mouse wheel dispatch leaves foreign windows untouched", ForeignWindowWheelPassesThroughAsync),
        ("Mouse wheel dispatch leaves zero delta input untouched", ZeroDeltaWheelPassesThroughAsync),
        ("Mouse wheel dispatch passes through after dispatcher shutdown starts", ShutdownWheelPassesThroughAsync),
        ("Mouse wheel dispatch suppresses input while its UI route is still executing", ExecutingWheelRouteDoesNotPassThroughAsync)
    ];

    private static async Task BusyDispatcherRetainsWheelBurstAsync()
    {
        await using var ui = await TestDispatcherThread.StartAsync(blockInitially: true);
        var captured = new ConcurrentQueue<LowLevelMouseHookEvent>();
        var routed = new ConcurrentQueue<LowLevelMouseHookEvent>();
        var fallback = new ConcurrentQueue<LowLevelMouseHookEvent>();
        var router = new LowLevelMouseHookDispatcher(
            ui.Dispatcher,
            hookEvent =>
            {
                routed.Enqueue(hookEvent);
                return true;
            },
            () => false,
            captureWheelFallback: hookEvent =>
            {
                captured.Enqueue(hookEvent);
                return () => fallback.Enqueue(hookEvent);
            });
        var deltas = new[] { 120, 120, -120, 30, 30, -240, -120, 360 };
        var expected = Enumerable.Range(0, 8)
            .SelectMany(_ => deltas.Select(WheelEvent))
            .ToArray();

        // The hook thread must finish the entire burst before the dispatcher is released.
        // Identical adjacent packets are separate physical input, not duplicates to discard.
        var suppressed = await Task.Run(() => expected.Select(router.ProcessEvent).ToArray())
            .WaitAsync(WaitTimeout);
        Assert.True(suppressed.All(value => value), "Every accepted wheel packet must be suppressed immediately.");
        await Task.Delay(LongerThanHookTimeout);
        Assert.Equal(0, routed.Count);
        Assert.Equal(0, fallback.Count);
        Assert.SequenceEqual(expected, captured);

        ui.Release();
        await ui.FlushAsync();
        Assert.SequenceEqual(expected, routed);
        Assert.Equal(0, fallback.Count);
    }

    private static async Task UnhandledWheelFallsBackOnceAsync()
    {
        await using var ui = await TestDispatcherThread.StartAsync(blockInitially: true);
        var delivered = new ConcurrentQueue<(string Phase, LowLevelMouseHookEvent Event)>();
        var router = new LowLevelMouseHookDispatcher(
            ui.Dispatcher,
            hookEvent =>
            {
                delivered.Enqueue(("route", hookEvent));
                return false;
            },
            () => false,
            captureWheelFallback: hookEvent => () => delivered.Enqueue(("fallback", hookEvent)));
        var expected = new[] { 120, 120, -120, -30, 240 }.Select(WheelEvent).ToArray();

        var suppressed = await Task.Run(() => expected.Select(router.ProcessEvent).ToArray())
            .WaitAsync(WaitTimeout);
        Assert.True(suppressed.All(value => value));
        await Task.Delay(LongerThanHookTimeout);
        Assert.Equal(0, delivered.Count);

        ui.Release();
        await ui.FlushAsync();
        Assert.SequenceEqual(
            expected.SelectMany(hookEvent => new[] { ("route", hookEvent), ("fallback", hookEvent) }),
            delivered);
    }

    private static async Task ForeignWindowWheelPassesThroughAsync()
    {
        await using var ui = await TestDispatcherThread.StartAsync();
        var routeCount = 0;
        var captured = new ConcurrentQueue<LowLevelMouseHookEvent>();
        var router = new LowLevelMouseHookDispatcher(
            ui.Dispatcher,
            _ =>
            {
                Interlocked.Increment(ref routeCount);
                return true;
            },
            () => false,
            captureWheelFallback: hookEvent =>
            {
                captured.Enqueue(hookEvent);
                return null;
            });
        var hookEvent = WheelEvent(-120);

        Assert.Equal(false, router.ProcessEvent(hookEvent));
        await ui.FlushAsync();
        Assert.SequenceEqual(new[] { hookEvent }, captured);
        Assert.Equal(0, Volatile.Read(ref routeCount));
    }

    private static async Task ZeroDeltaWheelPassesThroughAsync()
    {
        await using var ui = await TestDispatcherThread.StartAsync();
        var routeCount = 0;
        var captureCount = 0;
        var fallbackCount = 0;
        var router = new LowLevelMouseHookDispatcher(
            ui.Dispatcher,
            _ =>
            {
                Interlocked.Increment(ref routeCount);
                return true;
            },
            () => false,
            captureWheelFallback: _ =>
            {
                Interlocked.Increment(ref captureCount);
                return () => Interlocked.Increment(ref fallbackCount);
            });

        Assert.Equal(false, router.ProcessEvent(WheelEvent(0)));
        await ui.FlushAsync();
        Assert.Equal(0, Volatile.Read(ref routeCount));
        Assert.Equal(0, Volatile.Read(ref captureCount));
        Assert.Equal(0, Volatile.Read(ref fallbackCount));
    }

    private static async Task ShutdownWheelPassesThroughAsync()
    {
        await using var ui = await TestDispatcherThread.StartAsync();
        var routeCount = 0;
        var fallbackCount = 0;
        var suppressedAtShutdownStart = true;
        var observedShutdownStart = false;
        var router = new LowLevelMouseHookDispatcher(
            ui.Dispatcher,
            _ =>
            {
                Interlocked.Increment(ref routeCount);
                return true;
            },
            () => false,
            captureWheelFallback: _ => () => Interlocked.Increment(ref fallbackCount));
        await ui.Dispatcher.InvokeAsync(() =>
        {
            // ShutdownStarted is raised before HasShutdownStarted is set. InvokeShutdown
            // inside a running dispatcher frame leaves the started state observable until
            // this callback returns and the frame exits.
            ui.Dispatcher.InvokeShutdown();
            observedShutdownStart = ui.Dispatcher.HasShutdownStarted && !ui.Dispatcher.HasShutdownFinished;
            suppressedAtShutdownStart = router.ProcessEvent(WheelEvent(120));
        }).Task.WaitAsync(WaitTimeout);

        await ui.StopAsync();
        Assert.True(observedShutdownStart);
        Assert.Equal(false, suppressedAtShutdownStart);
        Assert.Equal(false, router.ProcessEvent(WheelEvent(-120)));
        Assert.Equal(0, Volatile.Read(ref routeCount));
        Assert.Equal(0, Volatile.Read(ref fallbackCount));
    }

    private static async Task ExecutingWheelRouteDoesNotPassThroughAsync()
    {
        await using var ui = await TestDispatcherThread.StartAsync();
        using var releaseRoute = new ManualResetEventSlim();
        var routeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var routeCount = 0;
        var fallbackCount = 0;
        var routeGateReleased = false;
        var router = new LowLevelMouseHookDispatcher(
            ui.Dispatcher,
            _ =>
            {
                routeEntered.TrySetResult();
                routeGateReleased = releaseRoute.Wait(WaitTimeout);
                Interlocked.Increment(ref routeCount);
                return true;
            },
            () => false,
            captureWheelFallback: _ => () => Interlocked.Increment(ref fallbackCount));

        try
        {
            var hookResult = Task.Run(() => router.ProcessEvent(WheelEvent(120)));
            await routeEntered.Task.WaitAsync(WaitTimeout);
            await Task.Delay(LongerThanHookTimeout);
            Assert.Equal(true, await hookResult.WaitAsync(WaitTimeout));
            Assert.Equal(0, Volatile.Read(ref routeCount));
            Assert.Equal(0, Volatile.Read(ref fallbackCount));
        }
        finally
        {
            releaseRoute.Set();
            await ui.FlushAsync();
        }

        Assert.True(routeGateReleased, "The UI route must remain gated until the hook result has been observed.");
        Assert.Equal(1, Volatile.Read(ref routeCount));
        Assert.Equal(0, Volatile.Read(ref fallbackCount));
    }

    private static LowLevelMouseHookEvent WheelEvent(int delta)
    {
        return new LowLevelMouseHookEvent(LowLevelMouseHookEvent.WmMouseWheel, 100, 200, delta << 16);
    }
}

using System.Windows.Threading;

internal static class PictureInPictureContextMenuDispatchTestCatalog
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LongerThanHookTimeout = TimeSpan.FromMilliseconds(100);

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("PiP context menu dispatch preserves every captured click while the UI is busy", BusyDispatcherRetainsEveryClickAsync),
        ("PiP context menu dispatch retains the owner and coordinates captured at the press", CapturedOwnerSurvivesOwnershipChangesAsync),
        ("PiP context menu dispatch suppresses input while its callback is still executing", ExecutingCallbackDoesNotPassThroughAsync),
        ("PiP context menu dispatch leaves foreign right clicks untouched", ForeignRightClickPassesThroughAsync),
        ("PiP context menu dispatch suppresses only the matching owned release after pointer movement", MatchingOwnedReleaseAsync),
        ("PiP context menu dispatch passes through after dispatcher shutdown starts", ShutdownRightClickPassesThroughAsync)
    ];

    private static async Task BusyDispatcherRetainsEveryClickAsync()
    {
        await using var ui = await DispatcherThread.StartAsync(blockInitially: true);
        var captured = new ConcurrentQueue<(LowLevelMouseHookEvent Event, int ThreadId)>();
        var delivered = new ConcurrentQueue<(LowLevelMouseHookEvent Event, bool OnUiThread)>();
        var genericRouteCount = 0;
        var hookThreadId = 0;
        var router = new LowLevelMouseHookDispatcher(
            ui.Dispatcher,
            _ =>
            {
                Interlocked.Increment(ref genericRouteCount);
                return true;
            },
            () => false,
            captureContextMenuRoute: hookEvent =>
            {
                captured.Enqueue((hookEvent, Environment.CurrentManagedThreadId));
                return () => delivered.Enqueue((hookEvent, ui.Dispatcher.CheckAccess()));
            });
        // Adjacent identical packets are distinct presses. Include negative screen
        // coordinates to cover PiP windows on displays left of the primary display.
        var expected = Enumerable.Range(0, 32)
            .Select(index => RightDown(-300 + index / 2 * 23, 200 + index / 2 * 17))
            .ToArray();

        var suppressed = await Task.Run(() =>
        {
            hookThreadId = Environment.CurrentManagedThreadId;
            return expected.SelectMany(hookEvent => new[]
            {
                router.ProcessEvent(hookEvent),
                router.ProcessEvent(RightUp(hookEvent.ScreenX, hookEvent.ScreenY))
            }).ToArray();
        }).WaitAsync(WaitTimeout);

        Assert.True(suppressed.All(value => value), "Every accepted press and its release must be suppressed before the UI is available.");
        await Task.Delay(LongerThanHookTimeout);
        Assert.Equal(0, delivered.Count);
        Assert.Equal(0, Volatile.Read(ref genericRouteCount));
        Assert.SequenceEqual(expected, captured.Select(item => item.Event));
        Assert.True(captured.All(item => item.ThreadId == hookThreadId), "Ownership must be captured on the hook thread.");

        ui.Release();
        await ui.FlushAsync();
        Assert.SequenceEqual(expected, delivered.Select(item => item.Event));
        Assert.True(delivered.All(item => item.OnUiThread), "Captured menu callbacks must execute on the UI dispatcher.");
        Assert.Equal(0, Volatile.Read(ref genericRouteCount));
    }

    private static async Task CapturedOwnerSurvivesOwnershipChangesAsync()
    {
        await using var ui = await DispatcherThread.StartAsync(blockInitially: true);
        var delivered = new ConcurrentQueue<(string Owner, LowLevelMouseHookEvent Event)>();
        var currentOwner = "PiP at press";
        var genericRouteCount = 0;
        var captureCount = 0;
        var router = new LowLevelMouseHookDispatcher(
            ui.Dispatcher,
            _ =>
            {
                Interlocked.Increment(ref genericRouteCount);
                return true;
            },
            () => false,
            captureContextMenuRoute: hookEvent =>
            {
                Interlocked.Increment(ref captureCount);
                var capturedOwner = currentOwner;
                return () => delivered.Enqueue((capturedOwner, hookEvent));
            });
        var press = RightDown(127, 241);

        Assert.Equal(true, await Task.Run(() => router.ProcessEvent(press)).WaitAsync(WaitTimeout));
        currentOwner = "Other window after press";
        Assert.Equal(true, router.ProcessEvent(RightUp(1700, -60)));
        await Task.Delay(LongerThanHookTimeout);
        Assert.Equal(0, delivered.Count);

        ui.Release();
        await ui.FlushAsync();
        Assert.SequenceEqual(new[] { ("PiP at press", press) }, delivered);
        Assert.Equal(1, Volatile.Read(ref captureCount));
        Assert.Equal(0, Volatile.Read(ref genericRouteCount));
    }

    private static async Task ExecutingCallbackDoesNotPassThroughAsync()
    {
        await using var ui = await DispatcherThread.StartAsync();
        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        var genericRouteCount = 0;
        var callbackGateReleased = false;
        var router = new LowLevelMouseHookDispatcher(
            ui.Dispatcher,
            _ =>
            {
                Interlocked.Increment(ref genericRouteCount);
                return true;
            },
            () => false,
            captureContextMenuRoute: _ => () =>
            {
                callbackEntered.TrySetResult();
                callbackGateReleased = releaseCallback.Wait(WaitTimeout);
                Interlocked.Increment(ref callbackCount);
            });

        try
        {
            var hookResult = Task.Run(() => router.ProcessEvent(RightDown(100, 200)));
            await callbackEntered.Task.WaitAsync(WaitTimeout);
            await Task.Delay(LongerThanHookTimeout);
            Assert.Equal(true, await hookResult.WaitAsync(WaitTimeout));
            Assert.Equal(true, router.ProcessEvent(RightUp(100, 200)));
            Assert.Equal(0, Volatile.Read(ref callbackCount));
            Assert.Equal(0, Volatile.Read(ref genericRouteCount));
        }
        finally
        {
            releaseCallback.Set();
            await ui.FlushAsync();
        }

        Assert.True(callbackGateReleased, "The callback must remain gated until the hook has returned suppression.");
        Assert.Equal(1, Volatile.Read(ref callbackCount));
        Assert.Equal(0, Volatile.Read(ref genericRouteCount));
    }

    private static async Task ForeignRightClickPassesThroughAsync()
    {
        await using var ui = await DispatcherThread.StartAsync(blockInitially: true);
        var captured = new ConcurrentQueue<LowLevelMouseHookEvent>();
        var genericRouteCount = 0;
        var router = new LowLevelMouseHookDispatcher(
            ui.Dispatcher,
            _ =>
            {
                Interlocked.Increment(ref genericRouteCount);
                return true;
            },
            () => false,
            captureContextMenuRoute: hookEvent =>
            {
                captured.Enqueue(hookEvent);
                return null;
            });
        var press = RightDown(400, 500);

        var suppressed = await Task.Run(() => new[]
        {
            router.ProcessEvent(press),
            router.ProcessEvent(RightUp(400, 500))
        }).WaitAsync(WaitTimeout);
        Assert.SequenceEqual(new[] { false, false }, suppressed);
        Assert.SequenceEqual(new[] { press }, captured);

        ui.Release();
        await ui.FlushAsync();
        Assert.Equal(0, Volatile.Read(ref genericRouteCount));
    }

    private static async Task MatchingOwnedReleaseAsync()
    {
        await using var ui = await DispatcherThread.StartAsync(blockInitially: true);
        var captured = new ConcurrentQueue<LowLevelMouseHookEvent>();
        var callbackCount = 0;
        var genericRouteCount = 0;
        var ownedPress = RightDown(100, 200);
        var foreignPress = RightDown(3000, -150);
        var router = new LowLevelMouseHookDispatcher(
            ui.Dispatcher,
            _ =>
            {
                Interlocked.Increment(ref genericRouteCount);
                return true;
            },
            () => false,
            captureContextMenuRoute: hookEvent =>
            {
                captured.Enqueue(hookEvent);
                if (hookEvent.ScreenX != ownedPress.ScreenX || hookEvent.ScreenY != ownedPress.ScreenY)
                {
                    return null;
                }

                return () => Interlocked.Increment(ref callbackCount);
            });

        var suppressed = await Task.Run(() => new[]
        {
            router.ProcessEvent(RightUp(100, 200)),
            router.ProcessEvent(ownedPress),
            router.ProcessEvent(new LowLevelMouseHookEvent(LowLevelMouseHookEvent.WmMouseMove, 3000, -150, 0)),
            router.ProcessEvent(RightUp(3000, -150)),
            router.ProcessEvent(RightUp(100, 200)),
            router.ProcessEvent(foreignPress),
            router.ProcessEvent(RightUp(100, 200))
        }).WaitAsync(WaitTimeout);

        Assert.SequenceEqual(new[] { false, true, false, true, false, false, false }, suppressed);
        Assert.SequenceEqual(new[] { ownedPress, foreignPress }, captured);
        ui.Release();
        await ui.FlushAsync();
        Assert.Equal(1, Volatile.Read(ref callbackCount));
        Assert.Equal(0, Volatile.Read(ref genericRouteCount));
    }

    private static async Task ShutdownRightClickPassesThroughAsync()
    {
        await using var ui = await DispatcherThread.StartAsync();
        var genericRouteCount = 0;
        var captureCount = 0;
        var callbackCount = 0;
        var suppressedPressAtShutdownStart = true;
        var suppressedReleaseAtShutdownStart = true;
        var observedShutdownStart = false;
        var router = new LowLevelMouseHookDispatcher(
            ui.Dispatcher,
            _ =>
            {
                Interlocked.Increment(ref genericRouteCount);
                return true;
            },
            () => false,
            captureContextMenuRoute: _ =>
            {
                Interlocked.Increment(ref captureCount);
                return () => Interlocked.Increment(ref callbackCount);
            });

        await ui.Dispatcher.InvokeAsync(() =>
        {
            // InvokeShutdown inside a running dispatcher frame exposes the started
            // state until this callback returns and the frame exits.
            ui.Dispatcher.InvokeShutdown();
            observedShutdownStart = ui.Dispatcher.HasShutdownStarted && !ui.Dispatcher.HasShutdownFinished;
            suppressedPressAtShutdownStart = router.ProcessEvent(RightDown(100, 200));
            suppressedReleaseAtShutdownStart = router.ProcessEvent(RightUp(100, 200));
        }).Task.WaitAsync(WaitTimeout);

        await ui.StopAsync();
        Assert.True(observedShutdownStart);
        Assert.Equal(false, suppressedPressAtShutdownStart);
        Assert.Equal(false, suppressedReleaseAtShutdownStart);
        Assert.Equal(false, router.ProcessEvent(RightDown(100, 200)));
        Assert.Equal(false, router.ProcessEvent(RightUp(100, 200)));
        Assert.Equal(0, Volatile.Read(ref captureCount));
        Assert.Equal(0, Volatile.Read(ref callbackCount));
        Assert.Equal(0, Volatile.Read(ref genericRouteCount));
    }

    private static LowLevelMouseHookEvent RightDown(int x, int y) =>
        new(LowLevelMouseHookEvent.WmRightButtonDown, x, y, 0);

    private static LowLevelMouseHookEvent RightUp(int x, int y) =>
        new(LowLevelMouseHookEvent.WmRightButtonUp, x, y, 0);

    private sealed class DispatcherThread : IAsyncDisposable
    {
        private readonly TaskCompletionSource<Dispatcher> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim release = new();
        private readonly Thread thread;
        private Exception? threadFailure;
        private bool dispatcherGateReleased = true;

        private DispatcherThread(bool blockInitially)
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
                Name = "PiP context menu regression dispatcher"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        internal Dispatcher Dispatcher => ready.Task.GetAwaiter().GetResult();

        internal static async Task<DispatcherThread> StartAsync(bool blockInitially = false)
        {
            var fixture = new DispatcherThread(blockInitially);
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
            // Input is higher priority, so this observes all previously queued menus.
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
}

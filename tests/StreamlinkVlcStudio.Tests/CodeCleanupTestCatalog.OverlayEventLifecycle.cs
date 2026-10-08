internal static partial class CodeCleanupTestCatalog
{
    private static readonly TimeSpan OverlayEventTestTimeout = TimeSpan.FromSeconds(5);

    private static async Task OverlayEventRetiredListenerAsync(bool replace)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new NativeOverlayReplayEventHost(new MemoryLogger(), action =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            action();
        }, () => { }, () => 1080);
        var pipeName = $"overlay-retired-{Guid.NewGuid():N}";
        var path = Path.Combine(Path.GetTempPath(), pipeName);
        Task? retiredListener = null;
        Task? disposal = null;
        try
        {
            host.Start(pipeName, path);
            retiredListener = (Task)GetPrivateField(host, "listeningTask")!;
            await WriteCleanupOverlayScrollAsync(pipeName);
            await entered.Task.WaitAsync(OverlayEventTestTimeout);
            Task? replacementListener = null;
            if (replace)
            {
                host.Start(pipeName + "-next", path + "-next");
                replacementListener = (Task)GetPrivateField(host, "listeningTask")!;
            }
            else host.Stop();

            disposal = host.DisposeAsync().AsTask();
            if (replacementListener is not null) await replacementListener.WaitAsync(OverlayEventTestTimeout);
            Assert.True(!disposal.IsCompleted, "Disposal skipped the retired listener's pending dispatch.");
            Assert.True(ReferenceEquals(disposal, host.DisposeAsync().AsTask()));
            release.TrySetResult();
            await disposal.WaitAsync(OverlayEventTestTimeout);
            Assert.True(retiredListener.IsCompleted);
            Assert.True(!host.IsRunning);
        }
        finally
        {
            release.TrySetResult();
            if (retiredListener is not null) await retiredListener.WaitAsync(OverlayEventTestTimeout);
            await (disposal ?? host.DisposeAsync().AsTask()).WaitAsync(OverlayEventTestTimeout);
        }
    }

    private static async Task OverlayEventStartReentryAsync()
    {
        var host = new NativeOverlayReplayEventHost(new MemoryLogger(), action => action(), () => { }, () => 1080);
        var pipeName = $"overlay-reentry-{Guid.NewGuid():N}";
        var path = Path.Combine(Path.GetTempPath(), pipeName);
        CancellationTokenSource? nestedCancellation = null;
        Task? nestedListener = null;
        host.Start(pipeName, path);
        var initialCancellation = (CancellationTokenSource)GetPrivateField(host, "cancellation")!;
        var initialListener = (Task)GetPrivateField(host, "listeningTask")!;
        using var registration = initialCancellation.Token.Register(() =>
        {
            host.Start(pipeName + "-reentered", path + "-reentered");
            nestedCancellation = (CancellationTokenSource)GetPrivateField(host, "cancellation")!;
            nestedListener = (Task)GetPrivateField(host, "listeningTask")!;
        });
        try
        {
            host.Start(pipeName + "-outer", path + "-outer");
            Assert.Equal(pipeName + "-reentered", host.PipeName);
            await host.DisposeAsync().AsTask().WaitAsync(OverlayEventTestTimeout);
            Assert.True(initialListener.IsCompleted && nestedListener!.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => _ = nestedCancellation!.Token);
        }
        finally
        {
            registration.Dispose();
            // Also revoke the orphan created by the old implementation so a failed assertion
            // cannot leave its named pipe open for the remaining test suite.
            if (nestedCancellation is not null)
            {
                try { nestedCancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            await host.DisposeAsync().AsTask().WaitAsync(OverlayEventTestTimeout);
            await initialListener.WaitAsync(OverlayEventTestTimeout);
            if (nestedListener is not null) await nestedListener.WaitAsync(OverlayEventTestTimeout);
        }
    }

    private static async Task OverlayEventStartAfterDisposalAsync()
    {
        var host = new NativeOverlayReplayEventHost(new MemoryLogger(), action => action(), () => { }, () => 1080);
        await host.DisposeAsync();
        try
        {
            var pipeName = $"overlay-disposed-{Guid.NewGuid():N}";
            Assert.Throws<ObjectDisposedException>(() => host.Start(pipeName, Path.Combine(Path.GetTempPath(), pipeName)));
            Assert.True(!host.IsRunning);
        }
        finally { await host.StopAsync().WaitAsync(OverlayEventTestTimeout); }
    }

    private static async Task OverlayEventStopReentryAsync()
    {
        await using var host = new NativeOverlayReplayEventHost(new MemoryLogger(), action => action(), () => { }, () => 1080);
        var pipeName = $"overlay-stop-reentry-{Guid.NewGuid():N}";
        var path = Path.Combine(Path.GetTempPath(), pipeName);
        host.Start(pipeName, path);
        var cancellation = (CancellationTokenSource)GetPrivateField(host, "cancellation")!;
        using var registration = cancellation.Token.Register(() => host.Start(pipeName + "-restarted", path + "-restarted"));

        await host.StopAsync().WaitAsync(OverlayEventTestTimeout);

        Assert.Equal(pipeName + "-restarted", host.PipeName);
        Assert.True(host.IsRunning);
    }

    private static async Task BackgroundDrainDiagnosticFailureAsync(bool timeout)
    {
        var operations = new BackgroundOperationController(MemoryLogger.WithWriteFailure());
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operations.Track(work.Task);
        var draining = operations.DrainAsync(timeout ? TimeSpan.FromMilliseconds(25) : OverlayEventTestTimeout);
        try
        {
            if (!timeout) work.TrySetException(new IOException("Background operation failed."));
            await draining.WaitAsync(OverlayEventTestTimeout);
        }
        finally
        {
            work.TrySetResult();
            await operations.WaitForIdleAsync().WaitAsync(OverlayEventTestTimeout);
        }
    }

    private static async Task OverlayEventBusyDiagnosticsAsync()
    {
        var pipeName = $"overlay-busy-diagnostics-{Guid.NewGuid():N}";
        var busyLogged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new MemoryLogger();
        logger.EntryWritten += (_, entry) =>
        {
            if (entry.Message.Contains("pipe was busy", StringComparison.Ordinal)) busyLogged.TrySetResult();
        };
        logger.FailWrites();
        await using var busyPipe = new NamedPipeServerStream(pipeName + "_events", PipeDirection.In,
            1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var host = new NativeOverlayReplayEventHost(logger, action => action(), () => { }, () => 1080,
            replayScrolled: _ => received.TrySetResult());
        try
        {
            host.Start(pipeName, Path.Combine(Path.GetTempPath(), pipeName));
            await busyLogged.Task.WaitAsync(OverlayEventTestTimeout);
            await busyPipe.DisposeAsync();
            await WriteCleanupOverlayScrollAsync(pipeName);
            await received.Task.WaitAsync(OverlayEventTestTimeout);
            Assert.True(host.IsRunning);
        }
        finally
        {
            try { await host.DisposeAsync().AsTask().WaitAsync(OverlayEventTestTimeout); }
            catch (IOException) { }
        }
    }

    private static Task WriteCleanupOverlayScrollAsync(string pipeName) =>
        ApplicationTestCatalog.WriteNativeOverlayEventPipeMessageAsync(pipeName + "_events",
            ApplicationTestCatalog.BuildNativeOverlayEventMessage(NativeOverlayProtocolCodec.ScrollEventType, 1),
            OverlayEventTestTimeout);
}

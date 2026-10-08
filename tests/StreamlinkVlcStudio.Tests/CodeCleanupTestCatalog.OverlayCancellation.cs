internal static partial class CodeCleanupTestCatalog
{
    private static async Task FrameWriteShutdownCancellationFailureAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeReplayOverlayFrameWriteGate? writeGate = null;
        Task? reenteredDisposal = null;
        CancellationToken writerToken = default;
        writeGate = CreateCancellationWriteGate(async (_, token) =>
        {
            writerToken = token;
            using var registration = token.Register(() =>
            {
                reenteredDisposal = writeGate!.DisposeAsync().AsTask();
                throw new IOException("Injected frame-write shutdown cancellation failure.");
            });
            entered.TrySetResult();
            await release.Task;
            return new NativeReplayOverlayFrameWriteResult(true, null);
        });
        var lifetime = (CancellationTokenSource)GetPrivateField(writeGate, "lifetimeCancellation")!;
        Task? writer = null;
        try
        {
            writeGate.QueueWrite("cancellation-fixture", [1], 1);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            writer = (Task)GetPrivateField(writeGate, "writeLoopTask")!;

            var disposal = writeGate.DisposeAsync().AsTask();

            Assert.True(writerToken.IsCancellationRequested);
            Assert.True(ReferenceEquals(disposal, reenteredDisposal));
            Assert.True(ReferenceEquals(disposal, writeGate.DisposeAsync().AsTask()));
            Assert.True(!disposal.IsCompleted, "Frame shutdown skipped its outstanding writer.");
            writeGate.QueueWrite("cancellation-fixture", [2], 1);
            Assert.Equal(0L, writeGate.QueuedByteCount);
            release.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<ObjectDisposedException>(() => _ = lifetime.Token);
        }
        finally
        {
            release.TrySetResult();
            if (writer is not null) await writer.WaitAsync(TimeSpan.FromSeconds(5));
            // The failing implementation publishes a disposal task but never completes it.
            // Release the fixture without hiding that original failure behind another wait.
            lifetime.Dispose();
            writeGate.Dispose();
        }
    }

    private static async Task FrameWriteReplacementCancellationFailureAsync(bool supersedeClear)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken firstToken = default;
        await using var writeGate = CreateCancellationWriteGate(async (request, token) =>
        {
            if (request.Frame[0] == 1)
            {
                firstToken = token;
                using var registration = token.Register(() =>
                    throw new IOException("Injected frame replacement cancellation failure."));
                entered.TrySetResult();
                await release.Task;
            }
            else replacementWritten.TrySetResult();
            return new NativeReplayOverlayFrameWriteResult(true, null);
        });
        try
        {
            writeGate.QueueWrite("cancellation-fixture", [1], 1,
                isCritical: supersedeClear, writeKind: supersedeClear ? "critical-clear" : "frame");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            if (supersedeClear) writeGate.SupersedePersistentCriticalClears();
            else writeGate.Invalidate();

            Assert.True(firstToken.IsCancellationRequested);
            writeGate.QueueWrite("cancellation-fixture", [2], 1);
            release.TrySetResult();
            await replacementWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            if (GetPrivateField(writeGate, "writeLoopTask") is Task writer)
                await writer.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static NativeReplayOverlayFrameWriteGate CreateCancellationWriteGate(
        Func<NativeReplayOverlayFrameWriteRequest, CancellationToken, Task<NativeReplayOverlayFrameWriteResult>> write) =>
        new(new MemoryLogger(), write, () => 1, _ => { }, TimeSpan.FromSeconds(10));

    private static async Task OverlayEventStopCancellationFailureAsync()
    {
        await using var host = new NativeOverlayReplayEventHost(new MemoryLogger(), action => action(), () => { }, () => 1080);
        host.Start($"overlay-cancellation-{Guid.NewGuid():N}", Path.Combine(Path.GetTempPath(), $"overlay-cancellation-{Guid.NewGuid():N}"));
        var cancellation = (CancellationTokenSource)GetPrivateField(host, "cancellation")!;
        using var registration = cancellation.Token.Register(() =>
            throw new IOException("Injected overlay event cancellation failure."));

        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(!host.IsRunning);
        Assert.Equal<object?>(null, GetPrivateField(host, "listeningTask"));
        Assert.Throws<ObjectDisposedException>(() => _ = cancellation.Token);
        host.Start($"overlay-cancellation-restart-{Guid.NewGuid():N}",
            Path.Combine(Path.GetTempPath(), $"overlay-cancellation-restart-{Guid.NewGuid():N}"));
        Assert.True(host.IsRunning);
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(!host.IsRunning);
    }

    private static Task SeekPreviewHideCancellationFailureAsync() => TestSta.RunOffscreenAsync(() =>
    {
        var overlay = new ReplaySeekOverlay();
        using var cancellation = new CancellationTokenSource();
        var detachedBeforeCallback = false;
        using var registration = cancellation.Token.Register(() =>
        {
            detachedBeforeCallback = GetPrivateField(overlay, "previewCancellation") is null;
            throw new IOException("Injected seek preview cancellation failure.");
        });
        SetPrivateField(overlay, "previewCancellation", cancellation);
        var image = (Image)overlay.FindName("SeekPreviewImage");
        image.Source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[] { 0, 0, 0, 255 }, 4);

        overlay.IsOverlayEnabled = false;

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(detachedBeforeCallback, "Seek cancellation retained its old request while invoking callbacks.");
        Assert.Equal<object?>(null, GetPrivateField(overlay, "previewCancellation"));
        Assert.Equal<ImageSource?>(null, image.Source);
        Assert.True(!overlay.IsSeekHoverOpen);
        return Task.CompletedTask;
    });
}

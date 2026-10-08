internal static partial class CodeCleanupTestCatalog
{
    private static async Task NativeOverlayShutdownCallbackFailureAsync()
    {
        await using var tab = CreateCleanupTab();
        var overlay = (NativeChatOverlayController)GetPrivateField(tab, "nativeOverlay")!;
        await AssertViewModelShutdownAsync(overlay);
        var writeGate = GetPrivateField(overlay, "nativeReplayOverlayFrameWriteGate")!;
        Assert.Equal(true, GetPrivateField(writeGate, "disposed"));
    }

    private static async Task NativeOverlayStartupCancellationReentryAsync()
    {
        await using var tab = CreateCleanupTab();
        var overlay = (NativeChatOverlayController)GetPrivateField(tab, "nativeOverlay")!;
        using var cancellation = new CancellationTokenSource();
        SetPrivateField(overlay, "nativeOverlayStartupCancellation", cancellation);
        Task? reentry = null;
        var callbackFinished = false;
        using var registration = cancellation.Token.Register(() =>
        {
            reentry = Task.Run(() => overlay.IsCurrentNativeOverlayStartup(0, CancellationToken.None));
            callbackFinished = reentry.Wait(TimeSpan.FromSeconds(1));
        });

        await overlay.StartNativeOverlayChatTrackedAsync(new AppSettings(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));
        await reentry!.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(callbackFinished, "Overlay cancellation blocked reentrant access to its startup state.");
    }

    private static async Task NativeOverlayShutdownDiagnosticsAsync()
    {
        foreach (var schedulerFailure in new[] { false, true })
        {
            await using var tab = CreateCleanupTab(MemoryLogger.WithWriteFailure());
            var overlay = (NativeChatOverlayController)GetPrivateField(tab, "nativeOverlay")!;
            var cancellation = (CancellationTokenSource)GetPrivateField(overlay, "lifetimeCancellation")!;
            var failure = new IOException("Injected overlay startup failure.");
            if (schedulerFailure)
                SetPrivateField(overlay, "nativeReplayOverlayFrameSchedulerCreationTask",
                    Task.FromException<NativeReplayOverlayFrameScheduler>(failure));
            else
                SetPrivateField(overlay, "nativeOverlayStartupTask", Task.FromException(failure));

            await overlay.DisposeAsync();

            Assert.Throws<ObjectDisposedException>(() => _ = cancellation.Token);
            var writeGate = GetPrivateField(overlay, "nativeReplayOverlayFrameWriteGate")!;
            Assert.Equal(true, GetPrivateField(writeGate, "disposed"));
        }
    }

    private static async Task NativeOverlayAnimationCancellationFailureAsync()
    {
        await using var tab = CreateCleanupTab();
        var overlay = (NativeChatOverlayController)GetPrivateField(tab, "nativeOverlay")!;
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(() =>
            throw new IOException("Injected animation cancellation failure."));
        SetPrivateField(overlay, "nativeReplayOverlayAnimationCancellation", cancellation);
        SetPrivateField(overlay, "nativeReplayOverlayActiveImageCachePinOwner", new object());

        overlay.CancelNativeReplayOverlayAnimationState();

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal<object?>(null, GetPrivateField(overlay, "nativeReplayOverlayAnimationCancellation"));
        Assert.Equal<object?>(null, GetPrivateField(overlay, "nativeReplayOverlayActiveImageCachePinOwner"));
    }
}

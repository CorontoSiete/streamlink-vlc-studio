internal static partial class CodeCleanupTestCatalog
{
    private static async Task ReplayPreparationCancellationFailureAsync(bool disposalFailure)
    {
        using var fixture = new CleanupReplayPreparation(disposalFailure);
        using var registration = fixture.Cancellation.Token.Register(() =>
        {
            Assert.Equal<object?>(null, GetPrivateField(fixture.Engine, "replayPreparation"));
            if (!disposalFailure) throw new IOException("Injected replay preparation cancellation failure.");
        });

        fixture.Cancel();

        Assert.True(fixture.Cancellation.IsCancellationRequested);
        Assert.True(!fixture.Worker.IsCompleted, "Replay cancellation completed an unfinished preparation.");
        Assert.Equal(0, fixture.Lease.DisposeCount);
        _ = fixture.Cancellation.Token;
        fixture.Complete();
        await fixture.Lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await TestWait.UntilAsync(() => IsCleanupCancellationDisposed(fixture.Cancellation), TimeSpan.FromSeconds(3));
        Assert.Equal(1, fixture.Lease.DisposeCount);
        Assert.True(!fixture.Lease.DisposedInsideNativeGate, "Prepared native input cleanup ran inside the player lock.");
    }

    private static async Task ReplayPreparationWorkerFailureAsync()
    {
        using var fixture = new CleanupReplayPreparation();
        fixture.Logger.FailWrites();
        var failure = new IOException("Injected prepared replay worker failure.");
        fixture.Fail(failure);

        fixture.Cancel();

        await TestWait.UntilAsync(() => IsCleanupCancellationDisposed(fixture.Cancellation), TimeSpan.FromSeconds(3));
        Assert.Equal<object?>(null, GetPrivateField(fixture.Engine, "replayPreparation"));
        Assert.True(fixture.Logger.Entries.Any(entry => ReferenceEquals(entry.Exception?.GetBaseException(), failure)),
            "The failed replay preparation was discarded without observing or reporting its exception.");
    }

    private static bool IsCleanupCancellationDisposed(CancellationTokenSource cancellation)
    {
        try { _ = cancellation.Token; return false; }
        catch (ObjectDisposedException) { return true; }
    }

    // Exercise preparation ownership without opening a native player or loading network media.
    // Only the detached state is populated; native handles stay zero throughout this fixture.
    private sealed class CleanupReplayPreparation : IDisposable
    {
        private readonly object nativeGate = new();
        private readonly object completion;
        private readonly object input;
        private readonly Type completionType;
        private readonly PlaybackMediaSource source;
        internal LibVlcPlaybackEngine Engine { get; } =
            (LibVlcPlaybackEngine)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibVlcPlaybackEngine));
        internal CancellationTokenSource Cancellation { get; } = new();
        internal MemoryLogger Logger { get; } = new();
        internal CleanupPreparedReplayLease Lease { get; }
        internal Task Worker { get; }

        internal CleanupReplayPreparation(bool disposalFailure = false)
        {
            var engineType = typeof(LibVlcPlaybackEngine);
            var inputType = engineType.GetNestedType("PreparedReplayInput", BindingFlags.NonPublic)!;
            var preparationType = engineType.GetNestedType("ReplayPreparation", BindingFlags.NonPublic)!;
            completionType = typeof(TaskCompletionSource<>).MakeGenericType(inputType);
            completion = Activator.CreateInstance(completionType, TaskCreationOptions.RunContinuationsAsynchronously)!;
            Worker = (Task)completionType.GetProperty("Task")!.GetValue(completion)!;
            Lease = new CleanupPreparedReplayLease(nativeGate, disposalFailure);
            var uri = new Uri("https://example.com/preparation.m3u8");
            source = new PlaybackMediaSource(uri, Lease);
            input = CreateDetachedReplayState(inputType, [source]);
            var preparation = CreateDetachedReplayState(preparationType, [uri, Cancellation, Worker]);
            SetPrivateField(Engine, "nativeGate", nativeGate);
            SetPrivateField(Engine, "logger", Logger);
            SetPrivateField(Engine, "replayPreparation", preparation);
        }

        internal void Cancel()
        {
            lock (nativeGate)
                typeof(LibVlcPlaybackEngine).GetMethod("CancelReplayPreparationCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(Engine, null);
        }
        internal void Complete() => completionType.GetMethod("TrySetResult")!.Invoke(completion, [input]);
        internal void Fail(Exception exception) => completionType.GetMethod("TrySetException", [typeof(Exception)])!
            .Invoke(completion, [exception]);
        public void Dispose()
        {
            Complete();
            try { source.Dispose(); }
            catch (IOException) { }
            Cancellation.Dispose();
            _ = Worker.Exception;
        }
        private static object CreateDetachedReplayState(Type type, object[] arguments) =>
            Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: arguments, culture: null)!;
    }

    private sealed class CleanupPreparedReplayLease(object nativeGate, bool disposalFailure) : IDisposable
    {
        private int disposeCount;
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCount => Volatile.Read(ref disposeCount);
        internal bool DisposedInsideNativeGate { get; private set; }
        public void Dispose()
        {
            Interlocked.Increment(ref disposeCount);
            DisposedInsideNativeGate = Monitor.IsEntered(nativeGate);
            Disposed.TrySetResult();
            if (disposalFailure) throw new IOException("Injected prepared replay lease disposal failure.");
        }
    }
}

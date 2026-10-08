internal static partial class CodeCleanupTestCatalog
{
    private static async Task PlaybackGatewayDiagnosticsAsync()
    {
        using var fixture = new DetachedPlaybackEngine(useAvformat: false, failSourceDisposal: false);
        var gateway = new UnavailableMediaGateway();
        SetPrivateField(fixture.Engine, "logger", MemoryLogger.WithWriteFailure());
        SetPrivateField(fixture.Engine, "mediaSourceGateway", gateway);
        var uri = new Uri("https://example.com/replay.m3u8");
        var prepare = typeof(LibVlcPlaybackEngine).GetMethod("PrepareMediaSourceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var source = await (Task<PlaybackMediaSource>)prepare.Invoke(fixture.Engine, [uri, CancellationToken.None, true])!;

        Assert.Equal(uri, source.PlaybackUri);
        Assert.Equal(1, gateway.Calls);
    }

    private sealed class UnavailableMediaGateway : IPlaybackMediaSourceGateway
    {
        internal int Calls { get; private set; }
        public Task<PlaybackMediaSource> PrepareAsync(Uri mediaUri, Version? libVlcVersion, CancellationToken cancellationToken,
            bool preferFastReplay = false)
        {
            Calls++;
            throw new IOException("The media source gateway is unavailable.");
        }
    }

    private static async Task PlaybackSourceDisposalFailureAsync(bool disposeEngine)
    {
        foreach (var useAvformat in new[] { false, true })
        {
            using var fixture = new DetachedPlaybackEngine(useAvformat, failSourceDisposal: true);
            if (disposeEngine) Assert.Throws<IOException>(fixture.Engine.Dispose);
            else await Assert.ThrowsAsync<IOException>(() => fixture.Engine.StopAsync());

            Assert.True(fixture.PauseReady.SafeWaitHandle.IsClosed, "Playback retained its pause event after source cleanup failed.");
            Assert.True(fixture.VideoReady.SafeWaitHandle.IsClosed, "Playback retained its video event after source cleanup failed.");
            Assert.Equal<object?>(null, GetPrivateField(fixture.Engine, "currentMediaSource"));
            Assert.Equal(1, fixture.SourceLease.DisposeCount);
            if (disposeEngine)
            {
                Assert.Equal(1, fixture.RuntimeReleases);
                Assert.Equal<object?>(null, GetPrivateField(fixture.Engine, "runtimeLease"));
                Assert.Equal(IntPtr.Zero, GetPrivateField(fixture.Engine, "instance"));
                await TestWait.UntilAsync(() => IsCleanupCancellationDisposed(fixture.Cancellation), TimeSpan.FromSeconds(3));
            }
            else
            {
                Assert.Equal(0, fixture.RuntimeReleases);
                fixture.Engine.Dispose();
                Assert.Equal(1, fixture.RuntimeReleases);
            }
        }
    }

    private static async Task PlaybackWorkerCancellationFailureAsync()
    {
        using var fixture = new DetachedPlaybackEngine(useAvformat: false, failSourceDisposal: false);
        using var registration = fixture.Cancellation.Token.Register(() => throw new IOException("Worker cancellation failed."));

        fixture.Engine.Dispose();

        Assert.Equal(1, fixture.RuntimeReleases);
        await TestWait.UntilAsync(() => IsCleanupCancellationDisposed(fixture.Cancellation), TimeSpan.FromSeconds(3));
        Assert.Throws<ObjectDisposedException>(() => fixture.AudioSignal.Release());
        fixture.Engine.Dispose();
        Assert.Equal(1, fixture.RuntimeReleases);
    }

    // Leave every native media/player handle zero. Exercise real ownership and worker
    // teardown with a counted runtime lease, without loading VLC or opening a window.
    private sealed class DetachedPlaybackEngine : IDisposable
    {
        private readonly RuntimeLease runtime;
        private readonly PlaybackMediaSource source;
        internal LibVlcPlaybackEngine Engine { get; } =
            (LibVlcPlaybackEngine)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibVlcPlaybackEngine));
        internal CancellationTokenSource Cancellation { get; } = new();
        internal SemaphoreSlim AudioSignal { get; } = new(0, 1);
        internal EventWaitHandle PauseReady { get; } = new(false, EventResetMode.ManualReset);
        internal EventWaitHandle VideoReady { get; } = new(false, EventResetMode.ManualReset);
        internal CleanupPreparedReplayLease SourceLease { get; }
        internal int RuntimeReleases { get; private set; }

        internal DetachedPlaybackEngine(bool useAvformat, bool failSourceDisposal)
        {
            var nativeGate = new object();
            runtime = new RuntimeLease(new IntPtr(1), () => RuntimeReleases++);
            SourceLease = new CleanupPreparedReplayLease(nativeGate, failSourceDisposal);
            source = new PlaybackMediaSource(new Uri("https://example.com/replay.m3u8"), SourceLease, useAvformatDemuxer: useAvformat);
            SetPrivateField(Engine, "nativeGate", nativeGate);
            SetPrivateField(Engine, "logger", new MemoryLogger());
            SetPrivateField(Engine, "audioStateController", new LibVlcAudioStateController());
            SetPrivateField(Engine, "runtimeLease", runtime);
            SetPrivateField(Engine, "instance", runtime.Instance);
            SetPrivateField(Engine, "currentMediaSource", source);
            SetPrivateField(Engine, "replayPauseReady", PauseReady);
            SetPrivateField(Engine, "replayVideoReady", VideoReady);
            SetPrivateField(Engine, "audioApplyCancellation", Cancellation);
            SetPrivateField(Engine, "audioApplySignal", AudioSignal);
            SetPrivateField(Engine, "audioApplyTask", Task.Delay(Timeout.InfiniteTimeSpan, Cancellation.Token));
        }

        public void Dispose()
        {
            try { Engine.Dispose(); }
            catch (Exception error) when (error is IOException or AggregateException) { }
            // A failed regression must also release its deliberately detached resources.
            try { source.Dispose(); }
            catch (IOException) { }
            runtime.Dispose();
            PauseReady.Dispose();
            VideoReady.Dispose();
            if (!IsCleanupCancellationDisposed(Cancellation)) Cancellation.Cancel();
            Cancellation.Dispose();
            AudioSignal.Dispose();
        }
    }
}

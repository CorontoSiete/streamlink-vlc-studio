using StreamlinkVlcStudio.Infrastructure.Previews;

internal static partial class StreamHoverPreviewTestCatalog
{
    private static IReadOnlyList<(string Name, Func<Task> Run)> CleanupTests =>
    [
        ("stream hover preview: cleanup replaces a session after a cancellation callback fails", ReplacementCallbackFailureAsync),
        ("stream hover preview: cleanup publishes shutdown before reentrant cancellation and drains playback", ShutdownCallbackFailureAsync),
        ("stream hover preview: cleanup invokes cancellation outside controller and session locks", CancellationLocksAsync),
        ("stream hover preview: cleanup isolates failed frame subscribers and keeps delivering state", SubscriberFailureAsync),
        ("review preview diagnostics: failed logging allows the next hover to play", FailureDiagnosticsAsync)
    ];

    private static async Task FailureDiagnosticsAsync()
    {
        var attempts = 0;
        var controller = new StreamHoverPreviewController(Settings(), MemoryLogger.WithWriteFailure(),
            async (_, _, present, token) =>
            {
                if (Interlocked.Increment(ref attempts) == 1) throw new IOException("Preview unavailable.");
                present(Frame());
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, TimeSpan.Zero);
        try
        {
            var first = controller.Begin(Target())!;
            await WaitAsync(() => first.State == StreamHoverPreviewState.Unavailable);
            var second = controller.Begin(Target("second"))!;
            await WaitAsync(() => second.State is StreamHoverPreviewState.Playing or StreamHoverPreviewState.Unavailable);
            Assert.Equal(StreamHoverPreviewState.Playing, second.State);
            Assert.NotNull(second.TakeFrame());
            Assert.Equal(2, attempts);
        }
        finally
        {
            try { await controller.DisposeAsync(); }
            catch (IOException) { }
        }
    }

    private static async Task ReplacementCallbackFailureAsync()
    {
        var entered = Signal();
        await using var controller = new StreamHoverPreviewController(Settings(), new MemoryLogger(),
            async (request, _, present, token) =>
            {
                var wait = Task.Delay(Timeout.InfiniteTimeSpan, token);
                using var registration = token.Register(() =>
                {
                    if (request.Target.Channel == "first")
                        throw new InvalidOperationException("Injected preview cancellation failure.");
                });
                if (request.Target.Channel == "first") entered.TrySetResult();
                else present(Frame());
                await wait;
            }, TimeSpan.Zero);
        var first = controller.Begin(Target())!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = controller.Begin(Target("second"))!;
        await WaitAsync(() => second.State == StreamHoverPreviewState.Playing);
        Assert.True(first.Token.IsCancellationRequested);
        Assert.Equal(StreamHoverPreviewState.Stopped, first.State);
        Assert.NotNull(second.TakeFrame());
    }

    private static async Task ShutdownCallbackFailureAsync()
    {
        var entered = Signal();
        var release = Signal();
        var controller = new StreamHoverPreviewController(Settings(), new MemoryLogger(),
            async (_, _, _, _) => { entered.TrySetResult(); await release.Task; }, TimeSpan.Zero);
        var session = controller.Begin(Target())!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task? reentered = null;
        using var registration = session.Token.Register(() =>
        {
            reentered = controller.DisposeAsync().AsTask();
            throw new InvalidOperationException("Injected preview shutdown failure.");
        });
        try
        {
            var closing = controller.DisposeAsync().AsTask();
            Assert.True(ReferenceEquals(closing, reentered));
            Assert.True(!closing.IsCompleted, "Preview shutdown did not wait for playback cleanup.");
            Assert.True(controller.Begin(Target("second")) is null);
            release.TrySetResult();
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(ReferenceEquals(closing, controller.DisposeAsync().AsTask()));
            var cancellation = (CancellationTokenSource)typeof(StreamHoverPreviewSession)
                .GetField("cancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            Assert.Throws<ObjectDisposedException>(() => _ = cancellation.Token);
        }
        finally
        {
            registration.Dispose();
            release.TrySetResult();
            await controller.DisposeAsync();
        }
    }

    private static async Task CancellationLocksAsync()
    {
        var entered = Signal();
        await using var controller = new StreamHoverPreviewController(Settings(), new MemoryLogger(),
            async (_, _, _, token) => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }, TimeSpan.Zero);
        var first = controller.Begin(Target())!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var controllerGate = typeof(StreamHoverPreviewController)
            .GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
        var sessionGate = typeof(StreamHoverPreviewSession)
            .GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(first)!;
        var controllerLocked = false;
        var sessionLocked = false;
        using var registration = first.Token.Register(() =>
        {
            controllerLocked = Monitor.IsEntered(controllerGate);
            sessionLocked = Monitor.IsEntered(sessionGate);
        });
        controller.Begin(Target("second"));
        Assert.True(!controllerLocked, "Cancellation callbacks ran while holding the preview controller lock.");
        Assert.True(!sessionLocked, "Cancellation callbacks ran while holding the preview session lock.");
    }

    private static async Task SubscriberFailureAsync()
    {
        var entered = Signal();
        var release = Signal();
        var logger = new MemoryLogger();
        var controller = new StreamHoverPreviewController(Settings(), logger,
            async (_, _, _, _) => { entered.TrySetResult(); await release.Task; }, TimeSpan.Zero);
        var session = controller.Begin(Target())!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Action broken = () => throw new InvalidOperationException("Injected preview subscriber failure.");
        var delivered = 0;
        session.Changed += broken;
        session.Changed += () => delivered++;
        try
        {
            session.Present(Frame());
            Assert.Equal((byte)1, session.TakeFrame()!.Pixels[0]);
            session.SetState(StreamHoverPreviewState.Unavailable);
            session.Stop();
            Assert.Equal(3, delivered);
            Assert.Equal(StreamHoverPreviewState.Stopped, session.State);
            Assert.True(logger.Entries.Count >= 3);
        }
        finally
        {
            session.Changed -= broken;
            release.TrySetResult();
            await controller.DisposeAsync();
        }
    }
}

internal static partial class StreamHoverPreviewSourceTestCatalog
{
    private static IReadOnlyList<(string Name, Func<Task> Run)> PlaylistCleanupTests =>
    [
        ("stream hover preview: cleanup drains playlist shutdown after reentrant cancellation fails", PlaylistShutdownCallbackFailureAsync),
        ("stream hover preview: cleanup releases playlist sources after a refresh worker faults", PlaylistWorkerFailureAsync),
        ("stream hover preview: cleanup survives a failed fallback cancellation subscriber", PlaylistFallbackCallbackFailureAsync)
    ];

    private static CancellationTokenSource PlaylistCancellation(LivePreviewPlaylistSession source, string name) =>
        (CancellationTokenSource)typeof(LivePreviewPlaylistSession)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;

    private static async Task PlaylistShutdownCallbackFailureAsync()
    {
        var entered = Signal();
        var release = Signal();
        var source = new LivePreviewPlaylistSession(Media, async _ => { entered.TrySetResult(); await release.Task; return Media; });
        var lifetime = PlaylistCancellation(source, "lifetime");
        var fallback = PlaylistCancellation(source, "fallback");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await client.GetStringAsync(source.PlaybackUri);
        var request = client.GetStringAsync(source.PlaybackUri);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task? reentered = null;
        using var registration = lifetime.Token.Register(() =>
        {
            reentered = source.DisposeAsync().AsTask();
            throw new InvalidOperationException("Injected playlist shutdown failure.");
        });
        try
        {
            var closing = source.DisposeAsync().AsTask();
            Assert.True(ReferenceEquals(closing, reentered), "Reentrant playlist shutdown received a different task.");
            Assert.True(!closing.IsCompleted, "Playlist shutdown skipped its outstanding refresh.");
            release.TrySetResult();
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<ObjectDisposedException>(() => _ = lifetime.Token);
            Assert.Throws<ObjectDisposedException>(() => _ = fallback.Token);
            source.Stop(); // Native cancellation may race completed playlist disposal.
        }
        finally
        {
            registration.Dispose();
            release.TrySetResult();
            try { await source.DisposeAsync(); }
            catch (AggregateException) { }
            await Assert.ThrowsAsync<HttpRequestException>(() => request);
            lifetime.Dispose();
            fallback.Dispose();
        }
    }

    private static async Task PlaylistWorkerFailureAsync()
    {
        var source = new LivePreviewPlaylistSession(Media, _ => Task.FromException<string>(
            new InvalidCastException("Injected playlist worker failure.")));
        var lifetime = PlaylistCancellation(source, "lifetime");
        var fallback = PlaylistCancellation(source, "fallback");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            await client.GetStringAsync(source.PlaybackUri);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(source.PlaybackUri));
            await Assert.ThrowsAsync<InvalidCastException>(() => source.DisposeAsync().AsTask());
            Assert.Throws<ObjectDisposedException>(() => _ = lifetime.Token);
            Assert.Throws<ObjectDisposedException>(() => _ = fallback.Token);
        }
        finally
        {
            try { await source.DisposeAsync(); }
            catch (InvalidCastException) { }
            lifetime.Dispose();
            fallback.Dispose();
        }
    }

    private static async Task PlaylistFallbackCallbackFailureAsync()
    {
        var source = new LivePreviewPlaylistSession(Media, _ => Task.FromException<string>(
            new InvalidDataException("Injected unsupported playlist.")));
        var fallback = PlaylistCancellation(source, "fallback");
        using var registration = source.FallbackToken.Register(() =>
            throw new InvalidOperationException("Injected fallback cancellation failure."));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            await client.GetStringAsync(source.PlaybackUri);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(source.PlaybackUri));
            await source.DisposeAsync();
            Assert.Throws<ObjectDisposedException>(() => _ = fallback.Token);
        }
        finally
        {
            registration.Dispose();
            try { await source.DisposeAsync(); }
            catch (AggregateException) { }
            PlaylistCancellation(source, "lifetime").Dispose();
            fallback.Dispose();
        }
    }
}

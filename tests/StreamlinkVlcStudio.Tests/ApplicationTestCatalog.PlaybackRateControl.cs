internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> PlaybackRateControlTests =>
    [
        ("playback speed control: a newer selection cancels an obsolete wait", PlaybackRateSupersededWaitAsync),
        ("playback speed control: rejection restores the last speed actually applied", PlaybackRateSupersededSuccessAsync),
        ("playback speed control: reload cancels pending speed selections", PlaybackRateReloadCancellationAsync),
        ("playback speed control: completion from an old player cannot overwrite a reload", PlaybackRateReloadCompletionAsync),
        ("playback speed control: invalid selections preserve a pending valid speed", PlaybackRateInvalidSelectionAsync),
        ("playback speed control: seeking cancels a pending selection and restores control availability", PlaybackRateSeekCancellationAsync),
        ("playback speed control: live edge respects a pending slower selection", PlaybackRateLiveEdgePendingSelectionAsync),
        ("playback speed control: live edge discards a sample older than the manual selection", PlaybackRateLiveEdgeStaleSampleAsync),
        .. PlaybackRateClockTests,
        .. string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ? [] :
            new (string, Func<Task>)[]
            {
                ("playback speed control: native repeated speed avoids another recovery wait", NativePlaybackRateNoOpAsync),
                ("playback speed control: native stop invalidates pending speed requests", NativePlaybackRateStopAsync),
                ("playback speed control: native seek invalidates pending speed requests", NativePlaybackRateSeekAsync),
                ("playback speed control: native cancellation preserves the applied speed", NativePlaybackRateCancellationAsync),
                ("playback speed control: cancelling fallback slow recovery restores the original native speed", NativePlaybackRateSlowRecoveryCancellationAsync),
                ("playback speed control: seeking during fallback slow recovery retains the original speed", NativePlaybackRateSlowRecoverySeekAsync),
                ("playback speed control: replacement during fallback slow recovery inherits the original speed", NativePlaybackRateSlowRecoveryReplacementAsync),
                ("playback speed control: native paused selections retain position and apply on resume", NativePlaybackRatePausedAsync),
                ("playback speed control: native speed selections keep the seekbar synchronized", NativePlaybackRateClockAsync),
                ("playback speed control: all speeds decode at the requested rate in a completed VOD", () => NativePlaybackRateOptionsAsync(false)),
                ("playback speed control: all speeds decode at the requested rate in a muted VOD", () => NativePlaybackRateOptionsAsync(true)),
                ("playback speed control: all speeds decode at the requested rate in a video-only VOD", () => NativePlaybackRateOptionsAsync(false, withAudio: false))
            }
    ];

    private static Task PlaybackRateSupersededWaitAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = await ReplayOverlayTestSession.CreateAsync(playbackRateOverride: async (rate, token) =>
        {
            if (rate == 1.25f)
            {
                started.TrySetResult();
                await finish.Task.WaitAsync(token);
            }
            return true;
        });
        await session.Tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        await StopReplayClockPollingAsync(session.Tab);
        try
        {
            session.Tab.PlaybackRateIndex = 3;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            session.Tab.PlaybackRateIndex = 5;
            await TestWait.UntilAsync(() => session.PlaybackFactory.Engine!.PlaybackRate == 1.75f,
                TimeSpan.FromSeconds(1), "The latest speed must apply without waiting for an obsolete request to recover.");
            Assert.Equal(5, session.Tab.PlaybackRateIndex);
            Assert.Equal(5, ReadAppliedPlaybackRateIndex(session.Tab));
        }
        finally { finish.TrySetResult(); }
    });

    private static Task PlaybackRateSupersededSuccessAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = await ReplayOverlayTestSession.CreateAsync(playbackRateOverride: async (rate, _) =>
        {
            if (rate == 1.25f)
            {
                started.TrySetResult();
                // Model a native setter that has already committed when cancellation arrives.
                await finish.Task;
            }
            return rate != 2f;
        });
        await session.Tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        await StopReplayClockPollingAsync(session.Tab);
        try
        {
            session.Tab.PlaybackRateIndex = 3;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            session.Tab.PlaybackRateIndex = 6;
            finish.TrySetResult();
            await WaitForPlaybackRateSelectionIdleAsync(session.Tab);
            Assert.Equal(1.25f, session.PlaybackFactory.Engine!.PlaybackRate);
            Assert.Equal(3, session.Tab.PlaybackRateIndex);
            Assert.Equal(3, ReadAppliedPlaybackRateIndex(session.Tab));
        }
        finally { finish.TrySetResult(); }
    });

    private static Task PlaybackRateReloadCancellationAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = await ReplayOverlayTestSession.CreateAsync(playbackRateOverride: async (rate, token) =>
        {
            if (rate == 1.5f)
            {
                started.TrySetResult();
                try { await finish.Task.WaitAsync(token); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            }
            return true;
        });
        await session.Tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        await StopReplayClockPollingAsync(session.Tab);
        try
        {
            session.Tab.PlaybackRateIndex = 4;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await session.Tab.StartAsync(PlaybackRateTestSettings());
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await WaitForPlaybackRateSelectionIdleAsync(session.Tab);
            Assert.Equal(2, session.Tab.PlaybackRateIndex);
            Assert.Equal(2, ReadAppliedPlaybackRateIndex(session.Tab));
            Assert.Equal(1f, session.PlaybackFactory.Engine!.PlaybackRate);
        }
        finally { finish.TrySetResult(); }
    });

    private static Task PlaybackRateReloadCompletionAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var session = await ReplayOverlayTestSession.CreateAsync(
            playbackRateOverride: (rate, _) => Task.FromResult(rate != 2f));
        await session.Tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        await StopReplayClockPollingAsync(session.Tab);
        var clockRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseClock = new ManualResetEventSlim();
        var oldEngine = session.PlaybackFactory.Engine!;
        var interceptNextClock = 1;
        oldEngine.PlaybackClockOverride = source =>
        {
            if (source.PlaybackRate == 1.5f && Interlocked.Exchange(ref interceptNextClock, 0) == 1)
            {
                clockRead.TrySetResult();
                Assert.True(releaseClock.Wait(TimeSpan.FromSeconds(5)), "The test must release the old player's clock read.");
            }
            return (true, new PlaybackClock(source.Position, source.Duration, source.Seekable));
        };
        var selection = Task.Run(() => session.Tab.PlaybackRateIndex = 4);
        try
        {
            await clockRead.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await session.Tab.StartAsync(PlaybackRateTestSettings());
            Assert.True(!ReferenceEquals(oldEngine, session.PlaybackFactory.Engine));
            releaseClock.Set();
            await selection.WaitAsync(TimeSpan.FromSeconds(1));
            await WaitForPlaybackRateSelectionIdleAsync(session.Tab);
            Assert.Equal(2, ReadAppliedPlaybackRateIndex(session.Tab));
            await session.Tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
            session.Tab.PlaybackRateIndex = 6;
            await WaitForPlaybackRateSelectionIdleAsync(session.Tab);
            Assert.Equal(2, session.Tab.PlaybackRateIndex);
            Assert.Equal(1f, session.PlaybackFactory.Engine!.PlaybackRate);
        }
        finally
        {
            releaseClock.Set();
            await selection.WaitAsync(TimeSpan.FromSeconds(1));
        }
    });

    private static Task PlaybackRateInvalidSelectionAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = await ReplayOverlayTestSession.CreateAsync(playbackRateOverride: async (_, token) =>
        {
            started.TrySetResult();
            await finish.Task.WaitAsync(token);
            return true;
        });
        await session.Tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        try
        {
            session.Tab.PlaybackRateIndex = 4;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            foreach (var invalid in new[] { -1, 7, int.MaxValue })
            {
                session.Tab.PlaybackRateIndex = invalid;
                Assert.Equal(4, session.Tab.PlaybackRateIndex);
            }
            finish.TrySetResult();
            await WaitForPlaybackRateSelectionIdleAsync(session.Tab);
            Assert.True(session.PlaybackFactory.Engine!.PlaybackRateRequests.ToArray().SequenceEqual([1.5f]));
            Assert.Equal(1.5f, session.PlaybackFactory.Engine.PlaybackRate);
        }
        finally { finish.TrySetResult(); }
    });

    private static AppSettings PlaybackRateTestSettings()
    {
        var settings = new AppSettings { StreamlinkPath = "streamlink.exe", VlcDirectory = @"C:\VLC" };
        settings.Chat.ConnectAutomatically = false;
        return settings;
    }

    private static Task PlaybackRateSeekCancellationAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = await ReplayOverlayTestSession.CreateAsync(playbackRateOverride: async (rate, token) =>
        {
            if (rate == 1.5f)
            {
                started.TrySetResult();
                try { await finish.Task.WaitAsync(token); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            }
            return true;
        });
        await session.Tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        var availability = new List<bool>();
        session.Tab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(StreamTabViewModel.CanChangePlaybackRate))
                availability.Add(session.Tab.CanChangePlaybackRate);
        };
        try
        {
            session.Tab.PlaybackRateIndex = 4;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await session.Tab.SeekReplayAsync(TimeSpan.FromMinutes(20));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await WaitForPlaybackRateSelectionIdleAsync(session.Tab);
            Assert.Equal(2, session.Tab.PlaybackRateIndex);
            Assert.Equal(1f, session.PlaybackFactory.Engine!.PlaybackRate);
            Assert.True(availability.Contains(false), "Seeking must disable the speed control and notify its binding.");
            Assert.True(session.Tab.CanChangePlaybackRate, "Speed input must become available after the seek completes.");
            session.Tab.PlaybackRateIndex = 6;
            await WaitForPlaybackRateSelectionIdleAsync(session.Tab);
            Assert.Equal(2f, session.PlaybackFactory.Engine.PlaybackRate);
        }
        finally { finish.TrySetResult(); }
    });

    private static Task PlaybackRateLiveEdgePendingSelectionAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = await ReplayOverlayTestSession.CreateAsync(playbackRateOverride: async (rate, token) =>
        {
            if (rate == 0.5f)
            {
                started.TrySetResult();
                await finish.Task.WaitAsync(token);
            }
            return true;
        });
        await session.Tab.SeekReplayAsync(TimeSpan.FromSeconds(3590), holdExactPosition: true);
        await StopReplayClockPollingAsync(session.Tab);
        try
        {
            session.Tab.PlaybackRateIndex = 6;
            await WaitForPlaybackRateSelectionIdleAsync(session.Tab);
            session.Tab.PlaybackRateIndex = 0;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var update = InvokePlaybackRateClockUpdateAsync(session.Tab);
            finish.TrySetResult();
            await update.WaitAsync(TimeSpan.FromSeconds(1));
            await WaitForPlaybackRateSelectionIdleAsync(session.Tab);
            Assert.Equal(0, session.Tab.PlaybackRateIndex);
            Assert.Equal(0.5f, session.PlaybackFactory.Engine!.PlaybackRate);
        }
        finally { finish.TrySetResult(); }
    });

    private static Task PlaybackRateLiveEdgeStaleSampleAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var session = await ReplayOverlayTestSession.CreateAsync();
        await session.Tab.SeekReplayAsync(TimeSpan.FromSeconds(3590), holdExactPosition: true);
        await StopReplayClockPollingAsync(session.Tab);
        session.Tab.PlaybackRateIndex = 6;
        await WaitForPlaybackRateSelectionIdleAsync(session.Tab);
        var transition = (SemaphoreSlim)typeof(StreamTabViewModel).GetField("playbackTransitionGate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session.Tab)!;
        await transition.WaitAsync();
        Task? update = null;
        try
        {
            update = InvokePlaybackRateClockUpdateAsync(session.Tab);
            Assert.Equal(false, update.IsCompleted);
            session.Tab.PlaybackRateIndex = 5;
            await WaitForPlaybackRateSelectionIdleAsync(session.Tab);
        }
        finally { transition.Release(); }
        await update!.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(5, session.Tab.PlaybackRateIndex);
        Assert.Equal(1.75f, session.PlaybackFactory.Engine!.PlaybackRate);
        // A fresh edge sample still performs the normal catch-up reset.
        await InvokePlaybackRateClockUpdateAsync(session.Tab);
        Assert.Equal(2, session.Tab.PlaybackRateIndex);
        Assert.Equal(1f, session.PlaybackFactory.Engine.PlaybackRate);
    });

    private static Task InvokePlaybackRateClockUpdateAsync(StreamTabViewModel tab) =>
        (Task)typeof(StreamTabViewModel).GetMethod("UpdateReplayClockAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(tab, [CancellationToken.None])!;

    private static int ReadAppliedPlaybackRateIndex(StreamTabViewModel tab) =>
        (int)typeof(StreamTabViewModel).GetField("playbackRateIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;

    private static Task WaitForPlaybackRateSelectionIdleAsync(StreamTabViewModel tab) => TestWait.UntilAsync(
        () => ((SemaphoreSlim)typeof(StreamTabViewModel).GetField("playbackRateChangeGate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!).CurrentCount == 1,
        TimeSpan.FromSeconds(2), "Playback speed selections must finish.");

    private static Task NativePlaybackRateNoOpAsync() => WithPlaybackRateReplayAsync(async (engine, _) =>
    {
        Assert.True(await engine.TrySetPlaybackRateAsync(2f));
        Assert.True(IsPlaybackRateRecoveryPending(engine), "The unchanged request must arrive during HLS audio recovery.");
        var watch = Stopwatch.StartNew();
        Assert.True(await engine.TrySetPlaybackRateAsync(2f));
        Console.WriteLine($"Repeated 2x request: {watch.Elapsed.TotalMilliseconds:0.0} ms.");
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(400),
            "Requesting the speed already applied must not wait for the previous audio resynchronization.");
    });

    private static Task NativePlaybackRateStopAsync() => WithPlaybackRateReplayAsync(async (engine, _) =>
    {
        Assert.True(await engine.TrySetPlaybackRateAsync(2f));
        Assert.True(IsPlaybackRateRecoveryPending(engine), "The stop must invalidate requests waiting on actual HLS audio recovery.");
        var first = engine.TrySetPlaybackRateAsync(1.75f);
        var second = engine.TrySetPlaybackRateAsync(0.75f);
        await engine.StopAsync();
        Assert.Equal(false, await first);
        Assert.Equal(false, await second);
        await engine.PlayFromAsync(FastVodFixture.MediaUri, TimeSpan.FromSeconds(5.25), 0, PlaybackAudioState.Muted);
        await WaitForPlaybackRateOutputAsync(engine, TimeSpan.FromSeconds(5.25));
        await VerifyMeasuredPlaybackRateAsync(engine, 2f, TimeSpan.FromSeconds(3));
    });

    private static Task NativePlaybackRateSeekAsync() => WithPlaybackRateReplayAsync(async (engine, _) =>
    {
        Assert.True(await engine.TrySetPlaybackRateAsync(2f));
        Assert.True(IsPlaybackRateRecoveryPending(engine), "The seek must invalidate a request waiting on actual HLS audio recovery.");
        var pending = engine.TrySetPlaybackRateAsync(1.75f);
        await engine.SeekAsync(TimeSpan.FromSeconds(30.25));
        Assert.Equal(false, await pending);
        await WaitForPlaybackRateOutputAsync(engine, TimeSpan.FromSeconds(30.25));
        await VerifyMeasuredPlaybackRateAsync(engine, 2f, TimeSpan.FromSeconds(3));
    });

    private static Task NativePlaybackRateCancellationAsync() => WithPlaybackRateReplayAsync(async (engine, _) =>
    {
        Assert.True(await engine.TrySetPlaybackRateAsync(2f));
        using var cancellation = new CancellationTokenSource();
        var pending = engine.TrySetPlaybackRateAsync(0.5f, cancellation.Token);
        cancellation.Cancel();
        try
        {
            await pending;
            throw new InvalidOperationException("A cancelled recovery wait must not accept another speed.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Assert.True(await engine.TrySetPlaybackRateAsync(2f));
        await Task.Delay(750);
        await VerifyMeasuredPlaybackRateAsync(engine, 2f, TimeSpan.FromSeconds(3));
    });

    private static Task NativePlaybackRatePausedAsync() => WithPlaybackRateReplayAsync(async (engine, _) =>
    {
        await engine.PauseAsync();
        Assert.True(engine.TryGetPlaybackClock(out var held));
        foreach (var rate in new[] { 2f, 0.5f, 0.75f })
            Assert.True(await engine.TrySetPlaybackRateAsync(rate));
        await Task.Delay(500);
        Assert.True(engine.TryGetPlaybackClock(out var paused));
        Assert.True(Math.Abs((paused.Position - held.Position).TotalMilliseconds) < 250,
            "Speed changes must retain the paused position.");
        await engine.ResumeAsync();
        await TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var clock) && clock.Position > held.Position + TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(5), "The paused player's latest selected speed must resume playback.");
        await VerifyMeasuredPlaybackRateAsync(engine, 0.75f, TimeSpan.FromSeconds(3));
    });

    private static Task NativePlaybackRateSlowRecoveryCancellationAsync() => WithPlaybackRateReplayAsync(async (engine, _) =>
    {
        using var cancellation = new CancellationTokenSource();
        var pending = await BeginNativeSlowRecoveryAsync(engine, cancellation.Token);
        cancellation.Cancel();
        try
        {
            await pending;
            throw new InvalidOperationException("Cancelling before slow motion is committed must cancel the request.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Assert.Equal(2f, ReadNativeRequestedPlaybackRate(ReadNativePlaybackRatePlayer(engine)));
        await Task.Delay(750);
        await VerifyMeasuredPlaybackRateAsync(engine, 2f, TimeSpan.FromSeconds(3));
    });

    private static Task NativePlaybackRateSlowRecoverySeekAsync() => WithPlaybackRateReplayAsync(async (engine, _) =>
    {
        var pending = await BeginNativeSlowRecoveryAsync(engine);
        await engine.SeekAsync(TimeSpan.FromSeconds(30.25));
        Assert.Equal(false, await pending);
        Assert.Equal(2f, ReadNativeRequestedPlaybackRate(ReadNativePlaybackRatePlayer(engine)));
        await WaitForPlaybackRateOutputAsync(engine, TimeSpan.FromSeconds(30.25));
        await VerifyMeasuredPlaybackRateAsync(engine, 2f, TimeSpan.FromSeconds(3));
    });

    private static Task NativePlaybackRateSlowRecoveryReplacementAsync() => WithPlaybackRateReplayAsync(async (engine, _) =>
    {
        var pending = await BeginNativeSlowRecoveryAsync(engine);
        await engine.PlayFromAsync(FastVodFixture.MediaUri, TimeSpan.FromSeconds(30.25), 0, PlaybackAudioState.Muted);
        Assert.Equal(false, await pending);
        Assert.Equal(2f, ReadNativeRequestedPlaybackRate(ReadNativePlaybackRatePlayer(engine)));
        await WaitForPlaybackRateOutputAsync(engine, TimeSpan.FromSeconds(30.25));
        await VerifyMeasuredPlaybackRateAsync(engine, 2f, TimeSpan.FromSeconds(3));
    });

    private static async Task<Task<bool>> BeginNativeSlowRecoveryAsync(IPlaybackEngine engine, CancellationToken cancellationToken = default)
    {
        // Exercise the managed fallback used when the verified corrected core is
        // unavailable. The normal bundled path applies the target before preroll.
        typeof(LibVlcPlaybackEngine).GetField("rateAwareReplayPreroll",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(engine, false);
        Assert.True(engine.TryGetPlaybackHealth(out var before));
        Assert.True(await engine.TrySetPlaybackRateAsync(2f));
        await WaitForRateChangeVideoAsync(engine, before);
        var player = ReadNativePlaybackRatePlayer(engine);
        var pending = engine.TrySetPlaybackRateAsync(0.5f, cancellationToken);
        await TestWait.UntilAsync(() => ReadNativeRequestedPlaybackRate(player) == 1f && IsPlaybackRateRecoveryPending(engine),
            TimeSpan.FromSeconds(3), "The real player must be recovering its temporary 1x clock before interruption.");
        Assert.Equal(2f, (float)typeof(LibVlcPlaybackEngine).GetField("requestedPlaybackRate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!);
        return pending;
    }

    private static IntPtr ReadNativePlaybackRatePlayer(IPlaybackEngine engine) =>
        (IntPtr)typeof(LibVlcPlaybackEngine).GetField("player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;

    [DllImport("libvlc", EntryPoint = "libvlc_media_player_get_rate", CallingConvention = CallingConvention.Cdecl)]
    private static extern float ReadNativeRequestedPlaybackRate(IntPtr player);

    private static Task NativePlaybackRateOptionsAsync(bool muted, bool withAudio = true) => WithPlaybackRateReplayAsync(async (engine, _) =>
    {
        foreach (var rate in new[] { 2f, 0.5f, 0.75f, 1.25f, 1.5f, 1.75f, 1f })
        {
            Assert.True(engine.TryGetPlaybackHealth(out var before));
            Console.WriteLine($"Selecting {rate:0.##}x at {DateTime.UtcNow:O}: positionMs={before.PositionMilliseconds}, pictures={before.DisplayedPictures}.");
            Assert.True(await engine.TrySetPlaybackRateAsync(rate), $"VLC must accept the selected {rate:0.##}x speed.");
            try { await WaitForRateChangeVideoAsync(engine, before); }
            catch
            {
                if (engine.TryGetPlaybackHealth(out var failed))
                    Console.WriteLine($"Video after {rate:0.##}x: state={failed.State}, positionMs={failed.PositionMilliseconds}, " +
                        $"pictures={failed.DisplayedPictures}, decodedVideo={failed.DecodedVideo}, decodedAudio={failed.DecodedAudio}.");
                throw;
            }
            await VerifyMeasuredPlaybackRateAsync(engine, rate, TimeSpan.FromSeconds(3));
        }
    }, muted, withAudio);

    private static Task WaitForPlaybackRateOutputAsync(IPlaybackEngine engine, TimeSpan position)
    {
        return TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var clock) &&
            (clock.Position - position).Duration() < TimeSpan.FromSeconds(3) && clock.Duration == TimeSpan.FromSeconds(60) &&
            engine.TryGetPlaybackHealth(out var health) && health.State == PlaybackEngineState.Playing && health.DisplayedPictures > 0,
            TimeSpan.FromSeconds(5), $"The decoder must present video at {position} before speed input.");
    }

    private static bool IsPlaybackRateRecoveryPending(IPlaybackEngine engine) =>
        (bool)typeof(LibVlcPlaybackEngine).GetField("playbackRateResynchronizationPending",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;

    private static Task WithPlaybackRateReplayAsync(Func<IPlaybackEngine, MemoryLogger, Task> verify, bool muted = false, bool withAudio = true) =>
        TestSta.RunOffscreenAsync(async () =>
        {
            await using var fixture = new FastVodFixture(muted, withAudio: withAudio);
            var handle = NativeWindowTest.CreateHiddenParentWindow();
            try
            {
                using var engine = await new LibVlcPlaybackEngineFactory(fixture.Logger, new ChatSettings(), fixture.Gateway)
                    .CreateAsync(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!,
                        enableNativeOverlay: true, rendererMode: VideoRendererMode.Gdi);
                if (Environment.GetEnvironmentVariable("SVS_TEST_VLC_LOG_DIRECTORY") is { Length: > 0 } logDirectory)
                {
                    Directory.CreateDirectory(logDirectory);
                    MultistreamVlcDiagnostics.Attach((LibVlcPlaybackEngine)engine, logDirectory);
                }
                engine.SetVideoHandle(handle);
                await engine.PlayFromAsync(FastVodFixture.MediaUri, TimeSpan.FromSeconds(5.25), 0, PlaybackAudioState.Muted);
                await WaitForPlaybackRateOutputAsync(engine, TimeSpan.FromSeconds(5.25));
                await verify(engine, fixture.Logger);
            }
            finally
            {
                foreach (var entry in fixture.Logger.Entries.Where(entry => entry.Level >= AppLogLevel.Warning))
                    Console.WriteLine($"{entry.Source}: {entry.Message}");
                NativeWindowTest.DestroyWindow(handle);
            }
        });
}

internal static partial class ApplicationTestCatalog
{
    private static IReadOnlyList<(string Name, Func<Task> Run)> PlaybackRateClockTests =>
    [
        ("playback speed control: clock read failure preserves the applied speed and clock continuity", PlaybackRateClockReadFailureAsync),
        ("playback speed control: clock samples from before a speed change cannot update replay", PlaybackRateOldClockSampleAsync),
        ("playback speed control: clock updates queued before a speed change cannot update the seekbar", PlaybackRateQueuedClockUpdateAsync)
    ];

    private static Task PlaybackRateClockReadFailureAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var session = await ReplayOverlayTestSession.CreateAsync(
            playbackRateOverride: (rate, _) => Task.FromResult(rate != 2f));
        await session.Tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        await StopReplayClockPollingAsync(session.Tab);
        var clock = session.Tab.ReplayClock;
        var anchor = clock.GetReplayClockAnchor()!.Value;
        clock.SetReplayClockAnchor(TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), anchor.SeekGeneration,
            awaitingSeekConfirmation: false, DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        var expectedPosition = clock.EstimateReplayClockFromAnchor(TimeSpan.FromHours(1), DateTimeOffset.UtcNow);
        var engine = session.PlaybackFactory.Engine!;
        var failNextClock = 1;
        engine.PlaybackClockOverride = source =>
        {
            if (source.PlaybackRate == 1.5f && Interlocked.Exchange(ref failNextClock, 0) == 1)
                throw new InvalidOperationException("Replay clock is temporarily unavailable after the committed rate change.");
            return (true, new PlaybackClock(source.Position, source.Duration, source.Seekable));
        };

        session.Tab.PlaybackRateIndex = 4;
        await WaitForPlaybackRateSelectionIdleAsync(session.Tab);
        Assert.Equal(1.5f, engine.PlaybackRate);
        Assert.Equal(4, session.Tab.PlaybackRateIndex);
        Assert.Equal(4, ReadAppliedPlaybackRateIndex(session.Tab));
        var continuedPosition = clock.EstimateReplayClockFromAnchor(TimeSpan.FromHours(1), DateTimeOffset.UtcNow);
        Assert.True(Math.Abs((continuedPosition - expectedPosition).TotalMilliseconds) < 500,
            "When the native clock is unavailable, the new rate must start at the position estimated using the old rate.");

        session.Tab.PlaybackRateIndex = 6;
        await WaitForPlaybackRateSelectionIdleAsync(session.Tab);
        Assert.Equal(1.5f, engine.PlaybackRate);
        Assert.Equal(4, session.Tab.PlaybackRateIndex);
        Assert.Equal(4, ReadAppliedPlaybackRateIndex(session.Tab));
    });

    private static Task PlaybackRateOldClockSampleAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var session = await ReplayOverlayTestSession.CreateAsync();
        await session.Tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        await StopReplayClockPollingAsync(session.Tab);
        var clockRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseClock = new ManualResetEventSlim();
        var engine = session.PlaybackFactory.Engine!;
        var interceptNextClock = 1;
        engine.PlaybackClockOverride = source =>
        {
            if (source.PlaybackRate == 1f && Interlocked.Exchange(ref interceptNextClock, 0) == 1)
            {
                clockRead.TrySetResult();
                Assert.True(releaseClock.Wait(TimeSpan.FromSeconds(5)), "The test must release the pre-change clock sample.");
                return (true, new PlaybackClock(TimeSpan.FromSeconds(601), source.Duration, source.Seekable));
            }
            return (true, new PlaybackClock(TimeSpan.FromSeconds(604), source.Duration, source.Seekable));
        };
        var previousSeekValue = session.Tab.ReplaySeekValue;
        var update = Task.Run(() => InvokePlaybackRateClockUpdateAsync(session.Tab));
        try
        {
            await clockRead.Task.WaitAsync(TimeSpan.FromSeconds(1));
            session.Tab.PlaybackRateIndex = 6;
            await WaitForPlaybackRateSelectionIdleAsync(session.Tab);
            Assert.Equal(2f, engine.PlaybackRate);
            Assert.Equal(TimeSpan.FromSeconds(604), session.Tab.ReplayClock.GetReplayClockAnchor()!.Value.Offset);

            releaseClock.Set();
            await update.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(false, session.Tab.ReplayClock.GetReplayClockAnchor()!.Value.AcceptedSampleAvailable);
            Assert.Equal(previousSeekValue, session.Tab.ReplaySeekValue);

            await InvokePlaybackRateClockUpdateAsync(session.Tab);
            Assert.Equal(604d, session.Tab.ReplaySeekValue);
            Assert.Equal(6, session.Tab.PlaybackRateIndex);
        }
        finally
        {
            releaseClock.Set();
            await update.WaitAsync(TimeSpan.FromSeconds(1));
        }
    });

    private static Task PlaybackRateQueuedClockUpdateAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var notifications = new ConcurrentQueue<Action>();
        var holdNotifications = false;
        await using var session = await ReplayOverlayTestSession.CreateAsync(dispatchOverride: action =>
        {
            if (holdNotifications) notifications.Enqueue(action);
            else action();
        });
        await session.Tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        await StopReplayClockPollingAsync(session.Tab);
        session.PlaybackFactory.Engine!.PlaybackClockOverride = source =>
            (true, new PlaybackClock(TimeSpan.FromSeconds(source.PlaybackRate == 1f ? 601 : 604), source.Duration, source.Seekable));
        var previousSeekValue = session.Tab.ReplaySeekValue;
        holdNotifications = true;
        try
        {
            await InvokePlaybackRateClockUpdateAsync(session.Tab);
            Assert.True(!notifications.IsEmpty, "The old clock update must be queued on the UI dispatcher.");
            session.Tab.PlaybackRateIndex = 6;
            await WaitForPlaybackRateSelectionIdleAsync(session.Tab);
            Assert.Equal(2f, session.PlaybackFactory.Engine.PlaybackRate);
        }
        finally
        {
            holdNotifications = false;
            while (notifications.TryDequeue(out var notification)) notification();
        }
        Assert.Equal(previousSeekValue, session.Tab.ReplaySeekValue);
        await InvokePlaybackRateClockUpdateAsync(session.Tab);
        Assert.Equal(604d, session.Tab.ReplaySeekValue);
        Assert.Equal(6, session.Tab.PlaybackRateIndex);
    });

    private static Task NativePlaybackRateClockAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new FastVodFixture(withAudio: true);
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            var target = StreamInputParser.Parse("https://www.twitch.tv/videos/123", PlatformKind.Twitch) with
            {
                MediaDuration = TimeSpan.FromSeconds(60)
            };
            var streamlink = new FakeStreamlinkService
            {
                ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(FastVodFixture.MediaUri, "Local HLS VOD"))
            };
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!,
                VideoRendererMode = VideoRendererMode.Gdi
            };
            settings.Chat.ConnectAutomatically = false;
            await using var tab = TestViewModels.CreateTab(target, "best", streamlink,
                new LibVlcPlaybackEngineFactory(fixture.Logger, settings.Chat, fixture.Gateway),
                new FakeChatClientFactory(), fixture.Logger, action => action(), initialVolume: 0,
                vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
            tab.SetVideoHandle(handle);
            await tab.StartAsync(settings);
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            await TestWait.UntilAsync(() => tab.CanSeekReplay, TimeSpan.FromSeconds(5), tab.ReplaySeekToolTip);
            await tab.SeekReplayAsync(TimeSpan.FromSeconds(5.25), forceReload: true);
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            var engine = (IPlaybackEngine)typeof(StreamTabViewModel).GetField("playbackEngine",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;
            await WaitForPlaybackRateOutputAsync(engine, TimeSpan.FromSeconds(5.25));
            var source = (PlaybackMediaSource)typeof(LibVlcPlaybackEngine).GetField("currentMediaSource",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
            Assert.True(source.UseAvformatDemuxer, "The control must exercise the completed replay's production FFmpeg transport.");

            foreach (var (index, rate) in new[] { (6, 2f), (0, 0.5f), (1, 0.75f), (3, 1.25f), (4, 1.5f), (5, 1.75f), (2, 1f) })
            {
                Assert.True(engine.TryGetPlaybackHealth(out var before));
                tab.PlaybackRateIndex = index;
                await WaitForPlaybackRateSelectionIdleAsync(tab);
                Assert.Equal(index, tab.PlaybackRateIndex);
                Assert.Equal(index, ReadAppliedPlaybackRateIndex(tab));
                await WaitForRateChangeVideoAsync(engine, before);
                await VerifyMeasuredPlaybackRateAsync(engine, rate, TimeSpan.FromSeconds(3));
                await TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var clock) &&
                    Math.Abs(tab.ReplaySeekValue - clock.Position.TotalSeconds) < 1.5,
                    TimeSpan.FromSeconds(3), "The regularly polled seekbar must follow the real decoder after each speed selection.");
            }

            await tab.PauseOrResumeAsync();
            Assert.Equal(PlaybackStatus.Paused, tab.Status);
            await InvokePlaybackRateClockUpdateAsync(tab);
            var held = tab.ReplaySeekValue;
            tab.PlaybackRateIndex = 1;
            await WaitForPlaybackRateSelectionIdleAsync(tab);
            await InvokePlaybackRateClockUpdateAsync(tab);
            Assert.Equal(held, tab.ReplaySeekValue);
            Assert.Equal(1, ReadAppliedPlaybackRateIndex(tab));
            Assert.True(engine.TryGetPlaybackHealth(out var paused));
            await tab.PauseOrResumeAsync();
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            await WaitForRateChangeVideoAsync(engine, paused);
            await VerifyMeasuredPlaybackRateAsync(engine, 0.75f, TimeSpan.FromSeconds(3));
        }
        finally
        {
            foreach (var entry in fixture.Logger.Entries.Where(entry => entry.Level >= AppLogLevel.Warning))
                Console.WriteLine($"{entry.Source}: {entry.Message}");
            NativeWindowTest.DestroyWindow(handle);
        }
    });
}

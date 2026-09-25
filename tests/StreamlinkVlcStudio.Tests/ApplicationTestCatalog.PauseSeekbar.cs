internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> PauseSeekbarTests { get; } =
    [
        ("pause continuity: Twitch replay resumes in place with confirmed preservation", () => PreservedReplayResumeAsync(PlatformKind.Twitch, false)),
        ("pause continuity: Kick replay resumes in place with confirmed preservation", () => PreservedReplayResumeAsync(PlatformKind.Kick, false)),
        ("pause continuity: hidden replay resumes in place with confirmed preservation", () => PreservedReplayResumeAsync(PlatformKind.Twitch, true)),
        ("pause continuity: loss of input preservation falls back to exact restoration", LostReplayPausePreservationAsync),
        ("pause seekbar: Twitch VOD accepts the real clock after a long pause", () => VodPauseClockAsync(PlatformKind.Twitch, false, false)),
        ("pause seekbar: Kick VOD accepts the real clock after a long pause", () => VodPauseClockAsync(PlatformKind.Kick, false, false)),
        ("pause seekbar: unavailable VLC clock excludes paused time", () => VodPauseClockAsync(PlatformKind.Twitch, false, true)),
        ("pause seekbar: hidden VOD excludes paused time", () => VodPauseClockAsync(PlatformKind.Twitch, true, false)),
        ("pause seekbar: live clock freezes at the pause request", () => PauseBeforeFirstClockPollAsync(false)),
        ("pause seekbar: behind-live clock freezes at the pause request", () => PauseBeforeFirstClockPollAsync(true)),
        ("pause seekbar: repeated pauses capture a fresh position without an intervening poll", RepeatedPauseClockAsync),
        ("pause seekbar: queued pre-pause UI samples are discarded", QueuedPauseClockAsync),
        ("pause seekbar: in-flight pre-pause VLC samples cannot replace the resumed anchor", InFlightPauseClockAsync),
        ("pause seekbar: the first available VLC sample after resume initializes the clock", FirstPauseClockSampleAsync),
        ("pause seekbar: failed resume restoration stops the restarted replay and reports the error", FailedPauseRestoreAsync),
        ("pause seekbar: behind-live resume opens at the held position without unpausing old media", () => ResumeOpensAtHeldPositionAsync(false)),
        ("pause seekbar: hidden behind-live resume opens at the held position without unpausing old media", () => ResumeOpensAtHeldPositionAsync(true)),
        .. NativePauseSeekbarTests
    ];

    private static async Task PreservedReplayResumeAsync(PlatformKind platform, bool hidden)
    {
        var (tab, engine) = await CreatePauseClockTabAsync(platform, explicitVod: false);
        await using (tab)
        {
            engine.PreservesReplayPositionOnResume = true;
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
            await engine.SeekAsync(TimeSpan.FromSeconds(603));
            if (hidden) await tab.PauseForTabSwitchAsync();
            else await tab.PauseOrResumeAsync();
            MarkReplayClockSeekConfirmed(tab, TimeSpan.FromMinutes(5));
            var plays = engine.PlayCount;
            var seeks = engine.SeekCount;
            var resumes = engine.ResumeCount;
            if (hidden) await tab.ResumeFromTabSwitchAsync();
            else await tab.PauseOrResumeAsync();
            Assert.Equal(plays, engine.PlayCount);
            Assert.Equal(seeks, engine.SeekCount);
            Assert.Equal(resumes + 1, engine.ResumeCount);
            Assert.Equal(TimeSpan.FromSeconds(603), engine.Position);
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            Assert.Equal(false, tab.PausedByTabSwitch);
            Assert.True(tab.IsBehindLive);
            InvokeReplayClockUpdate(tab);
            AssertPauseClockPosition(tab, engine.Position);
        }
    }

    private static async Task LostReplayPausePreservationAsync()
    {
        var (tab, engine) = await CreatePauseClockTabAsync(PlatformKind.Twitch, explicitVod: false);
        await using (tab)
        {
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
            engine.PreservesReplayPositionOnResume = true;
            await tab.PauseOrResumeAsync();
            engine.PreservesReplayPositionOnResume = false;
            engine.ResumeJumpsToPosition = engine.Duration;
            var plays = engine.PlayCount;
            await tab.PauseOrResumeAsync();
            Assert.Equal(plays + 1, engine.PlayCount);
            Assert.Equal(0, engine.ResumeCount);
            Assert.Equal(TimeSpan.FromMinutes(10), engine.Position);
        }
    }

    private static async Task ResumeOpensAtHeldPositionAsync(bool hidden)
    {
        var (tab, engine) = await CreatePauseClockTabAsync(PlatformKind.Twitch, explicitVod: false);
        await using (tab)
        {
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
            await engine.SeekAsync(TimeSpan.FromSeconds(603));
            if (hidden) await tab.PauseForTabSwitchAsync();
            else await tab.PauseOrResumeAsync();
            var resumes = engine.ResumeCount;
            var plays = engine.PlayCount;
            if (hidden) await tab.ResumeFromTabSwitchAsync();
            else await tab.PauseOrResumeAsync();
            Assert.Equal(resumes, engine.ResumeCount);
            Assert.Equal(plays + 1, engine.PlayCount);
            Assert.Equal<TimeSpan?>(TimeSpan.FromSeconds(603), engine.LastStartPosition);
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
        }
    }

    private static async Task FailedPauseRestoreAsync()
    {
        var (tab, engine) = await CreatePauseClockTabAsync(PlatformKind.Twitch, explicitVod: false);
        await using (tab)
        {
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
            await tab.PauseOrResumeAsync();
            engine.FailingSeekCount = 1;
            var playCount = engine.PlayCount;
            var stopCount = engine.StopCount;
            await tab.PauseOrResumeAsync();
            Assert.Equal(playCount + 1, engine.PlayCount);
            Assert.Equal(stopCount + 1, engine.StopCount);
            Assert.True(engine.Stopped);
            Assert.Equal(PlaybackStatus.Error, tab.Status);
            Assert.Contains("could not be restored", tab.ErrorMessage);
            Assert.Equal(false, tab.IsBusy);
        }
    }

    private static async Task VodPauseClockAsync(PlatformKind platform, bool hidden, bool unavailableOnResume)
    {
        var (tab, engine) = await CreatePauseClockTabAsync(platform, explicitVod: true);
        await using (tab)
        {
            var position = TimeSpan.FromMinutes(10);
            await tab.SeekReplayAsync(position);
            await StopReplayClockPollingAsync(tab);
            InvokeReplayClockUpdate(tab);
            if (hidden)
                await tab.PauseForTabSwitchAsync();
            else
                await tab.PauseOrResumeAsync();
            InvokeReplayClockUpdate(tab);

            // Advance the age of the clock's last observation, without sleeping. The decoder
            // remains at the same media position for this entire simulated 45-second pause.
            MarkReplayClockSeekConfirmed(tab, TimeSpan.FromSeconds(45));
            InvokeReplayClockUpdate(tab);
            AssertPauseClockPosition(tab, position);
            if (unavailableOnResume)
                engine.PlaybackClockOverride = _ => (false, default!);

            if (hidden)
                await tab.ResumeFromTabSwitchAsync();
            else
                await tab.PauseOrResumeAsync();
            await StopReplayClockPollingAsync(tab);
            InvokeReplayClockUpdate(tab);
            AssertPauseClockPosition(tab, position);

            engine.PlaybackClockOverride = null;
            await engine.SeekAsync(position + TimeSpan.FromSeconds(2));
            InvokeReplayClockUpdate(tab);
            AssertPauseClockPosition(tab, position + TimeSpan.FromSeconds(2));
        }
    }

    private static async Task PauseBeforeFirstClockPollAsync(bool behindLive)
    {
        var (tab, engine) = await CreatePauseClockTabAsync(PlatformKind.Twitch, explicitVod: false);
        await using (tab)
        {
            if (behindLive)
                await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
            await StopReplayClockPollingAsync(tab);
            InvokeReplayClockUpdate(tab);
            var pausedPosition = TimeSpan.FromSeconds(tab.ReplaySeekValue);
            await tab.PauseOrResumeAsync();

            // The first poll arrives late. A growing live timeline or drifting decoder must
            // not change the timestamp captured when the user actually pressed Pause.
            var field = typeof(StreamTabViewModel).GetField("replaySession", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var replay = (ReplaySessionInfo)field.GetValue(tab)!;
            field.SetValue(tab, replay with { StreamStartedAtUtc = DateTimeOffset.UtcNow - replay.Duration - TimeSpan.FromSeconds(45) });
            engine.PlaybackClockOverride = _ => (true, new PlaybackClock(pausedPosition + TimeSpan.FromSeconds(4), replay.Duration, true));
            InvokeReplayClockUpdate(tab);
            AssertPauseClockPosition(tab, pausedPosition);

            engine.PlaybackClockOverride = null;
            engine.ResumeJumpsToPosition = TimeSpan.FromHours(1);
            await tab.PauseOrResumeAsync();
            await StopReplayClockPollingAsync(tab);
            InvokeReplayClockUpdate(tab);
            AssertPauseClockPosition(tab, pausedPosition);
            Assert.True(tab.IsBehindLive);
            Assert.True((engine.Position - pausedPosition).Duration() < TimeSpan.FromSeconds(1));
        }
    }

    private static async Task RepeatedPauseClockAsync()
    {
        var (tab, engine) = await CreatePauseClockTabAsync(PlatformKind.Twitch, explicitVod: true);
        await using (tab)
        {
            var position = TimeSpan.FromMinutes(10);
            await tab.SeekReplayAsync(position);
            await StopReplayClockPollingAsync(tab);
            InvokeReplayClockUpdate(tab);
            for (var iteration = 0; iteration < 3; iteration++)
            {
                await tab.PauseOrResumeAsync();
                InvokeReplayClockUpdate(tab);
                AssertPauseClockPosition(tab, position);
                await tab.PauseOrResumeAsync();
                position += TimeSpan.FromSeconds(2);
                await engine.SeekAsync(position);
                // Deliberately no playing clock poll before the next pause.
            }
        }
    }

    private static async Task QueuedPauseClockAsync()
    {
        var queued = new Queue<Action>();
        var defer = false;
        var (tab, engine) = await CreatePauseClockTabAsync(PlatformKind.Twitch, explicitVod: true,
            dispatch: action => { if (defer) queued.Enqueue(action); else action(); });
        await using (tab)
        {
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
            await StopReplayClockPollingAsync(tab);
            InvokeReplayClockUpdate(tab);
            defer = true;
            await engine.SeekAsync(TimeSpan.FromSeconds(602));
            InvokeReplayClockUpdate(tab);
            await engine.SeekAsync(TimeSpan.FromSeconds(604));
            await tab.PauseOrResumeAsync();
            defer = false;
            while (queued.TryDequeue(out var action))
                action();
            AssertPauseClockPosition(tab, TimeSpan.FromMinutes(10));
            InvokeReplayClockUpdate(tab);
            AssertPauseClockPosition(tab, TimeSpan.FromSeconds(604));
        }
    }

    private static async Task InFlightPauseClockAsync()
    {
        var (tab, engine) = await CreatePauseClockTabAsync(PlatformKind.Twitch, explicitVod: true);
        await using (tab)
        {
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
            await StopReplayClockPollingAsync(tab);
            InvokeReplayClockUpdate(tab);
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            engine.PlaybackClockOverride = _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.TrySetResult();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(5)), "Clock sample was not released.");
                    return (true, new PlaybackClock(TimeSpan.FromSeconds(603), engine.Duration, true));
                }
                return (true, new PlaybackClock(engine.Position, engine.Duration, true));
            };
            var pendingPoll = Task.Run(() => InvokeReplayClockUpdate(tab));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await tab.PauseOrResumeAsync();
                await tab.PauseOrResumeAsync();
            }
            finally
            {
                release.Set();
                await pendingPoll;
            }
            engine.PlaybackClockOverride = _ => (false, default!);
            InvokeReplayClockUpdate(tab);
            AssertPauseClockPosition(tab, TimeSpan.FromMinutes(10));
        }
    }

    private static async Task FirstPauseClockSampleAsync()
    {
        var (tab, engine) = await CreatePauseClockTabAsync(PlatformKind.Twitch, explicitVod: true,
            createEngine: () => new FakePlaybackEngine { PlaybackClockOverride = _ => (false, default!) });
        await using (tab)
        {
            // VLC has been playing, but its clock has not been available since opening the VOD.
            await engine.SeekAsync(TimeSpan.FromSeconds(10));
            await tab.PauseOrResumeAsync();
            await tab.PauseOrResumeAsync();
            engine.PlaybackClockOverride = null;
            InvokeReplayClockUpdate(tab);
            AssertPauseClockPosition(tab, TimeSpan.FromSeconds(10));
        }
    }

    private static async Task<(StreamTabViewModel Tab, FakePlaybackEngine Engine)> CreatePauseClockTabAsync(
        PlatformKind platform, bool explicitVod, Action<Action>? dispatch = null,
        Func<FakePlaybackEngine>? createEngine = null)
    {
        var duration = TimeSpan.FromHours(1);
        var target = new StreamTarget(platform, "streamer",
            platform == PlatformKind.Twitch ? "https://www.twitch.tv/videos/123" : "https://example.invalid/replay.m3u8",
            explicitVod ? platform == PlatformKind.Twitch ? StreamTargetKind.TwitchVod : StreamTargetKind.KickVod : StreamTargetKind.Live,
            MediaId: "123", MediaDuration: duration);
        var replay = new ReplaySessionInfo(platform, "streamer", target.Url, "123", null, duration, true, "");
        var factory = new FakePlaybackEngineFactory(createEngine ?? (() => new FakePlaybackEngine { Duration = duration }));
        var tab = TestViewModels.CreateTab(target, "best", new FakeStreamlinkService(), factory,
            new FakeChatClientFactory(), new MemoryLogger(), dispatch ?? (action => action()),
            replayResolver: new FakeReplayResolver(replay),
            vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
        var settings = new AppSettings { StreamlinkPath = "streamlink.exe", VlcDirectory = @"C:\VLC" };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(settings);
        await TestWait.UntilAsync(() => tab.CanSeekReplay, TimeSpan.FromSeconds(2));
        await StopReplayClockPollingAsync(tab);
        return (tab, factory.Engine!);
    }

    private static void AssertPauseClockPosition(StreamTabViewModel tab, TimeSpan expected)
    {
        Assert.True(Math.Abs(tab.ReplaySeekValue - expected.TotalSeconds) < 0.5,
            $"Expected seekbar at {expected.TotalSeconds:0.000}s, actual {tab.ReplaySeekValue:0.000}s ({tab.ReplayElapsedText}).");
        Assert.Equal(tab.ReplaySeekValue, tab.ReplaySeekSliderValue);
    }
}

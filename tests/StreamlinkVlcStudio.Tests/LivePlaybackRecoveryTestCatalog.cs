internal static partial class LivePlaybackRecoveryTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All =>
    [
        ("live recovery detects terminal states and video stalls without mistaking static scenes or pauses", HealthDetectionAsync),
        ("live recovery replaces an ended background session and preserves audio preferences", ReconnectAsync),
        ("live recovery backs off failed attempts and requires actual output", RetryAsync),
        ("live recovery respects manual and inactive pauses and stopped tabs", PausedAsync),
        ("live recovery leaves replay playback alone", ReplayAsync),
        ("live recovery stop cancels an in-flight attempt and disposes a late transport", () => CancelAsync(false)),
        ("live recovery close cancels an in-flight attempt and disposes a late transport", () => CancelAsync(true)),
        ("live recovery manual pause cancels recovery and resume retries the missing connection", PauseDuringRecoveryAsync),
        .. NativeTests
    ];

    private static Task HealthDetectionAsync()
    {
        var monitor = new LivePlaybackHealthMonitor();
        foreach (var state in new[] { PlaybackEngineState.Ended, PlaybackEngineState.Error, PlaybackEngineState.Stopped })
            Assert.True(monitor.Observe(Sample(state), 0, false) is not null);
        monitor.Reset();
        Assert.Equal<string?>(null, monitor.Observe(Sample(), 0, false));
        // Advancing audio and presentation time do not conceal a frozen video decoder.
        Assert.Equal<string?>(null, monitor.Observe(Sample() with { PositionMilliseconds = 29_000, DecodedAudio = 900 }, 29_999, false));
        Assert.True(monitor.Observe(Sample(), 30_000, false) is not null);
        monitor.Reset();
        for (var n = 0; n < 500; n++)
            Assert.Equal<string?>(null, monitor.Observe(Sample() with { DecodedVideo = n, DisplayedPictures = n }, n * 1_000, false));
        Assert.True(monitor.Observe(Sample() with { DecodedVideo = 499, DisplayedPictures = 999 }, 530_000, false) is not null,
            "Redisplaying the same decoded frame must not conceal a decoder stall.");
        Assert.Equal<string?>(null, monitor.Observe(Sample() with { Generation = 2 }, 900_000, false));
        Assert.Equal<string?>(null, monitor.Observe(Sample(PlaybackEngineState.Paused), 1_000_000, false));
        monitor.Reset();
        Assert.Equal<string?>(null, monitor.Observe(Sample(), 0, true));
        Assert.Equal<string?>(null, monitor.Observe(Sample() with { DecodedAudio = 100 }, 40_000, true));
        Assert.Equal(TimeSpan.FromSeconds(2), LivePlaybackHealthMonitor.RetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(60), LivePlaybackHealthMonitor.RetryDelay(100));
        return Task.CompletedTask;
    }

    private static async Task ReconnectAsync()
    {
        await using var f = new Fixture();
        await f.StartAsync();
        f.Tab.Volume = 37;
        f.Tab.SetSelectedForAudio(false);
        await f.Tab.CheckLivePlaybackHealthAsync(Environment.TickCount64);
        Assert.Equal(2, f.Service.StartCount);
        Assert.Equal(1, f.Sessions[0].DisposeCount);
        Assert.Equal(0, f.Sessions[1].DisposeCount);
        Assert.Equal(2, f.Engine.PlayCount);
        Assert.Equal(37, f.Engine.RequestedVolume);
        Assert.Equal(PlaybackAudioState.Muted, f.Engine.AudioState);
        Assert.Equal(PlaybackStatus.Playing, f.Tab.Status);
        Assert.Equal(false, f.Tab.IsRecoveringLivePlayback);
        Assert.Equal(f.Service.StartExternalHttpRequests[0], f.Service.StartExternalHttpRequests[1]);
    }

    private static async Task RetryAsync()
    {
        await using var f = new Fixture();
        await f.StartAsync();
        f.Engine.PlaybackHealthOverride = () => Sample(PlaybackEngineState.Ended);
        await f.Tab.CheckLivePlaybackHealthAsync(Environment.TickCount64);
        Assert.Equal(PlaybackStatus.Starting, f.Tab.Status);
        Assert.True(f.Tab.IsRecoveringLivePlayback);
        Assert.Equal(1, f.Sessions[1].DisposeCount);
        await f.Tab.CheckLivePlaybackHealthAsync(Environment.TickCount64);
        Assert.Equal(2, f.Service.StartCount);
        f.EnableSuccessfulOutput();
        await f.Tab.CheckLivePlaybackHealthAsync(Environment.TickCount64 + 120_000);
        Assert.Equal(3, f.Service.StartCount);
        Assert.Equal(PlaybackStatus.Playing, f.Tab.Status);
    }

    private static async Task PausedAsync()
    {
        await using var f = new Fixture();
        await f.StartAsync();
        await f.Tab.PauseOrResumeAsync();
        await f.Tab.CheckLivePlaybackHealthAsync(120_000);
        Assert.Equal(1, f.Service.StartCount);
        await f.Tab.PauseOrResumeAsync();
        await f.Tab.PauseForTabSwitchAsync();
        await f.Tab.CheckLivePlaybackHealthAsync(240_000);
        Assert.Equal(1, f.Service.StartCount);
        await f.Tab.StopAsync();
        await f.Tab.CheckLivePlaybackHealthAsync(360_000);
        Assert.Equal(1, f.Service.StartCount);
    }

    private static async Task ReplayAsync()
    {
        await using var f = new Fixture();
        await f.StartAsync();
        typeof(StreamTabViewModel).GetProperty(nameof(StreamTabViewModel.IsReplayMode))!.SetValue(f.Tab, true);
        await f.Tab.CheckLivePlaybackHealthAsync(Environment.TickCount64);
        Assert.Equal(1, f.Service.StartCount);
    }

    private static async Task CancelAsync(bool close)
    {
        await using var f = new Fixture();
        await f.StartAsync();
        var pending = new TaskCompletionSource<IStreamTransportSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Service.StartExternalHttpOverride = (_, _) => pending.Task;
        var recovery = f.Tab.CheckLivePlaybackHealthAsync(Environment.TickCount64);
        await TestWait.UntilAsync(() => f.Service.StartCount == 2, TimeSpan.FromSeconds(2));
        var stop = close ? f.Tab.DisposeAsync().AsTask() : f.Tab.StopAsync();
        var late = new FakeTransportSession();
        pending.SetResult(late);
        await Task.WhenAll(recovery, stop).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, late.DisposeCount);
        Assert.Equal(1, f.Engine.PlayCount);
        Assert.Equal(PlaybackStatus.Stopped, f.Tab.Status);
    }

    private static async Task PauseDuringRecoveryAsync()
    {
        await using var f = new Fixture();
        await f.StartAsync();
        f.Service.StartExternalHttpOverride = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new FakeTransportSession();
        };
        var recovery = f.Tab.CheckLivePlaybackHealthAsync(Environment.TickCount64);
        await TestWait.UntilAsync(() => f.Service.StartCount == 2, TimeSpan.FromSeconds(2));
        await f.Tab.PauseOrResumeAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await recovery;
        Assert.Equal(PlaybackStatus.Paused, f.Tab.Status);
        await f.Tab.CheckLivePlaybackHealthAsync(Environment.TickCount64);
        Assert.Equal(2, f.Service.StartCount);
        f.Service.StartExternalHttpOverride = null;
        await f.Tab.PauseOrResumeAsync();
        await f.Tab.CheckLivePlaybackHealthAsync(Environment.TickCount64);
        Assert.Equal(3, f.Service.StartCount);
        Assert.Equal(PlaybackStatus.Playing, f.Tab.Status);
    }

    private static PlaybackHealth Sample(PlaybackEngineState state = PlaybackEngineState.Playing) => new(1, state, 0, 0, 0, 0);

    private sealed class Fixture : IAsyncDisposable
    {
        internal FakePlaybackEngine Engine { get; } = new();
        internal FakeStreamlinkService Service { get; } = new();
        internal List<FakeTransportSession> Sessions { get; } = [];
        internal StreamTabViewModel Tab { get; }
        internal Fixture()
        {
            Service.StartExternalHttpOverride = (_, _) =>
            {
                var session = new FakeTransportSession();
                Sessions.Add(session);
                return Task.FromResult<IStreamTransportSession>(session);
            };
            Tab = TestViewModels.CreateTab(StreamInputParser.Parse("shroud", PlatformKind.Twitch), "best",
                Service, new FakePlaybackEngineFactory(() => Engine), new FakeChatClientFactory(), new MemoryLogger(), action => action());
            Tab.SetVideoHandle(new IntPtr(1234));
            EnableSuccessfulOutput();
        }
        internal void EnableSuccessfulOutput()
        {
            var frames = 0;
            Engine.PlaybackHealthOverride = () => Engine.PlayCount < 2
                ? Sample(PlaybackEngineState.Ended)
                : Sample() with { Generation = Engine.PlayCount, DecodedVideo = ++frames, DisplayedPictures = frames };
        }
        internal async Task StartAsync()
        {
            var settings = new AppSettings { StreamlinkPath = "streamlink.exe", VlcDirectory = "vlc", KeepInactiveTabsRunning = true };
            settings.Chat.ConnectAutomatically = false;
            settings.Chat.Layout = ChatLayout.Docked;
            await Tab.StartAsync(settings);
            // Sample explicitly in these deterministic lifecycle tests.
            var timer = (System.Threading.Timer)typeof(StreamTabViewModel)
                .GetField("liveHealthTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Tab)!;
            timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        public ValueTask DisposeAsync() => Tab.DisposeAsync();
    }
}

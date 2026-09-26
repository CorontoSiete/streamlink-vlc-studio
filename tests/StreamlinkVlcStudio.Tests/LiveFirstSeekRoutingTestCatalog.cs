internal static class LiveFirstSeekRoutingTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All =>
    [
        ("live first seek: direct replay preparation runs behind live playback and cancels on stop", () => PreparationRoutingAsync(true)),
        ("live first seek: resolved replay preparation runs behind live playback and cancels on stop", () => PreparationRoutingAsync(false))
    ];

    private static async Task PreparationRoutingAsync(bool direct)
    {
        var uri = new Uri("https://cdn.example.com/replay/index.m3u8");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellations = 0;
        var resolutions = 0;
        var engine = new FakePlaybackEngine
        {
            PrepareReplayOverride = async (_, token) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { Interlocked.Increment(ref cancellations); }
            }
        };
        var factory = new FakePlaybackEngineFactory(() => engine);
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) =>
            {
                Interlocked.Increment(ref resolutions);
                return Task.FromResult(new StreamlinkResolvedUrl(uri, "Resolved."));
            }
        };
        var replay = new ReplaySessionInfo(PlatformKind.Twitch, "streamer",
            direct ? uri.ToString() : "https://www.twitch.tv/videos/123", "123", DateTimeOffset.UtcNow.AddHours(-1),
            TimeSpan.FromHours(1), true, "");
        await using var tab = TestViewModels.CreateTab(StreamInputParser.Parse("streamer", PlatformKind.Twitch), "best",
            streamlink, factory, new FakeChatClientFactory(), new MemoryLogger(), action => action(),
            replayResolver: new FakeReplayResolver(replay));
        var settings = new AppSettings { StreamlinkPath = "streamlink.exe", VlcDirectory = @"C:\VLC" };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(settings).WaitAsync(TimeSpan.FromSeconds(3));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(tab.CanSeekReplay && !tab.IsReplayMode);
        Assert.Equal(1, engine.PlayCount);
        Assert.Equal(new Uri("http://127.0.0.1:5000/"), engine.LastPlayedUri);
        Assert.SequenceEqual(new[] { uri }, engine.PreparedReplayUris);
        var originalResolutions = resolutions;
        await tab.PauseForTabSwitchAsync();
        await TestWait.UntilAsync(() => Volatile.Read(ref cancellations) == 1, TimeSpan.FromSeconds(3));
        await tab.ResumeFromTabSwitchAsync();
        await TestWait.UntilAsync(() => engine.PreparedReplayUris.Count == 2, TimeSpan.FromSeconds(3));
        Assert.Equal(originalResolutions, resolutions);
        Assert.Equal(2, engine.PlayCount);
        await tab.StopAsync();
        await TestWait.UntilAsync(() => Volatile.Read(ref cancellations) == 2, TimeSpan.FromSeconds(3));
    }
}

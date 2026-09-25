internal static class ReplayPromotionRaceTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All =>
    [
        ("replay promotion race: promotion during a queued seek retains the seek and VOD chat", () => SeekAsync(queued: true)),
        ("replay promotion race: a seek adopts the VOD promoted before its transition", () => SeekAsync(queued: false)),
        ("replay promotion race: queued promotion remains cancellable", () => SeekAsync(queued: true, cancelPromotion: true))
    ];

    private static async Task SeekAsync(bool queued, bool cancelPromotion = false)
    {
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var liveReplay = new ReplaySessionInfo(PlatformKind.Twitch, "streamer",
            "https://d1g1f25tn8m2e6.cloudfront.net/live/index-dvr.m3u8", "live-dvr-123456789",
            startedAt, TimeSpan.FromHours(1), true, "", "best", ReplayMediaKind.CurrentLiveDvr);
        var promotedReplay = liveReplay with
        {
            ReplayUrl = "https://www.twitch.tv/videos/123",
            ReplayId = "123",
            MediaKind = ReplayMediaKind.Archive
        };
        var provider = new FakeVodChatProvider(VodChatFetchResult.Unsupported("Live DVR chat is unavailable."));
        var settings = new AppSettings { StreamlinkPath = "streamlink.exe", VlcDirectory = @"C:\VLC" };
        settings.Chat.Layout = ChatLayout.Docked;
        await using var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch), "source",
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
            new MemoryLogger(), action => action(), replayResolver: new FakeReplayResolver(liveReplay),
            vodChatProvider: provider, twitchLiveDvrPromotionPollInterval: TimeSpan.FromHours(1));
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(settings);
        await TestWait.UntilAsync(() => tab.CanSeekReplay, TimeSpan.FromSeconds(3));

        var gate = (SemaphoreSlim)typeof(StreamTabViewModel)
            .GetField("replayPlaybackTransitionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;
        var promote = typeof(StreamTabViewModel)
            .GetMethod("PromoteLiveDvrReplayAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task Promote(CancellationToken token = default) => (Task)promote.Invoke(tab, [promotedReplay, settings, token])!;
        var seekStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task promotion = Task.CompletedTask;
        tab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(tab.IsReplaySeekInProgress) || !tab.IsReplaySeekInProgress) return;
            // This event occurs after the seek captures its replay identity and before it
            // enters the replay transition. Exercise both possible gate orderings.
            if (!queued) promotion = Promote();
            seekStarted.TrySetResult();
        };

        if (queued) await gate.WaitAsync();
        Task seek;
        try
        {
            seek = tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
            await seekStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (queued)
            {
                if (cancelPromotion)
                {
                    using var cancellation = new CancellationTokenSource();
                    var canceledPromotion = Promote(cancellation.Token);
                    cancellation.Cancel();
                    await Assert.ThrowsAsync<OperationCanceledException>(() => canceledPromotion);
                }
                promotion = Promote();
            }
        }
        finally
        {
            if (queued) gate.Release();
        }
        await Task.WhenAll(seek, promotion).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);
        await TestWait.UntilAsync(() => provider.RequestedReplays.Any(replay => replay.ReplayId == "123"),
            TimeSpan.FromSeconds(3));
        Assert.Contains("123", tab.ReplaySeekToolTip);
    }
}

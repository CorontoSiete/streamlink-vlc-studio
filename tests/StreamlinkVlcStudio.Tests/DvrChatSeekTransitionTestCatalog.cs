internal static class DvrChatSeekTransitionTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("DVR chat seek transition: incoming live chat waits for its replay position", () => SeekAsync(ReplayMediaKind.CurrentLiveDvr, messagesAtTarget: false)),
        ("DVR chat seek transition: archive seeks also suppress incoming live chat", () => SeekAsync(ReplayMediaKind.Archive, messagesAtTarget: false)),
        ("DVR chat seek transition: captured target messages resume after the seek", () => SeekAsync(ReplayMediaKind.CurrentLiveDvr, messagesAtTarget: true)),
        ("DVR chat seek transition: seekbar explains unavailable historical chat", AvailabilityAsync),
        ("DVR chat status: unavailable history is visible and clears at captured messages", HistoryStatusAsync),
        ("DVR chat status: queued notices cannot overwrite a newer seek", QueuedHistoryStatusAsync)
    ];

    private static async Task SeekAsync(ReplayMediaKind mediaKind, bool messagesAtTarget)
    {
        var startedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var replayOpening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playback = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            PlayCompletionOverride = count =>
            {
                if (count != 2) return Task.CompletedTask;
                replayOpening.TrySetResult();
                return releaseReplay.Task;
            }
        });
        var chat = new FakeChatClientFactory();
        await using var tab = CreateTab(startedAt, mediaKind, playback, chat);
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(Settings());
        await TestWait.UntilAsync(() => tab.CanSeekReplay && chat.Client.Connected, TimeSpan.FromSeconds(3));
        await tab.VodChatIdleTask.WaitAsync(TimeSpan.FromSeconds(3));

        var target = TimeSpan.FromHours(1);
        var messageOffset = messagesAtTarget ? target : TimeSpan.FromMinutes(115);
        var seek = tab.SeekReplayAsync(target);
        try
        {
            await replayOpening.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(tab.IsReplaySeekInProgress);
            // The real first-seek open took about three seconds in studio.log. IRC continues
            // during that interval even though the replay flags have not yet been committed.
            for (var index = 0; index < 3; index++)
            {
                chat.Client.Receive(Message(startedAt + messageOffset, index));
            }
            Assert.Equal(0, CapturedMessages(tab).Length);
        }
        finally
        {
            releaseReplay.TrySetResult();
            await seek.WaitAsync(TimeSpan.FromSeconds(3));
        }

        Assert.True(tab.IsBehindLive);
        Assert.True(chat.Client.Connected);
        if (!messagesAtTarget)
        {
            Assert.Equal(0, CapturedMessages(tab).Length);
            chat.Client.Receive(Message(startedAt + messageOffset, 3));
            Assert.Equal(0, CapturedMessages(tab).Length);
            await tab.SeekReplayAsync(messageOffset);
        }

        var count = messagesAtTarget ? 3 : 4;
        await TestWait.UntilAsync(() => CapturedMessages(tab).Length == count, TimeSpan.FromSeconds(3));
        Assert.SequenceEqual(Enumerable.Range(0, count).Select(index => $"during-seek-{index}"),
            CapturedMessages(tab).Select(message => message.MessageId));
    }

    private static async Task AvailabilityAsync()
    {
        await using var tab = CreateTab(DateTimeOffset.UtcNow.AddHours(-2), ReplayMediaKind.CurrentLiveDvr,
            new FakePlaybackEngineFactory(), new FakeChatClientFactory());
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(Settings());
        await TestWait.UntilAsync(() => tab.CanSeekReplay, TimeSpan.FromSeconds(3));
        Assert.True(tab.ReplaySeekToolTip.Contains("chat", StringComparison.OrdinalIgnoreCase));
        Assert.True(tab.ReplaySeekToolTip.Contains("captured", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task HistoryStatusAsync()
    {
        var startedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var chat = new FakeChatClientFactory();
        await using var tab = CreateTab(startedAt, ReplayMediaKind.CurrentLiveDvr,
            new FakePlaybackEngineFactory(), chat);
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(Settings());
        await TestWait.UntilAsync(() => tab.CanSeekReplay && chat.Client.Connected, TimeSpan.FromSeconds(3));
        chat.Client.Receive(Message(startedAt.AddMinutes(115), 1));
        await tab.SeekReplayAsync(TimeSpan.FromHours(1));

        Assert.True(tab.HasReplayChatStatus);
        Assert.Contains("hasn't published", tab.ReplayChatStatusText);
        Assert.Equal("REPLAY CHAT", tab.ChatModeText);
        Assert.Equal(0, CapturedMessages(tab).Length);
        // More current chat must not replace the notice or leak into historical playback.
        chat.Client.Receive(Message(startedAt.AddMinutes(115), 2));
        Assert.True(tab.HasReplayChatStatus);
        Assert.Equal(0, CapturedMessages(tab).Length);

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(115));
        Assert.Equal(false, tab.HasReplayChatStatus);
        Assert.Equal(2, CapturedMessages(tab).Length);
        await tab.SeekReplayAsync(TimeSpan.FromHours(1));
        Assert.True(tab.HasReplayChatStatus);
        await tab.ReturnToLiveAsync();
        Assert.Equal(false, tab.HasReplayChatStatus);
        Assert.Equal("LIVE CHAT", tab.ChatModeText);
    }

    private static async Task QueuedHistoryStatusAsync()
    {
        var startedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var chat = new FakeChatClientFactory();
        var defer = 0;
        var queued = new ConcurrentQueue<Action>();
        await using var tab = CreateTab(startedAt, ReplayMediaKind.CurrentLiveDvr,
            new FakePlaybackEngineFactory(), chat, action =>
            {
                if (Volatile.Read(ref defer) == 1) queued.Enqueue(action);
                else action();
            });
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(Settings());
        await TestWait.UntilAsync(() => tab.CanSeekReplay && chat.Client.Connected, TimeSpan.FromSeconds(3));
        chat.Client.Receive(Message(startedAt.AddMinutes(115), 1));
        Volatile.Write(ref defer, 1);
        await tab.SeekReplayAsync(TimeSpan.FromHours(1));
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(115));
        while (queued.TryDequeue(out var action)) action();
        Volatile.Write(ref defer, 0);
        Assert.Equal(false, tab.HasReplayChatStatus);
        Assert.Equal(1, CapturedMessages(tab).Length);
    }

    private static ChatMessage[] CapturedMessages(StreamTabViewModel tab) =>
        tab.ChatMessages.ToArray().Where(message => message.MessageId?.StartsWith("during-seek-", StringComparison.Ordinal) == true).ToArray();

    private static ChatMessage Message(DateTimeOffset timestamp, int index) => new(
        PlatformKind.Twitch, "streamer", "viewer", $"message {index}", timestamp, MessageId: $"during-seek-{index}");

    private static AppSettings Settings() => new()
    {
        StreamlinkPath = "streamlink.exe",
        VlcDirectory = @"C:\VLC",
        Chat = new ChatSettings { Layout = ChatLayout.Docked, ConnectAutomatically = true }
    };

    private static StreamTabViewModel CreateTab(DateTimeOffset startedAt, ReplayMediaKind mediaKind,
        FakePlaybackEngineFactory playback, FakeChatClientFactory chat, Action<Action>? dispatch = null) => TestViewModels.CreateTab(
        StreamInputParser.Parse("streamer", PlatformKind.Twitch), "source",
        new FakeStreamlinkService(), playback, chat, new MemoryLogger(), dispatch ?? (action => action()),
        replayResolver: new FakeReplayResolver(new ReplaySessionInfo(
            PlatformKind.Twitch, "streamer", "https://example.invalid/index-dvr.m3u8",
            mediaKind == ReplayMediaKind.CurrentLiveDvr ? "live-dvr-123" : "123",
            startedAt, TimeSpan.FromHours(2), true, "", "best", mediaKind)),
        vodChatProvider: new FakeVodChatProvider(VodChatFetchResult.Unsupported("No published comments.")));
}

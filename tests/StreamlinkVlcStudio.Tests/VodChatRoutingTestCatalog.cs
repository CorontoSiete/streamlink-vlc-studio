internal static class VodChatRoutingTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All =>
    [
        .. new[] { PlatformKind.Twitch, PlatformKind.Kick }.SelectMany(platform =>
            new[] { ChatLayout.Docked, ChatLayout.Overlay }.Select(layout =>
                ($"VOD chat routing: {platform} {layout} restart and visibility preserve archived messages",
                    (Func<Task>)(() => RestartAsync(platform, layout))))),
        ("VOD chat routing: unresolved archive cannot select the live overlay", UnresolvedOverlayAsync)
    ];

    private static async Task RestartAsync(PlatformKind platform, ChatLayout layout)
    {
        var target = VodResumeTestCatalog.Target(platform);
        var settings = VodResumeTestCatalog.Settings();
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = layout;
        var factory = new FakePlaybackEngineFactory();
        var chat = new FakeChatClientFactory();
        var archived = new ChatMessage(platform, target.Channel, "past-viewer", "archived message",
            DateTimeOffset.UtcNow.AddDays(-4), MessageId: "archive-10");
        var provider = new FakeVodChatProvider(VodChatFetchResult.Completed(
            [new VodChatMessage(TimeSpan.FromSeconds(10), archived)], target.MediaDuration));
        await using var tab = new StreamTabViewModel(new StreamTabViewModelDependencies
        {
            Target = target,
            Quality = "best",
            StreamlinkService = new FakeStreamlinkService(),
            PlaybackFactory = factory,
            ChatFactory = chat,
            Logger = new MemoryLogger(),
            Dispatch = action => action(),
            VodChatProvider = provider
        });
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(settings);
        await tab.VodChatIdleTask;
        await tab.SeekReplayAsync(TimeSpan.FromSeconds(20));
        await TestWait.UntilAsync(() => tab.ChatMessages.Any(message => message.MessageId == "archive-10"), TimeSpan.FromSeconds(3));
        await tab.RestartChatAsync(settings);
        Assert.Equal(0, chat.Client.ConnectCount);
        tab.IsChatVisible = false;
        tab.IsChatVisible = true;
        await tab.RestartChatAsync(settings);
        Assert.Equal(0, chat.Client.ConnectCount);
        Assert.Equal("REPLAY CHAT", tab.ChatModeText);
        Assert.True(tab.ChatMessages.Any(message => message.MessageId == "archive-10"));
        Assert.True(provider.RequestedReplays.All(replay => replay.ReplayId == target.MediaId));
    }

    private static async Task UnresolvedOverlayAsync()
    {
        var settings = VodResumeTestCatalog.Settings(replay: false);
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Overlay;
        var factory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine { UsesNativeOverlayOverride = true });
        var chat = new FakeChatClientFactory();
        await using var tab = new StreamTabViewModel(new StreamTabViewModelDependencies
        {
            Target = VodResumeTestCatalog.Target() with { MediaDuration = TimeSpan.Zero },
            Quality = "best",
            StreamlinkService = new FakeStreamlinkService(),
            PlaybackFactory = factory,
            ChatFactory = chat,
            Logger = new MemoryLogger(),
            Dispatch = action => action()
        });
        await tab.RestartChatAsync(settings);
        Assert.Equal(0, chat.Client.ConnectCount);
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(settings);
        var selector = typeof(StreamTabViewModel).GetMethod("ShouldUseNativeOverlayController",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(false, (bool)selector.Invoke(tab, [settings])!);
        Assert.Equal("REPLAY CHAT", tab.ChatModeText);
        await tab.RestartChatAsync(settings);
        Assert.Equal(0, chat.Client.ConnectCount);
    }
}

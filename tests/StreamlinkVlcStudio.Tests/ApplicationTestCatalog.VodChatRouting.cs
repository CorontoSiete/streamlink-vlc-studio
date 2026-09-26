internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> VodChatRoutingNativeTests =>
    [
        ("VOD chat routing: Twitch native overlay preserves archive frames after chat restart", () => VodNativeChatRestartAsync(PlatformKind.Twitch)),
        ("VOD chat routing: Kick native overlay preserves archive frames after chat restart", () => VodNativeChatRestartAsync(PlatformKind.Kick))
    ];

    private static Task VodNativeChatRestartAsync(PlatformKind platform) => TestSta.RunOffscreenAsync(async () =>
    {
        var pipeName = $"svs_vod_chat_routing_{Guid.NewGuid():N}";
        var factory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            UsesNativeOverlayOverride = true,
            NativeOverlayPipeNameOverride = pipeName
        });
        var target = VodResumeTestCatalog.Target(platform);
        var chat = new FakeChatClientFactory();
        var provider = new FakeVodChatProvider(VodChatFetchResult.Completed(
            [new VodChatMessage(TimeSpan.FromSeconds(10), new ChatMessage(platform, target.Channel,
                "past-viewer", "archived overlay message", DateTimeOffset.UtcNow.AddDays(-4), MessageId: "archive-overlay"))],
            target.MediaDuration));
        await using var tab = new StreamTabViewModel(new StreamTabViewModelDependencies
        {
            Target = target,
            Quality = "best",
            StreamlinkService = new FakeStreamlinkService(),
            PlaybackFactory = factory,
            ChatFactory = chat,
            VodChatProvider = provider,
            Logger = new MemoryLogger(),
            Dispatch = action => action()
        });
        var settings = VodResumeTestCatalog.Settings();
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Overlay;
        await tab.RestartChatAsync(settings);
        Assert.Equal(0, chat.Client.ConnectCount);
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(settings);
        await tab.VodChatIdleTask;
        var initialFrame = ReadNativeOverlayPipeMatchingMessageAsync(pipeName, IsNativeOverlayRenderedChatFrame, TimeSpan.FromSeconds(6));
        await tab.SeekReplayAsync(TimeSpan.FromSeconds(20));
        AssertNativeOverlayChatFrame(await initialFrame);

        var restartedFrame = ReadNativeOverlayPipeMatchingMessageAsync(pipeName, IsNativeOverlayRenderedChatFrame, TimeSpan.FromSeconds(6));
        await tab.RestartChatAsync(settings);
        AssertNativeOverlayChatFrame(await restartedFrame);
        Assert.Equal(0, chat.Client.ConnectCount);
        Assert.True(tab.ChatMessages.Any(message => message.MessageId == "archive-overlay"));

        var blankFrame = ReadNativeOverlayPipeMatchingMessageAsync(pipeName, IsNativeOverlayBlankFrame, TimeSpan.FromSeconds(6));
        tab.IsChatVisible = false;
        Assert.True(IsNativeOverlayBlankFrame(await blankFrame));
        var shownFrame = ReadNativeOverlayPipeMatchingMessageAsync(pipeName, IsNativeOverlayRenderedChatFrame, TimeSpan.FromSeconds(6));
        tab.IsChatVisible = true;
        await tab.RestartChatAsync(settings);
        AssertNativeOverlayChatFrame(await shownFrame);
        Assert.Equal(0, chat.Client.ConnectCount);

        var dockedFrame = ReadNativeOverlayPipeMatchingMessageAsync(pipeName, IsNativeOverlayBlankFrame, TimeSpan.FromSeconds(6));
        settings.Chat.Layout = ChatLayout.Docked;
        tab.RefreshChatOverlay(settings.Chat);
        Assert.True(IsNativeOverlayBlankFrame(await dockedFrame));
        var overlayFrame = ReadNativeOverlayPipeMatchingMessageAsync(pipeName, IsNativeOverlayRenderedChatFrame, TimeSpan.FromSeconds(6));
        settings.Chat.Layout = ChatLayout.Overlay;
        tab.RefreshChatOverlay(settings.Chat);
        AssertNativeOverlayChatFrame(await overlayFrame);
        Assert.Equal(0, chat.Client.ConnectCount);
    });
}

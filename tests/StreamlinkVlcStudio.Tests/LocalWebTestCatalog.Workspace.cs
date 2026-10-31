using Microsoft.AspNetCore.Http;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static Task WorkspaceAsync() => WorkspaceAsync(false);
    private static Task ProfileImagesAsync() => WorkspaceAsync(true);
    private static Task FollowStatusAsync() => WorkspaceAsync(false, true);
    private static Task OverlayLayoutAsync() => WorkspaceAsync(false, overlayLayout: true);
    private static Task CategoryViewersAsync() => WorkspaceAsync(false, categoryViewers: true);
    private static Task CategoryArtworkAsync() => WorkspaceAsync(false, categoryArtwork: true);
    private static Task CategoryArtworkLiveAsync() => WorkspaceAsync(false, categoryArtwork: true, liveArtwork: true);
    private static Task TwitchBadgeRecoveryAsync() => WorkspaceAsync(false, twitchBadgeRecovery: true);
    private static Task FollowingLastLiveAsync() => WorkspaceAsync(false, lastLive: true);
    private static async Task WorkspaceAsync(bool profileImages, bool followStatus = false, bool overlayLayout = false, bool categoryViewers = false,
        bool categoryArtwork = false, bool liveArtwork = false, bool twitchBadgeRecovery = false, bool lastLive = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "StreamStudioTests", "localweb-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var logger = new MemoryLogger();
        var settings = new AppSettings { StreamlinkPath = "streamlink.exe" };
        settings.Chat.TwitchOAuthToken = "secret-twitch-credential";
        settings.Chat.KickOAuthToken = "secret-kick-credential";
        var history = new JsonVodPlaybackHistory(Path.Combine(directory, "history.json"), logger);
        var chats = new List<FakeChatClientFactory>();
        var streamlink = new FakeStreamlinkService();
        var presentation = new WorkspaceChatPresentation();
        var archiveMessage = new ChatMessage(PlatformKind.Twitch, "streamer", "archive", "Wave", DateTimeOffset.UnixEpoch);
        var vodChat = new FakeVodChatProvider(VodChatFetchResult.Completed([new VodChatMessage(TimeSpan.FromSeconds(3), archiveMessage)], TimeSpan.FromSeconds(60)));
        await using var downloads = new VodDownloadService(Path.Combine(directory, "downloads"), streamlink, logger);
        var dependencies = new LocalWebDependencies
        {
            GetSettingsAsync = _ => Task.FromResult(settings),
            ChangeSettingsAsync = (change, _) => { change(settings); return Task.CompletedTask; },
            Streamlink = streamlink,
            Following = new FakeFollowedStreamsService(
                new(PlatformKind.Twitch, "streamer", "Streamer", "Twitch title", "Just Chatting", 100, "", null, false, "en", "https://twitch.tv/streamer"),
                new(PlatformKind.Kick, "kickstreamer", "Kickstreamer", "Kick title", "Music", 50, "", null, false, "en", "https://kick.com/kickstreamer")),
            Search = new FakeStreamSearchService(new StreamSearchResult(StreamSearchResultStatus.Available, [], "")),
            Browse = new FakeBrowseService(),
            TwitchVods = new FakeTwitchVodService(new TwitchVodSearchResult(TwitchVodSearchStatus.Available, null, [], "", "")),
            KickVods = new FakeKickVodService(new KickVodSearchResult(KickVodSearchStatus.Available, [], "", "")),
            Downloads = downloads,
            History = history,
            VodChat = vodChat,
            ChatPresentation = presentation,
            Replay = new FakeReplayResolver(ReplaySessionInfo.Unavailable(PlatformKind.Twitch, "streamer", "No archive")),
            SubOnlyVods = new FakeTwitchSubOnlyVodResolver(),
            ChatFactory = _ => { var factory = new FakeChatClientFactory(); chats.Add(factory); return factory; },
            CreateClipAsync = (_, _) => Task.FromResult<Uri?>(new("https://clips.twitch.tv/confirmed")),
            GetFollowStatusAsync = (_, _) => Task.FromResult<ChannelFollowResult?>(null),
            FollowAsync = (target, desired, _) => Task.FromResult<ChannelFollowResult?>(new(target.Platform, target.Channel, IsFollowing: desired))
        };
        try
        {
            if (lastLive)
            {
                await VerifyFollowingLastLiveAsync(dependencies);
                return;
            }
            if (twitchBadgeRecovery)
            {
                await VerifyTwitchBadgeRecoveryAsync(dependencies, chats);
                return;
            }
            if (categoryArtwork)
            {
                if (liveArtwork) await VerifyCategoryArtworkLiveAsync(dependencies);
                else await VerifyCategoryArtworkAsync(dependencies);
                return;
            }
            if (categoryViewers)
            {
                await VerifyCategoryViewersAsync(dependencies);
                return;
            }
            if (overlayLayout)
            {
                await VerifyOverlayLayoutAsync(dependencies, directory);
                return;
            }
            if (followStatus)
            {
                await VerifyFollowStatusAsync(dependencies);
                return;
            }
            if (profileImages)
            {
                await VerifyProfileImagesAsync(dependencies, settings, directory);
                return;
            }
            var users = await LocalWebUserStore.OpenAsync(Path.Combine(directory, "web-users.json"));
            var user = await users.AddAsync("watcher", "Watcher password 123!");
            await using var first = new LocalWebUserWorkspace(new LocalWebWorkspace(dependencies), users, user.Id);
            await using var second = new LocalWebUserWorkspace(new LocalWebWorkspace(dependencies), users, user.Id);
            var exposed = await Call(first, "settings", new { });
            Assert.Equal(false, exposed.ToString().Contains("secret-", StringComparison.Ordinal));
            Assert.Equal(false, exposed.ToString().Contains("streamlink.exe", StringComparison.Ordinal));
            var following = await Call(first, "following", new { });
            Assert.Equal("Just Chatting", following.GetProperty("items")[0].GetProperty("categoryName").GetString());
            Assert.Equal("Music", following.GetProperty("items")[1].GetProperty("categoryName").GetString());
            await Call(first, "settings/save", new { quality = "720p", lowLatency = false });
            Assert.Equal("720p", users.PreferencesFor(user.Id).Quality); Assert.Equal(false, users.PreferencesFor(user.Id).LowLatency);
            Assert.Equal("best", settings.DefaultQuality); Assert.Equal(true, settings.LowLatency);
            var opened = await Call(first, "open", new { input = "https://www.twitch.tv/videos/12345" });
            var id = opened.GetProperty("id").GetString()!;
            var archive = await Call(first, "chat", new { player = id, from = 0 });
            Assert.Equal("#123456", archive.GetProperty("messages")[0].GetProperty("message").GetProperty("color").GetString());
            presentation.Version = 1;
            var refreshedArchive = await Call(first, "chat/presentation", new { player = id, version = 0 });
            Assert.Equal(1, refreshedArchive.GetProperty("messages").GetArrayLength());
            Assert.Equal("https://example.com/animated.gif", refreshedArchive.GetProperty("messages")[0].GetProperty("emotes")[0].GetProperty("imageUrl").GetString());
            var unchanged = await Call(first, "chat/presentation", new { player = id, version = 1 });
            Assert.Equal(0, unchanged.GetProperty("messages").GetArrayLength());
            vodChat.Enqueue(VodChatFetchResult.Failed("Provider temporarily unavailable."));
            var retry = await Call(first, "chat", new { player = id, from = 60 });
            Assert.Equal("Failed", retry.GetProperty("outcome").GetString());
            Assert.Contains("Provider temporarily unavailable.", retry.GetProperty("status").GetString()!);
            Assert.Contains("Retrying", retry.GetProperty("status").GetString()!);
            Assert.Equal(0d, retry.GetProperty("through").GetDouble());
            vodChat.Enqueue(VodChatFetchResult.Unsupported("This video has no archived chat."));
            var unavailable = await Call(first, "chat", new { player = id, from = 60 });
            Assert.Equal("This video has no archived chat.", unavailable.GetProperty("status").GetString());
            vodChat.Enqueue(VodChatFetchResult.Completed(Enumerable.Range(0, 2101).Select(index => new VodChatMessage(TimeSpan.FromSeconds(index),
                archiveMessage with { MessageId = "archive-" + index })).ToArray(), TimeSpan.FromHours(1)));
            await Call(first, "chat", new { player = id, from = 60 });
            var boundedRefresh = await Call(first, "chat/presentation", new { player = id, version = 0 });
            Assert.Equal(2000, boundedRefresh.GetProperty("messages").GetArrayLength());
            Assert.True(boundedRefresh.GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("messageId").GetString() == "archive-2100"));
            Assert.True(opened.GetProperty("url").GetString()!.StartsWith("/media/", StringComparison.Ordinal));
            Assert.Equal(false, opened.ToString().Contains("example.com", StringComparison.Ordinal));
            await Call(first, "progress", new { player = id, position = 120, duration = 600, ended = false });
            await Call(first, "close", new { player = id });
            var resumed = await Call(second, "open", new { input = "https://www.twitch.tv/videos/12345" });
            Assert.Equal(120d, resumed.GetProperty("resume").GetDouble());
            await Assert.ThrowsAsync<ArgumentException>(() => Call(first, "progress", new { player = resumed.GetProperty("id").GetString(), position = 0, duration = 600 }));
            var context = new DefaultHttpContext();
            await first.ServeMediaAsync(context, resumed.GetProperty("id").GetString()!, "guess");
            Assert.Equal(404, context.Response.StatusCode);
            await Assert.ThrowsAsync<ArgumentException>(() => Call(first, "open", new { input = "http://127.0.0.1/private" }));
            await Assert.ThrowsAsync<ArgumentException>(() => Call(first, "open", new { input = "https://twitch.tv/streamer", quality = "--exec=command" }));
            Assert.Equal(31, dependencies.PlaybackSlots.CurrentCount); // one open player owns one of 32 slots
            var live = await Call(first, "open", new { input = "https://twitch.tv/streamer" });
            var liveId = live.GetProperty("id").GetString();
            chats.Single().Client.Receive(new ChatMessage(PlatformKind.Twitch, "streamer", "live", "Wave", DateTimeOffset.UnixEpoch, MessageId: "live-message"));
            var liveChat = await Call(first, "chat", new { player = liveId, after = 0 });
            Assert.Equal("https://example.com/animated.gif", liveChat.GetProperty("messages")[0].GetProperty("message").GetProperty("emotes")[0].GetProperty("imageUrl").GetString());
            var noDuplicate = await Call(first, "chat", new { player = liveId, after = 1 });
            Assert.Equal(0, noDuplicate.GetProperty("messages").GetArrayLength());
            await Assert.ThrowsAsync<ArgumentException>(() => Call(second, "chat/presentation", new { player = liveId, version = 0 }));
            Assert.Equal(0, settings.RecentStreams.Count);
            await Call(first, "started", new { player = liveId });
            Assert.Equal("streamer", settings.RecentStreams.Single().Channel);
            await Call(first, "chat/send", new { player = liveId, message = "hello" });
            Assert.SequenceEqual(new[] { "hello" }, chats.Single().Client.SentMessages);
            var followed = await Call(first, "follow", new { player = liveId }); Assert.True(followed.GetProperty("following").GetBoolean());
            var clip = await Call(first, "clip", new { player = liveId }); Assert.Equal("https://clips.twitch.tv/confirmed", clip.GetProperty("url").GetString());
            settings.RecentStreams = Enumerable.Range(0, 5000).Select(index => new RecentStreamSettings
            {
                Platform = PlatformKind.Twitch,
                Channel = "channel" + index
            }).ToList();
            await Assert.ThrowsAsync<InvalidDataException>(() => Call(first, "recent", new { }));
            var slotsBeforeRejectedOpen = dependencies.PlaybackSlots.CurrentCount;
            await Assert.ThrowsAsync<InvalidDataException>(() => Call(first, "open", new { input = "https://www.twitch.tv/videos/99999" }));
            Assert.Equal(slotsBeforeRejectedOpen, dependencies.PlaybackSlots.CurrentCount);
            await first.DisposeAsync(); Assert.Equal(1, chats.Single().Client.DisposeCount);
            await second.DisposeAsync(); Assert.Equal(32, dependencies.PlaybackSlots.CurrentCount);
        }
        finally { await downloads.DisposeAsync(); Directory.Delete(directory, true); }
    }

    private static async Task<JsonElement> Call(ILocalWebWorkspace workspace, string action, object input) =>
        JsonSerializer.SerializeToElement(await workspace.InvokeAsync(action, JsonSerializer.SerializeToElement(input), CancellationToken.None),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private sealed class WorkspaceChatPresentation : ILocalWebChatPresentation
    {
        public long Version { get; set; }
        public ChatMessage Present(ChatMessage message, ChatSettings settings) => message with
        {
            Color = "#123456",
            Emotes = Version == 0 ? [] : [new(0, 4, "Wave", "https://example.com/animated.gif")]
        };
        public Task ServeAssetAsync(HttpContext context, string assetId) { context.Response.StatusCode = 404; return Task.CompletedTask; }
    }
}

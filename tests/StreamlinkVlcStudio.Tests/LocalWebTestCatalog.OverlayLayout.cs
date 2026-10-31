using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static async Task VerifyOverlayLayoutAsync(LocalWebDependencies dependencies, string directory)
    {
        var path = Path.Combine(directory, "web-users.json");
        var users = await LocalWebUserStore.OpenAsync(path);
        var watcher = await users.AddAsync("watcher", "Watcher password 123!");
        var other = await users.AddAsync("other", "Other password 456!");
        var settings = new AppSettings { DefaultQuality = "720p" };
        dependencies = dependencies with
        {
            GetSettingsAsync = _ => Task.FromResult(settings),
            TwitchVods = new FakeTwitchVodService(new TwitchVodSearchResult(TwitchVodSearchStatus.Available, null,
                [new("12345", "", "1", "streamer", "Streamer", "Archive", "", "https://www.twitch.tv/videos/12345", "",
                    null, null, TimeSpan.FromMinutes(10), 1, TwitchVodTypeFilter.Archive)], "", ""))
        };
        var firstLayout = new WebChatOverlayLayout(420, 260, .23, .87);
        var otherLayout = new WebChatOverlayLayout(280, 400, .9, .1);
        var kickLayout = new WebChatOverlayLayout(320, 240, 0, 1);
        async Task<JsonElement> Open(ILocalWebWorkspace workspace, string input) => await Call(workspace, "open", new { input });
        async Task Save(ILocalWebWorkspace workspace, JsonElement player, WebChatOverlayLayout layout) =>
            await Call(workspace, "chat/layout", new
            {
                player = player.GetProperty("id").GetString(),
                layout = new { width = layout.Width, height = layout.Height, x = layout.X, y = layout.Y }
            });
        static void Check(JsonElement player, WebChatOverlayLayout? expected)
        {
            var actual = player.GetProperty("chatLayout");
            if (expected is null) Assert.Equal(JsonValueKind.Null, actual.ValueKind);
            else Assert.Equal(expected, actual.Deserialize<WebChatOverlayLayout>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        await using (var first = new LocalWebUserWorkspace(new LocalWebWorkspace(dependencies), users, watcher.Id))
        await using (var second = new LocalWebUserWorkspace(new LocalWebWorkspace(dependencies), users, watcher.Id))
        await using (var otherUser = new LocalWebUserWorkspace(new LocalWebWorkspace(dependencies), users, other.Id))
        {
            var streamer = await Open(first, "https://twitch.tv/StReAmEr");
            var otherStream = await Open(first, "https://twitch.tv/other");
            var kick = await Open(first, "https://kick.com/streamer");
            Check(streamer, null); Check(otherStream, null); Check(kick, null);
            await Save(first, streamer, firstLayout);
            await Save(first, otherStream, otherLayout);
            await Save(first, kick, kickLayout);
            await Assert.ThrowsAsync<ArgumentException>(() => Save(second, streamer, otherLayout));
            foreach (var invalid in new object[]
            {
                new { width = (double?)0, height = (double?)260, x = .2, y = .8 },
                new { width = (double?)420, height = (double?)16385, x = .2, y = .8 },
                new { width = (double?)420, height = (double?)260, x = -1d, y = .8 },
                new { width = (double?)420, height = (double?)260, x = .2, y = 2d },
                new { width = "420", height = 260, x = .2, y = .8 },
                new { width = 420, height = 260, x = .2 },
                new { width = 420, height = 260, x = (double?)null, y = .8 }
            })
                await Assert.ThrowsAsync<ArgumentException>(() => Call(first, "chat/layout", new { player = streamer.GetProperty("id").GetString(), layout = invalid }));
            Check(await Open(second, "https://twitch.tv/streamer"), firstLayout);
            Check(await Open(otherUser, "https://twitch.tv/streamer"), null);
        }
        Assert.Equal(0, settings.WebChatOverlayLayouts.Count);
        users = await LocalWebUserStore.OpenAsync(path);
        Assert.Equal(3, users.PreferencesFor(watcher.Id).ChatLayouts.Count);
        Assert.Equal(0, users.PreferencesFor(other.Id).ChatLayouts.Count);
        await using (var restarted = new LocalWebUserWorkspace(new LocalWebWorkspace(dependencies), users, watcher.Id))
        {
            Check(await Open(restarted, "https://twitch.tv/STREAMER"), firstLayout);
            Check(await Open(restarted, "https://twitch.tv/other"), otherLayout);
            Check(await Open(restarted, "https://kick.com/streamer"), kickLayout);
            var card = (await Call(restarted, "vods", new { channel = "streamer" })).GetProperty("items")[0];
            var vod = await Call(restarted, "open", new { key = card.GetProperty("key").GetString() });
            Check(vod, firstLayout);
            var live = await Open(restarted, "https://twitch.tv/streamer");
            var resetSize = new WebChatOverlayLayout(null, null, .23, .87);
            await Save(restarted, live, resetSize);
            Check(await Open(restarted, "https://twitch.tv/streamer"), resetSize);
            await Save(restarted, live, new());
        }
        users = await LocalWebUserStore.OpenAsync(path);
        await using var reset = new LocalWebUserWorkspace(new LocalWebWorkspace(dependencies), users, watcher.Id);
        Check(await Open(reset, "https://twitch.tv/streamer"), new());
        Assert.Equal(kickLayout, users.PreferencesFor(watcher.Id).ChatLayouts["Kick:streamer"]);
        Assert.Equal(otherLayout, users.PreferencesFor(watcher.Id).ChatLayouts["Twitch:other"]);
    }
}

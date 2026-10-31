using Microsoft.AspNetCore.Http;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static async Task VerifyProfileImagesAsync(LocalWebDependencies dependencies, AppSettings settings, string directory)
    {
        const string picture = "https://static-cdn.jtvnw.net/jtv_user_pictures/profile.png";
        const string kickPicture = "https://files.kick.com/profile.png";
        const string thumbnail = "https://static-cdn.jtvnw.net/previews/live.jpg";
        var following = new FakeFollowedStreamsService();
        following.EnqueueResult(_ => Task.FromResult(new FollowedLiveStreamsResult(
            [new(PlatformKind.Twitch, "creator", "Creator", "Live title", "Games", 10, thumbnail, null, false, "en", "https://twitch.tv/creator", picture)], [],
            OfflineChannels: [new(PlatformKind.Kick, "offline", "Offline", "https://kick.com/offline", kickPicture)])));
        var browse = new FakeBrowseService();
        browse.EnqueueStreams(new(BrowseResultStatus.Available,
            [new(PlatformKind.Kick, "creator", "Creator", "Title", "1", "Games", 20, thumbnail, null, false, "en", "https://kick.com/creator", kickPicture)], "", ""));
        using var client = new HttpClient(new ProfileFixtureHandler());
        var services = dependencies with
        {
            Following = following,
            Browse = browse,
            Search = new FakeStreamSearchService(new StreamSearchResult(StreamSearchResultStatus.Available,
                [new(PlatformKind.Twitch, "creator", "Creator", "https://twitch.tv/creator", thumbnail, "Title", "Games",
                    StreamSearchChannelState.Live, StreamSearchSourceStatus.Available, "", true, ProfileImageUrl: picture)], "")),
            TwitchVods = new FakeTwitchVodService(new TwitchVodSearchResult(TwitchVodSearchStatus.Available, null,
                [new("123", "", "1", "creator", "Creator", "VOD title", "", "https://www.twitch.tv/videos/123", thumbnail,
                    null, null, TimeSpan.FromMinutes(1), 1, TwitchVodTypeFilter.Archive, ProfileImageUrl: picture)], "", "")),
            KickVods = new FakeKickVodService(new KickVodSearchResult(KickVodSearchStatus.Available,
                [new("456", "", "", "creator", "Creator", "Kick VOD", "https://kick.com/creator/videos/456", "https://stream.kick.com/video.m3u8",
                    thumbnail, "Games", null, null, TimeSpan.FromMinutes(1), 1, ProfileImageUrl: kickPicture)], "", "")),
            ChannelProfiles = new(client)
        };
        await using var workspace = new LocalWebWorkspace(services);
        var followed = await Call(workspace, "following", new { });
        AssertPicture(followed.GetProperty("items")[0], picture);
        AssertPicture(followed.GetProperty("offline")[0], kickPicture);
        AssertPicture((await Call(workspace, "search", new { query = "creator" })).GetProperty("items")[0], picture);
        AssertPicture((await Call(workspace, "streams", new { platform = "kick" })).GetProperty("items")[0], kickPicture);
        foreach (var provider in new[] { "twitch", "kick" })
        {
            var vod = (await Call(workspace, "vods", new { platform = provider, channel = "creator" })).GetProperty("items")[0];
            AssertPicture(vod, provider == "twitch" ? picture : kickPicture);
            var openedVod = await Call(workspace, "open", new { key = vod.GetProperty("key").GetString() });
            AssertPicture(openedVod, provider == "twitch" ? picture : kickPicture);
            await Call(workspace, "close", new { player = openedVod.GetProperty("id").GetString() });
        }
        var opened = await Call(workspace, "open", new { key = followed.GetProperty("items")[0].GetProperty("key").GetString() });
        AssertPicture(opened, picture);
        var player = opened.GetProperty("id").GetString();
        await Call(workspace, "started", new { player });
        AssertPicture((await Call(workspace, "recent", new { })).GetProperty("items")[0], picture);
        var profile = await Call(workspace, "channel/profile", new { player });
        AssertPicture(profile, "https://static-cdn.jtvnw.net/refreshed.png");
        AssertPicture((await Call(workspace, "recent", new { })).GetProperty("items")[0], "https://static-cdn.jtvnw.net/refreshed.png");
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal("https://static-cdn.jtvnw.net/refreshed.png", restored.RecentStreams.Single().ProfileImageUrl);
        var downloadVod = (await Call(workspace, "vods", new { platform = "twitch", channel = "creator" })).GetProperty("items")[0];
        var queued = await Call(workspace, "downloads/add", new { key = downloadVod.GetProperty("key").GetString() });
        AssertPicture((await Call(workspace, "downloads", new { })).GetProperty("items")[0], picture);
        Assert.True(queued.TryGetProperty("id", out _));

        foreach (var unsafeUrl in new[] { "file:///C:/private/avatar.png", "http://images.example/pic.png", "https://user:secret@images.example/pic.png", "javascript:alert(1)" })
            Assert.Equal("", LocalWebProfileImages.PublicUrl(unsafeUrl));
        var local = Path.Combine(directory, "profile-image");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "profile-images", "nicewigg-transparent-150.png"));
        await File.WriteAllBytesAsync(local, bytes);
        var offline = new StreamTarget(PlatformKind.Twitch, "creator", "https://www.twitch.tv/videos/123", StreamTargetKind.TwitchVod,
            LocalMediaPath: Path.Combine(directory, "index.m3u8"), ProfileImageUrl: new Uri(local).AbsoluteUri);
        Assert.True(LocalWebProfileImages.HasCachedImage(offline));
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
        await LocalWebProfileImages.ServeCachedAsync(context, offline);
        Assert.Equal("image/png", context.Response.ContentType);
        Assert.SequenceEqual(bytes, ((MemoryStream)context.Response.Body).ToArray());
        var webp = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "profile-images", "xqc-2026-10-22.webp"));
        await File.WriteAllBytesAsync(local, webp);
        var kickContext = new DefaultHttpContext(); kickContext.Response.Body = new MemoryStream();
        await LocalWebProfileImages.ServeCachedAsync(kickContext, offline with { Platform = PlatformKind.Kick, Kind = StreamTargetKind.KickVod });
        Assert.Equal("image/webp", kickContext.Response.ContentType);
        Assert.SequenceEqual(webp, ((MemoryStream)kickContext.Response.Body).ToArray());
        await File.WriteAllTextAsync(local, "<html>upstream error</html>");
        var rejected = new DefaultHttpContext();
        await LocalWebProfileImages.ServeCachedAsync(rejected, offline);
        Assert.Equal(404, rejected.Response.StatusCode);
        File.Delete(local);
        Assert.Equal(false, LocalWebProfileImages.HasCachedImage(offline));
        var missing = new DefaultHttpContext();
        await LocalWebProfileImages.ServeCachedAsync(missing, offline);
        Assert.Equal(404, missing.Response.StatusCode);

        static void AssertPicture(JsonElement row, string expected)
        {
            Assert.Equal(expected, row.GetProperty("profileImageUrl").GetString());
            Assert.Equal(false, row.ToString().Contains("file:", StringComparison.Ordinal));
        }
    }

    private sealed class ProfileFixtureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":{"user":{"displayName":"Creator","profileImageURL":"https://static-cdn.jtvnw.net/refreshed.png"}}}""")
            });
    }
}

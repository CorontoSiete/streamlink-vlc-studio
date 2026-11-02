using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static async Task VerifyVodRefreshAsync(LocalWebDependencies dependencies)
    {
        const string picture = "https://static-cdn.jtvnw.net/avatar.png";
        var original = new TwitchVodItem("789", "", "1", "creator", "Creator", "Original title", "",
            "https://www.twitch.tv/videos/789", "", null, null, TimeSpan.FromMinutes(1), null,
            TwitchVodTypeFilter.Archive, ProfileImageUrl: picture);
        var updated = original with { Title = "Updated title", Duration = TimeSpan.FromMinutes(2), ProfileImageUrl = "" };
        var services = dependencies with
        {
            TwitchVods = new FakeTwitchVodService(
                new(TwitchVodSearchStatus.Available, null, [original], "", ""),
                new(TwitchVodSearchStatus.Available, null, [updated], "", ""))
        };
        await using var workspace = new LocalWebWorkspace(services);
        var first = (await Call(workspace, "vods", new { channel = "creator" })).GetProperty("items")[0];
        var refreshed = (await Call(workspace, "vods", new { channel = "creator" })).GetProperty("items")[0];
        var key = first.GetProperty("key").GetString();
        Assert.Equal(key, refreshed.GetProperty("key").GetString());
        var opened = await Call(workspace, "open", new { key });
        Assert.Equal("Updated title", opened.GetProperty("title").GetString());
        Assert.Equal(120d, opened.GetProperty("duration").GetDouble());
        Assert.Equal(picture, opened.GetProperty("profileImageUrl").GetString());
        Assert.SequenceEqual(QualityOption.Defaults.Select(quality => quality.Id), LocalWebWorkspace.Qualities);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)LocalWebWorkspace.Qualities)[0] = "changed");
    }
}

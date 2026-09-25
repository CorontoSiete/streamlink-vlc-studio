using StreamlinkVlcStudio.App.Wpf.ViewModels;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

internal static class PagingRegressionTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("paging review: Twitch VOD pages deduplicate overlapping videos", () => VodDuplicatesAsync(PlatformKind.Twitch)),
        ("paging review: Kick VOD pages deduplicate overlapping videos", () => VodDuplicatesAsync(PlatformKind.Kick)),
        ("paging review: Twitch VOD cursor cycles stop and refresh restarts paging", () => VodCursorCycleAsync(PlatformKind.Twitch)),
        ("paging review: Kick VOD cursor cycles stop and refresh restarts paging", () => VodCursorCycleAsync(PlatformKind.Kick)),
        ("paging review: browse categories reject repeated cursors", BrowseCategoryCursorAsync),
        ("paging review: browse streams reject cursor cycles", BrowseStreamCursorAsync)
    ];

    private static async Task VodDuplicatesAsync(PlatformKind platform)
    {
        await using var viewModel = CreateVodViewModel(platform,
            [(["1", "1"], "next"), (["1", "2", "2"], "")]);
        Assert.Equal(1, viewModel.TwitchVods.Count);
        await viewModel.LoadMoreTwitchVodsCommand.ExecuteAsync();
        Assert.SequenceEqual(new[] { "1", "2" }, viewModel.TwitchVods.Select(vod => vod.Id));
        Assert.Equal(false, viewModel.CanLoadMoreTwitchVods);
    }

    private static async Task VodCursorCycleAsync(PlatformKind platform)
    {
        await using var viewModel = CreateVodViewModel(platform,
            [(["1"], "a"), (["2"], "b"), (["3"], "a"), (["4"], "a")]);
        await viewModel.LoadMoreTwitchVodsCommand.ExecuteAsync();
        Assert.True(viewModel.CanLoadMoreTwitchVods);
        await viewModel.LoadMoreTwitchVodsCommand.ExecuteAsync();
        Assert.Equal(false, viewModel.CanLoadMoreTwitchVods);
        Assert.Equal(3, viewModel.TwitchVods.Count);

        await viewModel.SearchTwitchVodsCommand.ExecuteAsync();
        Assert.True(viewModel.CanLoadMoreTwitchVods);
        Assert.Equal("4", viewModel.TwitchVods.Single().Id);
    }

    private static async Task BrowseCategoryCursorAsync()
    {
        var service = new FakeBrowseService();
        var category = Category();
        service.EnqueueCategories(new(BrowseResultStatus.Available, [category], "a", "first"));
        service.EnqueueCategories(new(BrowseResultStatus.Available, [category], "a", "repeated"));
        service.EnqueueCategories(new(BrowseResultStatus.Available, [category], "a", "refreshed"));
        await using var viewModel = CreateViewModel(browse: service);
        viewModel.ShowBrowseHomePageCommand.Execute(null);
        await viewModel.LoadMoreBrowseCategoriesCommand.ExecuteAsync();
        Assert.Equal(false, viewModel.CanLoadMoreBrowseCategories);
        Assert.Equal(1, viewModel.BrowseCategories.Count);
        await viewModel.RefreshBrowseCommand.ExecuteAsync();
        Assert.True(viewModel.CanLoadMoreBrowseCategories);
    }

    private static async Task BrowseStreamCursorAsync()
    {
        var service = new FakeBrowseService();
        service.EnqueueCategories(new(BrowseResultStatus.Available, [Category()], "", "first"));
        var stream = new BrowseLiveStream(PlatformKind.Twitch, "streamer", "Streamer", "Live",
            "1", "Category", 10, "", null, false, "en", "https://www.twitch.tv/streamer");
        service.EnqueueStreams(new(BrowseResultStatus.Available, [stream], "a", "first"));
        service.EnqueueStreams(new(BrowseResultStatus.Available, [stream], "b", "second"));
        service.EnqueueStreams(new(BrowseResultStatus.Available, [stream], "a", "cycle"));
        await using var viewModel = CreateViewModel(browse: service);
        viewModel.ShowBrowseHomePageCommand.Execute(null);
        await viewModel.BrowseCategories.Single().SelectCommand.ExecuteAsync();
        await viewModel.LoadMoreBrowseStreamsCommand.ExecuteAsync();
        Assert.True(viewModel.CanLoadMoreBrowseStreams);
        await viewModel.LoadMoreBrowseStreamsCommand.ExecuteAsync();
        Assert.Equal(false, viewModel.CanLoadMoreBrowseStreams);
        Assert.Equal(1, viewModel.BrowseStreams.Count);
    }

    private static BrowseCategory Category() => new(PlatformKind.Twitch, "1", "Category", "", [], 10);

    private static MainViewModel CreateVodViewModel(
        PlatformKind platform, (string[] Ids, string Cursor)[] pages)
    {
        var twitch = new FakeTwitchVodService(pages.Select(page => new TwitchVodSearchResult(
            TwitchVodSearchStatus.Available, null, page.Ids.Select(id => new TwitchVodItem(
                id, "stream", "broadcaster", "streamer", "Streamer", "Video", "",
                $"https://www.twitch.tv/videos/{id}", "", null, null, TimeSpan.FromMinutes(1),
                10, TwitchVodTypeFilter.Archive)).ToArray(), page.Cursor, "page")).ToArray());
        var kick = new FakeKickVodService(pages.Select(page => new KickVodSearchResult(
            KickVodSearchStatus.Available, page.Ids.Select(id => new KickVodItem(
                id, "stream", id, "streamer", "Streamer", "Video", $"https://kick.com/streamer/videos/{id}",
                $"https://vod.kick.com/{id}.m3u8", "", "", null, null, TimeSpan.FromMinutes(1), 10))
                .ToArray(), page.Cursor, "page")).ToArray());
        var viewModel = CreateViewModel(twitch, kick);
        viewModel.ShowTwitchVodsHomePageCommand.Execute(null);
        if (platform == PlatformKind.Kick) viewModel.SelectKickVodPlatformCommand.Execute(null);
        viewModel.TwitchVodSearchText = "streamer";
        return viewModel;
    }

    private static MainViewModel CreateViewModel(
        ITwitchVodService? twitch = null, IKickVodService? kick = null, IBrowseService? browse = null)
    {
        var settings = new AppSettings();
        return TestViewModels.CreateMain(settings, new FakeSettingsService(settings),
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
            new MemoryLogger(), action => action(), twitchVodService: twitch, kickVodService: kick,
            browseService: browse, twitchVodSearchDebounceInterval: TimeSpan.Zero);
    }
}

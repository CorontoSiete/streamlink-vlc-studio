using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static async Task VerifyVodLibraryAsync(LocalWebDependencies dependencies)
    {
        var newest = new DateTimeOffset(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);
        const string thumbnail = "https://files.kick.com/vod.jpg";
        var twitch = new TwitchVodItem("123", "", "1", "creator", "Creator", "Newest broadcast", "",
            "https://www.twitch.tv/videos/123", thumbnail, newest.AddDays(-3), newest, TimeSpan.FromHours(2), 1, TwitchVodTypeFilter.Archive);
        var createdOnly = twitch with { Id = "124", Title = "Created date fallback", PublishedAtUtc = null, CreatedAtUtc = newest.AddDays(-4) };
        var unknown = twitch with { Id = "125", Title = "No date", PublishedAtUtc = null, CreatedAtUtc = null, ThumbnailUrl = "file:///C:/private/image.jpg" };
        var kickNew = new KickVodItem("456", "stream-new", "uuid-new", "creator", "Creator", "Kick broadcast", "https://kick.com/creator/videos/uuid-new",
            "https://stream.kick.com/new.m3u8", thumbnail, "Games", newest.AddDays(-2), newest.AddDays(-5), TimeSpan.FromHours(1), 1);
        var kickOld = kickNew with { Id = "457", LiveStreamId = "stream-old", Uuid = "uuid-old", CreatedAtUtc = newest.AddDays(-3) };
        StreamTarget Target(string id, bool kick = false) => new(kick ? PlatformKind.Kick : PlatformKind.Twitch, "creator",
            kick ? "https://kick.com/creator/videos/" + id : "https://www.twitch.tv/videos/" + id,
            kick ? StreamTargetKind.KickVod : StreamTargetKind.TwitchVod, id, id);
        VodDownloadItem Download(StreamTarget target, int queuedDays) => new(Guid.NewGuid(), target, "best", newest.AddDays(queuedDays), VodDownloadState.Completed);
        var stored = Target("888") with { MediaThumbnailUrl = thumbnail, MediaPublishedAtUtc = newest.AddDays(-1) };
        var downloads = new VodLibraryDownloads([
            Download(Target("999"), 10), Download(Target("uuid-old", true), 9), Download(Target("123"), -10),
            Download(stored, -9), Download(Target("stream-new", true), 8)
        ]);
        var kickVods = new FakeKickVodService(new(KickVodSearchStatus.Available, [kickNew], "page2", ""),
            new(KickVodSearchStatus.Available, [kickOld], "", ""));
        var services = dependencies with
        {
            Downloads = downloads,
            TwitchVods = new FakeTwitchVodService(new TwitchVodSearchResult(TwitchVodSearchStatus.Available, null, [unknown, createdOnly, twitch], "next", "")),
            KickVods = kickVods
        };
        await using var workspace = new LocalWebWorkspace(services);
        var library = (await Call(workspace, "downloads", new { })).GetProperty("items");
        Assert.SequenceEqual(new[] { "123", "888", "stream-new", "uuid-old", "999" }, library.EnumerateArray().Select(row => row.GetProperty("title").GetString()!));
        Assert.Equal(newest, library[0].GetProperty("published").GetDateTimeOffset());
        foreach (var row in library.EnumerateArray().Take(4)) Assert.Equal(thumbnail, row.GetProperty("thumbnailUrl").GetString());
        Assert.Equal(JsonValueKind.Null, library[4].GetProperty("published").ValueKind);
        Assert.Equal("", library[4].GetProperty("thumbnailUrl").GetString());
        Assert.Equal(2, kickVods.CallCount);
        await Call(workspace, "downloads", new { });
        Assert.Equal(2, kickVods.CallCount); // Progress polls do not fetch the channel again.

        var vods = (await Call(workspace, "vods", new { channel = "creator" })).GetProperty("items");
        Assert.SequenceEqual(new[] { "Newest broadcast", "Created date fallback", "No date" }, vods.EnumerateArray().Select(row => row.GetProperty("title").GetString()!));
        Assert.Equal(newest, vods[0].GetProperty("published").GetDateTimeOffset());
        Assert.Equal(createdOnly.CreatedAtUtc, vods[1].GetProperty("published").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, vods[2].GetProperty("published").ValueKind);
        Assert.Equal("", vods[2].GetProperty("thumbnailUrl").GetString());
        Assert.Equal("next", (await Call(workspace, "vods", new { channel = "creator" })).GetProperty("nextCursor").GetString());
        await Call(workspace, "downloads/add", new { key = vods[0].GetProperty("key").GetString() });
        var saved = JsonSerializer.Deserialize<StreamTarget>(JsonSerializer.Serialize(downloads.Enqueued!.Target))!;
        Assert.Equal(thumbnail, saved.MediaThumbnailUrl);
        Assert.Equal(newest, saved.MediaPublishedAtUtc);

        var kickBrowse = services with
        {
            KickVods = new FakeKickVodService(new KickVodSearchResult(KickVodSearchStatus.Available,
            [kickOld with { CreatedAtUtc = null }, kickNew], "", ""))
        };
        await using var kickWorkspace = new LocalWebWorkspace(kickBrowse);
        var kickCards = (await Call(kickWorkspace, "vods", new { channel = "creator", platform = "kick" })).GetProperty("items");
        Assert.Equal(kickNew.CreatedAtUtc, kickCards[0].GetProperty("published").GetDateTimeOffset());
        Assert.Equal(kickOld.StartedAtUtc, kickCards[1].GetProperty("published").GetDateTimeOffset());

        await using var unavailable = new LocalWebWorkspace(services with { TwitchVods = new UnavailableLibraryVods() });
        Assert.Equal(5, (await Call(unavailable, "downloads", new { })).GetProperty("items").GetArrayLength());
    }

    private sealed class UnavailableLibraryVods : ITwitchVodService
    {
        public Task<TwitchVodItem?> GetVideoAsync(string vodId, CancellationToken cancellationToken = default) => throw new HttpRequestException("Provider unavailable");
        public Task<TwitchVodSearchResult> SearchAsync(TwitchVodSearchRequest request, AppSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class VodLibraryDownloads(IReadOnlyList<VodDownloadItem> items, IVodDownloadPlayback? playback = null) : IVodDownloadService
    {
        public VodDownloadRequest? Enqueued { get; private set; }
        public string DownloadDirectory => "";
        public event Action<VodDownloadItem>? DownloadChanged { add { } remove { } }
        public Task<IReadOnlyList<VodDownloadItem>> GetDownloadsAsync(CancellationToken cancellationToken = default) => Task.FromResult(items);
        public Task<VodDownloadItem> EnqueueAsync(VodDownloadRequest request, CancellationToken cancellationToken = default)
        {
            Enqueued = request;
            return Task.FromResult(new VodDownloadItem(Guid.NewGuid(), request.Target, request.Quality, DateTimeOffset.UtcNow));
        }
        public void SetBandwidthLimit(long bytesPerSecond) { }
        public Task ChangeDownloadDirectoryAsync(string downloadDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ChangeDownloadFormatAsync(VodDownloadFormat format, VodDownloadOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RetryAsync(Guid id, VodDownloadOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StreamTarget> GetOfflineTargetAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IVodDownloadPlayback> AcquireOfflinePlaybackAsync(Guid id, CancellationToken cancellationToken = default) =>
            playback is not null ? Task.FromResult(playback) : throw new NotSupportedException();
        public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

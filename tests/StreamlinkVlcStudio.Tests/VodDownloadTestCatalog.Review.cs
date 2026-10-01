using System.Collections;

internal static partial class VodDownloadTestCatalog
{
    private static async Task DeltaPlaylistAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        var playlist = DownloadFixture.SimplePlaylist.Replace("#EXT-X-TARGETDURATION:3",
            "#EXT-X-TARGETDURATION:3\n#EXT-X-SKIP:SKIPPED-SEGMENTS=42", StringComparison.Ordinal);
        fixture.Handler.Put("/redirected/index.m3u8", Encoding.UTF8.GetBytes(playlist));
        var item = await fixture.EnqueueAsync();
        await fixture.WaitAsync(item.Id, VodDownloadState.Failed);
        Assert.Equal(1, fixture.Handler.RequestCount);
        Assert.True(!Directory.Exists(Path.Combine(fixture.Library, item.Id.ToString("N"), "media")));
    }

    private static Task KickFlightRecordsAsync()
    {
        const string first = "1:{\"channel_id\":668}\n";
        Assert.Equal(668L, KickVodDownloadResolver.ReadChannelId(NextScript(first + "2:{\"channel_id\":668}\n")));
        foreach (var tail in new[] { "2:{\"channel_id\":999}\n", "2:{\"channel_id\":668", "2:Tffff,short" })
            Assert.Throws<InvalidDataException>(() => KickVodDownloadResolver.ReadChannelId(NextScript(first) + NextScript(tail)));
        return Task.CompletedTask;
    }

    private static async Task MutedKeyNameAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        fixture.Handler.Put("/redirected/index.m3u8", Encoding.UTF8.GetBytes(DownloadFixture.SimplePlaylist.Replace(
            "#EXT-X-TARGETDURATION:3", "#EXT-X-TARGETDURATION:3\n#EXT-X-KEY:METHOD=AES-128,URI=\"encryption-muted.ts\"\n#EXT-X-KEY:METHOD=NONE",
            StringComparison.Ordinal)));
        fixture.Handler.Put("/redirected/encryption-muted.ts", new byte[16]);
        var item = await fixture.DownloadAsync();
        var offline = await fixture.Service.GetOfflineTargetAsync(item.Id);
        Assert.True(offline.IsOfflineVod);
        var manifest = await File.ReadAllTextAsync(offline.LocalMediaPath);
        Assert.Contains("asset-000000.key", manifest);
        await fixture.RestartAsync();
        Assert.Equal(VodDownloadState.Completed, (await fixture.Service.GetDownloadsAsync()).Single().State);
    }

    private static async Task ReplacedQueuedJobsAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var entered = fixture.BlockSegment();
        await fixture.EnqueueAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = await fixture.EnqueueAsync(VodDownloadUrlParser.Parse("https://twitch.tv/videos/54321"));
        var cancellations = new List<CancellationTokenSource>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var jobs = (IDictionary)typeof(VodDownloadService).GetField("jobs", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(fixture.Service)!;
            var job = jobs[queued.Id]!;
            cancellations.Add((CancellationTokenSource)job.GetType()
                .GetProperty("Cancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(job)!);
            await fixture.Service.CancelAsync(queued.Id);
            await fixture.Service.RetryAsync(queued.Id, fixture.Options);
        }
        await fixture.Service.DisposeAsync();
        foreach (var cancellation in cancellations)
            Assert.Throws<ObjectDisposedException>(() => _ = cancellation.Token);
    }
}

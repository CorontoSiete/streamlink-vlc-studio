using StreamlinkVlcStudio.Infrastructure.Previews;

internal static class ComprehensiveReviewTestCatalog
{
    private static readonly Uri PlaylistUri = new("https://vod-secure.twitch.tv/archive/index.m3u8");
    private const string Playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXTINF:3,\n0.ts\n#EXTINF:3,\n1.ts\n";

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("comprehensive review: BOM playlists retain their published duration", PublishedDurationAsync),
        ("comprehensive review: BOM playlists preserve live previews and variant selection", PreviewHeadersAsync),
        ("comprehensive review: muted downloads retry stalled HTTP reads", MutedReadRetryAsync),
        ("comprehensive review: VOD downloads retry transient timeout exceptions", DownloadTimeoutRetryAsync),
        ("comprehensive review: muted repair preserves independent source cancellation", RepairCancellationAsync),
        ("comprehensive review: muted repair proxy keeps progressing transfers alive", ProxyReadBudgetAsync),
        ("comprehensive review: muted repair proxy reports stalled upstream reads", ProxyReadTimeoutAsync)
    ];

    private static Task PublishedDurationAsync()
    {
        Assert.Equal(TimeSpan.FromSeconds(6), HlsReplayTimeline.ParsePublishedDuration("\uFEFF" + Playlist));
        return Task.CompletedTask;
    }

    private static Task PreviewHeadersAsync()
    {
        var expected = LivePreviewPlaylist.Rewrite(Playlist, PlaylistUri, PlatformKind.Twitch, out _);
        Assert.Equal(expected, LivePreviewPlaylist.Rewrite("\uFEFF" + Playlist, PlaylistUri, PlatformKind.Twitch, out _));
        const string master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000,IVS-NAME=\"source\"\nsource/index.m3u8\n";
        Assert.Equal(new Uri(PlaylistUri, "source/index.m3u8"),
            TwitchVodVariantPlaylist.Select("\uFEFF" + master, PlaylistUri, "best"));
        return Task.CompletedTask;
    }

    private static async Task MutedReadRetryAsync()
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(PlatformKind.Twitch);
        fixture.Client.Timeout = TimeSpan.FromMilliseconds(150);
        fixture.Handler.Put("/redirected/index.m3u8", Encoding.UTF8.GetBytes(
            VodDownloadTestCatalog.DownloadFixture.SimplePlaylist.Replace("segment-a.ts", "segment-a-muted.ts", StringComparison.Ordinal)));
        fixture.Handler.Put("/redirected/segment-a-muted.ts", new byte[8]);
        using var stalled = new CodeReviewTestCatalog.StalledReadStream();
        var attempts = 0;
        fixture.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(
            request.RequestUri!.AbsolutePath.EndsWith("segment-a-muted.ts", StringComparison.Ordinal) &&
            Interlocked.Increment(ref attempts) == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalled), RequestMessage = request }
                : null);
        var item = await fixture.DownloadAsync();
        Assert.Equal(2, attempts);
        Assert.True(stalled.SawCancellation);
        Assert.Equal(20L, item.BytesDownloaded);
        Assert.True((await fixture.Service.GetOfflineTargetAsync(item.Id)).IsOfflineVod);
    }

    private static async Task DownloadTimeoutRetryAsync()
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(PlatformKind.Kick);
        var attempts = 0;
        fixture.Handler.Override = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("segment-a.ts", StringComparison.Ordinal) &&
                Interlocked.Increment(ref attempts) == 1)
                throw new TimeoutException("Transient segment timeout.");
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        var item = await fixture.DownloadAsync();
        Assert.Equal(2, attempts);
        Assert.Equal(20L, item.BytesDownloaded);
    }

    private static async Task RepairCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var source = new IndependentlyCanceledStream(cancellation.Token);
        var canceled = await Assert.ThrowsAsync<OperationCanceledException>(() => TwitchMutedSegmentRepairCopier.CopyAsync(
            source, Stream.Null, 188,
            TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
    }

    private static async Task ProxyReadBudgetAsync()
    {
        var bytes = Enumerable.Range(0, 8).Select(index => (byte)index).ToArray();
        using var source = new RepositoryAuditTestCatalog.PacedReadStream(bytes);
        var result = await ReadProxySegmentAsync(source);
        Assert.SequenceEqual(bytes, result.Bytes);
    }

    private static async Task ProxyReadTimeoutAsync()
    {
        using var source = new CodeReviewTestCatalog.StalledReadStream();
        var result = await ReadProxySegmentAsync(source);
        Assert.True(source.SawCancellation);
        Assert.True(result.Logger.Entries.Any(entry => entry.Source == TwitchMutedVodRepairLog.Source &&
            entry.Message.Contains("was interrupted", StringComparison.Ordinal) &&
            entry.Exception is OperationCanceledException), "A stalled upstream read must retain its cancellation cause in the log.");
        Assert.True(result.Logger.Entries.All(entry => !entry.Message.Contains("secret", StringComparison.Ordinal)));
    }

    private static async Task<(byte[] Bytes, MemoryLogger Logger)> ReadProxySegmentAsync(Stream source)
    {
        using var upstream = new HttpClient(new FakeHttpMessageHandler(request => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(source),
            RequestMessage = request
        }))
        { Timeout = TimeSpan.FromMilliseconds(150) };
        var logger = new MemoryLogger();
        await using var proxy = new TwitchMutedVodRepairProxy(logger, upstream, TestReplayUrlSecurity.PublicValidator);
        using var session = proxy.OpenSession(new TwitchVodPlaylistSource(PlaylistUri,
            static _ => Task.FromResult("#EXTM3U\n#EXTINF:3,\n0-muted.ts?sig=secret\n#EXT-X-ENDLIST\n")));
        using var player = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var segment = (await player.GetStringAsync(session.PlaylistUri))
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .First(line => !line.StartsWith('#'));
        return (await player.GetByteArrayAsync(segment), logger);
    }

    private sealed class IndependentlyCanceledStream(CancellationToken canceledToken) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromCanceled<int>(canceledToken);
    }
}

internal static class HlsReplayTimelineTestCatalog
{
    private static readonly Uri PlaylistUri = new("https://d1m7jfoe9zdc1j.cloudfront.net/vod/chunked/index.m3u8");
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("long VOD timeline preserves duration precision sequences and preroll", () => Run(Precision)),
        ("long VOD timeline preserves discontinuities and muted segment URLs", () => Run(Discontinuities)),
        ("long VOD timeline leaves growing encrypted ranged and master playlists untouched", () => Run(Unsupported)),
        ("long VOD timeline rejects malformed segments and unsafe media URLs", () => Run(Invalid)),
        ("long VOD timeline owns and releases its temporary playlist and upstream lease", LeaseAsync),
        ("long VOD timeline bounds stalled local playlist reads", LocalPlaylistTimeoutAsync)
    ];
    private const string Header = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:12\n#EXT-X-MEDIA-SEQUENCE:10\n";
    private const string Body = "#EXTINF:10.001,\n0.ts\n#EXTINF:9.999,\n1.ts\n#EXTINF:10.000,\n2-muted.ts\n#EXTINF:10.000,\n3.ts\n#EXT-X-ENDLIST\n";
    private static Task Run(Action action) { action(); return Task.CompletedTask; }
    private static void Precision()
    {
        var result = HlsReplayTimeline.Rebase(Header + Body, PlaylistUri, TimeSpan.FromSeconds(25))!.Value;
        Assert.Equal(TimeSpan.FromSeconds(10.001), result.Offset);
        Assert.True(result.Playlist.Contains("#EXT-X-MEDIA-SEQUENCE:11"));
        Assert.True(result.Playlist.Contains("#EXTINF:9.999,"));
        Assert.True(result.Playlist.Contains(new Uri(PlaylistUri, "1.ts").AbsoluteUri));
        Assert.Equal(false, result.Playlist.Contains("/0.ts"));
        Assert.Equal(TimeSpan.FromSeconds(20), HlsReplayTimeline.Rebase(Header + Body, PlaylistUri, TimeSpan.FromHours(20))!.Value.Offset);
        Assert.Equal(TimeSpan.FromSeconds(10.001), HlsReplayTimeline.Rebase(Header + Body, PlaylistUri, TimeSpan.FromSeconds(20))!.Value.Offset);
        Assert.True(HlsReplayTimeline.Rebase(Header + Body, PlaylistUri, TimeSpan.Zero) is null);
    }
    private static void Discontinuities()
    {
        var content = Header + "#EXT-X-DISCONTINUITY-SEQUENCE:4\n" + Body
            .Replace("#EXTINF:9.999,", "#EXT-X-DISCONTINUITY\n#EXTINF:9.999,")
            .Replace("#EXTINF:10.000,\n2", "#EXT-X-DISCONTINUITY\n#EXTINF:10.000,\n2");
        var result = HlsReplayTimeline.Rebase(content, PlaylistUri, TimeSpan.FromSeconds(35))!.Value;
        Assert.True(result.Playlist.Contains("#EXT-X-DISCONTINUITY-SEQUENCE:5"));
        Assert.True(result.Playlist.Replace("\r\n", "\n").Contains("#EXT-X-DISCONTINUITY\n#EXTINF:10.000,"));
        Assert.True(result.Playlist.Contains("/2-muted.ts"));
    }
    private static void Unsupported()
    {
        foreach (var tag in new[] { "#EXT-X-STREAM-INF:BANDWIDTH=100", "#EXT-X-KEY:METHOD=AES-128", "#EXT-X-MAP:URI=\"init.mp4\"", "#EXT-X-BYTERANGE:50@0" })
            Assert.True(HlsReplayTimeline.Rebase(Header + tag + "\n" + Body, PlaylistUri, TimeSpan.FromHours(14)) is null);
        Assert.True(HlsReplayTimeline.Rebase(Header + Body.Replace("#EXT-X-ENDLIST", ""), PlaylistUri, TimeSpan.FromHours(14)) is null);
    }
    private static void Invalid()
    {
        foreach (var body in new[] { Body.Replace("10.001", "NaN"), Body.Replace("0.ts", "https://127.0.0.1/0.ts"), Body.Replace("0.ts\n", "") })
        {
            try { HlsReplayTimeline.Rebase(Header + body, PlaylistUri, TimeSpan.FromHours(14)); throw new Exception("Expected malformed playlist rejection."); }
            catch (InvalidDataException) { }
        }
    }
    private static async Task LeaseAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"svs-timeline-{Guid.NewGuid():N}.m3u8");
        await File.WriteAllTextAsync(path, Header + Body);
        var lease = new TrackingLease();
        try
        {
            using var prepared = await HlsReplayTimeline.PrepareAsync(new PlaybackMediaSource(new Uri(path), lease), TimeSpan.FromHours(14), CancellationToken.None);
            var temporary = prepared.PlaybackUri.LocalPath;
            Assert.True(File.Exists(temporary));
            Assert.Equal(false, lease.Disposed);
            prepared.Dispose();
            Assert.Equal(false, File.Exists(temporary));
            Assert.True(lease.Disposed);
        }
        finally { File.Delete(path); }
    }
    private static async Task LocalPlaylistTimeoutAsync()
    {
        using var stream = new CodeReviewTestCatalog.StalledReadStream();
        using var client = new HttpClient(new FakeHttpMessageHandler(_ => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        }))
        { Timeout = TimeSpan.FromMilliseconds(300) };
        using var source = PlaybackMediaSource.Direct(new Uri("http://127.0.0.1:54321/replay.m3u8"));
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => HlsReplayTimeline.PrepareAsync(
                source, TimeSpan.FromHours(14), cleanup.Token, client).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(stream.SawCancellation);
            Assert.Equal(false, cleanup.IsCancellationRequested);
        }
        finally { cleanup.Cancel(); }
    }

    private sealed class TrackingLease : IDisposable
    {
        internal bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}

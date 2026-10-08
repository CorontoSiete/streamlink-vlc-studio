internal static class LiveReplayDemuxerTestCatalog
{
    private static readonly Uri MediaUri = new("https://d2vi6trrdongqn.cloudfront.net/fixture/chunked/index-dvr.m3u8");
    private static readonly Version Vlc = new(3, 0, 23);
    private const string Playlist = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n#EXT-X-PLAYLIST-TYPE:EVENT\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:9.75,\n0.ts\n#EXTINF:10.0,\n1.ts\n";

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("live replay demuxer: only tested VLC and unencrypted TS EVENT playlists opt in", PolicyAsync),
        ("live replay demuxer: direct and repaired growing replays retain source ownership", GatewayAsync),
        ("live replay demuxer: unsupported and failed inspections retain installed VLC modules", FallbackAsync)
    ];

    private static Task PolicyAsync()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), TwitchVodReplayPolicy.GetLiveReplaySegmentDuration(Playlist, Vlc));
        Assert.Equal(TimeSpan.Zero, TwitchVodReplayPolicy.GetPreroll(Playlist, Vlc));
        foreach (var version in new Version?[] { null, new(3, 0, 12), new(3, 0, 24), new(4, 0, 0) })
            Assert.Equal(TimeSpan.Zero, TwitchVodReplayPolicy.GetLiveReplaySegmentDuration(Playlist, version));
        foreach (var unsupported in new[]
        {
            Playlist + "#EXT-X-ENDLIST\n", Playlist.Replace("EVENT", "VOD"),
            Playlist.Replace("#EXT-X-PLAYLIST-TYPE:EVENT\n", ""), Playlist.Replace("0.ts", "0.m4s"),
            Playlist.Replace("9.75", "NaN"), Playlist.Replace("9.75", "31"),
            Playlist.Replace("0.ts\n", ""), Playlist.Replace("#EXTINF:9.75,\n", ""),
            Playlist.Replace("#EXTM3U", "#BAD"), Playlist.Replace("TARGETDURATION:10", "TARGETDURATION:1")
        }) Assert.Equal(TimeSpan.Zero, TwitchVodReplayPolicy.GetLiveReplaySegmentDuration(unsupported, Vlc));
        foreach (var tag in new[] { "KEY:METHOD=AES-128,URI=\"key\"", "MAP:URI=\"init.mp4\"", "BYTERANGE:99@0",
            "DISCONTINUITY", "DISCONTINUITY-SEQUENCE:1", "GAP", "STREAM-INF:BANDWIDTH=100", "PART:DURATION=1,URI=\"part.ts\"" })
            Assert.Equal(TimeSpan.Zero, TwitchVodReplayPolicy.GetLiveReplaySegmentDuration(
                Playlist.Replace("#EXTINF:9.75,", "#EXT-X-" + tag + "\n#EXTINF:9.75,"), Vlc));
        return Task.CompletedTask;
    }

    private static async Task GatewayAsync()
    {
        foreach (var muted in new[] { false, true })
            foreach (var timestampedOpen in new[] { false, true })
            {
                using var upstream = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(muted ? Playlist.Replace("1.ts", "1-muted.ts") : Playlist) }));
                await using var gateway = new TwitchMutedVodPlaybackGateway(new MemoryLogger(), upstream, TestReplayUrlSecurity.PublicValidator);
                using var source = await gateway.PrepareAsync(MediaUri, Vlc, CancellationToken.None, timestampedOpen);
                Assert.True(source.UseLiveReplayDemuxer);
                Assert.Equal(TimeSpan.FromSeconds(10), source.LiveReplaySegmentDuration);
                Assert.Equal(false, source.UseAvformatDemuxer);
                Assert.Equal(TimeSpan.Zero, source.ReplaySeekPreroll);
                Assert.Equal(muted, source.PlaybackUri.IsLoopback);
                if (!muted) Assert.Equal(MediaUri, source.PlaybackUri);
                else
                {
                    using var client = new HttpClient();
                    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(source.PlaybackUri)).StatusCode);
                    source.Dispose();
                    Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(source.PlaybackUri)).StatusCode);
                }
            }
    }

    private static async Task FallbackAsync()
    {
        foreach (var content in new string?[] { Playlist + "#EXT-X-ENDLIST\n", Playlist.Replace("0.ts", "0.m4s"), null })
        {
            using var upstream = new HttpClient(new FakeHttpMessageHandler(_ => content is null
                ? throw new HttpRequestException("Fixture inspection failure")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) }));
            await using var gateway = new TwitchMutedVodPlaybackGateway(new MemoryLogger(), upstream, TestReplayUrlSecurity.PublicValidator);
            using var source = await gateway.PrepareAsync(MediaUri, Vlc, CancellationToken.None);
            Assert.Equal(false, source.UseLiveReplayDemuxer);
            Assert.Equal(TimeSpan.Zero, source.LiveReplaySegmentDuration);
            Assert.Equal(MediaUri, source.PlaybackUri);
        }
    }
}

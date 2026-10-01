using StreamlinkVlcStudio.Infrastructure.Previews;

internal static class HlsDeltaPlaylistTestCatalog
{
    private static readonly Uri PlaylistUri = new("https://d1m7jfoe9zdc1j.cloudfront.net/vod/index.m3u8");
    private const string Header = "#EXTM3U\n#EXT-X-TARGETDURATION:5\n#EXT-X-MEDIA-SEQUENCE:0\n";
    private const string Segments = "#EXTINF:5,\n2.ts\n#EXTINF:5,\n3.ts\n#EXTINF:5,\n4.ts\n#EXTINF:5,\n5.ts\n";
    private const string Delta = "#EXT-X-SKIP:SKIPPED-SEGMENTS=2\n";

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("HLS delta playlists: published duration rejects omitted segments", () => Run(PublishedDuration)),
        ("HLS delta playlists: long replay rebasing requires all segment metadata", () => Run(ReplayRebasing)),
        ("HLS delta playlists: seek previews reject incomplete timelines", () => Run(SeekPreviews)),
        ("HLS delta playlists: direct live previews require a complete manifest", () => Run(LivePreviews)),
        ("HLS delta playlists: DVR discovery rejects omitted segments", () => Run(DvrDiscovery)),
        ("HLS playlists: DVR discovery requires an exact header", () => Run(DvrHeader)),
        ("HLS playlists: seek previews require an exact header", () => Run(SeekHeader))
    ];

    private static void PublishedDuration()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), HlsReplayTimeline.ParsePublishedDuration(Header + Segments));
        Assert.True(HlsReplayTimeline.ParsePublishedDuration(Header + Delta + Segments) is null);
        Assert.Equal(TimeSpan.FromSeconds(20), HlsReplayTimeline.ParsePublishedDuration(
            Header + "#EXT-X-SKIPPER:custom-metadata\n" + Segments));
    }

    private static void ReplayRebasing()
    {
        Assert.NotNull(HlsReplayTimeline.Rebase(Header + Segments + "#EXT-X-ENDLIST\n", PlaylistUri,
            TimeSpan.FromSeconds(15)));
        Assert.True(HlsReplayTimeline.Rebase(Header + Delta + Segments + "#EXT-X-ENDLIST\n", PlaylistUri,
            TimeSpan.FromSeconds(15)) is null);
    }

    private static void SeekPreviews()
    {
        Assert.NotNull(LiveSeekPlaylist.Parse(Header + Segments, PlaylistUri, PlatformKind.Twitch, null));
        Assert.True(LiveSeekPlaylist.Parse(Header + Delta + Segments, PlaylistUri, PlatformKind.Twitch, null) is null);
    }

    private static void LivePreviews()
    {
        Assert.Contains("/vod/2.ts", LivePreviewPlaylist.Rewrite(Header + Segments, PlaylistUri, PlatformKind.Twitch));
        Assert.Throws<InvalidDataException>(() => LivePreviewPlaylist.Rewrite(Header + Delta + Segments,
            PlaylistUri, PlatformKind.Twitch));
    }

    private static Task Run(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private static void DvrDiscovery()
    {
        Assert.True(ReplayResolver.IsValidTwitchDvrPlaylist(Header + Segments));
        Assert.True(!ReplayResolver.IsValidTwitchDvrPlaylist(Header + Delta + Segments));
        Assert.True(ReplayResolver.IsValidTwitchDvrPlaylist(Header + "#EXT-X-SKIPPER:custom-metadata\n" + Segments));
    }

    private static void DvrHeader()
    {
        Assert.True(ReplayResolver.IsValidTwitchDvrPlaylist("\uFEFF" + Header + Segments));
        Assert.True(!ReplayResolver.IsValidTwitchDvrPlaylist(Header.Replace("#EXTM3U", "#EXTM3UX") + Segments));
        Assert.True(!ReplayResolver.IsValidTwitchDvrPlaylist("<html>\n" + Header + Segments + "</html>"));
        Assert.True(!ReplayResolver.IsValidTwitchDvrPlaylist(Header + Segments.Replace("#EXTINF:", "#EXTINFO:")));
        Assert.True(!ReplayResolver.IsValidTwitchDvrPlaylist(Header + "#EXT-X-STREAM-INF:BANDWIDTH=1000\n" + Segments));
    }

    private static void SeekHeader()
    {
        Assert.NotNull(LiveSeekPlaylist.Parse("\uFEFF" + Header + Segments, PlaylistUri, PlatformKind.Twitch, null));
        Assert.True(LiveSeekPlaylist.Parse(Header.Replace("#EXTM3U", "#EXTM3UX") + Segments,
            PlaylistUri, PlatformKind.Twitch, null) is null);
        Assert.True(LiveSeekPlaylist.Parse("<html>\n" + Header + Segments + "</html>",
            PlaylistUri, PlatformKind.Twitch, null) is null);
    }
}

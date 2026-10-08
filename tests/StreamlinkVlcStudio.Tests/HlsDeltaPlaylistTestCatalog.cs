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
        ("HLS playlists: seek previews require an exact header", () => Run(SeekHeader)),
        ("HLS playlist review: offline VODs accept completion markers at any position", () => Run(OfflineCompletionMarker)),
        ("HLS playlist review: offline VODs accept global target duration after media", () => Run(OfflineGlobalDuration)),
        ("HLS playlist review: rebased VODs retain early completion markers", () => Run(RebasedCompletionMarker)),
        ("HLS playlist review: rebased VODs retain global tags after media", () => Run(RebasedGlobalTags)),
        ("HLS playlist review: Twitch downloads materialize ranges before segment durations", () => RangesBeforeDurationsAsync(PlatformKind.Twitch)),
        ("HLS playlist review: Kick downloads materialize ranges before segment durations", () => RangesBeforeDurationsAsync(PlatformKind.Kick)),
        ("HLS playlist review: seek previews reject duplicate segment durations", () => Run(DuplicatePreviewDuration)),
        ("HLS playlist review: seek previews reject incomplete final segments", () => Run(IncompletePreviewSegment)),
        ("HLS playlist review: duration readers reject unsupported notation", () => Run(InvalidDurationNotation)),
        ("HLS playlist review: duration readers preserve fractional seconds", () => Run(FractionalDurations)),
        ("HLS playlist review: offline completion requires complete segments and one marker", () => Run(InvalidOfflineCompletion))
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
        Assert.Contains("/vod/2.ts", LivePreviewPlaylist.Rewrite(Header + Segments, PlaylistUri, PlatformKind.Twitch, out _));
        Assert.Throws<InvalidDataException>(() => LivePreviewPlaylist.Rewrite(Header + Delta + Segments,
            PlaylistUri, PlatformKind.Twitch, out _));
    }

    private static Task Run(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private static void DvrDiscovery()
    {
        Assert.True(TwitchReplayProvider.IsValidTwitchDvrPlaylist(Header + Segments));
        Assert.True(!TwitchReplayProvider.IsValidTwitchDvrPlaylist(Header + Delta + Segments));
        Assert.True(TwitchReplayProvider.IsValidTwitchDvrPlaylist(Header + "#EXT-X-SKIPPER:custom-metadata\n" + Segments));
    }

    private static void DvrHeader()
    {
        Assert.True(TwitchReplayProvider.IsValidTwitchDvrPlaylist("\uFEFF" + Header + Segments));
        Assert.True(!TwitchReplayProvider.IsValidTwitchDvrPlaylist(Header.Replace("#EXTM3U", "#EXTM3UX") + Segments));
        Assert.True(!TwitchReplayProvider.IsValidTwitchDvrPlaylist("<html>\n" + Header + Segments + "</html>"));
        Assert.True(!TwitchReplayProvider.IsValidTwitchDvrPlaylist(Header + Segments.Replace("#EXTINF:", "#EXTINFO:")));
        Assert.True(!TwitchReplayProvider.IsValidTwitchDvrPlaylist(Header + "#EXT-X-STREAM-INF:BANDWIDTH=1000\n" + Segments));
    }

    private static void SeekHeader()
    {
        Assert.NotNull(LiveSeekPlaylist.Parse("\uFEFF" + Header + Segments, PlaylistUri, PlatformKind.Twitch, null));
        Assert.True(LiveSeekPlaylist.Parse(Header.Replace("#EXTM3U", "#EXTM3UX") + Segments,
            PlaylistUri, PlatformKind.Twitch, null) is null);
        Assert.True(LiveSeekPlaylist.Parse("<html>\n" + Header + Segments + "</html>",
            PlaylistUri, PlatformKind.Twitch, null) is null);
    }

    private static void OfflineCompletionMarker()
    {
        foreach (var content in new[]
        {
            Header + "#EXT-X-ENDLIST\n" + Segments,
            Header + Segments.Replace("3.ts\n", "3.ts\n#EXT-X-ENDLIST\n", StringComparison.Ordinal),
            Header + Segments + "#EXT-X-ENDLIST\n"
        })
        {
            var playlist = OfflineHlsPlaylist.Parse(content, PlaylistUri);
            Assert.Equal(4, playlist.SegmentCount);
            Assert.Equal(TimeSpan.FromSeconds(20), playlist.Duration);
            Assert.True(playlist.Content.TrimEnd().EndsWith("#EXT-X-ENDLIST", StringComparison.Ordinal));
            Assert.Equal(1, playlist.Content.Split('\n').Count(line => line.TrimEnd('\r') == "#EXT-X-ENDLIST"));
        }
    }

    private static void OfflineGlobalDuration()
    {
        var playlist = OfflineHlsPlaylist.Parse(
            Header.Replace("#EXT-X-TARGETDURATION:5\n", "", StringComparison.Ordinal) + Segments +
            "#EXT-X-TARGETDURATION:5\n#EXT-X-ENDLIST\n", PlaylistUri);
        Assert.Equal(TimeSpan.FromSeconds(20), playlist.Duration);
        Assert.Contains("#EXT-X-TARGETDURATION:5", playlist.Content);
    }

    private static void RebasedCompletionMarker()
    {
        foreach (var content in new[]
        {
            Header + "#EXT-X-ENDLIST\n" + Segments,
            Header + Segments.Replace("3.ts\n", "3.ts\n#EXT-X-ENDLIST\n", StringComparison.Ordinal)
        })
        {
            var rebased = HlsReplayTimeline.Rebase(content, PlaylistUri, TimeSpan.FromSeconds(19))!.Value;
            Assert.Equal(TimeSpan.FromSeconds(10), rebased.Offset);
            Assert.True(rebased.Playlist.TrimEnd().EndsWith("#EXT-X-ENDLIST", StringComparison.Ordinal));
            Assert.Equal(1, rebased.Playlist.Split('\n').Count(line => line.TrimEnd('\r') == "#EXT-X-ENDLIST"));
        }
    }

    private static void RebasedGlobalTags()
    {
        var content = Header.Replace("#EXT-X-TARGETDURATION:5\n", "", StringComparison.Ordinal) + Segments +
            "#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:5\n#EXT-X-INDEPENDENT-SEGMENTS\n#EXT-X-PLAYLIST-TYPE:EVENT\n#EXT-X-ENDLIST\n";
        var rebased = HlsReplayTimeline.Rebase(content, PlaylistUri, TimeSpan.FromSeconds(19))!.Value;
        var firstSegment = rebased.Playlist.IndexOf("#EXTINF:", StringComparison.Ordinal);
        foreach (var tag in new[] { "#EXT-X-VERSION:3", "#EXT-X-TARGETDURATION:5", "#EXT-X-INDEPENDENT-SEGMENTS" })
        {
            var tagIndex = rebased.Playlist.IndexOf(tag, StringComparison.Ordinal);
            Assert.True(tagIndex >= 0 && tagIndex < firstSegment);
            Assert.Equal(tagIndex, rebased.Playlist.LastIndexOf(tag, StringComparison.Ordinal));
        }
        Assert.Equal(1, rebased.Playlist.Split('\n').Count(line => line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal)));
        Assert.Contains("#EXT-X-PLAYLIST-TYPE:VOD", rebased.Playlist);
    }

    private static async Task RangesBeforeDurationsAsync(PlatformKind platform)
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(platform);
        var content = "#EXTM3U\n#EXT-X-VERSION:4\n#EXT-X-TARGETDURATION:3\n" +
            "#EXT-X-BYTERANGE:4@0\n#EXTINF:3,\nsegment-a.ts\n" +
            "#EXT-X-BYTERANGE:4\n#EXTINF:3,\nsegment-a.ts\n#EXT-X-ENDLIST\n";
        fixture.Handler.Put("/redirected/index.m3u8", Encoding.UTF8.GetBytes(content));
        fixture.Handler.Put("/redirected/segment-a.ts", Enumerable.Range(0, 8).Select(value => (byte)value).ToArray());
        var item = await fixture.DownloadAsync();
        Assert.Equal(8L, item.BytesDownloaded);
        Assert.Equal(2, item.CompletedSegments);
        Assert.True(fixture.Handler.Ranges.Contains("bytes=0-3"));
        Assert.True(fixture.Handler.Ranges.Contains("bytes=4-7"));
        var directory = Path.GetDirectoryName(item.LocalMediaPath)!;
        Assert.SequenceEqual(new byte[] { 0, 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(directory, "asset-000000.ts")));
        Assert.SequenceEqual(new byte[] { 4, 5, 6, 7 }, await File.ReadAllBytesAsync(Path.Combine(directory, "asset-000001.ts")));
        Assert.DoesNotContain("BYTERANGE", await File.ReadAllTextAsync(item.LocalMediaPath));
        Assert.True((await fixture.Service.GetOfflineTargetAsync(item.Id)).IsOfflineVod);
    }

    private static void DuplicatePreviewDuration() => Assert.True(LiveSeekPlaylist.Parse(
        Header + Segments.Replace("#EXTINF:5,\n2.ts", "#EXTINF:5,\n#EXTINF:10,\n2.ts", StringComparison.Ordinal),
        PlaylistUri, PlatformKind.Twitch, null) is null);

    private static void IncompletePreviewSegment() => Assert.True(LiveSeekPlaylist.Parse(
        Header + Segments + "#EXTINF:5,\n", PlaylistUri, PlatformKind.Twitch, null) is null);

    private static void InvalidDurationNotation()
    {
        foreach (var duration in new[] { "5e0", "+5", " 5", "5 ", "NaN", "Infinity", "-5", "5\0" })
        {
            var content = Header + Segments.Replace("#EXTINF:5,", $"#EXTINF:{duration},", StringComparison.Ordinal);
            Assert.True(LiveSeekPlaylist.Parse(content, PlaylistUri, PlatformKind.Twitch, null) is null);
            Assert.True(HlsReplayTimeline.ParsePublishedDuration(content) is null);
            Assert.Throws<InvalidDataException>(() => HlsReplayTimeline.Rebase(content + "#EXT-X-ENDLIST\n",
                PlaylistUri, TimeSpan.FromSeconds(19)));
            Assert.Throws<InvalidDataException>(() => OfflineHlsPlaylist.Parse(content + "#EXT-X-ENDLIST\n", PlaylistUri));
            Assert.Throws<InvalidDataException>(() => LivePreviewPlaylist.Rewrite(content, PlaylistUri, PlatformKind.Twitch, out _));
        }
    }

    private static void FractionalDurations()
    {
        var content = Header + Segments.Replace("#EXTINF:5,", "#EXTINF:5.0000001, title, with commas", StringComparison.Ordinal);
        var expected = TimeSpan.FromSeconds(20) + TimeSpan.FromTicks(4);
        Assert.Equal(expected, HlsReplayTimeline.ParsePublishedDuration(content));
        Assert.Equal(expected, OfflineHlsPlaylist.Parse(content + "#EXT-X-ENDLIST\n", PlaylistUri).Duration);
        var previews = LiveSeekPlaylist.Parse(content, PlaylistUri, PlatformKind.Twitch, null)!;
        Assert.Equal(new Uri(PlaylistUri, "3.ts"), previews.GetSegment(5.0000001)!.Uri);
        Assert.Contains("#EXTINF:5.0000001, title, with commas",
            LivePreviewPlaylist.Rewrite(content, PlaylistUri, PlatformKind.Twitch, out _));
    }

    private static void InvalidOfflineCompletion()
    {
        foreach (var content in new[]
        {
            Header + Segments + "#EXT-X-ENDLIST\n#EXTINF:5,\n",
            Header + Segments + "#EXT-X-ENDLIST\n#EXT-X-ENDLIST\n",
            Header + Segments + "#EXT-X-ENDLIST:unexpected\n",
            Header + "#EXT-X-ENDLIST\n#EXT-X-BYTERANGE:4@0\n" + Segments + "#EXT-X-BYTERANGE:4\n"
        }) Assert.Throws<InvalidDataException>(() => OfflineHlsPlaylist.Parse(content, PlaylistUri));
    }
}

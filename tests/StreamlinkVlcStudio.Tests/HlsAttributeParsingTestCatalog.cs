internal static class HlsAttributeParsingTestCatalog
{
    private static readonly Uri PlaylistUri = new("https://vod-secure.twitch.tv/archive/index.m3u8");

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("HLS attributes: quoted key URLs cannot disable seek-preview encryption", QuotedKeyEncryptionAsync),
        ("HLS attributes: malformed encryption declarations do not produce seek previews", InvalidPreviewKeysAsync),
        ("HLS attributes: offline keys reject malformed or unquoted values", () => InvalidOfflineAttributesAsync(map: false)),
        ("HLS attributes: offline maps reject malformed or unquoted values", () => InvalidOfflineAttributesAsync(map: true)),
        ("HLS attributes: quoted commas preserve quality selection and offline ranges", ValidAttributeListsAsync),
        ("HLS attributes: malformed unquoted master values require the fallback", InvalidMasterAttributesAsync)
    ];

    private static Task QuotedKeyEncryptionAsync()
    {
        var playlist = LiveSeekPlaylist.Parse(
            "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key,METHOD=NONE,value.key\"\n" +
            "#EXTINF:6,\nencrypted.ts\n#EXT-X-KEY:METHOD=NONE\n#EXTINF:6,\nclear.ts\n",
            PlaylistUri, PlatformKind.Twitch, null);

        Assert.NotNull(playlist);
        Assert.True(playlist!.GetSegment(0) is null, "Encrypted segments cannot be decoded as plain preview video.");
        Assert.Equal("/archive/clear.ts", playlist.GetSegment(6)!.Uri.AbsolutePath);
        return Task.CompletedTask;
    }

    private static Task InvalidPreviewKeysAsync()
    {
        foreach (var attributes in new[]
        {
            "METHOD=AES-128,METHOD=NONE", "METHOD=NONE,URI=\"key.bin\"", "METHOD=NONE,",
            "URI=\"key,METHOD=NONE,value.key\"", "METHOD=NONE,IV=0x1", "METHOD=NONE,X-INVALID=value\"oops"
        })
        {
            Assert.True(LiveSeekPlaylist.Parse(MediaPlaylist("#EXT-X-KEY:" + attributes),
                PlaylistUri, PlatformKind.Twitch, null) is null, "Accepted invalid key attributes: " + attributes);
        }
        return Task.CompletedTask;
    }

    private static Task InvalidOfflineAttributesAsync(bool map)
    {
        foreach (var value in new[] { "init.bin", "init\"broken.bin", "\"init.bin\"suffix", "\"unterminated.bin" })
        {
            var tag = map ? "#EXT-X-MAP:URI=" + value : "#EXT-X-KEY:METHOD=AES-128,URI=" + value;
            Assert.Throws<InvalidDataException>(() => OfflineHlsPlaylist.Parse(MediaPlaylist(tag), PlaylistUri));
        }
        return Task.CompletedTask;
    }

    private static Task ValidAttributeListsAsync()
    {
        var selected = TwitchVodVariantPlaylist.Select(
            "#EXTM3U\n#EXT-X-MEDIA:TYPE=VIDEO,GROUP-ID=\"video\",NAME=\"720p60\"\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=1000000,VIDEO=\"video\",CODECS=\"avc1.4d002a,mp4a.40.2\"\n" +
            "720p.m3u8?token=one,two\n", PlaylistUri, "720p60");
        Assert.Equal("/archive/720p.m3u8", selected.AbsolutePath);
        Assert.Equal("?token=one,two", selected.Query);

        var offline = OfflineHlsPlaylist.Parse(MediaPlaylist(
            "#EXT-X-KEY:METHOD=AES-128,URI=\"key,METHOD=NONE,value.key\",IV=0x1\n" +
            "#EXT-X-MAP:BYTERANGE=\"16@32\",URI=\"init,part.mp4\""), PlaylistUri);
        Assert.Equal(3, offline.Assets.Count);
        Assert.True(offline.Assets[2].IsEncrypted);
        Assert.Equal<long?>(32, offline.Assets[1].Offset);
        Assert.Equal<long?>(16, offline.Assets[1].Length);
        Assert.Contains("key,METHOD=NONE,value.key", offline.Assets[0].Uri.AbsoluteUri);
        Assert.Contains("#EXT-X-MAP:URI=\"asset-000001.mp4\"", offline.Content);
        return Task.CompletedTask;
    }

    private static Task InvalidMasterAttributesAsync()
    {
        foreach (var attributes in new[]
        {
            "BANDWIDTH=1\t2,IVS-NAME=\"720p\"", "BANDWIDTH=1000,IVS-NAME=\"720p\",",
            "BANDWIDTH=1000,IVS-NAME=\"720p\",BANDWIDTH=2000"
        })
        {
            Assert.Throws<InvalidDataException>(() => TwitchVodVariantPlaylist.Select(
                "#EXTM3U\n#EXT-X-STREAM-INF:" + attributes + "\n720p.m3u8\n", PlaylistUri, "best"));
        }
        return Task.CompletedTask;
    }

    private static string MediaPlaylist(string tag) =>
        "#EXTM3U\n#EXT-X-TARGETDURATION:6\n" + tag + "\n#EXTINF:6,\nsegment.ts\n#EXT-X-ENDLIST\n";
}

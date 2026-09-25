using System.Net.Http;

internal static class PlaylistRedirectTestCatalog
{
    private const string OriginalUrl = "https://d2e2de1etea730.cloudfront.net/abc_def/chunked/index-dvr.m3u8";
    private const string RedirectedBase = "https://d111111abcdef8.cloudfront.net/relocated/chunked/";
    private const string Playlist = "#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\"\n" +
        "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n#EXTINF:10,\n0-unmuted.ts\n#EXTINF:10,\n1.ts\n";

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("playlist redirects: sub-only VOD media and attributes use the final location", SubOnlyPlaylistUsesFinalLocationAsync),
        ("playlist redirects: muted repair uses the initial response location", () => MutedRepairUsesFinalLocationAsync(refresh: false)),
        ("playlist redirects: muted repair refresh follows a changed response location", () => MutedRepairUsesFinalLocationAsync(refresh: true))
    ];

    private static async Task SubOnlyPlaylistUsesFinalLocationAsync()
    {
        const string metadata = """
            {"data":{"video":{"broadcastType":"ARCHIVE","createdAt":"2023-01-01T00:00:00Z",
            "seekPreviewsURL":"https://d2e2de1etea730.cloudfront.net/abc_def/storyboards/0.jpg",
            "owner":{"login":"streamer"}}}}
            """;
        using var upstream = new HttpClient(new FakeHttpMessageHandler(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://gql.twitch.tv/gql" => Text(metadata),
                OriginalUrl => Redirect("../moved.m3u8"),
                "https://d2e2de1etea730.cloudfront.net/abc_def/moved.m3u8" => Redirect(RedirectedBase + "index.m3u8"),
                RedirectedBase + "index.m3u8" => Text(Playlist),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            }));
        var directory = Path.Combine(Path.GetTempPath(), $"svs-playlist-redirect-{Guid.NewGuid():N}");
        try
        {
            var resolver = new TwitchSubOnlyVodResolver(
                new MemoryLogger(), upstream, directory, TestReplayUrlSecurity.PublicValidator);
            var resolution = await resolver.ResolveAsync(new TwitchSubOnlyVodRequest("123456", "best"));
            var content = await File.ReadAllTextAsync(resolution.PlaybackUri.LocalPath);

            Assert.Contains(RedirectedBase + "0-muted.ts", content);
            Assert.Contains(RedirectedBase + "1.ts", content);
            Assert.Contains($"URI=\"{RedirectedBase}init.mp4\"", content);
            Assert.Contains($"URI=\"{RedirectedBase}key.bin\"", content);
            Assert.DoesNotContain("abc_def/chunked/", content);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task MutedRepairUsesFinalLocationAsync(bool refresh)
    {
        var redirect = !refresh;
        var requests = new ConcurrentQueue<string>();
        var segment = new byte[TwitchMutedSegmentSanitizer.PacketSize];
        using var upstream = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            requests.Enqueue(url);
            return url switch
            {
                OriginalUrl when redirect => Redirect(RedirectedBase + "index.m3u8"),
                OriginalUrl or RedirectedBase + "index.m3u8" =>
                    Text("#EXTM3U\n#EXTINF:10,\n0-muted.ts\n#EXTINF:10,\n1.ts\n"),
                RedirectedBase + "0-muted.ts" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(segment)
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        await using var gateway = new TwitchMutedVodPlaybackGateway(
            new MemoryLogger(), upstream, TestReplayUrlSecurity.PublicValidator);
        using var source = await gateway.PrepareAsync(new Uri(OriginalUrl), new Version(3, 0, 12), CancellationToken.None);
        using var player = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        Assert.True(source.PlaybackUri.IsLoopback);
        var content = await player.GetStringAsync(source.PlaybackUri);
        if (refresh)
        {
            Assert.Contains("https://d2e2de1etea730.cloudfront.net/abc_def/chunked/1.ts", content);
            redirect = true;
            content = await player.GetStringAsync(source.PlaybackUri);
        }

        Assert.Contains(RedirectedBase + "1.ts", content);
        var repairedSegmentUrl = content.Split('\n').First(line => line.StartsWith("http://", StringComparison.Ordinal));
        Assert.SequenceEqual(segment, await player.GetByteArrayAsync(repairedSegmentUrl));
        Assert.True(requests.Contains(RedirectedBase + "0-muted.ts"));
    }

    private static HttpResponseMessage Text(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Redirect)
    {
        Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) }
    };
}

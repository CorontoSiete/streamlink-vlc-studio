using Microsoft.AspNetCore.Http;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    // Kick's live IVS playlist captured on 2026-11-04 used these tags. Replace
    // expiring CDN signatures with deterministic paths; retain the tag structure.
    private const string KickPrefetchPlaylist = """
        #EXTM3U
        #EXT-X-VERSION:3
        #EXT-X-TARGETDURATION:6
        #EXT-X-MEDIA-SEQUENCE:4361
        #EXT-X-NET-LIVE-VIDEO-LIVE-SEQUENCE:4361
        #EXT-X-NET-LIVE-VIDEO-ELAPSED-SECS:8720.700
        #EXT-X-NET-LIVE-VIDEO-TOTAL-SECS:8752.700
        #EXT-X-DATERANGE:ID="playlist-creation-1793839216",CLASS="timestamp",START-DATE="2026-11-05T00:40:16.349Z",END-ON-NEXT=YES,X-SERVER-TIME="1793839216.35"
        #EXT-X-DATERANGE:ID="source-1793839187",CLASS="live-video-net-stream-source",START-DATE="2026-11-05T00:39:47.597Z",END-ON-NEXT=YES,X-NET-LIVE-VIDEO-STREAM-SOURCE="live"
        #EXT-X-PROGRAM-DATE-TIME:2026-11-05T00:39:47.597Z
        #EXTINF:2.000,live
        https://cdn.example/index0.ts?dna=private
        #EXT-X-PREFETCH:https://cdn.example/index1.ts?dna=private
        #EXT-X-PREFETCH:index2.ts?dna=private
        """;

    private static async Task KickPrefetchAsync()
    {
        var origin = new Uri("https://cdn.example/index.m3u8");
        var requested = new List<Uri>();
        using var http = new HttpClient(new AsyncHttpMessageHandler((request, _) =>
        {
            requested.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("segment-bytes")
            });
        }));
        using var media = new LocalWebMedia("kick", origin, http);
        var rewritten = media.RewritePlaylist(KickPrefetchPlaylist, origin);
        Assert.Equal(false, rewritten.Contains("://", StringComparison.Ordinal));
        Assert.Equal(false, rewritten.Contains("private", StringComparison.Ordinal));
        Assert.Equal(2, rewritten.Split("#EXT-X-PREFETCH:/media/kick/").Length - 1);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:4361", rewritten);
        Assert.Contains("X-NET-LIVE-VIDEO-STREAM-SOURCE=\"live\"", rewritten);
        var reference = rewritten.Split('\n').First(line => line.StartsWith("#EXT-X-PREFETCH:", StringComparison.Ordinal))[16..];
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        await media.ServeAsync(context, reference.Split('/')[^1]);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("video/mp2t", context.Response.ContentType);
        Assert.Equal("https://cdn.example/index1.ts?dna=private", requested.Single().AbsoluteUri);
        context.Response.Body.Position = 0;
        Assert.Equal("segment-bytes", await new StreamReader(context.Response.Body).ReadToEndAsync());

        // A prefetched segment keeps its opaque identity when it becomes complete.
        var refreshed = media.RewritePlaylist("#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:4362\n#EXTINF:2.000,live\nhttps://cdn.example/index1.ts?dna=private\n#EXT-X-PREFETCH-DISCONTINUITY\n#EXT-X-PREFETCH:index2.ts?dna=private\n", origin);
        Assert.Contains("\n" + reference + "\n", refreshed);
        Assert.Contains("#EXT-X-PREFETCH-DISCONTINUITY\n", refreshed);
        Assert.Contains("#EXT-X-PREFETCH:/media/kick/", refreshed);
    }

    private static Task PrefetchSecurityAsync()
    {
        using var media = new LocalWebMedia("kick", new Uri("https://cdn.example/index.m3u8"));
        foreach (var prefix in new[] { "#EXT-X-PREFETCH:", "#EXT-X-TWITCH-PREFETCH:" })
        {
            foreach (var value in new[] { "", "http://cdn.example/a.ts", "https://127.0.0.1/a.ts", "https://192.168.1.1/a.ts", "https://[::1]/a.ts", "https://cdn.example:8443/a.ts", "https://user:pass@cdn.example/a.ts", "file:///C:/private.ts", "data:text/plain,secret", "{$segment}", "a\t.ts" })
            {
                var rejected = false;
                try { media.RewritePlaylist("#EXTM3U\n" + prefix + value + "\n", new Uri("https://cdn.example/index.m3u8")); }
                catch (InvalidDataException) { rejected = true; }
                Assert.True(rejected, "Unsafe prefetch must not bypass ordinary media validation: " + value);
            }
        }
        return Task.CompletedTask;
    }

    private static Task BrowserKickPrefetchAsync() => BrowserAsync(false, prefetch: true);

    private sealed partial class BrowserWorkspace
    {
        internal bool Prefetch { get; init; }

        private static LocalWebMedia CreatePrefetchMedia(string id)
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-event");
            var http = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
            {
                var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
                HttpContent content;
                if (name == "index.m3u8")
                {
                    var playlist = await File.ReadAllTextAsync(Path.Combine(directory, name), token);
                    playlist = System.Text.RegularExpressions.Regex.Replace(playlist, @"(?m)^(index\d+\.ts)\r?$", "https://cdn.example/$1");
                    // An ordinary HLS client ignores these hints and plays complete segments.
                    playlist += "\n#EXT-X-PREFETCH:https://cdn.example/index6.ts\n#EXT-X-PREFETCH:https://cdn.example/index7.ts\n";
                    content = new StringContent(playlist);
                }
                else content = new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(directory, name), token));
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
            }));
            return new LocalWebMedia(id, new Uri("https://cdn.example/index.m3u8"), http);
        }
    }
}

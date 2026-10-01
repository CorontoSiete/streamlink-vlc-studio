using System.Net.Http.Headers;
using StreamlinkVlcStudio.Infrastructure.Http;

internal static partial class VodDownloadTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> LiveProbeTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VOD_PROBE_URL")) ? [] :
        [("VOD downloads: opt-in public VOD source and segment compatibility probe", PublicVodProbeAsync)];

    private static async Task PublicVodProbeAsync()
    {
        var target = VodDownloadUrlParser.Parse(Environment.GetEnvironmentVariable("SVS_TEST_VOD_PROBE_URL")!);
        var path = Environment.GetEnvironmentVariable("SVS_TEST_VOD_PROBE_STREAMLINK");
        Assert.True(File.Exists(path), "Set SVS_TEST_VOD_PROBE_STREAMLINK to the installed Streamlink executable.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var logger = new MemoryLogger();
        var source = target.IsExplicitKickVod
            ? await new KickVodDownloadResolver(logger).ResolveAsync(target, cancellation.Token) : target;
        var resolved = await new StreamlinkService(logger).ResolveStreamUrlAsync(new StreamTransportRequest(source,
            "worst", path!, false, ["--no-config", "--webbrowser", "no"]), cancellation.Token);
        using var client = HttpClientFactory.Create(TimeSpan.FromSeconds(20), includeUserAgent: true, allowAutoRedirect: false);
        var playlist = await ValidatedReplayHttpClient.ReadPlaylistAsync(client, ReplayUrlSecurityValidator.Shared,
            resolved.StreamUri, target.Platform, cancellation.Token);
        var parsed = OfflineHlsPlaylist.Parse(playlist.Content, playlist.Uri);
        Assert.True(parsed.SegmentCount > 0);
        Assert.True(parsed.Duration > TimeSpan.Zero);
        var asset = parsed.Assets.First(asset => asset.IsSegment);
        using var response = await ValidatedReplayHttpClient.SendGetAsync(client, ReplayUrlSecurityValidator.Shared,
            asset.Uri, target.Platform, address =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, address);
                var offset = asset.Offset ?? 0;
                request.Headers.Range = new RangeHeaderValue(offset, offset + Math.Min(asset.Length ?? 8192, 8192) - 1);
                return request;
            }, cancellation.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
        var bytes = new byte[188 * 4];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellation.Token);
            if (read == 0) break;
            count += read;
        }
        Assert.True(count > 0);
        if (!asset.IsEncrypted && asset.FileName.EndsWith(".ts", StringComparison.Ordinal))
        {
            Assert.Equal(bytes.Length, count);
            foreach (var offset in new[] { 0, 188, 376, 564 }) Assert.Equal((byte)0x47, bytes[offset]);
        }
        Console.WriteLine($"Public {target.Platform} VOD {target.MediaId}: {parsed.SegmentCount} complete-playlist segments, duration {parsed.Duration}, first media bytes {count}, CDN {asset.Uri.Host}.");
    }
}

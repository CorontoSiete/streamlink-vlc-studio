using System.Net;
using System.Text;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Core.Twitch;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Processes;
using StreamlinkVlcStudio.Infrastructure.Text;
using StreamlinkVlcStudio.Infrastructure.Viewers;
using StreamlinkVlcStudio.Infrastructure.Vod;
using StreamlinkVlcStudio.Maintenance;

internal static class ReviewFollowupTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("review followup: canceled processes are never started", CanceledProcessesDoNotStartAsync),
        ("review followup: invalid process timeout is rejected before startup", InvalidProcessTimeoutAsync),
        ("review followup: buffered text lines honor cancellation", BufferedTextCancellationAsync),
        ("review followup: buffered IRC lines honor cancellation", BufferedIrcCancellationAsync),
        ("review followup: failed live snapshots are retried immediately", FailedSnapshotsAreNotCachedAsync),
        ("review followup: canceled live snapshots do not start requests", CanceledSnapshotsAsync),
        ("review followup: canceled Kick token resolution has no side effects", CanceledKickTokensAsync),
        ("review followup: canceled Twitch token resolution has no side effects", CanceledTwitchTokensAsync),
        ("review followup: local HTTP rejects malformed names and targets", LocalHttpSyntaxAsync),
        ("review followup: playlist URI rewriting respects quoted metadata", PlaylistQuotedMetadata),
        ("review followup: playlist URI rewriting rejects duplicate attributes", PlaylistDuplicateUris),
        ("review followup: maintenance containment accepts drive and share roots", MaintenanceRootContainment),
        ("review followup: maintenance rejects padded device file names", MaintenanceDeviceNames),
        ("review followup: VOD chat rejects overflowing ranges before network access", VodChatRangeOverflowAsync),
        ("review followup: Twitch chat frontier arithmetic does not overflow", TwitchFrontierOverflow)
    ];

    private static async Task CanceledProcessesDoNotStartAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new BoundedProcessRunner().RunAsync(
            BoundedProcessRunner.CreateRedirectedStartInfo(MissingExecutable(), []),
            TimeSpan.FromSeconds(1), cancellation.Token));
    }

    private static async Task InvalidProcessTimeoutAsync()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new BoundedProcessRunner().RunAsync(
            BoundedProcessRunner.CreateRedirectedStartInfo(MissingExecutable(), []),
            TimeSpan.MaxValue));
    }

    private static string MissingExecutable() =>
        Path.Combine(Path.GetTempPath(), $"StreamStudio-nonexistent-{Guid.NewGuid():N}.exe");

    private static async Task BufferedTextCancellationAsync()
    {
        using var stream = new MemoryStream("first\nsecond\n"u8.ToArray());
        using var reader = new BoundedStreamLineReader(stream, Encoding.UTF8, 20);
        Assert.Equal("first", (await reader.ReadLineAsync())?.Text);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => reader.ReadLineAsync(cancellation.Token));
        Assert.Equal("second", (await reader.ReadLineAsync())?.Text);
        Assert.Equal<BoundedTextLine?>(null, await reader.ReadLineAsync());
    }

    private static async Task BufferedIrcCancellationAsync()
    {
        using var stream = new MemoryStream("first\r\nsecond\r\n"u8.ToArray());
        using var reader = new BoundedUtf8LineReader(stream, 20);
        Assert.Equal("first", await reader.ReadLineAsync());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => reader.ReadLineAsync(cancellation.Token));
        Assert.Equal("second", await reader.ReadLineAsync());
        Assert.Equal<string?>(null, await reader.ReadLineAsync());
    }

    private static async Task FailedSnapshotsAreNotCachedAsync()
    {
        var requests = 0;
        using var client = new HttpClient(new FakeHttpMessageHandler(_ =>
            JsonResponse(++requests == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)));
        var provider = new LiveChannelSnapshotProvider(client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await provider.GetTwitchAsync("channel", "token", "client", default)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await provider.GetTwitchAsync("channel", "token", "client", default)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await provider.GetTwitchAsync("channel", "token", "client", default)).StatusCode);
        Assert.Equal(2, requests);
    }

    private static async Task CanceledSnapshotsAsync()
    {
        var requests = 0;
        using var client = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            requests++;
            return JsonResponse();
        }));
        var provider = new LiveChannelSnapshotProvider(client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.GetKickAsync("channel", "token", cancellation.Token));
        Assert.Equal(0, requests);
        await provider.GetKickAsync("channel", "token", default);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.GetKickAsync("channel", "token", cancellation.Token));
        Assert.Equal(1, requests);
    }

    private static async Task CanceledKickTokensAsync()
    {
        var requests = 0;
        var provider = new KickTokenProvider((_, _, _) =>
        {
            requests++;
            return Task.FromResult<string?>("access-token");
        });
        var settings = new ChatSettings { KickClientId = "client" };
        var logger = new MemoryLogger();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.ResolveAsync(settings, logger, cancellation.Token));
        Assert.Equal(0, requests);
        Assert.Equal("access-token", await provider.ResolveAsync(settings, logger));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.ResolveAsync(settings, logger, cancellation.Token));
        Assert.Equal(1, requests);
    }

    private static async Task CanceledTwitchTokensAsync()
    {
        var requests = 0;
        using var client = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            requests++;
            return JsonResponse(body: "{\"client_id\":\"client\",\"login\":\"user\",\"user_id\":\"1\",\"expires_in\":3600}");
        }));
        var token = Guid.NewGuid().ToString("N");
        var logger = new MemoryLogger();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task<string?> Resolve(CancellationToken ct) =>
            TwitchClientIdCache.GetOrResolveAsync(client, token, logger, "test", "failed", ct);
        await Assert.ThrowsAsync<OperationCanceledException>(() => Resolve(cancellation.Token));
        Assert.Equal(0, requests);
        Assert.Equal("client", await Resolve(default));
        await Assert.ThrowsAsync<OperationCanceledException>(() => Resolve(cancellation.Token));
        Assert.Equal(1, requests);
    }

    private static async Task LocalHttpSyntaxAsync()
    {
        string[] malformed =
        [
            "GET /capture HTTP/1.1\r\nHost : localhost\r\n\r\n",
            "GET /capture HTTP/1.1\r\n Host: localhost\r\n\r\n",
            "GET /cap\tture HTTP/1.1\r\nHost: localhost\r\n\r\n",
            "GET /capture#fragment HTTP/1.1\r\nHost: localhost\r\n\r\n",
            "GET /capture HTTP/1.1\r\nX-Test: value\u007f\r\n\r\n",
            "G\u00c9T /capture HTTP/1.1\r\nHost: localhost\r\n\r\n"
        ];
        foreach (var request in malformed)
        {
            using var stream = new MemoryStream(Encoding.Latin1.GetBytes(request));
            var result = await LocalHttpRequestReader.ReadWithStatusAsync(stream, 4096, default);
            Assert.Equal(400, result.StatusCode);
            Assert.Equal(false, result.IsSuccess);
        }

        using var valid = new MemoryStream("POST /capture?source=test HTTP/1.1\r\nHost: localhost\r\nContent-Length: 2\r\n\r\n{}"u8.ToArray());
        var parsed = await LocalHttpRequestReader.ReadWithStatusAsync(valid, 4096, default);
        Assert.True(parsed.IsSuccess);
        Assert.Equal("/capture", parsed.Request!.Path);
        Assert.SequenceEqual("{}"u8.ToArray(), parsed.Request.Body);
    }

    private static Task PlaylistQuotedMetadata()
    {
        var uri = new Uri("https://vod-secure.twitch.tv/path/index.m3u8");
        const string metadata = "#EXT-X-DATERANGE:ID=\"chapter,URI=title\",START-DATE=\"2026-09-24T00:00:00Z\"";
        var playlist = $"#EXTM3U\n{metadata}\n#EXT-X-MAP:URI=\"init.mp4\",BYTERANGE=\"100@0\"\n#EXTINF:2,\n0.ts\n";
        var rewritten = TwitchSubOnlyVodPlaylist.RewriteMediaPlaylist(playlist, uri);
        Assert.Contains(metadata, rewritten);
        Assert.Contains("URI=\"https://vod-secure.twitch.tv/path/init.mp4\",BYTERANGE=\"100@0\"", rewritten);
        Assert.Contains("https://vod-secure.twitch.tv/path/0.ts", rewritten);
        return Task.CompletedTask;
    }

    private static Task PlaylistDuplicateUris()
    {
        var uri = new Uri("https://vod-secure.twitch.tv/path/index.m3u8");
        const string playlist = "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",URI=\"https://unapproved.example/key\"\n#EXTINF:2,\n0-muted.ts\n";
        Assert.Throws<InvalidDataException>(() => TwitchSubOnlyVodPlaylist.RewriteMediaPlaylist(playlist, uri));
        Assert.Throws<InvalidDataException>(() => TwitchMutedVodPlaylist.RewriteForRepair(playlist, uri, _ => "http://127.0.0.1/segment"));
        return Task.CompletedTask;
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status = HttpStatusCode.OK, string body = "{\"data\":[]}") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Task MaintenanceRootContainment()
    {
        Assert.True(PathSafety.IsSameOrUnder(@"C:\folder\file", @"C:\"));
        Assert.True(PathSafety.IsSameOrUnder(@"\\server\share\folder", @"\\server\share\"));
        Assert.True(PathSafety.IsSameOrUnder(@"C:\folder\file", @"C:\folder\"));
        Assert.True(PathSafety.IsSameOrUnder(@"C:\folder", @"C:\folder"));
        Assert.Equal(false, PathSafety.IsSameOrUnder(@"C:\folder-other\file", @"C:\folder"));
        Assert.Equal(false, PathSafety.IsSameOrUnder(@"D:\folder\file", @"C:\"));
        Assert.Equal(false, PathSafety.IsSameOrUnder(@"C:\folder\..\outside", @"C:\folder"));
        return Task.CompletedTask;
    }

    private static Task MaintenanceDeviceNames()
    {
        foreach (var name in new[] { "NUL .txt", "COM1 .log", "LPT\u00b9 .dll", "conin$ .txt", "AUX  .bin" })
        {
            Assert.Equal(false, PathSafety.IsSafeManifestRelativePath($"folder/{name}"));
        }

        Assert.True(PathSafety.IsSafeManifestRelativePath("folder/Console .txt"));
        Assert.True(PathSafety.IsSafeManifestRelativePath("folder/COM10.txt"));
        return Task.CompletedTask;
    }

    private static async Task VodChatRangeOverflowAsync()
    {
        var requests = 0;
        using var client = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            requests++;
            return JsonResponse(body: "{\"data\":{\"video\":{\"comments\":{\"edges\":[],\"pageInfo\":{\"hasNextPage\":false}}}}}");
        }));
        var provider = new VodChatProvider(client);
        foreach (var platform in new[] { PlatformKind.Kick, PlatformKind.Twitch })
        {
            var replay = new ReplaySessionInfo(platform, "channel", "https://example.test/replay", "123",
                DateTimeOffset.UnixEpoch, TimeSpan.FromHours(1), true, "");
            var result = await provider.FetchAsync(replay, new AppSettings(), TimeSpan.MaxValue);
            Assert.Equal(VodChatFetchOutcome.Unsupported, result.Outcome);
            Assert.Equal(0, requests);
        }
    }

    private static Task TwitchFrontierOverflow()
    {
        Assert.Equal(TimeSpan.MaxValue,
            TwitchVodChatFetcher.ResolveCoveredThroughOffset(TimeSpan.MaxValue, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(12),
            TwitchVodChatFetcher.ResolveCoveredThroughOffset(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12.9)));
        return Task.CompletedTask;
    }
}

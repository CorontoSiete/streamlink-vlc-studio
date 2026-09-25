using System.Net;
using System.Text;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Core.Twitch;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Viewers;

internal static class ReviewContinuationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("review continuation: malformed followed responses cannot establish offline status", MalformedFollowedResponsesAsync),
        ("review continuation: malformed followed rows preserve valid streams without declaring success", MalformedFollowedRowsAsync),
        ("review continuation: malformed later followed pages retain earlier results", MalformedLaterFollowedPageAsync),
        ("review continuation: malformed followed pagination cannot establish a complete result", MalformedFollowedPaginationAsync),
        ("review continuation: Kick followed results exclude unrequested channels", UnrequestedKickChannelsAsync),
        ("review continuation: followed streams deduplicate overlapping pages", OverlappingFollowedPagesAsync),
        ("review continuation: valid empty and offline followed responses remain successful", OfflineFollowedResponsesAsync),
        ("review continuation: VOD filename rewriting preserves keys queries and metadata", PlaylistRewritingPreservesNonSegmentText)
    ];

    private static readonly PlatformKind[] Platforms = [PlatformKind.Twitch, PlatformKind.Kick];

    private static async Task MalformedFollowedResponsesAsync()
    {
        foreach (var platform in Platforms)
        {
            foreach (var payload in new[] { "null", "[]", "{}", "{\"data\":null}", "{\"data\":{}}" })
            {
                var result = await ReadFollowedAsync(platform, payload);
                Assert.Equal(0, result.Streams.Count);
                Assert.Equal(false, result.SucceededPlatforms!.Contains(platform));
                Assert.True(result.Messages.Any(message => message.StartsWith($"{platform}:", StringComparison.Ordinal)));
            }
        }
    }

    private static async Task MalformedFollowedRowsAsync()
    {
        foreach (var platform in Platforms)
        {
            var invalidRows = platform == PlatformKind.Twitch
                ? new[] { "null", "{}", "{\"user_login\":123}" }
                : new[] { "null", "{}", "{\"slug\":123}", "{\"slug\":\"channel\"}", "{\"slug\":\"channel\",\"stream\":17}" };
            foreach (var invalidRow in invalidRows)
            {
                var result = await ReadFollowedAsync(platform, $$"""{"data":[{{LiveRow(platform)}},{{invalidRow}}]}""");
                Assert.Equal(1, result.Streams.Count);
                Assert.Equal("channel", result.Streams[0].Channel);
                Assert.Equal(ProfileImage, result.Streams[0].ProfileImageUrl);
                Assert.Equal(false, result.SucceededPlatforms!.Contains(platform));
            }
        }
    }

    private static async Task MalformedLaterFollowedPageAsync()
    {
        var result = await ReadFollowedAsync(PlatformKind.Twitch,
            $$$"""{"data":[{{{LiveRow(PlatformKind.Twitch)}}}],"pagination":{"cursor":"next"}}""",
            "{\"data\":null}");
        Assert.Equal(1, result.Streams.Count);
        Assert.Equal(ProfileImage, result.Streams[0].ProfileImageUrl);
        Assert.Equal(false, result.SucceededPlatforms!.Contains(PlatformKind.Twitch));
    }

    private static async Task UnrequestedKickChannelsAsync()
    {
        var result = await ReadFollowedAsync(PlatformKind.Kick,
            $$"""{"data":[{{LiveRow(PlatformKind.Kick, "other")}},{{LiveRow(PlatformKind.Kick)}}]}""");
        Assert.SequenceEqual(["channel"], result.Streams.Select(stream => stream.Channel));
        Assert.True(result.SucceededPlatforms!.Contains(PlatformKind.Kick));
    }

    private static async Task MalformedFollowedPaginationAsync()
    {
        foreach (var pagination in new[] { "null", "[]", "\"bad\"", "{\"cursor\":null}", "{\"cursor\":17}", "{\"cursor\":false}" })
        {
            var result = await ReadFollowedAsync(PlatformKind.Twitch,
                $$"""{"data":[{{LiveRow(PlatformKind.Twitch)}}],"pagination":{{pagination}}}""");
            Assert.Equal(1, result.Streams.Count);
            Assert.Equal(ProfileImage, result.Streams[0].ProfileImageUrl);
            Assert.Equal(false, result.SucceededPlatforms!.Contains(PlatformKind.Twitch));
        }
    }

    private static async Task OverlappingFollowedPagesAsync()
    {
        var result = await ReadFollowedAsync(PlatformKind.Twitch,
            $$$"""{"data":[{{{LiveRow(PlatformKind.Twitch)}}}],"pagination":{"cursor":"next"}}""",
            $$$"""{"data":[{{{LiveRow(PlatformKind.Twitch, "CHANNEL")}}}],"pagination":{}}""");
        Assert.Equal(1, result.Streams.Count);
        Assert.True(result.SucceededPlatforms!.Contains(PlatformKind.Twitch));
    }

    private static async Task OfflineFollowedResponsesAsync()
    {
        foreach (var platform in Platforms)
        {
            var result = await ReadFollowedAsync(platform, "{\"data\":[]}");
            Assert.Equal(0, result.Streams.Count);
            Assert.True(result.SucceededPlatforms!.Contains(platform));
        }

        foreach (var stream in new[] { "null", "{\"is_live\":false}" })
        {
            var result = await ReadFollowedAsync(PlatformKind.Kick,
                $$"""{"data":[{"slug":"channel","stream":{{stream}}}]}""");
            Assert.Equal(0, result.Streams.Count);
            Assert.True(result.SucceededPlatforms!.Contains(PlatformKind.Kick));
        }
    }

    private static Task PlaylistRewritingPreservesNonSegmentText()
    {
        const string origin = "https://d111111abcdef8.cloudfront.net/folder-unmuted/";
        var playlist = "#EXTM3U\n" +
            "#EXT-X-KEY:METHOD=AES-128,URI=\"key-unmuted.bin?token=key-unmuted\"\n" +
            "#EXT-X-MAP:URI=\"init-unmuted.mp4\"\n" +
            "#EXTINF:10.0,Title-unmuted\n" +
            "0-unmuted.ts?token=value-unmuted%2Fkeep\n" +
            "#EXTINF:10.0,\n" +
            "folder-unmuted/1.ts?token=another-unmuted\n" +
            "#EXTINF:10.0,\n" +
            "2-unmuted.mp4\n";
        var rewritten = TwitchSubOnlyVodPlaylist.RewriteMediaPlaylist(playlist, new Uri(origin + "index.m3u8"));
        Assert.Contains($"URI=\"{origin}key-unmuted.bin?token=key-unmuted\"", rewritten);
        Assert.Contains($"URI=\"{origin}init-unmuted.mp4\"", rewritten);
        Assert.Contains("#EXTINF:10.0,Title-unmuted\n", rewritten);
        Assert.Contains($"{origin}0-muted.ts?token=value-unmuted%2Fkeep\n", rewritten);
        Assert.Contains($"{origin}folder-unmuted/1.ts?token=another-unmuted\n", rewritten);
        Assert.Contains($"{origin}2-muted.mp4\n", rewritten);
        return Task.CompletedTask;
    }

    private const string ProfileImage = "https://example.invalid/profile.jpg";

    private static string LiveRow(PlatformKind platform, string channel = "channel") => platform == PlatformKind.Twitch
        ? $$"""{"user_login":"{{channel}}","viewer_count":42,"title":"Title"}"""
        : $$"""{"slug":"{{channel}}","broadcaster_user_id":17,"stream":{"is_live":true,"viewer_count":42},"stream_title":"Title"}""";

    private static async Task<FollowedLiveStreamsResult> ReadFollowedAsync(PlatformKind platform, params string[] pages)
    {
        var pageIndex = 0;
        using var client = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!;
            var body = uri.Host == "id.twitch.tv"
                ? """{"client_id":"client","login":"viewer","user_id":"1","expires_in":3600,"scopes":["user:read:follows"]}"""
                : uri.AbsolutePath == "/helix/users"
                    ? $$"""{"data":[{"login":"channel","profile_image_url":"{{ProfileImage}}"}]}"""
                : uri.AbsolutePath == "/public/v1/users"
                    ? $$"""{"data":[{"user_id":17,"profile_picture":"{{ProfileImage}}"}]}"""
                : pages[pageIndex++];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }));
        var settings = new AppSettings();
        if (platform == PlatformKind.Twitch) settings.Chat.TwitchOAuthToken = "token";
        else settings.FollowedChannels.KickChannelSlugs = ["channel"];
        var tokens = new KickTokenProvider((_, _, _) => Task.FromResult<string?>("token"));
        var service = new FollowedStreamsService(new MemoryLogger(), client, tokens);
        return await service.GetLiveFollowedStreamsAsync(settings);
    }
}

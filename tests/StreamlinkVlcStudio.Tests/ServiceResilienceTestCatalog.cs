using System.Net;
using System.Text.Json;
using StreamlinkVlcStudio.App.Wpf.Chat;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Limits;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Replay;
using StreamlinkVlcStudio.Infrastructure.Viewers;

internal static class ServiceResilienceTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("service resilience: malformed optional VOD labels preserve videos", MalformedVodLabelsAsync),
        ("service resilience: optional VOD label read failures preserve videos", VodLabelReadFailuresAsync),
        ("service resilience: optional VOD label body limits preserve videos", VodLabelBodyLimitsAsync),
        ("service resilience: canceling optional VOD labels still cancels the search", CanceledVodLabelsAsync),
        ("service resilience: Twitch VOD lookup selects the requested channel", TwitchVodChannelIdentityAsync),
        ("service resilience: Twitch VOD lookup rejects unidentified channels", UnidentifiedTwitchVodChannelAsync),
        ("service resilience: Kick replay selects an identified matching channel", KickReplayChannelIdentity),
        ("service resilience: Kick website replay rejects unidentified channels", KickWebsiteReplayChannelIdentity),
        ("service resilience: Kick chat requires a matching channel and positive broadcaster ID", KickChatChannelIdentity),
        ("service resilience: native overlay tolerates concurrent stop requests", ConcurrentOverlayStopAsync)
    ];

    private const string Broadcaster = """{"id":"1","login":"streamer","display_name":"Streamer"}""";
    private const string LiveStream = """{"id":"123","is_live":true,"started_at":"2026-09-25T12:00:00Z"}""";

    private static async Task MalformedVodLabelsAsync()
    {
        foreach (var body in new[] { "null", "[]", "17", "\"unavailable\"", "{\"data\":null}" })
        {
            var result = await ReadVodsAsync(() => new StringContent(body));
            AssertUnclassifiedVideo(result);
        }
    }

    private static async Task VodLabelReadFailuresAsync()
    {
        foreach (var failure in new Exception[] { new IOException("Disconnected"), new OperationCanceledException("Body deadline") })
        {
            var result = await ReadVodsAsync(() => new StreamContent(new FailingReadStream(failure)));
            AssertUnclassifiedVideo(result);
        }
    }

    private static async Task VodLabelBodyLimitsAsync()
    {
        Func<HttpContent>[] contents =
        [
            () =>
            {
                var content = new StringContent("{}");
                content.Headers.ContentLength = PayloadLimits.HttpJsonBytes + 1;
                return content;
            },
            () =>
            {
                var content = new StringContent("{}");
                content.Headers.ContentType!.CharSet = "unsupported-studio-charset";
                return content;
            },
            () => new ByteArrayContent([0xff, 0xfe, 0xfd])
        ];
        foreach (var content in contents)
        {
            AssertUnclassifiedVideo(await ReadVodsAsync(content));
        }
    }

    private static async Task CanceledVodLabelsAsync()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ReadVodsAsync(() =>
        {
            cancellation.Cancel();
            return new StringContent("{}");
        }, cancellationToken: cancellation.Token));
    }

    private static async Task TwitchVodChannelIdentityAsync()
    {
        var result = await ReadVodsAsync(() => new StringContent("{}"),
            """{"data":[{"id":"other","login":"someone_else"},{"id":"numeric","login":17},""" + Broadcaster + "]}");
        Assert.Equal("streamer", result.Broadcaster!.Login);
        AssertUnclassifiedVideo(result);
    }

    private static async Task UnidentifiedTwitchVodChannelAsync()
    {
        foreach (var login in new[] { "null", "17", "true", "\"someone_else\"" })
        {
            var result = await ReadVodsAsync(() => new StringContent("{}"),
                $$"""{"data":[{"id":"other","login":{{login}}}]}""");
            Assert.Equal(TwitchVodSearchStatus.NotFound, result.Status);
            Assert.Equal(0, result.Videos.Count);
        }
    }

    private static Task KickReplayChannelIdentity()
    {
        foreach (var slug in new[] { "", "\"slug\":null,", "\"slug\":17,", "\"slug\":\"other\"," })
        {
            var unidentified = $$$"""{ {{{slug}}} "stream":{"id":"wrong","is_live":true}}""";
            using var missing = JsonDocument.Parse($$"""{"data":[{{unidentified}}]}""");
            Assert.Equal<KickLiveStreamInfo?>(null, ReplayResolver.ReadKickLiveStream(missing.RootElement, "streamer"));
            using var mixed = JsonDocument.Parse(
                $$"""{"data":[{{unidentified}}, {"slug":"STREAMER","stream":{{LiveStream}}}]}""");
            Assert.Equal("123", ReplayResolver.ReadKickLiveStream(mixed.RootElement, "streamer")!.StreamId);
        }
        return Task.CompletedTask;
    }

    private static Task KickWebsiteReplayChannelIdentity()
    {
        foreach (var slug in new[] { "", "\"slug\":null,", "\"slug\":17,", "\"slug\":\"other\"," })
        {
            using var document = JsonDocument.Parse($$"""{ {{slug}} "livestream":{{LiveStream}} }""");
            Assert.Equal<KickLiveStreamInfo?>(null, ReplayResolver.ReadKickWebsiteLiveStream(document.RootElement, "streamer"));
        }
        using var valid = JsonDocument.Parse($$"""{"slug":"STREAMER","livestream":{{LiveStream}}}""");
        Assert.Equal("123", ReplayResolver.ReadKickWebsiteLiveStream(valid.RootElement, "streamer")!.StreamId);
        return Task.CompletedTask;
    }

    private static void AssertUnclassifiedVideo(TwitchVodSearchResult result)
    {
        Assert.Equal(TwitchVodSearchStatus.Available, result.Status);
        Assert.Equal("123", result.Videos.Single().Id);
        Assert.Equal(TwitchVodAccessKind.Unknown, result.Videos[0].AccessKind);
        Assert.Equal("next-page", result.NextCursor);
        Assert.Contains("Access could not be checked for 1 VOD", result.Message);
    }

    private static Task KickChatChannelIdentity()
    {
        foreach (var row in new[]
        {
            """{"broadcaster_user_id":99}""",
            """{"slug":17,"broadcaster_user_id":99}""",
            """{"slug":"other","broadcaster_user_id":99}""",
            """{"slug":"streamer","broadcaster_user_id":0}""",
            """{"slug":"streamer","broadcaster_user_id":-1}""",
            """{"slug":"streamer","broadcaster_user_id":null}"""
        })
        {
            using var missing = JsonDocument.Parse($$"""{"data":[{{row}}]}""");
            Assert.Equal<long?>(null, KickOAuthService.ReadBroadcasterUserId(missing.RootElement, "streamer"));
            using var mixed = JsonDocument.Parse(
                $$$"""{"data":[{{{row}}}, {"slug":"STREAMER","broadcaster_user_id":"123"}]}""");
            Assert.Equal<long?>(123, KickOAuthService.ReadBroadcasterUserId(mixed.RootElement, "streamer"));
        }
        return Task.CompletedTask;
    }

    private static async Task ConcurrentOverlayStopAsync()
    {
        await using var host = new NativeOverlayReplayEventHost(new MemoryLogger(),
            action => action(), () => { }, () => 720);
        var name = $"svs-review-{Guid.NewGuid():N}";
        for (var index = 0; index < 200; index++)
        {
            host.Start(name, Path.Combine(Path.GetTempPath(), name + ".json"));
            await Task.WhenAll(Task.Run(host.Stop), Task.Run(host.StopAsync), Task.Run(host.StopAsync));
            Assert.Equal(false, host.IsRunning);
        }
    }

    private static async Task<TwitchVodSearchResult> ReadVodsAsync(
        Func<HttpContent> accessContent, string? users = null, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "gql.twitch.tv")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = accessContent() };
            }
            var body = request.RequestUri.AbsolutePath switch
            {
                "/oauth2/validate" => """{"client_id":"client","login":"viewer","user_id":"2","expires_in":3600}""",
                "/helix/users" => users ?? $$"""{"data":[{{Broadcaster}}]}""",
                "/helix/videos" => """{"data":[{"id":"123","user_id":"1","user_login":"streamer","url":"https://www.twitch.tv/videos/123","duration":"1h"}],"pagination":{"cursor":"next-page"}}""",
                _ => throw new InvalidOperationException($"Unexpected request {request.RequestUri}")
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "client";
        settings.Chat.TwitchOAuthToken = Guid.NewGuid().ToString("N");
        return await new TwitchVodService(new MemoryLogger(), client)
            .SearchAsync(new TwitchVodSearchRequest("streamer"), settings, cancellationToken);
    }

    private sealed class FailingReadStream(Exception failure) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(failure);
    }
}

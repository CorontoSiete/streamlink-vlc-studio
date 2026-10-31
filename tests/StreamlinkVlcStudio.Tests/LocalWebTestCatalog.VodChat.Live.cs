using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> LiveVodChatTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VOD_CHAT_URL")) ? [] :
        [("local web: opt-in public VOD plays with synchronized archived chat", BrowserPublicVodChatAsync)];

    private static async Task CheckPublicBrowserVodChat(CoreWebView2 core, BrowserWorkspace workspace, string password)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var original = VodDownloadUrlParser.Parse(Environment.GetEnvironmentVariable("SVS_TEST_VOD_CHAT_URL")!);
        var logger = new MemoryLogger();
        var target = original.Platform == PlatformKind.Kick
            ? await new KickVodDownloadResolver(logger).ResolveAsync(original, cancellation.Token)
            : original with
            {
                Url = (await new StreamlinkService(logger).ResolveStreamUrlAsync(new StreamTransportRequest(original,
                "worst", Environment.GetEnvironmentVariable("SVS_TEST_VOD_CHAT_STREAMLINK") ?? @"C:\Program Files\Streamlink\bin\streamlink.exe",
                false, ["--no-config", "--webbrowser", "no"]), cancellation.Token)).StreamUri.AbsoluteUri
            };
        workspace.PublicVodTarget = target;
        var replay = new ReplaySessionInfo(target.Platform, target.Channel, target.Url, target.MediaId, target.MediaStartedAtUtc,
            target.MediaDuration, true, "", ChatRoomId: target.ChatRoomId);
        var provider = new VodChatProvider(logger);
        var offsets = new System.Collections.Concurrent.ConcurrentDictionary<string, double>();
        var coverage = new System.Collections.Concurrent.ConcurrentDictionary<double, double>();
        workspace.VodChatRead = async (input, token) =>
        {
            var from = input.GetProperty("from").GetDouble();
            var result = await provider.FetchAsync(replay, new AppSettings(), TimeSpan.FromSeconds(from), token);
            Assert.True(result.Outcome is VodChatFetchOutcome.Loaded or VodChatFetchOutcome.Completed, result.Reason);
            foreach (var entry in result.Messages) offsets[entry.Message.MessageId!] = entry.Offset.TotalSeconds;
            coverage[from] = result.CoveredThroughOffset.TotalSeconds;
            Console.WriteLine($"Public {target.Platform} VOD {target.MediaId}: request {from:0.###}s, {result.Messages.Count} messages, coverage {result.CoveredThroughOffset.TotalSeconds:0.###}s, {result.Outcome}.");
            return new
            {
                messages = result.Messages.Select(entry => new { offset = entry.Offset.TotalSeconds, message = entry.Message }).ToArray(),
                through = result.CoveredThroughOffset.TotalSeconds,
                outcome = result.Outcome.ToString(),
                status = "Replay chat"
            };
        };
        await core.ExecuteScriptAsync($"document.querySelector('#password').value={JsonSerializer.Serialize(password)};document.querySelector('#login-form').requestSubmit()");
        await WaitBrowser(core, "!document.querySelector('#studio').hidden && document.querySelectorAll('.card').length===1");
        await core.ExecuteScriptAsync("document.dispatchEvent(new KeyboardEvent('keydown',{key:'k',ctrlKey:true,bubbles:true}))");
        await core.ExecuteScriptAsync($"document.querySelector('#open-query').value={JsonSerializer.Serialize(original.Url)};document.querySelector('#open-form').requestSubmit()");
        await WaitBrowser(core, "document.querySelector('#players video')?.currentTime>=600 && document.querySelector('#players video').readyState>=2");
        await core.ExecuteScriptAsync("window.vodVideo=document.querySelector('#players video');vodVideo.pause()");
        foreach (var position in new[] { 600, 1800, 300, 360 })
        {
            await core.ExecuteScriptAsync($"vodVideo.currentTime={position}");
            await TestWait.UntilAsync(() => coverage.Any(range => range.Key >= position - 30 && range.Key <= position && range.Value >= position), TimeSpan.FromSeconds(15));
            var expectedCount = Math.Min(100, offsets.Count(entry => entry.Value >= position - 30 && entry.Value <= position));
            await WaitBrowser(core, $"!vodVideo.seeking && vodVideo.readyState>=2 && document.querySelectorAll('.chat-message').length==={expectedCount}");
            var now = JsonSerializer.Deserialize<double>(await core.ExecuteScriptAsync("vodVideo.currentTime"));
            var ids = JsonSerializer.Deserialize<string[]>(await core.ExecuteScriptAsync("[...document.querySelectorAll('.chat-message')].map(row=>row.chatMessage.messageId)"))!;
            Assert.True(ids.All(id => offsets[id] <= now && offsets[id] >= position - 30), "Rendered chat must belong to the selected playback time.");
            Console.WriteLine($"Public {target.Platform} browser decoded {await core.ExecuteScriptAsync("vodVideo.videoWidth")}px video at {now:0.###}s with {ids.Length} synchronized archived messages.");
        }
        await CaptureBrowser(core, target.Platform.ToString().ToLowerInvariant() + "-vod-chat-live");
    }

    private static async Task KickVodChatMetadataAsync()
    {
        const string id = "e22f14c4-1803-43c9-98f7-748f68546796";
        // Relevant fields from Kick's public UUID video response observed 2026-10-22.
        var body = $$$$"""{"uuid":"{{{{id}}}}","source":"https://stream.kick.com/recording/master.m3u8","livestream":{"channel_id":668,"start_time":"2026-10-21T19:56:17+00:00","duration":42908000,"session_title":"Recorded broadcast","channel":{"id":668,"slug":"xqc"}}}""";
        using var client = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal($"https://kick.com/api/v1/video/{id}", request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var resolver = new KickVodDownloadResolver(new MemoryLogger(), client, (_, _, _) => Task.FromResult<string?>(null));
        var target = VodDownloadUrlParser.Parse($"https://kick.com/xqc/videos/{id}");
        var resolved = await resolver.ResolveAsync(target, CancellationToken.None);
        Assert.Equal(DateTimeOffset.Parse("2026-10-21T19:56:17Z"), resolved.MediaStartedAtUtc);
        Assert.Equal(TimeSpan.FromMilliseconds(42908000), resolved.MediaDuration);
        Assert.Equal("668", resolved.ChatRoomId);
        foreach (var invalid in new[]
        {
            body.Replace(id, "another-video", StringComparison.Ordinal),
            body.Replace("\"slug\":\"xqc\"", "\"slug\":\"other\"", StringComparison.Ordinal),
            body.Replace("\"channel_id\":668", "\"channel_id\":111", StringComparison.Ordinal),
            body.Replace("https://stream.kick.com/", "http://stream.kick.com/", StringComparison.Ordinal),
            body.Replace("https://stream.kick.com/", "https://stream.kick.com.evil.invalid/", StringComparison.Ordinal)
        })
        {
            body = invalid;
            await Assert.ThrowsAsync<InvalidDataException>(() => resolver.ResolveAsync(target, CancellationToken.None));
        }
    }
}

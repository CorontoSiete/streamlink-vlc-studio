using Microsoft.Web.WebView2.Core;

internal static partial class LocalWebTestCatalog
{
    private static async Task CheckBrowserVodChat(CoreWebView2 core, BrowserWorkspace workspace, string password)
    {
        await core.ExecuteScriptAsync($"document.querySelector('#password').value={JsonSerializer.Serialize(password)};document.querySelector('#login-form').requestSubmit()");
        await WaitBrowser(core, "!document.querySelector('#studio').hidden && document.querySelectorAll('.card').length===1");
        foreach (var platform in new[] { "Twitch", "Kick" })
        {
            var requests = new List<double>();
            workspace.PlayerPlatform = platform;
            workspace.VodChatRead = (input, _) =>
            {
                var from = input.GetProperty("from").GetDouble();
                requests.Add(from);
                // Busy Twitch pages can advance by only one second. Kick chunks and Twitch
                // pages both overlap their boundary; the browser must deduplicate that overlap.
                var entries = new[] { from, from + 1 }.Select(offset => new
                {
                    offset,
                    message = new ChatMessage(Enum.Parse<PlatformKind>(platform), "streamer", platform + " viewer",
                        "Archive " + offset, DateTimeOffset.UnixEpoch.AddSeconds(offset), MessageId: "archive-" + offset)
                }).ToArray();
                return Task.FromResult<object>(new
                {
                    messages = entries,
                    through = from + 1,
                    outcome = from >= 39 ? "Completed" : "Loaded",
                    status = "Replay chat"
                });
            };
            await core.ExecuteScriptAsync("document.dispatchEvent(new KeyboardEvent('keydown',{key:'k',ctrlKey:true,bubbles:true}))");
            var url = platform == "Twitch" ? "https://twitch.tv/videos/12345" : "https://kick.com/streamer/videos/00000000-0000-0000-0000-000000000001";
            await core.ExecuteScriptAsync($"document.querySelector('#open-query').value={JsonSerializer.Serialize(url)};document.querySelector('#open-form').requestSubmit()");
            await WaitBrowser(core, "document.querySelector('#players video')?.readyState>=2");
            await core.ExecuteScriptAsync("window.vodVideo=document.querySelector('#players video');vodVideo.pause();vodVideo.currentTime=10");
            await WaitBrowser(core, "!vodVideo.seeking && document.querySelector('.chat-messages').textContent.includes('Archive 10')");
            Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('.chat-form').hidden && document.querySelector('.chat-live-indicator').textContent==='REPLAY'"));
            Assert.Equal("11", await core.ExecuteScriptAsync("document.querySelectorAll('.chat-message').length"));
            Assert.Equal("false", await core.ExecuteScriptAsync("document.querySelector('.chat-messages').textContent.includes('Archive 11')"));
            await Task.Delay(500);
            Assert.Equal("11", await core.ExecuteScriptAsync("document.querySelectorAll('.chat-message').length"));
            Assert.True(requests.Count >= 11, "The browser must fill the chat look-ahead without waiting two seconds per page.");

            await core.ExecuteScriptAsync("vodVideo.currentTime=3");
            await WaitBrowser(core, "!vodVideo.seeking && document.querySelectorAll('.chat-message').length===4");
            Assert.Equal("false", await core.ExecuteScriptAsync("document.querySelector('.chat-messages').textContent.includes('Archive 10')"));
            await core.ExecuteScriptAsync("vodVideo.playbackRate=2;vodVideo.play()");
            await WaitBrowser(core, "vodVideo.currentTime>=5 && document.querySelector('.chat-messages').textContent.includes('Archive 5')");

            object Entry(double offset, string text, string id) => new
            {
                offset,
                message = new ChatMessage(Enum.Parse<PlatformKind>(platform), "streamer", platform + " viewer",
                    text, DateTimeOffset.UnixEpoch.AddSeconds(offset), MessageId: id)
            };
            var heldPage = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            workspace.VodChatRead = (_, _) =>
            {
                if (++calls == 1) return Task.FromResult<object>(new
                {
                    messages = Enumerable.Range(1, 8)
                    .Select(offset => Entry(offset, "Buffered " + offset, "buffered-" + offset)).ToArray(),
                    through = 8,
                    outcome = "Loaded",
                    status = "Replay chat"
                });
                pageStarted.TrySetResult();
                // Deliberately ignore cancellation: a late provider response must not pollute a seek.
                return heldPage.Task;
            };
            try
            {
                await core.ExecuteScriptAsync("vodVideo.pause();vodVideo.currentTime=2");
                await pageStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("false", await core.ExecuteScriptAsync("document.querySelector('.chat-messages').textContent.includes('Buffered 5')"));
                await core.ExecuteScriptAsync("vodVideo.play()");
                await WaitBrowser(core, "vodVideo.currentTime>=5 && document.querySelector('.chat-messages').textContent.includes('Buffered 5')");
                Assert.Equal(false, heldPage.Task.IsCompleted);
                workspace.VodChatRead = (_, _) => Task.FromResult<object>(new
                {
                    messages = new[] { Entry(20, "New seek", "new-seek") },
                    through = 60,
                    outcome = "Completed",
                    status = "Replay chat"
                });
                await core.ExecuteScriptAsync("vodVideo.pause();vodVideo.currentTime=20");
                await Task.Delay(200);
                heldPage.TrySetResult(new { messages = new[] { Entry(1, "Stale response", "stale") }, through = 60, outcome = "Completed", status = "Replay chat" });
                await WaitBrowser(core, "!vodVideo.seeking && document.querySelector('.chat-messages').textContent.includes('New seek')");
                Assert.Equal("1", await core.ExecuteScriptAsync("document.querySelectorAll('.chat-message').length"));
                Assert.Equal("false", await core.ExecuteScriptAsync("document.querySelector('.chat-messages').textContent.includes('Stale response')"));
            }
            finally { heldPage.TrySetResult(new { messages = Array.Empty<object>(), through = 60, outcome = "Completed", status = "Replay chat" }); }

            var retryOffsets = new List<double>();
            workspace.VodChatRead = (input, _) =>
            {
                retryOffsets.Add(input.GetProperty("from").GetDouble());
                object page = retryOffsets.Count switch
                {
                    1 => new { messages = Array.Empty<object>(), through = 1, outcome = "Loaded", status = "Replay chat" },
                    2 => new { messages = new[] { Entry(3, "Partial page", "partial") }, through = 0, outcome = "Failed", status = "Temporary failure. Retrying…" },
                    _ => new { messages = new[] { Entry(3, "Partial page", "partial"), Entry(4, "Future message", "future") }, through = 60, outcome = "Completed", status = "Replay chat" }
                };
                return Task.FromResult(page);
            };
            await core.ExecuteScriptAsync("vodVideo.currentTime=3");
            await WaitBrowser(core, "document.querySelector('.chat-messages').textContent.includes('Partial page') && document.querySelector('.chat-status').textContent.includes('Retrying')");
            await WaitBrowser(core, "document.querySelector('.chat-status').textContent==='Replay chat'");
            Assert.SequenceEqual(new[] { 0d, 1d, 1d }, retryOffsets);
            Assert.Equal("1", await core.ExecuteScriptAsync("document.querySelectorAll('.chat-message').length"));
            Assert.Equal("false", await core.ExecuteScriptAsync("document.querySelector('.chat-messages').textContent.includes('Future message')"));
            await Task.Delay(1200);
            Assert.Equal(3, retryOffsets.Count); // Completed archives stop fetching, even while paused.
            await core.ExecuteScriptAsync("document.querySelector('[data-action=close]').click()");
            await WaitBrowser(core, "document.querySelectorAll('#players video').length===0");
        }
        Assert.Equal("[]", await core.ExecuteScriptAsync("window.testErrors"));
        Assert.Equal("false", await core.ExecuteScriptAsync("document.querySelector('#notice').textContent.includes('desktop is busy')"));
    }
}

using Microsoft.Web.WebView2.Core;

internal static partial class LocalWebTestCatalog
{
    private static async Task CheckBrowserFollowingLastLive(CoreWebView2 core, CoreWebView2Controller controller, BrowserWorkspace workspace, string password)
    {
        await core.ExecuteScriptAsync($"window.lastLiveNow={workspace.LastLiveNow.ToUnixTimeMilliseconds()};Date.now=()=>window.lastLiveNow;");
        await core.ExecuteScriptAsync($"document.querySelector('#password').value={JsonSerializer.Serialize(password)};document.querySelector('#login-form').requestSubmit();");
        const string settled = "document.querySelector('#cards').getAttribute('aria-busy')==='false'";
        await WaitBrowser(core, settled + " && document.querySelectorAll('#cards .card').length===10");
        await workspace.HistoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, workspace.HistoryCalls);
        // The list is usable while the provider is still held, and cached history is immediate.
        Assert.Equal("Last live 3 days ago", JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.querySelector('#cards [data-key=twitch] .last-live').textContent")));
        Assert.Equal("Last live unavailable", JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.querySelector('#cards [data-key=kick] .last-live').textContent")));
        await core.ExecuteScriptAsync("window.historyCard=document.querySelector('#cards [data-key=twitch]');window.historyButton=historyCard.querySelector('.card-main');historyButton.focus();");
        workspace.HoldHistory.TrySetResult();
        await WaitBrowser(core, "document.querySelector('#cards [data-key=twitch] .last-live').textContent==='Last live 1 day ago'");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('#cards [data-key=twitch]')===historyCard && document.activeElement===historyButton"));
        Assert.Equal("true", await core.ExecuteScriptAsync("[...document.querySelectorAll('#followed-channels .last-live')].some(e=>e.dataset.channelKey==='twitch:creator' && e.textContent==='Last live 1 day ago')"));
        foreach (var (key, expected) in new[]
        {
            ("kick", "Last live 2 hours ago"), ("unknown", "Last live unavailable"),
            ("invalid", "Last live unavailable"), ("future", "Last live unavailable"),
            ("sentinel", "Last live unavailable"), ("recent", "Last live just now"),
            ("month", "Last live 1 month ago"), ("year", "Last live 1 year ago")
        })
            Assert.Equal(expected, JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync($"document.querySelector('#cards [data-key={key}] .last-live').textContent")));
        Assert.Equal("0", await core.ExecuteScriptAsync("document.querySelectorAll('#cards [data-key=live] .last-live').length"));
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('#cards [data-key=twitch] .last-live').title==='Latest available broadcast started '+new Date(window.lastLiveNow-86400000).toLocaleString(undefined,{dateStyle:'full',timeStyle:'short'})"));
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('#cards [data-key=kick] .last-live').title.includes(new Date(window.lastLiveNow-7200000).toLocaleDateString(undefined,{weekday:'long',year:'numeric',month:'long',day:'numeric'}))"));
        Assert.Equal("0", await core.ExecuteScriptAsync("document.querySelectorAll('.last-live img,.last-live script').length"));

        // Time labels advance without rebuilding focused cards or requesting history again.
        await core.ExecuteScriptAsync("window.lastLiveNow+=120000;document.dispatchEvent(new Event('visibilitychange'));");
        Assert.Equal("Last live 2 minutes ago", JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.querySelector('#cards [data-key=recent] .last-live').textContent")));
        Assert.Equal("true", await core.ExecuteScriptAsync("document.activeElement===historyButton"));
        Assert.Equal(1, workspace.HistoryCalls);
        await core.ExecuteScriptAsync("document.querySelector('[data-filter=kick]').click()");
        Assert.Equal("Last live 2 hours ago", JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.querySelector('#cards .last-live').textContent")));
        await core.ExecuteScriptAsync("document.querySelector('[data-filter=all]').click()");
        Assert.Equal("Last live 1 day ago", JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.querySelector('#cards [data-key=twitch] .last-live').textContent")));
        await CaptureBrowser(core, "offline-last-live-desktop");
        controller.Bounds = new System.Drawing.Rectangle(0, 0, 360, 900);
        await WaitBrowser(core, "innerWidth===360");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.documentElement.scrollWidth<=document.documentElement.clientWidth"));
        await CaptureBrowser(core, "offline-last-live-mobile");
        controller.Bounds = new System.Drawing.Rectangle(0, 0, 1440, 1000);
        await WaitBrowser(core, "innerWidth===1440");

        // An optional provider failure keeps the status snapshot and cached time usable.
        workspace.FailHistory = true;
        await core.ExecuteScriptAsync("document.querySelector('#refresh').click()");
        await WaitBrowser(core, settled + " && document.querySelectorAll('#cards .card').length===10");
        await WaitBrowser(core, "document.querySelector('#cards [data-key=twitch] .last-live').textContent==='Last live 3 days ago'");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('#notice').hidden"));

        // A normal navigation cancels the outstanding provider request.
        workspace.FailHistory = false;
        workspace.HoldHistory = new(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.HistoryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await core.ExecuteScriptAsync("document.querySelector('#refresh').click()");
        await workspace.HistoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await core.ExecuteScriptAsync("document.querySelector('[data-page=settings]').click()");
        await WaitBrowser(core, "!document.querySelector('#preferences').hidden");
        await workspace.HistoryCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Model an already-completed network response that arrives after navigation. Even if
        // fetch cannot abort it, its old generation must not overwrite sidebar/library data.
        await core.ExecuteScriptAsync("window.fetch=new Proxy(window.fetch,{apply(target,self,args){const history=args[0]==='/api/following/history';if(history)args[1]={...args[1],signal:undefined};const result=Reflect.apply(target,self,args);return history?result.then(async response=>{await response.clone().text();setTimeout(()=>window.historyResponseCompleted=true,0);return response}):result}})");
        workspace.HoldHistory = new(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.HistoryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await core.ExecuteScriptAsync("document.querySelector('[data-page=following]').click()");
        await workspace.HistoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await core.ExecuteScriptAsync("document.querySelector('[data-page=settings]').click()");
        await WaitBrowser(core, "!document.querySelector('#preferences').hidden");
        workspace.HoldHistory.TrySetResult();
        await WaitBrowser(core, "window.historyResponseCompleted===true");
        Assert.Equal("true", await core.ExecuteScriptAsync("[...document.querySelectorAll('#followed-channels .last-live')].some(e=>e.dataset.channelKey==='twitch:creator' && e.textContent==='Last live 3 days ago') && document.querySelectorAll('#cards .last-live').length===0"));
        Assert.Equal("[]", await core.ExecuteScriptAsync("window.testErrors"));
    }

    private sealed partial class BrowserWorkspace
    {
        internal bool LastLive { get; init; }
        internal DateTimeOffset LastLiveNow { get; } = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        internal TaskCompletionSource HoldHistory { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource HistoryStarted { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource HistoryCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int HistoryCalls { get; private set; }
        internal bool FailHistory { get; set; }

        private async Task<object> LastLiveResult(string action, CancellationToken token)
        {
            var cases = new (string Key, string Channel, string Platform, object? Timestamp)[]
            {
                ("twitch", "creator", "Twitch", LastLiveNow.AddDays(-1)),
                ("kick", "creator", "Kick", LastLiveNow.AddHours(-2)),
                ("unknown", "unknown", "Twitch", null),
                ("invalid", "invalid", "Twitch", "<img src=x onerror=alert(1)>"),
                ("future", "future", "Twitch", LastLiveNow.AddDays(1)),
                ("sentinel", "sentinel", "Twitch", DateTimeOffset.MinValue),
                ("recent", "recent", "Twitch", LastLiveNow.AddSeconds(-59)),
                ("month", "month", "Twitch", LastLiveNow.AddDays(-30)),
                ("year", "year", "Twitch", LastLiveNow.AddDays(-365))
            };
            if (action == "following")
                return new
                {
                    items = new[] { new { key = "live", channel = "live", displayName = "Live channel", platform = "Twitch", state = "Live" } },
                    offline = cases.Select(item => new
                    {
                        key = item.Key,
                        channel = item.Channel,
                        displayName = item.Key + " channel",
                        platform = item.Platform,
                        state = "Offline",
                        lastLiveAtUtc = item.Key == "twitch" ? (DateTimeOffset?)LastLiveNow.AddDays(-3) : null
                    }).ToArray()
                };
            HistoryCalls++;
            HistoryStarted.TrySetResult();
            if (FailHistory) throw new IOException("Test-only history failure.");
            try { await HoldHistory.Task.WaitAsync(token); }
            catch (OperationCanceledException) { HistoryCancelled.TrySetResult(); throw; }
            return new { items = cases.Select(item => new { channel = item.Channel, platform = item.Platform, lastLiveAtUtc = item.Timestamp }).ToArray() };
        }
    }
}

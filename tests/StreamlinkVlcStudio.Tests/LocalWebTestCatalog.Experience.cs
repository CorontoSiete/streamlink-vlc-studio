using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static async Task CaptureBrowser(CoreWebView2 core, string name)
    {
        var repository = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(repository, "StreamlinkVlcStudio.sln")))
            repository = Directory.GetParent(repository)?.FullName ?? throw new DirectoryNotFoundException();
        var directory = Path.Combine(repository, ".artifacts", "local-web-design");
        Directory.CreateDirectory(directory);
        await using var file = File.Create(Path.Combine(directory, name + ".png"));
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
        var metrics = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("JSON.stringify({width:innerWidth,contentWidth:document.documentElement.scrollWidth,font:getComputedStyle(document.body).fontFamily,bodySize:getComputedStyle(document.body).fontSize})"));
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), metrics);
    }

    private static async Task CheckBrowserExperience(CoreWebView2 core, CoreWebView2Controller controller, BrowserWorkspace workspace, LocalWebServer server, string password)
    {
        const string settled = "document.querySelector('#cards').getAttribute('aria-busy')==='false'";
        const string noOverflow = "document.documentElement.scrollWidth<=document.documentElement.clientWidth";
        await CaptureBrowser(core, "login-desktop");
        await core.ExecuteScriptAsync("document.querySelector('#password').value='wrong password';document.querySelector('#login-form').requestSubmit()");
        await WaitBrowser(core, "document.querySelector('#login-error').textContent.length>0");
        Assert.Equal("true", await core.ExecuteScriptAsync("!document.querySelector('#login').hidden"));
        await core.ExecuteScriptAsync($"document.querySelector('#password').value={JsonSerializer.Serialize(password)};document.querySelector('#login-form').requestSubmit()");
        await WaitBrowser(core, settled + " && document.querySelectorAll('.card').length===6");
        await WaitBrowser(core, "Array.from(document.querySelectorAll('.card img')).every(i=>i.complete)");
        await WaitBrowser(core, "Array.from(document.querySelectorAll('.provider-logo')).every(i=>i.complete && i.naturalWidth>0)");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('.card[data-key=mountain] .provider-logo')?.alt==='Twitch' && document.querySelector('.card[data-key=music] .provider-logo')?.alt==='Kick' && !document.querySelector('#followed-channels .provider-logo') && [...document.querySelectorAll('.provider-label')].every(label=>label.textContent==='')"));
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('#featured')===null"));
        Assert.Equal("true", await core.ExecuteScriptAsync(noOverflow));
        Assert.Contains("4 live", await core.ExecuteScriptAsync("document.querySelector('#result-count').textContent"));
        Assert.Contains("Northstar", await core.ExecuteScriptAsync("document.querySelector('.card h3').textContent"));
        Assert.Contains("24,580 viewers", await core.ExecuteScriptAsync("document.querySelector('.card .media-meta').textContent"));
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('.card[data-key=mountain] .card-category').textContent==='Category: Outdoors' && document.querySelector('.card[data-key=music] .card-category').textContent==='Category: Music' && document.querySelector('.card[data-key=code] .card-category').textContent==='Category unavailable' && !document.querySelector('.card.is-offline .card-category')"));
        Assert.Equal("6", await core.ExecuteScriptAsync("document.querySelectorAll('.followed-channel').length"));
        Assert.Contains("4 LIVE", await core.ExecuteScriptAsync("document.querySelector('#sidebar-live-count').textContent"));
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('.topbar #search-form')!==null && document.querySelector('[data-nav-page=following]').getAttribute('aria-current')==='page'"));
        Assert.Equal("true", await core.ExecuteScriptAsync("!document.querySelector('#open-stream') && !document.querySelector('#open-input') && !document.querySelector('.search-shortcut')"));
        await CaptureBrowser(core, "following-desktop");

        // Local sorting keeps offline channels behind the live lineup, without provider requests.
        await core.ExecuteScriptAsync("document.querySelector('#channel-sort').value='name';document.querySelector('#channel-sort').dispatchEvent(new Event('change'))");
        Assert.Equal("Afterhours", JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.querySelector('.card h3').textContent")));
        Assert.Equal("true", await core.ExecuteScriptAsync("Array.from(document.querySelectorAll('.card')).slice(-2).every(c=>c.classList.contains('is-offline'))"));
        await core.ExecuteScriptAsync("document.querySelector('#channel-sort').value='viewers';document.querySelector('#channel-sort').dispatchEvent(new Event('change'))");
        Assert.Equal("Northstar", JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.querySelector('.card h3').textContent")));
        await core.ExecuteScriptAsync("document.querySelector('#channel-sort').value='recommended';document.querySelector('#channel-sort').dispatchEvent(new Event('change'));document.querySelector('#refresh').focus()");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyDown\",\"key\":\"/\",\"code\":\"Slash\",\"windowsVirtualKeyCode\":191}");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyUp\",\"key\":\"/\",\"code\":\"Slash\",\"windowsVirtualKeyCode\":191}");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.activeElement.id==='query'"));
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyDown\",\"key\":\"k\",\"code\":\"KeyK\",\"windowsVirtualKeyCode\":75,\"modifiers\":2}");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyUp\",\"key\":\"k\",\"code\":\"KeyK\",\"windowsVirtualKeyCode\":75,\"modifiers\":2}");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('#open-dialog').open && document.activeElement.id==='open-query'"));
        await core.ExecuteScriptAsync("document.querySelector('#close-open').click()");

        await core.ExecuteScriptAsync("document.querySelector('[data-filter=kick]').click()");
        Assert.Equal("2", await core.ExecuteScriptAsync("document.querySelectorAll('.card').length"));
        await core.ExecuteScriptAsync("document.querySelector('#live-filter').click()");
        Assert.Equal("1", await core.ExecuteScriptAsync("document.querySelectorAll('.card').length"));
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('.card').dataset.provider==='kick'"));
        await core.ExecuteScriptAsync("document.querySelector('[data-filter=all]').click();document.querySelector('#live-filter').click()");
        Assert.Equal("6", await core.ExecuteScriptAsync("document.querySelectorAll('.card').length"));

        // A slow earlier request must not overwrite the page selected afterwards.
        workspace.HoldFollowing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await core.ExecuteScriptAsync("document.querySelector('#refresh').click()");
        await WaitBrowser(core, "document.querySelector('#cards').getAttribute('aria-busy')==='true'");
        await core.ExecuteScriptAsync("document.querySelector('[data-page=downloads]').click()");
        workspace.HoldFollowing.SetResult(); workspace.HoldFollowing = null;
        await WaitBrowser(core, settled + " && document.querySelectorAll('.card').length===2 && document.querySelector('#page-title').textContent==='Downloads'");
        Assert.Contains("Ready to watch", await core.ExecuteScriptAsync("document.querySelector('#cards').textContent"));
        workspace.DownloadCompletedSegments = 65;
        await WaitBrowser(core, "document.querySelectorAll('.download-progress progress')[1]?.value===65");
        await CaptureBrowser(core, "downloads-desktop");

        await core.ExecuteScriptAsync("document.querySelector('[data-page=discover]').click()");
        await WaitBrowser(core, settled + " && document.querySelectorAll('.card').length===5");
        Assert.Equal("6", await core.ExecuteScriptAsync("document.querySelectorAll('.followed-channel').length"));
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('[data-nav-page=discover]').getAttribute('aria-current')==='page'"));
        await CaptureBrowser(core, "discover-desktop");
        await core.ExecuteScriptAsync("document.querySelector('.card-main').click()");
        await WaitBrowser(core, settled + " && document.querySelectorAll('.card').length===3");
        Assert.Contains("All categories", await core.ExecuteScriptAsync("document.querySelector('#breadcrumbs').textContent"));
        await core.ExecuteScriptAsync("document.querySelector('#more').click()");
        await WaitBrowser(core, settled + " && document.querySelectorAll('.card').length===4");

        await core.ExecuteScriptAsync("document.querySelector('[data-page=vods]').click()");
        await WaitBrowser(core, "!document.querySelector('#empty-state').hidden");
        Assert.Contains("back catalog", await core.ExecuteScriptAsync("document.querySelector('#empty-state').textContent"));
        await core.ExecuteScriptAsync("document.querySelector('#query').value='northstar';document.querySelector('#search-form').requestSubmit()");
        await WaitBrowser(core, settled + " && document.querySelectorAll('.card').length===2");
        Assert.Equal("true", await core.ExecuteScriptAsync("!!document.querySelector('.resume-bar progress')"));
        await CaptureBrowser(core, "videos-desktop");

        workspace.FailFollowing = true;
        await core.ExecuteScriptAsync("document.querySelector('[data-page=following]').click()");
        await WaitBrowser(core, settled + " && !document.querySelector('#empty-state').hidden");
        Assert.Contains("Couldn", await core.ExecuteScriptAsync("document.querySelector('#empty-state').textContent"));
        workspace.FailFollowing = false;
        await core.ExecuteScriptAsync("document.querySelector('#empty-state button').click()");
        await WaitBrowser(core, settled + " && document.querySelectorAll('.card').length===6");
        await core.ExecuteScriptAsync("window.searchLibraryCards=[...document.querySelectorAll('#cards .card')];document.querySelector('#query').focus();document.querySelector('#query').value='no-match';document.querySelector('#query').dispatchEvent(new Event('input'))");
        await WaitBrowser(core, "!document.querySelector('#search-dropdown').hidden && document.querySelector('#search-feedback').textContent.includes('No channels found')");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('#query').getAttribute('aria-expanded')==='true' && window.searchLibraryCards.every((card,index)=>document.querySelectorAll('#cards .card')[index]===card) && document.querySelectorAll('#cards .card').length===6"));

        await core.ExecuteScriptAsync("document.dispatchEvent(new KeyboardEvent('keydown',{key:'k',ctrlKey:true,bubbles:true}))");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('#open-dialog').open && document.activeElement.id==='open-query'"));
        await core.ExecuteScriptAsync("document.querySelector('#open-query').value='northstar';document.querySelector('#open-form').requestSubmit()");
        await WaitBrowser(core, "!document.querySelector('#open-dialog').open && document.querySelector('video')?.currentTime>0.1");
        Assert.Equal("false", await core.ExecuteScriptAsync("!!document.querySelector('#tabs')"));
        Assert.Equal("1", await core.ExecuteScriptAsync("document.querySelectorAll('.followed-exit').length"));
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('.is-open.is-selected .followed-link').getAttribute('aria-current')==='true'"));
        await WaitBrowser(core, "document.querySelectorAll('.channel-panel').length===7");
        Assert.Contains("public streamer bio", await core.ExecuteScriptAsync("document.querySelector('.channel-description').textContent"));
        Assert.Equal("false", await core.ExecuteScriptAsync("window.injected===true"));
        Assert.Equal("0", await core.ExecuteScriptAsync("document.querySelectorAll('.channel-information script').length"));
        Assert.Contains("6.8M", await core.ExecuteScriptAsync("document.querySelector('.channel-followers').textContent"));
        Assert.Contains("8,325", await core.ExecuteScriptAsync("document.querySelector('.channel-viewers').textContent"));
        Assert.Equal("8.3K", JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.querySelector('.is-open .watching-indicator span').textContent")));
        Assert.Contains("8,325 viewers", await core.ExecuteScriptAsync("document.querySelector('.is-open .followed-link').getAttribute('aria-label')"));
        Assert.Equal("false", await core.ExecuteScriptAsync("document.querySelector('video').controls"));
        await core.ExecuteScriptAsync("document.querySelector('[data-action=play]').click()");
        await WaitBrowser(core, "document.querySelector('video').paused && document.querySelector('[data-action=play]').getAttribute('aria-label')==='Play'");
        await core.ExecuteScriptAsync("document.querySelector('[data-action=play]').click();document.querySelector('.video-volume').value=.37;document.querySelector('.video-volume').dispatchEvent(new Event('input',{bubbles:true}))");
        await WaitBrowser(core, "!document.querySelector('video').paused && Math.abs(document.querySelector('video').volume-.37)<.001");
        Assert.Equal("true", await core.ExecuteScriptAsync("getComputedStyle(document.querySelector('#library')).display==='none'"));
        foreach (var width in new[] { 320, 390, 768, 1440, 1920 })
        {
            controller.Bounds = new System.Drawing.Rectangle(0, 0, width, 1000);
            await WaitBrowser(core, $"window.innerWidth==={width}");
            Assert.Equal("true", await core.ExecuteScriptAsync(noOverflow));
            await CaptureBrowser(core, "channel-watch-" + width);
        }
        controller.Bounds = new System.Drawing.Rectangle(0, 0, 1440, 1000);
        await WaitBrowser(core, "window.innerWidth===1440");
        Assert.Equal("true", await core.ExecuteScriptAsync("!document.querySelector('.player-options').open"));
        await core.ExecuteScriptAsync("document.querySelector('.player-options summary').click()");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('[data-action=reload]').getClientRects().length>0"));
        await CaptureBrowser(core, "player-desktop");
        await core.ExecuteScriptAsync("document.querySelector('.followed-exit').click()");
        await WaitBrowser(core, "document.querySelectorAll('video').length===0");
        Assert.Equal("true", await core.ExecuteScriptAsync("!document.querySelector('.is-open') && !document.querySelector('#studio').classList.contains('watching')"));

        await core.ExecuteScriptAsync("window.settingsPatches=[];window.fetch=new Proxy(window.fetch,{apply(target,self,args){if(args[0]==='/api/settings/save')window.settingsPatches.push(JSON.parse(args[1].body));return Reflect.apply(target,self,args)}})");
        await core.ExecuteScriptAsync("document.querySelector('[data-page=settings]').click();document.querySelector('#chat-size').value=22;document.querySelector('#chat-size').dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('#default-quality').value='720p';document.querySelector('#default-quality').dispatchEvent(new Event('change',{bubbles:true}));document.querySelector('#low-latency').checked=false;document.querySelector('#low-latency').dispatchEvent(new Event('change',{bubbles:true}))");
        await WaitBrowser(core, "document.querySelector('#save-status').textContent==='All changes saved to your account.'");
        Assert.Equal("true", await core.ExecuteScriptAsync("window.settingsPatches.length===1 && Object.keys(window.settingsPatches[0]).sort().join(',')==='lowLatency,quality,size'"));
        var user = server.Users.ListUsers().Single(u => u.Username == TestUsername);
        var savedPreferences = server.Users.PreferencesFor(user.Id);
        Assert.Equal("720p", savedPreferences.Quality); Assert.Equal(false, savedPreferences.LowLatency);
        Assert.Equal(22, savedPreferences.Size);
        Assert.Equal("22px", JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.querySelector('#chat-size-value').textContent")));
        Assert.Equal("null", await core.ExecuteScriptAsync("localStorage.getItem('stream-studio-preferences')"));
        await CaptureBrowser(core, "preferences-desktop");

        // The actual browser viewport is resized; this catches horizontal overflow and hidden actions.
        foreach (var width in new[] { 390, 320, 768, 1024, 1920 })
        {
            controller.Bounds = new System.Drawing.Rectangle(0, 0, width, 844);
            await WaitBrowser(core, $"window.innerWidth==={width}");
            foreach (var page in new[] { "following", "discover", "vods", "recent", "downloads", "settings" })
            {
                await core.ExecuteScriptAsync($"document.querySelector('[data-page={page}]').click()");
                await WaitBrowser(core, settled);
                Assert.Equal("true", await core.ExecuteScriptAsync(noOverflow));
                Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('#search-form').getBoundingClientRect().top>=0"));
                if (width < 651)
                {
                    Assert.Equal("true", await core.ExecuteScriptAsync("Math.abs(document.querySelector('.sidebar').getBoundingClientRect().bottom-innerHeight)<1"));
                    Assert.Equal("true", await core.ExecuteScriptAsync("Array.from(document.querySelectorAll('[data-page]')).every(b=>{const r=b.getBoundingClientRect();return r.width>=44 && r.height>=44 && r.left>=0 && r.right<=innerWidth})"));
                }
                if (width == 390) await CaptureBrowser(core, page + "-mobile");
            }
        }
        controller.Bounds = new System.Drawing.Rectangle(0, 0, 390, 844);
        await WaitBrowser(core, "window.innerWidth===390");
        await core.ExecuteScriptAsync("document.dispatchEvent(new KeyboardEvent('keydown',{key:'k',ctrlKey:true,bubbles:true}))");
        await CaptureBrowser(core, "open-stream-mobile");
        Assert.Equal("true", await core.ExecuteScriptAsync(noOverflow));
        await core.ExecuteScriptAsync("document.querySelector('#close-open').click();document.querySelector('#mobile-logout').click()");
        await WaitBrowser(core, "!document.querySelector('#login').hidden");
        Assert.Equal("true", await core.ExecuteScriptAsync(noOverflow));
        await CaptureBrowser(core, "login-mobile");
        Assert.Equal("[]", await core.ExecuteScriptAsync("window.testErrors"));
    }

    private sealed partial class BrowserWorkspace
    {
        private object? ExperienceResult(string action, JsonElement input)
        {
            object Stream(string id, string channel, string title, string provider, string state = "Live", int viewers = 1200, string categoryName = "") =>
                new { key = id, channel, displayName = channel, title, categoryName, platform = provider, state, viewers = state == "Live" ? (int?)viewers : null, thumbnailUrl = Address + "media/artwork/" + id };
            object Category(string id, string name, int viewers) => new { id, name, viewerCount = viewers, thumbnailUrl = Address + "media/artwork/" + id };
            object[] live = [Stream("mountain", "Northstar", "Taking the scenic route. Come along for the adventure.", "Twitch", viewers: 24580, categoryName: "Outdoors"), Stream("game", "Pixelquest", "A new world, a fresh save. Let’s explore.", "Twitch", viewers: 8312, categoryName: "Adventure games"), Stream("music", "Afterhours", "Late-night sessions · good music, good company", "Kick", viewers: 4260, categoryName: "Music"), Stream("code", "Buildspace", "Making something from nothing. Live creative session.", "Twitch", viewers: 1580)];
            return action switch
            {
                "following" => new { items = live, offline = new[] { Stream("offline1", "Slowmornings", "", "Twitch", "Offline"), Stream("offline2", "Sundayclub", "", "Kick", "Offline") } },
                "categories" => new { items = new[] { Category("game", "Adventure games", 234000), Category("music", "Music", 42000), Category("mountain", "Outdoors", 18000), Category("code", "Creative", 35000), Category("chat", "Just chatting", 480000) } },
                "streams" => new { items = input.TryGetProperty("cursor", out var cursor) && cursor.GetString() == "next" ? new[] { live[3] } : live.Take(3).ToArray(), nextCursor = input.TryGetProperty("cursor", out var next) && next.GetString() == "next" ? "" : "next" },
                "search" => new { items = Array.Empty<object>() },
                "recent" => new { items = live.Take(2).ToArray() },
                "vods" => new { items = new[] { new { key = "vod1", title = "The long way home · a mountain adventure", channel = "Northstar", platform = "Twitch", state = "VOD", duration = 7200, resume = 1820, watched = false, thumbnailUrl = Address + "media/artwork/mountain" }, new { key = "vod2", title = "One more chapter", channel = "Northstar", platform = "Twitch", state = "VOD", duration = 3600, resume = 0, watched = true, thumbnailUrl = Address + "media/artwork/game" } } },
                "downloads" => new { items = new[] { new { id = "download1", title = "Ready to watch", channel = "Northstar", platform = "Twitch", state = "Completed", format = "MP4", totalSegments = 100, completedSegments = 100, bytesDownloaded = 590000000, thumbnailUrl = Address + "media/artwork/mountain" }, new { id = "download2", title = "Saving your next adventure", channel = "Pixelquest", platform = "Twitch", state = "Downloading", format = "HLS", totalSegments = 100, completedSegments = DownloadCompletedSegments, bytesDownloaded = 281000000, thumbnailUrl = Address + "media/artwork/game" } } },
                _ => null
            };
        }

        // Deterministic artwork for layout evidence only; production always uses provider thumbnails.
        private static string Artwork(string name)
        {
            var colors = name switch { "mountain" => ("#224f50", "#7badaa", "#173b40"), "game" => ("#59417b", "#a88bc5", "#302749"), "music" => ("#805049", "#dfab84", "#3b303d"), "code" => ("#2a4b70", "#72a2b5", "#1d304a"), _ => ("#424852", "#899999", "#242c32") };
            return $"<svg xmlns='http://www.w3.org/2000/svg' width='960' height='540' viewBox='0 0 960 540'><rect width='960' height='540' fill='{colors.Item1}'/><circle cx='730' cy='150' r='90' fill='{colors.Item2}'/><path d='M0 420 230 120 470 400 650 190 960 470V540H0Z' fill='{colors.Item3}'/><path d='m0 500 300-200 270 230 250-220 140 130v100H0Z' fill='#111d29' opacity='.8'/><path d='M0 510q300-140 500 0t460-20' fill='none' stroke='{colors.Item2}' stroke-width='3' opacity='.4'/></svg>";
        }
    }
}

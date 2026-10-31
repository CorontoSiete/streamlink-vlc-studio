using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static async Task CheckBrowserOverlay(CoreWebView2 core, CoreWebView2Controller controller, BrowserWorkspace workspace, LocalWebServer server, string password)
    {
        var userId = server.Users.ListUsers().Single(u => u.Username == TestUsername).Id;
        await core.ExecuteScriptAsync($"document.querySelector('#password').value={JsonSerializer.Serialize(password)};document.querySelector('#login-form').requestSubmit()");
        await WaitBrowser(core, "!document.querySelector('#studio').hidden && document.querySelectorAll('.card').length===1");
        await core.ExecuteScriptAsync("document.querySelector('.card-main').click()");
        await WaitBrowser(core, "document.querySelector('video')?.currentTime>0.1");
        await SaveBrowserChat(core, "overlay", 22, 45);
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('video').controlsList.contains('nofullscreen')"));
        Assert.Equal("22px", JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("getComputedStyle(document.querySelector('.chat-messages')).fontSize")));
        Assert.Equal("rgba(24, 24, 27, 0.45)", JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("getComputedStyle(document.querySelector('.chat')).backgroundColor")));
        await AssertBrowserOverlay(core);
        await CheckBrowserDefaultChatBottom(core);
        await CheckBrowserChatImagesAndSizing(core, workspace);
        await CheckBrowserChatMovement(core);

        // Exercise the real form with trusted pointer input, including at narrow phone widths.
        foreach (var width in new[] { 390, 320, 768 })
        {
            controller.Bounds = new System.Drawing.Rectangle(0, 0, width, 844);
            await WaitBrowser(core, $"innerWidth==={width}");
            await AssertBrowserOverlay(core);
            await AssertChatAtVideoBottom(core);
            await core.ExecuteScriptAsync($"document.querySelector('.chat-form input').value='Message at {width}'");
            await ClickBrowser(core, ".chat-form button");
            await WaitBrowser(core, $"document.querySelector('.chat-messages').textContent.includes('Message at {width}') && document.querySelector('.chat-form input').value===''");
            await AssertBrowserOverlay(core);
            if (width == 390) await CaptureBrowser(core, "overlay-phone");
            if (width == 320) await CaptureBrowser(core, "overlay-bottom-phone");
        }

        // The message DOM and typed draft survive layout changes and temporary hiding.
        await core.ExecuteScriptAsync("document.querySelector('.chat-form input').value='Unsent draft';window.savedChat=document.querySelector('.chat')");
        await SaveBrowserChat(core, "docked");
        Assert.Equal("true", await core.ExecuteScriptAsync("getComputedStyle(document.querySelector('.chat-move')).display==='none'"));
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('.chat').parentElement.classList.contains('video-chat') && document.querySelector('.chat')===window.savedChat && document.querySelector('.chat-form input').value==='Unsent draft'"));
        await SaveBrowserChat(core, "hidden");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('.chat').hidden && document.querySelector('[data-action=chat]').getAttribute('aria-pressed')==='false'"));
        await SaveBrowserChat(core, "overlay", 32, 20);
        controller.Bounds = new System.Drawing.Rectangle(0, 0, 320, 844);
        await WaitBrowser(core, "innerWidth===320");
        await AssertBrowserOverlay(core);
        // Large text must stay readable while touch targets leave the message input clear.
        await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", "{\"enabled\":true,\"maxTouchPoints\":1}");
        try
        {
            await WaitBrowser(core, "matchMedia('(pointer: coarse)').matches && document.querySelector('.chat').getBoundingClientRect().height>=140");
            await AssertBrowserOverlay(core);
            Assert.Equal("true", await core.ExecuteScriptAsync("(()=>{const c=document.querySelector('.chat'),m=c.querySelector('.chat-move').getBoundingClientRect(),r=c.querySelector('.chat-resize').getBoundingClientRect(),i=c.querySelector('.chat-form input').getBoundingClientRect();return m.height>=44 && m.width>=44 && r.height>=44 && r.width>=44 && r.right<=i.left})()"));
            await core.ExecuteScriptAsync("document.querySelector('.video-surface').scrollIntoView({block:'center'})");
            await CaptureBrowser(core, "overlay-touch");
        }
        finally
        {
            await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", "{\"enabled\":false}");
        }
        await core.ExecuteScriptAsync("document.querySelector('[data-action=chat]').click()");
        await WaitBrowser(core, "document.querySelector('.chat').hidden");
        await core.ExecuteScriptAsync("document.querySelector('[data-action=chat]').click()");
        await WaitBrowser(core, "!document.querySelector('.chat').hidden");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('.chat')===window.savedChat && document.querySelector('.chat-form input').value==='Unsent draft' && document.querySelector('.chat-messages').textContent.includes('Message at 390')"));

        // Connection errors must remain visible even though the overlay omits routine chrome.
        workspace.ChatStatus = "Chat unavailable";
        await WaitBrowser(core, "document.querySelector('.chat-status').textContent==='Chat unavailable'");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('.chat-status').getClientRects().length>0"));
        workspace.ChatStatus = "Connected";
        await WaitBrowser(core, "document.querySelector('.chat-status').textContent==='Connected'");

        controller.Bounds = new System.Drawing.Rectangle(0, 0, 1440, 1000);
        await WaitBrowser(core, "innerWidth===1440");
        await SaveBrowserChat(core, "overlay");
        await AssertSavedChatPosition(core);
        await AssertChatAtVideoBottom(core);
        // A deliberately moved overlay can cover the video controls. The stream menu
        // keeps fullscreen reachable without moving or hiding that overlay.
        await ClickBrowser(core, ".player-options summary");
        await ClickBrowser(core, "[data-action=fullscreen-menu]");
        await WaitBrowser(core, "document.fullscreenElement?.classList.contains('player')===true");
        await AssertBrowserOverlay(core);
        await AssertSavedChatPosition(core);
        await AssertChatAtVideoBottom(core);
        Assert.Equal("true", await core.ExecuteScriptAsync("document.fullscreenElement.contains(document.querySelector('.chat'))"));
        await CaptureBrowser(core, "overlay-fullscreen");
        await core.ExecuteScriptAsync("document.exitFullscreen()");
        await WaitBrowser(core, "!document.fullscreenElement");

        // Two real players catch grid selectors overriding the overlay's size/position.
        await core.ExecuteScriptAsync("document.querySelector('[data-page=following]').click()");
        await WaitBrowser(core, "document.querySelectorAll('.card').length===1");
        // Reopening the same library entry now selects its existing player. Open a
        // distinct channel so this regression still exercises two independent overlays.
        workspace.PlayerChannel = "second-streamer";
        await core.ExecuteScriptAsync("document.dispatchEvent(new KeyboardEvent('keydown',{key:'k',ctrlKey:true,bubbles:true}));document.querySelector('#open-query').value='second-streamer';document.querySelector('#open-form').requestSubmit();document.querySelector('.multistream-toggle').checked=true;document.querySelector('.multistream-toggle').dispatchEvent(new Event('change'))");
        await WaitBrowser(core, "document.querySelectorAll('video').length===2 && [...document.querySelectorAll('video')].every(v=>v.readyState>=2)");
        workspace.PlayerChannel = "streamer";
        await AssertBrowserOverlay(core);
        await AssertChatAtVideoBottom(core);
        Assert.Equal(false, server.Users.PreferencesFor(userId).ChatLayouts.ContainsKey("Twitch:second-streamer"));
        Assert.Equal("true", await core.ExecuteScriptAsync("(()=>{const c=document.querySelectorAll('.chat')[1],r=c.getBoundingClientRect(),s=c.closest('.player').querySelector('.video-surface').getBoundingClientRect();return Math.abs(r.top-s.top-8)<1 && Math.abs(r.right-s.right+8)<1})()"));
        await core.ExecuteScriptAsync("window.secondChatPosition=document.querySelectorAll('.chat')[1].style.cssText;document.querySelector('.chat-move').focus()");
        await ChatBrowserKey(core, "ArrowRight", 39);
        await core.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'))");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelectorAll('.chat')[1].style.cssText===window.secondChatPosition && document.querySelector('.chat').style.cssText!==window.secondChatPosition"));
        await core.ExecuteScriptAsync("window.firstChatPosition=document.querySelector('.chat').style.cssText;document.querySelectorAll('.chat-move')[1].focus()");
        await ChatBrowserKey(core, "ArrowLeft", 37);
        await core.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'))");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('.chat').style.cssText===window.firstChatPosition && document.querySelectorAll('.chat')[1].style.cssText!==window.secondChatPosition"));
        await CaptureBrowser(core, "overlay-grid");
        // The earlier fullscreen check can leave Stream options open.
        // A bottom-aligned chat intentionally covers the video controls, so use that menu.
        if (await core.ExecuteScriptAsync("document.querySelector('.player .channel-information:not([hidden]) .player-options').open") == "false")
            await ClickBrowser(core, ".player .channel-information:not([hidden]) .player-options summary");
        await WaitBrowser(core, "document.querySelector('.player .channel-information:not([hidden]) [data-action=fullscreen-menu]').getBoundingClientRect().width>0");
        await ClickBrowser(core, ".player .channel-information:not([hidden]) [data-action=fullscreen-menu]");
        await WaitBrowser(core, "document.fullscreenElement?.id==='players'");
        await AssertBrowserOverlay(core);
        await AssertChatAtVideoBottom(core);
        await core.ExecuteScriptAsync("document.exitFullscreen()");
        await WaitBrowser(core, "!document.fullscreenElement");

        // A full page reload must restore this channel's server layout. Deliberately
        // clear device storage to prove that geometry does not depend on the origin.
        await SaveBrowserChat(core, "overlay", 22, 0);
        var savedLayout = server.Users.PreferencesFor(userId).ChatLayouts["Twitch:streamer"];
        await core.ExecuteScriptAsync("localStorage.clear()");
        Assert.Equal("[]", await core.ExecuteScriptAsync("window.testErrors"));
        core.Reload();
        await WaitBrowser(core, "document.querySelectorAll('.card').length===1 && !document.querySelector('#studio').hidden");
        await SaveBrowserChat(core, "overlay", 22, 0);
        Assert.True(savedLayout.Width > 340);
        await core.ExecuteScriptAsync("document.dispatchEvent(new KeyboardEvent('keydown',{key:'k',ctrlKey:true,bubbles:true}));document.querySelector('#open-query').value='https://www.twitch.tv/videos/123';document.querySelector('#open-form').requestSubmit()");
        await WaitBrowser(core, "document.querySelector('video')?.readyState>=2 && document.querySelector('#open-dialog').open===false");
        await AssertBrowserOverlay(core, false);
        await AssertSavedChatPosition(core, savedLayout);
        await AssertChatAtVideoBottom(core);
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('.player').classList.contains('chat-transparent') && document.querySelector('.chat').getBoundingClientRect().width>340"));
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('.chat-form').hidden"));
        await core.ExecuteScriptAsync("document.querySelector('video').pause();document.querySelector('video').currentTime=4");
        await WaitBrowser(core, "document.querySelector('.chat-messages').textContent.includes('Replay at 3')");
        Assert.Equal("false", await core.ExecuteScriptAsync("document.querySelector('.chat-messages').textContent.includes('Replay at 32')"));
        await core.ExecuteScriptAsync("document.querySelector('video').currentTime=33");
        await WaitBrowser(core, "document.querySelector('.chat-messages').textContent.includes('Replay at 32')");
        await AssertBrowserOverlay(core, false);
        Assert.Equal("[]", await core.ExecuteScriptAsync("window.testErrors"));
    }

    private static async Task SaveBrowserChat(CoreWebView2 core, string layout, int size = 16, int opacity = 90)
    {
        await core.ExecuteScriptAsync($"document.querySelector('[data-page=settings]').click();document.querySelector('#chat-layout').value={JsonSerializer.Serialize(layout)};document.querySelector('#chat-size').value={size};document.querySelector('#chat-size').dispatchEvent(new Event('input',{{bubbles:true}}));document.querySelector('#chat-opacity').value={opacity};document.querySelector('#chat-opacity').dispatchEvent(new Event('input',{{bubbles:true}}));document.querySelector('#preferences').requestSubmit()");
        await WaitBrowser(core, "document.querySelector('#save-status').textContent==='All changes saved to your account.'");
        // Clear the saved-preferences toast before exercising video overlay handles underneath it.
        await core.ExecuteScriptAsync("document.querySelector('#notice button')?.click();document.querySelector('.followed-link[aria-current=true]').click()");
    }

    private static async Task AssertBrowserOverlay(CoreWebView2 core, bool live = true)
    {
        var condition = $$"""
            [...document.querySelectorAll('.player')].every(p=>{
              const v=p.querySelector('video').getBoundingClientRect(),c=p.querySelector('.chat'),r=c.getBoundingClientRect(),m=p.querySelector('.chat-messages'),s=getComputedStyle(m);
              const f=p.querySelector('.chat-form').getBoundingClientRect();
              return !c.hidden && r.width>100 && r.right<=v.right && r.left>=v.left && r.top>=v.top && r.bottom<=v.bottom+.5 &&
                m.clientHeight-parseFloat(s.paddingTop)-parseFloat(s.paddingBottom)>=Math.min(48,parseFloat(s.fontSize)*1.6) &&
                {{(live ? "f.bottom<=r.bottom && f.top>=r.top" : "true")}} &&
                v.width>=p.querySelector('.screen').clientWidth-1;
            }) && document.documentElement.scrollWidth<=document.documentElement.clientWidth
            """;
        // A viewport change is observable before ResizeObserver has repositioned chat.
        // Wait for the rendered invariants while retaining the same geometry assertions.
        await WaitBrowser(core, condition);
        var metrics = await core.ExecuteScriptAsync("[...document.querySelectorAll('.player')].map(p=>{const r=e=>{const x=e.getBoundingClientRect();return {x:x.x,y:x.y,width:x.width,height:x.height,bottom:x.bottom}};return {video:r(p.querySelector('video')),chat:r(p.querySelector('.chat')),messages:r(p.querySelector('.chat-messages')),form:r(p.querySelector('.chat-form'))}})");
        Assert.True(await core.ExecuteScriptAsync(condition) == "true", metrics);
    }

    private static async Task ClickBrowser(CoreWebView2 core, string selector)
    {
        var pointJson = await core.ExecuteScriptAsync($$"""
            (()=>{const e=document.querySelector({{JsonSerializer.Serialize(selector)}});e.scrollIntoView({block:'center'});const r=e.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})()
            """);
        using var point = JsonDocument.Parse(pointJson);
        var x = point.RootElement.GetProperty("x").GetDouble();
        var y = point.RootElement.GetProperty("y").GetDouble();
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseMoved", x, y }));
        foreach (var type in new[] { "mousePressed", "mouseReleased" })
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x, y, button = "left", clickCount = 1 }));
    }
}

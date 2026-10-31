using Microsoft.Web.WebView2.Core;

internal static partial class LocalWebTestCatalog
{
    private static async Task CheckBrowserDefaultChatBottom(CoreWebView2 core)
    {
        // Moving the default-size overlay must work before the user has resized it.
        await core.ExecuteScriptAsync("window.defaultChatSize=(()=>{const r=document.querySelector('.chat').getBoundingClientRect();return [r.width,r.height]})()");
        await DragBrowserChat(core, ".chat-move", 0, 10000);
        await AssertChatAtVideoBottom(core);
        await WaitBrowser(core, "window.chatLayoutRequests.at(-1)?.layout.y===1");
        Assert.Equal("true", await core.ExecuteScriptAsync("(()=>{const r=document.querySelector('.chat').getBoundingClientRect();return r.width===window.defaultChatSize[0] && r.height===window.defaultChatSize[1] && window.chatLayoutRequests.at(-1).layout.y===1})()"));
        await ChatBrowserKey(core, "ArrowDown", 40, shift: true);
        await AssertChatAtVideoBottom(core);
        await ChatBrowserKey(core, "ArrowUp", 38, shift: true);
        Assert.Equal("true", await core.ExecuteScriptAsync("Math.abs(document.querySelector('video').getBoundingClientRect().bottom-document.querySelector('.chat').getBoundingClientRect().bottom-40)<1"));
        await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", "{\"enabled\":true,\"maxTouchPoints\":1}");
        try
        {
            var point = await ChatBrowserHandle(core, ".chat-move");
            await ChatBrowserTouch(core, "touchStart", point[0], point[1]);
            await ChatBrowserTouch(core, "touchMove", point[0], point[1] + 10000);
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchTouchEvent", "{\"type\":\"touchEnd\",\"touchPoints\":[]}");
            await AssertChatAtVideoBottom(core);
        }
        finally
        {
            await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", "{\"enabled\":false}");
        }
        await core.ExecuteScriptAsync("document.querySelector('.chat-move').focus()");
        await ChatBrowserKey(core, "Home", 36);
        // The maximum resize height must agree with the rendered CSS constraint.
        await DragBrowserChat(core, ".chat-resize", 0, 10000);
        await AssertChatAtVideoBottom(core);
        Assert.Equal("true", await core.ExecuteScriptAsync("Math.abs(document.querySelector('.chat').getBoundingClientRect().top-document.querySelector('video').getBoundingClientRect().top-8)<1"));
        await ChatBrowserKey(core, "Home", 36);
    }

    private static async Task CheckBrowserChatMovement(CoreWebView2 core)
    {
        await core.ExecuteScriptAsync("""
            window.overlayBox=(selector='.chat')=>{const c=document.querySelector(selector),r=c.getBoundingClientRect(),s=c.closest('.player').querySelector('.video-surface').getBoundingClientRect();return {left:r.left-s.left,top:r.top-s.top,right:r.right-s.left,bottom:r.bottom-s.top,width:r.width,height:r.height}};
            window.beforeMove=overlayBox();window.moveRow=document.querySelector('.chat-message');document.querySelector('.chat-form input').value='Draft survives moving';
            """);
        await DragBrowserChat(core, ".chat-move", -120, 25);
        Assert.Equal("true", await core.ExecuteScriptAsync("(()=>{const r=overlayBox(),b=window.beforeMove;return Math.abs(r.left-b.left+120)<1 && Math.abs(r.top-b.top-25)<1 && r.width===b.width && r.height===b.height && document.querySelector('.chat-message')===window.moveRow && document.querySelector('.chat-form input').value==='Draft survives moving'})()"));
        await CaptureBrowser(core, "overlay-moved");

        // Keyboard movement is independent of resizing; Home restores the top-right position.
        await core.ExecuteScriptAsync("window.beforeMove=overlayBox();document.querySelector('.chat-move').focus()");
        await ChatBrowserKey(core, "ArrowLeft", 37, shift: true);
        Assert.Equal("true", await core.ExecuteScriptAsync("Math.abs(overlayBox().left-window.beforeMove.left+40)<1 && overlayBox().width===window.beforeMove.width"));
        await ChatBrowserKey(core, "Home", 36);
        Assert.Equal("true", await core.ExecuteScriptAsync("(()=>{const r=overlayBox(),s=document.querySelector('.video-surface');return Math.abs(r.right-s.clientWidth+8)<1 && r.top===8})()"));
        await DragBrowserChat(core, ".chat-move", -120, 25);

        // Trusted touch events must move chat without starting page scrolling.
        await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", "{\"enabled\":true,\"maxTouchPoints\":1}");
        try
        {
            var point = await ChatBrowserHandle(core, ".chat-move");
            var savesBeforeTouch = int.Parse(await core.ExecuteScriptAsync("window.chatLayoutRequests.length"), CultureInfo.InvariantCulture);
            await core.ExecuteScriptAsync("window.beforeMove=overlayBox();window.beforeScroll=scrollY");
            await ChatBrowserTouch(core, "touchStart", point[0], point[1]);
            await ChatBrowserTouch(core, "touchMove", point[0] - 35, point[1] + 20);
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchTouchEvent", "{\"type\":\"touchEnd\",\"touchPoints\":[]}");
            Assert.Equal("true", await core.ExecuteScriptAsync("Math.abs(overlayBox().left-window.beforeMove.left+35)<1 && Math.abs(overlayBox().top-window.beforeMove.top-20)<1 && scrollY===window.beforeScroll"));
            await WaitBrowser(core, $"window.chatLayoutRequests.length>{savesBeforeTouch} && (()=>{{const p=window.chatLayoutRequests.at(-1).layout,r=overlayBox(),s=document.querySelector('.video-surface');return Math.abs(r.left-8-(s.clientWidth-r.width-16)*p.x)<1 && Math.abs(r.top-8-(s.clientHeight-r.height-8)*p.y)<1}})()");

            point = await ChatBrowserHandle(core, ".chat-move");
            await core.ExecuteScriptAsync("window.beforeMove=overlayBox();window.beforePreferences=JSON.stringify(window.chatLayoutRequests)");
            await ChatBrowserTouch(core, "touchStart", point[0], point[1]);
            await ChatBrowserTouch(core, "touchMove", point[0] + 50, point[1] - 15);
            Assert.Equal("true", await core.ExecuteScriptAsync("Math.abs(overlayBox().left-window.beforeMove.left)>40"));
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchTouchEvent", "{\"type\":\"touchCancel\",\"touchPoints\":[]}");
            await AssertChatMoveCancelled(core);
        }
        finally
        {
            await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", "{\"enabled\":false}");
        }

        // Escape and unexpected capture loss both restore the last committed position.
        foreach (var escape in new[] { true, false })
        {
            var point = await ChatBrowserHandle(core, ".chat-move");
            await core.ExecuteScriptAsync("window.beforeMove=overlayBox();window.beforePreferences=JSON.stringify(window.chatLayoutRequests);document.querySelector('.chat-move').addEventListener('pointerdown',e=>window.movePointer=e.pointerId,{once:true})");
            await ChatBrowserMouse(core, "mousePressed", point[0], point[1]);
            await ChatBrowserMouse(core, "mouseMoved", point[0] + 50, point[1] - 15);
            Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('.chat-move').hasPointerCapture(window.movePointer) && Math.abs(overlayBox().left-window.beforeMove.left)>40"));
            if (escape) await ChatBrowserKey(core, "Escape", 27);
            else await core.ExecuteScriptAsync("document.querySelector('.chat-move').releasePointerCapture(window.movePointer)");
            await ChatBrowserMouse(core, "mouseReleased", point[0] + 50, point[1] - 15);
            await AssertChatMoveCancelled(core);
        }

        // Drag beyond every edge; pointer capture keeps the gesture active outside the handle.
        await DragBrowserChat(core, ".chat-move", -10000, -10000);
        Assert.Equal("true", await core.ExecuteScriptAsync("overlayBox().left===8 && overlayBox().top===8"));
        await DragBrowserChat(core, ".chat-move", 10000, 10000);
        await CaptureBrowser(core, "overlay-bottom");
        await AssertChatAtVideoBottom(core);
        Assert.Equal("true", await core.ExecuteScriptAsync("Math.abs(overlayBox().right-document.querySelector('.video-surface').clientWidth+8)<1"));
        await AssertBrowserOverlay(core);

        // Resizing a moved overlay keeps its top-right corner fixed.
        await core.ExecuteScriptAsync("window.beforeMove=overlayBox()");
        await DragBrowserChat(core, ".chat-resize", 20, -20);
        Assert.Equal("true", await core.ExecuteScriptAsync("(()=>{const r=overlayBox(),b=window.beforeMove;return Math.abs(r.right-b.right)<1 && Math.abs(r.top-b.top)<1 && Math.abs(r.width-b.width+20)<1 && Math.abs(r.height-b.height+20)<1})()"));
        await core.ExecuteScriptAsync("window.beforeMove=overlayBox()");
        await DragBrowserChat(core, ".chat-resize", 0, 10000);
        await AssertChatAtVideoBottom(core);
        Assert.Equal("true", await core.ExecuteScriptAsync("Math.abs(overlayBox().top-window.beforeMove.top)<1 && Math.abs(overlayBox().right-window.beforeMove.right)<1"));
        await DragBrowserChat(core, ".chat-resize", 0, -20);
        await DragBrowserChat(core, ".chat-move", -100, 10000);
        await AssertChatAtVideoBottom(core);
        await AssertBrowserOverlay(core);
    }

    private static async Task AssertChatAtVideoBottom(CoreWebView2 core, string selector = ".chat")
    {
        // Layout observers run after viewport and fullscreen changes become visible.
        await WaitBrowser(core, $$"""
            (()=>{const c=document.querySelector({{JsonSerializer.Serialize(selector)}}),r=c.getBoundingClientRect(),v=c.closest('.player').querySelector('video').getBoundingClientRect();return Math.abs(v.bottom-r.bottom)<1;})()
            """);
        var metrics = await core.ExecuteScriptAsync($$"""
            (()=>{const c=document.querySelector({{JsonSerializer.Serialize(selector)}}),r=c.getBoundingClientRect(),v=c.closest('.player').querySelector('video').getBoundingClientRect();return {gap:v.bottom-r.bottom,chatHeight:r.height,videoHeight:v.height};})()
            """);
        using var result = JsonDocument.Parse(metrics);
        Assert.True(Math.Abs(result.RootElement.GetProperty("gap").GetDouble()) < 1,
            "Chat must reach the video's bottom edge: " + metrics);
    }

    private static async Task AssertChatMoveCancelled(CoreWebView2 core)
    {
        await WaitBrowser(core, "!document.querySelector('.chat').classList.contains('is-moving')");
        var details = await core.ExecuteScriptAsync("JSON.stringify({sameBox:JSON.stringify(overlayBox())===JSON.stringify(window.beforeMove),sameRequests:JSON.stringify(window.chatLayoutRequests)===window.beforePreferences,before:window.beforeMove,after:overlayBox(),beforeRequests:window.beforePreferences,afterRequests:window.chatLayoutRequests})");
        Assert.True(await core.ExecuteScriptAsync("JSON.stringify(overlayBox())===JSON.stringify(window.beforeMove) && JSON.stringify(window.chatLayoutRequests)===window.beforePreferences") == "true", details);
    }

    private static async Task<double[]> ChatBrowserHandle(CoreWebView2 core, string selector) =>
        JsonSerializer.Deserialize<double[]>(await core.ExecuteScriptAsync($$"""
            (()=>{const e=document.querySelector({{JsonSerializer.Serialize(selector)}});e.scrollIntoView({block:'center'});const r=e.getBoundingClientRect();return [r.x+r.width/2,r.y+r.height/2]})()
            """))!;

    private static Task<string> ChatBrowserMouse(CoreWebView2 core, string type, double x, double y) =>
        core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x, y, button = "left", buttons = type == "mouseReleased" ? 0 : 1, clickCount = 1 }));

    private static Task<string> ChatBrowserTouch(CoreWebView2 core, string type, double x, double y) =>
        core.CallDevToolsProtocolMethodAsync("Input.dispatchTouchEvent", JsonSerializer.Serialize(new { type, touchPoints = new[] { new { x, y } } }));

    private static async Task DragBrowserChat(CoreWebView2 core, string selector, double dx, double dy)
    {
        var point = await ChatBrowserHandle(core, selector);
        await ChatBrowserMouse(core, "mousePressed", point[0], point[1]);
        await ChatBrowserMouse(core, "mouseMoved", point[0] + dx, point[1] + dy);
        await ChatBrowserMouse(core, "mouseReleased", point[0] + dx, point[1] + dy);
    }

    private static async Task ChatBrowserKey(CoreWebView2 core, string key, int virtualKey, bool shift = false)
    {
        foreach (var type in new[] { "keyDown", "keyUp" })
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type, key, code = key, windowsVirtualKeyCode = virtualKey, modifiers = shift ? 8 : 0 }));
    }

    private static async Task AssertSavedChatPosition(CoreWebView2 core, WebChatOverlayLayout? expected = null)
    {
        if (expected is null) await WaitBrowser(core, "window.chatLayoutRequests.length>0");
        Assert.Equal("true", await core.ExecuteScriptAsync($$"""
            (()=>{const c=document.querySelector('.chat'),r=c.getBoundingClientRect(),s=c.parentElement.getBoundingClientRect(),p={{JsonSerializer.Serialize(expected, new JsonSerializerOptions(JsonSerializerDefaults.Web))}}||window.chatLayoutRequests.at(-1).layout;return p.x>=0 && p.x<=1 && p.y>=0 && p.y<=1 && Math.abs(r.left-s.left-8-(s.width-r.width-16)*p.x)<1 && Math.abs(r.top-s.top-8-(s.height-r.height-8)*p.y)<1})()
            """));
    }
}

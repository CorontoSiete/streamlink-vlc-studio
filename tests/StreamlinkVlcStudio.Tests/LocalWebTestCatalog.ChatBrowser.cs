using Microsoft.AspNetCore.Http;
using Microsoft.Web.WebView2.Core;

internal static partial class LocalWebTestCatalog
{
    private static async Task CheckBrowserChatImagesAndSizing(CoreWebView2 core, BrowserWorkspace workspace)
    {
        await WaitBrowser(core, "document.querySelectorAll('.chat-message').length===3");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('.chat-message').textContent.includes('!') && document.querySelectorAll('.chat-message')[1].textContent.endsWith('!')"));
        await core.ExecuteScriptAsync("window.chatRowBefore=document.querySelector('.chat-message')");
        workspace.DecorationsReady = true;
        await WaitBrowser(core, "document.querySelectorAll('.badge-image').length===2 && [...document.querySelectorAll('.chat-emote,.badge-image')].every(i=>i.complete&&i.naturalWidth>0)");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('.chat-message')===window.chatRowBefore && document.querySelector('.chat-message').querySelector('strong').style.color==='rgb(18, 52, 86)' && document.querySelectorAll('.chat-message').length===3"));
        await WaitBrowser(core, "document.querySelectorAll('.chat-message')[2].textContent.includes('BROKEN') && !document.querySelectorAll('.chat-message')[2].querySelector('img')");
        // Check composited pixels: drawing a GIF to canvas can keep returning its first frame.
        await core.ExecuteScriptAsync("document.querySelector('.chat-messages').scrollTop=0;document.querySelector('img[alt=Dance]').scrollIntoView({block:'center'})");
        await Task.Delay(100);
        var rect = JsonSerializer.Deserialize<double[]>(await core.ExecuteScriptAsync("(()=>{const r=document.querySelector('img[alt=Dance]').getBoundingClientRect();return [r.x+r.width/2,r.y+r.height/2]})()"))!;
        var colors = new HashSet<uint>();
        for (var frame = 0; frame < 10; frame++)
        {
            using var png = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, png);
            png.Position = 0;
            var bitmap = new FormatConvertedBitmap(BitmapDecoder.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);
            Assert.True(rect[0] >= 0 && rect[1] >= 0 && rect[0] < bitmap.PixelWidth && rect[1] < bitmap.PixelHeight,
                $"GIF center ({rect[0]}, {rect[1]}) is outside capture {bitmap.PixelWidth}x{bitmap.PixelHeight}.");
            var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect((int)rect[0], (int)rect[1], 1, 1), pixel, 4, 0);
            colors.Add(BitConverter.ToUInt32(pixel));
            await Task.Delay(60);
        }
        Assert.True(colors.Any(c => (c & 0x00ff0000) > 0x00c00000) && colors.Any(c => (c & 0x000000ff) > 192), "Animated GIF did not show both red and blue frames.");

        await core.ExecuteScriptAsync("window.originalOverlay=(()=>{const r=document.querySelector('.chat').getBoundingClientRect();return {width:r.width,height:r.height}})()");
        var handle = JsonSerializer.Deserialize<double[]>(await core.ExecuteScriptAsync("(()=>{const r=document.querySelector('.chat-resize').getBoundingClientRect();return [r.x+r.width/2,r.y+r.height/2]})()"))!;
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mousePressed", x = handle[0], y = handle[1], button = "left", clickCount = 1 }));
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseMoved", x = handle[0] - 65, y = handle[1] - 70, button = "left", buttons = 1 }));
        Assert.Equal("true", await core.ExecuteScriptAsync("(()=>{const c=document.querySelector('.chat'),r=c.getBoundingClientRect();return c.classList.contains('is-resizing') && getComputedStyle(c.querySelector('.chat-resize-tooltip')).display!=='none' && c.querySelector('.chat-resize-feedback').textContent===`${Math.round(r.width)} × ${Math.round(r.height)}`})()"));
        await CaptureBrowser(core, "overlay-resizing");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseReleased", x = handle[0] - 65, y = handle[1] - 70, button = "left", clickCount = 1 }));
        await WaitBrowser(core, "window.chatLayoutRequests.at(-1)?.layout.width>340");
        Assert.Equal("true", await core.ExecuteScriptAsync("(()=>{const r=document.querySelector('.chat').getBoundingClientRect();return r.width>window.originalOverlay.width+50 && r.height<window.originalOverlay.height-50 && window.chatLayoutRequests.at(-1).layout.width>340})()"));
        await core.ExecuteScriptAsync("document.querySelector('.chat-resize').focus();window.keyboardHeight=document.querySelector('.chat').getBoundingClientRect().height");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyDown\",\"key\":\"ArrowDown\",\"code\":\"ArrowDown\",\"windowsVirtualKeyCode\":40}");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyUp\",\"key\":\"ArrowDown\",\"code\":\"ArrowDown\",\"windowsVirtualKeyCode\":40}");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('.chat').getBoundingClientRect().height>window.keyboardHeight+9"));
        var touchPoint = JsonSerializer.Deserialize<double[]>(await core.ExecuteScriptAsync("(()=>{const r=document.querySelector('.chat-resize').getBoundingClientRect();window.beforeTouchWidth=document.querySelector('.chat').getBoundingClientRect().width;return [r.x+r.width/2,r.y+r.height/2]})()"))!;
        await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", "{\"enabled\":true,\"maxTouchPoints\":1}");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchTouchEvent", JsonSerializer.Serialize(new { type = "touchStart", touchPoints = new[] { new { x = touchPoint[0], y = touchPoint[1] } } }));
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchTouchEvent", JsonSerializer.Serialize(new { type = "touchMove", touchPoints = new[] { new { x = touchPoint[0] + 35, y = touchPoint[1] - 20 } } }));
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchTouchEvent", "{\"type\":\"touchEnd\",\"touchPoints\":[]}");
        await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", "{\"enabled\":false}");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('.chat').getBoundingClientRect().width<window.beforeTouchWidth-30"));
        await SaveBrowserChat(core, "overlay", 22, 0);
        await core.ExecuteScriptAsync("document.activeElement.blur()");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mouseMoved\",\"x\":0,\"y\":0}");
        Assert.Equal("true", await core.ExecuteScriptAsync("(()=>{const c=document.querySelector('.chat'),s=getComputedStyle(c);return s.backgroundColor==='rgba(24, 24, 27, 0)' && s.borderTopColor==='rgba(0, 0, 0, 0)' && getComputedStyle(c.querySelector('.chat-message')).opacity==='1' && getComputedStyle(c.querySelector('.chat-form')).opacity==='0' && getComputedStyle(c.querySelector('.chat-resize')).opacity==='0'})()"));
        Assert.Equal("true", await core.ExecuteScriptAsync("getComputedStyle(document.querySelector('.chat-move')).opacity==='0'"));
        var movePoint = await ChatBrowserHandle(core, ".chat-move");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseMoved", x = movePoint[0], y = movePoint[1] }));
        Assert.Equal("true", await core.ExecuteScriptAsync("getComputedStyle(document.querySelector('.chat-move')).opacity==='1'"));
        await DragBrowserChat(core, ".chat-move", -10, 0);
        Assert.Equal("true", await core.ExecuteScriptAsync("window.chatLayoutRequests.at(-1).layout.x<1"));
        await core.ExecuteScriptAsync("(()=>{const filler=document.createElement('div');filler.id='chat-scrollbar-fixture';filler.style.height='2000px';document.querySelector('.chat-messages').append(filler)})()");
        await MoveBrowserPointer(core, 0, 0);
        Assert.Equal("true", await core.ExecuteScriptAsync("(()=>{const c=document.querySelector('.chat'),m=c.querySelector('.chat-messages');return m.scrollHeight>m.clientHeight && c.contains(document.activeElement) && !c.matches(':hover') && getComputedStyle(m).scrollbarWidth==='none'})()"));
        var scrollbarPoint = await ChatBrowserHandle(core, ".chat-messages");
        await MoveBrowserPointer(core, scrollbarPoint[0], scrollbarPoint[1]);
        Assert.Equal("true", await core.ExecuteScriptAsync("(()=>{const c=document.querySelector('.chat'),m=c.querySelector('.chat-messages');return c.matches(':hover') && getComputedStyle(m).scrollbarWidth==='thin'})()"));
        await core.ExecuteScriptAsync("document.querySelector('#chat-scrollbar-fixture').remove()");
        await core.ExecuteScriptAsync("document.activeElement.blur()");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mouseMoved\",\"x\":0,\"y\":0}");
        await core.ExecuteScriptAsync("document.querySelector('.video-surface').scrollIntoView({block:'center'})");
        await CaptureBrowser(core, "overlay-transparent");
        await SaveBrowserChat(core, "overlay", 22, 45);
        await core.ExecuteScriptAsync("(()=>{const filler=document.createElement('div');filler.id='chat-scrollbar-fixture';filler.style.height='2000px';document.querySelector('.chat-messages').append(filler);document.querySelector('.chat-move').focus()})()");
        await MoveBrowserPointer(core, 0, 0);
        Assert.Equal("true", await core.ExecuteScriptAsync("(()=>{const c=document.querySelector('.chat'),m=c.querySelector('.chat-messages');return m.scrollHeight>m.clientHeight && c.contains(document.activeElement) && !c.matches(':hover') && getComputedStyle(m).scrollbarWidth==='none'})()"));
        scrollbarPoint = await ChatBrowserHandle(core, ".chat-messages");
        await MoveBrowserPointer(core, scrollbarPoint[0], scrollbarPoint[1]);
        Assert.Equal("true", await core.ExecuteScriptAsync("(()=>{const c=document.querySelector('.chat'),m=c.querySelector('.chat-messages');return c.matches(':hover') && getComputedStyle(m).scrollbarWidth==='thin'})()"));
        await core.ExecuteScriptAsync("document.querySelector('#chat-scrollbar-fixture').remove()");
    }

    private static Task<string> MoveBrowserPointer(CoreWebView2 core, double x, double y) =>
        core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseMoved", x, y }));

    private sealed partial class BrowserWorkspace
    {
        private ChatMessage[] DecoratedMessages()
        {
            string? Image(string name) => DecorationsReady ? "/chat-assets/" + name : null;
            return
            [
                new(PlatformKind.Twitch, "streamer", "Twitch viewer", "😀 Kappa! Dance", DateTimeOffset.UnixEpoch, "#123456",
                    [new("moderator", "1", "Twitch moderator", Image("badge"))],
                    [new(3, 8, "Kappa", Image("badge")), new(10, 15, "Dance", Image("animated"))], MessageId: "browser-twitch"),
                new(PlatformKind.Kick, "streamer", "Kick viewer", "[emote:37226:KEKW]!", DateTimeOffset.UnixEpoch,
                    Badges: [new("subscriber", "6", "Kick subscriber", Image("badge"))],
                    Emotes: [new(0, 18, "KEKW", Image("badge"))], MessageId: "browser-kick"),
                new(PlatformKind.Twitch, "streamer", "<script>window.injected=true</script>", "BROKEN", DateTimeOffset.UnixEpoch,
                    Emotes: [new(-1, 3, "invalid", "javascript:alert(1)"), new(0, 6, "BROKEN", Image("broken"))], MessageId: "browser-failed")
            ];
        }

        public async Task ServeChatAssetAsync(HttpContext context, string assetId)
        {
            if (assetId is not ("badge" or "animated")) { context.Response.StatusCode = 404; return; }
            context.Response.ContentType = "image/gif";
            // A looping two-frame GIF: a red pixel followed by a blue pixel, 100 ms each.
            await context.Response.Body.WriteAsync(Convert.FromBase64String("R0lGODlhAQABAIAAAP8AAAAA/yH/C05FVFNDQVBFMi4wAwEAAAAh+QQACgAAACwAAAAAAQABAAACAkQBACH5BAAKAAAALAAAAAABAAEAAAICTAEAOw=="), context.RequestAborted);
        }
    }
}

using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    internal const string BrowserVolumeTestName = "local web: account volume restores before playback across reopen, reload and user switching";

    private static async Task CheckBrowserVolume(CoreWebView2 core, BrowserWorkspace workspace, LocalWebServer server, string password)
    {
        // Record the native media volume at the application's first play call, before audio can start.
        const string instrumentPlay = "HTMLMediaElement.prototype.play=new Proxy(HTMLMediaElement.prototype.play,{apply(target,media,args){media.firstPlayVolume??=media.volume;return Reflect.apply(target,media,args)}})";
        await core.AddScriptToExecuteOnDocumentCreatedAsync(instrumentPlay);
        await core.ExecuteScriptAsync(instrumentPlay);
        await core.ExecuteScriptAsync($"document.querySelector('#password').value={JsonSerializer.Serialize(password)};document.querySelector('#login-form').requestSubmit()");
        await WaitBrowser(core, "!document.querySelector('#studio').hidden && document.querySelectorAll('.card').length===1");
        await OpenVolumePlayer(core);
        await AssertPlayerVolume(core, 1);
        await SetPlayerVolume(core, server, TestUsername, 0.37, "Twitch:streamer");

        // Reload and quality changes replace the media element; neither may briefly start at full volume.
        await core.ExecuteScriptAsync("document.querySelector('video').muted=true");
        await WaitBrowser(core, "document.querySelector('video').muted");
        await ReplaceVolumePlayer(core, "document.querySelector('[data-action=reload]').click()");
        await AssertPlayerVolume(core, 0.37);
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelector('video').muted"));
        await ReplaceVolumePlayer(core, "document.querySelector('.quality').value='720p';document.querySelector('.quality').dispatchEvent(new Event('change'))");
        await AssertPlayerVolume(core, 0.37);
        await CloseVolumePlayers(core);
        await OpenVolumePlayer(core);
        await AssertPlayerVolume(core, 0.37);
        Assert.Equal("false", await core.ExecuteScriptAsync("document.querySelector('video').muted"));

        // Separate channels and platforms have separate levels. Following switches the selected audio.
        workspace.PlayerChannel = "other";
        await OpenVolumePlayer(core, "other");
        await AssertPlayerVolume(core, 1, 1);
        await SetPlayerVolume(core, server, TestUsername, 0.64, "Twitch:other", 1);
        Assert.Equal("true", await core.ExecuteScriptAsync("document.querySelectorAll('video')[0].muted && document.querySelectorAll('video')[0].volume===0.37"));
        await core.ExecuteScriptAsync("document.querySelector('.followed-link').click()");
        await WaitBrowser(core, "!document.querySelectorAll('video')[0].muted && document.querySelectorAll('video')[1].muted");
        Assert.Equal(0.37, SavedVolume(server, TestUsername, "Twitch:streamer"));
        await CloseVolumePlayers(core);

        workspace.PlayerChannel = "streamer";
        workspace.PlayerPlatform = "Kick";
        await OpenVolumePlayer(core);
        await AssertPlayerVolume(core, 1);
        await SetPlayerVolume(core, server, TestUsername, 0.21, "Kick:streamer");
        await CloseVolumePlayers(core);

        // Use the server's resolved identity, not the pasted URL, display title, or player ID.
        workspace.PlayerPlatform = "Twitch";
        workspace.PlayerChannel = " StReAmEr ";
        await OpenVolumePlayer(core, "https://www.twitch.tv/videos/12345");
        await AssertPlayerVolume(core, 0.37);
        await SetPlayerVolume(core, server, TestUsername, 0, "Twitch:streamer");
        await CloseVolumePlayers(core);
        await OpenVolumePlayer(core);
        await AssertPlayerVolume(core, 0);

        // Page reload creates fresh application state and must restore actual persistent storage.
        core.Reload();
        await WaitBrowser(core, "document.readyState==='complete' && !document.querySelector('#studio')?.hidden && document.querySelectorAll('.card').length===1");
        await OpenVolumePlayer(core);
        await AssertPlayerVolume(core, 0);
        await CloseVolumePlayers(core);
        workspace.PlayerChannel = "other";
        await OpenVolumePlayer(core);
        await AssertPlayerVolume(core, 0.64);
        await CloseVolumePlayers(core);
        workspace.PlayerChannel = "streamer";
        workspace.PlayerPlatform = "Kick";
        await OpenVolumePlayer(core);
        await AssertPlayerVolume(core, 0.21);
        await CloseVolumePlayers(core);

        // Browser storage is not involved in preference persistence.
        workspace.PlayerChannel = "restricted";
        await core.ExecuteScriptAsync("Storage.prototype.getItem=()=>{throw new DOMException('Blocked','SecurityError')};Storage.prototype.setItem=()=>{throw new DOMException('Full','QuotaExceededError')}");
        await OpenVolumePlayer(core);
        await AssertPlayerVolume(core, 1);
        await core.ExecuteScriptAsync("document.querySelector('video').volume=0.48");
        await WaitBrowser(core, "document.querySelector('video').volume===0.48");
        await CloseVolumePlayers(core);
        await WaitSavedVolume(server, TestUsername, "Kick:restricted", 0.48);
        await OpenVolumePlayer(core);
        await AssertPlayerVolume(core, 0.48);
        await CloseVolumePlayers(core);

        // The same browser must not carry the first account's volume into another account.
        workspace.PlayerChannel = "streamer";
        workspace.PlayerPlatform = "Twitch";
        await core.ExecuteScriptAsync("document.querySelector('#logout').click()");
        await WaitBrowser(core, "document.querySelector('#login')?.hidden===false");
        await core.ExecuteScriptAsync("document.querySelector('#username').value='otherviewer';document.querySelector('#password').value='Other viewer password 123!';document.querySelector('#login-form').requestSubmit()");
        await WaitBrowser(core, "document.querySelector('#studio')?.hidden===false");
        await OpenVolumePlayer(core);
        await AssertPlayerVolume(core, 1);
        await SetPlayerVolume(core, server, "otherviewer", 0.72, "Twitch:streamer");
        await CloseVolumePlayers(core);
        await core.ExecuteScriptAsync("document.querySelector('#logout').click()");
        await WaitBrowser(core, "document.querySelector('#login')?.hidden===false");
        await core.ExecuteScriptAsync($"document.querySelector('#username').value={JsonSerializer.Serialize(TestUsername)};document.querySelector('#password').value={JsonSerializer.Serialize(TestPassword)};document.querySelector('#login-form').requestSubmit()");
        await WaitBrowser(core, "document.querySelector('#studio')?.hidden===false");
        await OpenVolumePlayer(core);
        await AssertPlayerVolume(core, 0);
        Assert.Equal("[]", await core.ExecuteScriptAsync("window.testErrors"));
    }

    private static async Task OpenVolumePlayer(CoreWebView2 core, string input = "streamer")
    {
        var count = int.Parse(await core.ExecuteScriptAsync("document.querySelectorAll('#players video').length"), CultureInfo.InvariantCulture);
        await core.ExecuteScriptAsync($"document.dispatchEvent(new KeyboardEvent('keydown',{{key:'k',ctrlKey:true,bubbles:true}}));document.querySelector('#open-query').value={JsonSerializer.Serialize(input)};document.querySelector('#open-form').requestSubmit()");
        await WaitBrowser(core, $"document.querySelectorAll('#players video').length==={count + 1} && !document.querySelector('#open-dialog').open && document.querySelectorAll('#players video')[{count}].currentTime>0.1");
    }

    private static async Task AssertPlayerVolume(CoreWebView2 core, double volume, int index = 0)
    {
        var expected = volume.ToString(CultureInfo.InvariantCulture);
        Assert.Equal("true", await core.ExecuteScriptAsync($"(()=>{{const v=document.querySelectorAll('#players video')[{index}];return v.volume==={expected} && v.firstPlayVolume==={expected}}})()"));
    }

    private static double? SavedVolume(LocalWebServer server, string username, string key)
    {
        var user = server.Users.ListUsers().Single(u => u.Username == username);
        return server.Users.PreferencesFor(user.Id).Volumes.TryGetValue(key, out var volume) ? volume : null;
    }

    private static async Task WaitSavedVolume(LocalWebServer server, string username, string key, double volume)
    {
        var time = Stopwatch.StartNew();
        while (SavedVolume(server, username, key) != volume)
        {
            if (time.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException($"Volume was not saved for {username}: {key}.");
            await Task.Delay(50);
        }
    }

    private static async Task SetPlayerVolume(CoreWebView2 core, LocalWebServer server, string username, double volume, string key, int index = 0)
    {
        var expected = volume.ToString(CultureInfo.InvariantCulture);
        await core.ExecuteScriptAsync($"document.querySelectorAll('#players video')[{index}].volume={expected}");
        await WaitSavedVolume(server, username, key, volume);
    }

    private static async Task ReplaceVolumePlayer(CoreWebView2 core, string action)
    {
        await core.ExecuteScriptAsync("window.previousVideo=document.querySelector('#players video');" + action);
        await WaitBrowser(core, "document.querySelectorAll('#players video').length===1 && document.querySelector('#players video')!==window.previousVideo && document.querySelector('#players video').currentTime>0.1");
    }

    private static async Task CloseVolumePlayers(CoreWebView2 core)
    {
        while (await core.ExecuteScriptAsync("document.querySelectorAll('#players video').length>0") == "true")
        {
            var count = int.Parse(await core.ExecuteScriptAsync("document.querySelectorAll('#players video').length"), CultureInfo.InvariantCulture);
            await core.ExecuteScriptAsync("document.querySelector('#players [data-action=close]').click()");
            await WaitBrowser(core, $"document.querySelectorAll('#players video').length==={count - 1}");
        }
    }
}

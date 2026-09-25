using System.Collections.ObjectModel;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.App.Wpf.Twitch;

internal static partial class TwitchChannelPointsTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Twitch bonuses follow open streams across pause stop restart and collection reset", LifecycleAsync),
        ("Twitch bonuses share duplicate channels and exclude Kick VOD and replay", ScopeAsync),
        ("Twitch bonuses stop on disable and observe replacement settings", SettingsAsync),
        ("Twitch bonuses cancel pending page creation when a tab closes", PendingPageAsync),
        ("Twitch bonuses cancel pending checks during shutdown", PendingCheckAsync),
        ("Twitch bonuses require website sign-in and close pages before sign-out", AccountAsync),
        ("Twitch bonuses close all chat pages when the website session expires", SessionExpiredAsync),
        ("Twitch bonuses recover failed page startup through bounded retry", RetryAsync),
        ("Twitch bonuses persist the toggle and default existing settings to enabled", SettingsRoundTripAsync),
        ("Twitch bonuses validate channel paths and website navigation", NavigationAsync),
        ("Twitch bonuses block streaming resources without blocking chat assets", StreamResourcesAsync),
        ("Twitch bonuses show independent sign-in status without open streams", SignInIndicatorAsync),
        ("Twitch bonuses count confirmed claims once per channel and persist across restart", ClaimCountsAsync),
        ("Twitch bonuses report failed history saves and retry without losing counts", ClaimSaveFailureAsync),
        ("Twitch bonuses reject unsuccessful mismatched and malformed claim responses", ClaimResponseAsync),
        ("Twitch bonuses normalize saved history and preserve existing settings", ClaimHistorySettingsAsync),
        ("Twitch bonuses settings bind sign-in and per-channel counts", IndicatorsUiAsync),
        .. (Environment.GetEnvironmentVariable("SVS_TEST_TWITCH_BONUS_BROWSER") == "true"
            ? new (string, Func<Task>)[]
            {
                ("Twitch bonuses real WebView2 clicks only available bonus controls", BrowserScriptAsync),
                ("Twitch bonuses real browser claims silently without opening a Twitch window", BrowserBackgroundAsync),
                ("Twitch bonuses real browser keeps claiming after inspection closes and owner minimizes", BrowserInspectionAsync),
                ("Twitch bonuses real browser blocks video and worker streaming requests before any claim check", BrowserMediaAsync),
                ("Twitch bonuses real browser confines navigation to the selected chat", BrowserNavigationAsync),
                ("Twitch bonuses real browser releases hidden hosts after canceled creation", BrowserCanceledCreationAsync),
                ("Twitch bonuses real browser records server confirmations and detects rejected sessions", BrowserClaimConfirmationAsync)
            }
            : []),
        .. (Environment.GetEnvironmentVariable("SVS_TEST_TWITCH_BONUS_LIVE") == "true"
            ? new (string, Func<Task>)[] { ("Twitch bonuses live public chat loads without a stream", BrowserLiveChatAsync) }
            : [])
    ];

    private static Task LifecycleAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new Fixture();
        var tab = fixture.Add("alpha");
        Assert.Equal(0, fixture.Browser.Pages.Count);
        await tab.StartAsync(fixture.Settings);
        await Eventually(() => fixture.Browser.Pages.Count == 1 && fixture.Browser.Pages[0].Checks > 0);
        var page = fixture.Browser.Pages[0];
        var checksBeforePause = page.Checks;
        await tab.PauseForTabSwitchAsync();
        await Eventually(() => page.Checks > checksBeforePause);
        Assert.Equal(1, fixture.Browser.Pages.Count);
        Assert.Equal(false, page.Disposed);
        await tab.ResumeFromTabSwitchAsync();
        // Live reconnect passes through Starting, so its previous companion is
        // closed and a new one starts once playback has actually resumed.
        await Eventually(() => fixture.Browser.Pages.Count > 1 && !fixture.Browser.Pages.Last().Disposed && fixture.Browser.Pages.Last().Checks > 0);
        page = fixture.Browser.Pages.Last();
        await tab.StopAsync();
        Assert.True(page.Disposed);
        var beforeRestart = fixture.Browser.Pages.Count;
        await tab.StartAsync(fixture.Settings);
        await Eventually(() => fixture.Browser.Pages.Count == beforeRestart + 1);
        fixture.Tabs.Clear();
        Assert.True(fixture.Browser.Pages.Last().Disposed);
        await tab.StopAsync();
        Assert.Equal(beforeRestart + 1, fixture.Browser.Pages.Count);
    });

    private static Task ScopeAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new Fixture();
        var first = fixture.Add("Alpha");
        var duplicate = fixture.Add("alpha");
        var other = fixture.Add("bravo");
        var kick = fixture.Add("kickone", PlatformKind.Kick);
        var vod = fixture.Add("vodone", kind: StreamTargetKind.TwitchVod);
        foreach (var tab in new[] { first, duplicate, other, kick, vod }) SetStatus(tab, PlaybackStatus.Playing);
        await Eventually(() => fixture.Browser.Pages.Count == 2);
        Assert.True(fixture.Browser.Pages.All(page => page.Channel is "alpha" or "bravo"));
        fixture.Tabs.Remove(first);
        Assert.True(fixture.Browser.Pages.All(page => !page.Disposed));
        fixture.Tabs.Remove(duplicate);
        Assert.True(fixture.Browser.Pages.Single(page => page.Channel == "alpha").Disposed);
        typeof(StreamTabViewModel).GetProperty(nameof(StreamTabViewModel.IsBehindLive))!.SetValue(other, true);
        Assert.True(fixture.Browser.Pages.Single(page => page.Channel == "bravo").Disposed);
    });

    private static Task SettingsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new Fixture();
        SetStatus(fixture.Add("alpha"), PlaybackStatus.Playing);
        await Eventually(() => fixture.Browser.Pages.Count == 1);
        fixture.Controller.Enabled = false;
        Assert.True(fixture.Browser.Pages[0].Disposed);
        var old = fixture.Settings.Chat;
        fixture.Settings.Chat = new ChatSettings { AutoClaimTwitchChannelPoints = true };
        await Eventually(() => fixture.Browser.Pages.Count == 2);
        old.AutoClaimTwitchChannelPoints = true;
        old.AutoClaimTwitchChannelPoints = false;
        Assert.Equal(false, fixture.Browser.Pages[1].Disposed);
        fixture.Settings.Chat.AutoClaimTwitchChannelPoints = false;
        Assert.True(fixture.Browser.Pages[1].Disposed);
    });

    private static Task PendingPageAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Browser.OpenGate = release.Task;
        var tab = fixture.Add("alpha");
        SetStatus(tab, PlaybackStatus.Playing);
        await Eventually(() => fixture.Browser.OpenAttempts == 1);
        fixture.Tabs.Remove(tab);
        Assert.True(fixture.Browser.LastOpenToken.IsCancellationRequested);
        release.SetResult();
        await Eventually(() => fixture.Browser.Pages.Count == 1 && fixture.Browser.Pages[0].Disposed);
        Assert.Equal(0, fixture.Browser.Pages[0].Checks);
    });

    private static Task PendingCheckAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Browser.CheckGate = release.Task;
        SetStatus(fixture.Add("alpha"), PlaybackStatus.Playing);
        await Eventually(() => fixture.Browser.Pages.Count == 1 && fixture.Browser.Pages[0].Checks == 1);
        var page = fixture.Browser.Pages[0];
        fixture.Controller.Dispose();
        Assert.True(page.Disposed);
        Assert.True(page.LastCheckToken.IsCancellationRequested);
        var status = fixture.Controller.Status;
        release.SetResult();
        await Task.Delay(40);
        Assert.Equal(status, fixture.Controller.Status);
        Assert.Equal(1, page.Checks);
    });

    private static Task AccountAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new Fixture(signedIn: false);
        SetStatus(fixture.Add("alpha"), PlaybackStatus.Playing);
        await Eventually(() => fixture.Controller.Status.StartsWith("Sign in", StringComparison.Ordinal));
        Assert.Equal(0, fixture.Browser.OpenAttempts);
        await fixture.Controller.SignInCommand.ExecuteAsync();
        await Eventually(() => fixture.Browser.Pages.Count == 1);
        await fixture.Controller.SignOutCommand.ExecuteAsync();
        Assert.True(fixture.Browser.Pages[0].Disposed);
        Assert.True(fixture.Browser.AllPagesClosedAtSignOut);
        Assert.Equal(false, fixture.Browser.SignedIn);
        await Task.Delay(40);
        Assert.Equal(1, fixture.Browser.OpenAttempts);
        await fixture.Controller.SignInCommand.ExecuteAsync();
        await Eventually(() => fixture.Browser.Pages.Count == 2);
    });

    private static Task RetryAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new Fixture();
        fixture.Browser.FailOpen = true;
        SetStatus(fixture.Add("alpha"), PlaybackStatus.Playing);
        await Eventually(() => fixture.Browser.OpenAttempts == 1);
        await Task.Delay(70);
        Assert.Equal(1, fixture.Browser.OpenAttempts);
        Assert.Contains("could not load", fixture.Controller.Status);
        fixture.Browser.FailOpen = false;
        await fixture.Controller.RetryCommand.ExecuteAsync();
        await Eventually(() => fixture.Browser.Pages.Count == 1 && fixture.Browser.Pages[0].Checks > 0);
    });

    private static Task SessionExpiredAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new Fixture();
        SetStatus(fixture.Add("alpha"), PlaybackStatus.Playing);
        SetStatus(fixture.Add("bravo"), PlaybackStatus.Playing);
        await Eventually(() => fixture.Browser.Pages.Count == 2);
        fixture.Browser.Pages[0].Expired = true;
        await Eventually(() => fixture.Browser.Pages.All(page => page.Disposed));
        Assert.Contains("session ended", fixture.Controller.Status);
        await Task.Delay(40);
        Assert.Equal(2, fixture.Browser.OpenAttempts);
    });

    private static async Task SettingsRoundTripAsync()
    {
        Assert.True(JsonSerializer.Deserialize<AppSettings>("{\"Chat\":{}}")!.Chat.AutoClaimTwitchChannelPoints);
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bonus-settings-{Guid.NewGuid():N}.json");
        try
        {
            var service = new JsonSettingsService(path);
            var settings = new AppSettings();
            settings.Chat.AutoClaimTwitchChannelPoints = false;
            await service.SaveAsync(settings);
            Assert.Equal(false, (await service.LoadAsync()).Chat.AutoClaimTwitchChannelPoints);
        }
        finally { System.IO.File.Delete(path); }
    }

    private static Task NavigationAsync()
    {
        foreach (var channel in new[] { "alpha", "abc_123", "a" }) Assert.True(TwitchBonusBrowser.IsChannelLogin(channel));
        foreach (var channel in new[] { "", "../login", "abc/def", "x?query", "x#hash", "x\"", new string('a', 26) })
            Assert.Equal(false, TwitchBonusBrowser.IsChannelLogin(channel));
        foreach (var url in new[] { "https://www.twitch.tv/alpha", "https://passport.twitch.tv/login", "about:blank" })
            Assert.True(TwitchBonusBrowser.IsAllowedNavigation(url));
        foreach (var url in new[] { "file:///C:/secret", "http://www.twitch.tv", "https://twitch.tv.evil.test", "https://www.twitch.tv:444/", "https://user@www.twitch.tv/", "javascript:alert(1)" })
            Assert.Equal(false, TwitchBonusBrowser.IsAllowedNavigation(url));
        Assert.Equal("https://www.twitch.tv/popout/alpha/chat?popout=", TwitchBonusBrowser.ChannelChatUrl("alpha"));
        foreach (var url in new[] { TwitchBonusBrowser.ChannelChatUrl("alpha"), "https://www.twitch.tv/popout/alpha/chat/", "about:blank" })
            Assert.True(TwitchBonusBrowser.IsAllowedChannelNavigation(url, "alpha"));
        foreach (var url in new[] { "https://www.twitch.tv/alpha", "https://www.twitch.tv/popout/bravo/chat", "https://www.twitch.tv/popout/alpha/chat/extra", "https://www.twitch.tv/videos/123", "https://player.twitch.tv/?channel=alpha", "https://www.twitch.tv:444/popout/alpha/chat", "https://user@www.twitch.tv/popout/alpha/chat", "https://www.twitch.tv.evil.test/popout/alpha/chat" })
            Assert.Equal(false, TwitchBonusBrowser.IsAllowedChannelNavigation(url, "alpha"));
        return Task.CompletedTask;
    }

    private static Task StreamResourcesAsync()
    {
        foreach (var url in new[] { "https://usher.ttvnw.net/api/channel/hls/alpha.m3u8", "https://video-weaver.example.hls.ttvnw.net/v1/segment/opaque", "https://player.twitch.tv/?channel=alpha", "https://cdn.example/stream.M3U8?token=fixture", "https://cdn.example/live.mpd", "https://cdn.example/chunk.ts", "https://cdn.example/chunk.m4s", "https://cdn.example/preview.mp4", "https://cdn.example/preview.webm", "https://cdn.example/audio.aac" })
            Assert.True(TwitchBonusBrowser.IsStreamResource(url, CoreWebView2WebResourceContext.Fetch));
        Assert.True(TwitchBonusBrowser.IsStreamResource("https://cdn.example/opaque", CoreWebView2WebResourceContext.Media));
        foreach (var url in new[] { TwitchBonusBrowser.ChannelChatUrl("alpha"), "https://gql.twitch.tv/gql", "https://static.twitchcdn.net/assets/chat.js", "https://static.twitchcdn.net/assets/chat.css", "https://static-cdn.jtvnw.net/emoticons/v2/example/default/dark/1.0", "https://example.test/icon.png?stream=alpha.m3u8", "https://notttvnw.net/chat.js", "https://ttvnw.net.example/chat.js" })
            Assert.Equal(false, TwitchBonusBrowser.IsStreamResource(url, CoreWebView2WebResourceContext.Fetch));
        return Task.CompletedTask;
    }

    // Executes the production script in actual Chromium, not a mocked DOM. All
    // twitch.tv requests are replaced with a local fixture; no account is used.
    private static Task BrowserScriptAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var profile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bonus-browser-test-{Guid.NewGuid():N}");
        using var host = new HwndSource(new HwndSourceParameters("Bonus test") { Width = 1280, Height = 800, WindowStyle = 0 });
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
        var controller = await environment.CreateCoreWebView2ControllerAsync(host.Handle);
        try
        {
            controller.IsVisible = false;
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1280, 800);
            var core = controller.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, args) =>
            {
                const string html = """
                    <!doctype html><html><body>
                    <button id="reward" onclick="window.spent++">Redeem a reward</button>
                    <button id="disabled" disabled onclick="window.spent++"><span class="claimable-bonus__icon"></span></button>
                    <button id="busy" aria-busy="true" onclick="window.spent++"><span class="claimable-bonus__icon"></span></button>
                    <button id="hidden" style="display:none" onclick="window.spent++"><span class="claimable-bonus__icon"></span></button>
                    <button id="destructive" class="ScCoreButtonDestructive" onclick="window.spent++"><span class="claimable-bonus__icon"></span></button>
                    <button id="bonus" onclick="window.claims++"><span class="claimable-bonus__icon"></span></button>
                    <script>window.claims=0; window.spent=0;</script>
                    </body></html>
                    """;
                args.Response = environment.CreateWebResourceResponse(
                    new System.IO.MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK", "Content-Type: text/html");
            };
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, args) => { if (args.IsSuccess) ready.TrySetResult(); };
            core.Navigate(TwitchBonusBrowser.ChannelChatUrl("alpha"));
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Contains("Bonus claim clicked", await core.ExecuteScriptAsync(TwitchBonusScript.Build("alpha")));
            await core.ExecuteScriptAsync(TwitchBonusScript.Build("alpha"));
            Assert.Equal("1", await core.ExecuteScriptAsync("window.claims"));
            Assert.Equal("0", await core.ExecuteScriptAsync("window.spent"));
            await core.ExecuteScriptAsync("document.getElementById('bonus').replaceWith(document.getElementById('bonus').cloneNode(true))");
            await core.ExecuteScriptAsync(TwitchBonusScript.Build("alpha"));
            Assert.Equal("1", await core.ExecuteScriptAsync("window.claims"));
            // Reusing a stale button is retried after the cooldown, not every tick.
            await core.ExecuteScriptAsync("window.__streamStudioBonusState.lastAttempt = performance.now()-61000");
            await core.ExecuteScriptAsync(TwitchBonusScript.Build("alpha"));
            Assert.Equal("2", await core.ExecuteScriptAsync("window.claims"));
            foreach (var path in new[] { "/popout/bravo/chat", "/alpha" })
            {
                await core.ExecuteScriptAsync($"history.replaceState(null,'',{JsonSerializer.Serialize(path)}); window.__streamStudioBonusState.lastAttempt = -Infinity");
                await core.ExecuteScriptAsync(TwitchBonusScript.Build("alpha"));
                Assert.Equal("2", await core.ExecuteScriptAsync("window.claims"));
            }
            // Twitch SPA navigation back permits a newly offered bonus.
            await core.ExecuteScriptAsync("history.replaceState(null,'','/popout/alpha/chat'); document.getElementById('bonus').replaceWith(document.getElementById('bonus').cloneNode(true))");
            await core.ExecuteScriptAsync(TwitchBonusScript.Build("alpha"));
            Assert.Equal("3", await core.ExecuteScriptAsync("window.claims"));
            Assert.Equal("0", await core.ExecuteScriptAsync("window.spent"));
            // Even if media appears in chat, claiming must never start playback.
            // Use real local media so this would fail with the old play() code.
            await core.ExecuteScriptAsync("""
                window.fixtureVideo = document.createElement('video');
                document.body.append(fixtureVideo);
                window.fixtureCanvas = document.createElement('canvas');
                fixtureCanvas.width = fixtureCanvas.height = 20;
                fixtureCanvas.getContext('2d').fillRect(0,0,20,20);
                fixtureVideo.srcObject = fixtureCanvas.captureStream(1);
                """);
            Assert.Contains("Checking chat for available bonuses", await core.ExecuteScriptAsync(TwitchBonusScript.Build("alpha")));
            Assert.Equal("true", await core.ExecuteScriptAsync("fixtureVideo.paused"));
            await core.ExecuteScriptAsync("fixtureVideo.srcObject.getTracks().forEach(track => track.stop())");
        }
        finally
        {
            await controller.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            controller.Close();
            // Chromium may retain profile file handles briefly after Close. The
            // uniquely named test profile is under the repository's TEMP folder.
        }
    });

    private static async Task Eventually(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("Bonus state did not converge.");
            await Task.Delay(10);
        }
    }

    private static void SetStatus(StreamTabViewModel tab, PlaybackStatus status) =>
        typeof(StreamTabViewModel).GetProperty(nameof(StreamTabViewModel.Status))!.SetValue(tab, status);

    private sealed class Fixture : IAsyncDisposable
    {
        internal AppSettings Settings { get; }
        internal ObservableCollection<StreamTabViewModel> Tabs { get; } = [];
        internal FakeBrowser Browser { get; } = new();
        internal TwitchChannelPointsController Controller { get; }
        private readonly List<StreamTabViewModel> ownedTabs = [];
        internal Fixture(bool signedIn = true, AppSettings? settings = null, ISettingsService? settingsService = null,
            Task? sessionGate = null)
        {
            Settings = settings ?? new();
            Settings.StreamlinkPath = "streamlink.exe";
            Settings.VlcDirectory = @"C:\Program Files\VideoLAN\VLC";
            Settings.Chat.ConnectAutomatically = false;
            Settings.Chat.Layout = ChatLayout.Docked;
            Browser.SignedIn = signedIn;
            Browser.SessionGate = sessionGate;
            Controller = new(Settings, Tabs, () => Tabs.FirstOrDefault(), Browser, new MemoryLogger(), TimeSpan.FromMilliseconds(10), settingsService);
        }
        internal StreamTabViewModel Add(string channel, PlatformKind platform = PlatformKind.Twitch, StreamTargetKind kind = StreamTargetKind.Live)
        {
            var tab = TestViewModels.CreateTab(new StreamTarget(platform, channel, $"https://www.twitch.tv/{channel}", kind),
                "best", new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
                new MemoryLogger(), action => action());
            tab.SetVideoHandle(new IntPtr(1234));
            ownedTabs.Add(tab);
            Tabs.Add(tab);
            return tab;
        }
        public async ValueTask DisposeAsync()
        {
            Controller.Dispose();
            foreach (var tab in ownedTabs) await tab.DisposeAsync();
        }
    }

    private sealed class FakeBrowser : ITwitchBonusBrowser
    {
        internal bool SignedIn = true, FailOpen, FailSession, AllPagesClosedAtSignOut;
        internal int OpenAttempts;
        internal Task? OpenGate, CheckGate, SessionGate;
        internal CancellationToken LastOpenToken;
        internal List<FakePage> Pages { get; } = [];
        public async Task<bool> HasSessionAsync(CancellationToken token)
        {
            if (SessionGate is not null) await SessionGate.WaitAsync(token);
            if (FailSession) throw new InvalidOperationException("test session failure");
            return SignedIn;
        }
        public Task SignInAsync(CancellationToken token) { SignedIn = true; return Task.CompletedTask; }
        public Task SignOutAsync(CancellationToken token)
        {
            AllPagesClosedAtSignOut = Pages.All(page => page.Disposed);
            SignedIn = false;
            return Task.CompletedTask;
        }
        public async Task<ITwitchBonusPage> OpenChannelAsync(string channel, CancellationToken token)
        {
            OpenAttempts++;
            LastOpenToken = token;
            if (FailOpen) throw new InvalidOperationException("test failure");
            if (OpenGate is not null) await OpenGate;
            var page = new FakePage(channel, CheckGate);
            Pages.Add(page);
            return page;
        }
        public void Dispose() { foreach (var page in Pages) page.Dispose(); }
    }

    private sealed class FakePage(string channel, Task? checkGate) : ITwitchBonusPage
    {
        public event EventHandler<string>? ClaimConfirmed;
        internal void Confirm(string id) => ClaimConfirmed?.Invoke(this, id);
        internal string Channel => channel;
        internal int Checks;
        internal bool Disposed, Expired;
        internal CancellationToken LastCheckToken;
        internal string Result = "Checking chat for available bonuses (no background video).";
        public async Task<string> CheckAsync(CancellationToken token)
        {
            Assert.Equal(false, Disposed);
            if (Expired) throw new TwitchBonusSessionExpiredException();
            Checks++;
            LastCheckToken = token;
            if (checkGate is not null) await checkGate;
            token.ThrowIfCancellationRequested();
            return Result;
        }
        public void Show() { }
        public void Dispose() => Disposed = true;
    }
}

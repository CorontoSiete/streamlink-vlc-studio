using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.App.Wpf.Twitch;

internal static partial class TwitchChannelPointsTestCatalog
{
    private static Task BrowserClaimConfirmationAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        using var fixture = await NativeBrowserFixture.CreateAsync();
        using var page = await fixture.Browser.OpenChannelAsync("alpha", CancellationToken.None);
        var core = fixture.Controllers.Single().CoreWebView2;
        var confirmed = new List<string>();
        page.ClaimConfirmed += (_, id) => confirmed.Add(id);
        await WaitForBrowserAsync(core, "!!document.getElementById('bonus')");
        await core.ExecuteScriptAsync($$"""
            window.fixtureRequest = {{ClaimRequest("claim-one")}};
            document.getElementById('bonus').onclick = () => {
                window.responseDone = false;
                fetch('https://gql.twitch.tv/gql', {
                    method: 'POST', body: JSON.stringify(window.fixtureRequest)
                }).then(response => response.text()).then(() => window.responseDone = true);
            };
            """);
        fixture.GraphQlResponse = ClaimSuccess("claim-one");
        Assert.Contains("Bonus claim clicked", await page.CheckAsync(CancellationToken.None));
        await WaitForBrowserAsync(core, "window.responseDone === true");
        await Eventually(() => confirmed.Count == 1);
        Assert.Equal("claim-one", confirmed[0]);

        // A real 200 transport response with a GraphQL error is not a claim.
        fixture.GraphQlResponse = ClaimSuccess("claim-one").Replace("\"error\":null", "\"error\":{\"code\":\"NOT_FOUND\"}");
        await ClickAgainAsync();
        Assert.Equal(1, confirmed.Count);
        fixture.GraphQlResponse = ClaimSuccess("mismatched-id");
        await ClickAgainAsync();
        Assert.Equal(1, confirmed.Count);
        fixture.GraphQlStatusCode = 500;
        fixture.GraphQlResponse = ClaimSuccess("claim-one");
        await ClickAgainAsync();
        Assert.Equal(1, confirmed.Count);

        // Twitch's batched transport must match each response to its request.
        fixture.GraphQlStatusCode = 200;
        await core.ExecuteScriptAsync($"window.fixtureRequest = [{ClaimRequest("two")},{ClaimRequest("three")}]");
        fixture.GraphQlResponse = $"[{ClaimSuccess("two")},{ClaimSuccess("three")}]";
        await ClickAgainAsync();
        await Eventually(() => confirmed.Count == 3);
        Assert.True(confirmed.Contains("two") && confirmed.Contains("three"));

        // A revoked session can leave its cookie behind; HTTP 401 must still stop claims.
        fixture.GraphQlStatusCode = 401;
        fixture.GraphQlResponse = "{\"errors\":[{\"message\":\"Unauthorized\"}]}";
        await ClickAgainAsync();
        Assert.True((await core.CookieManager.GetCookiesAsync("https://www.twitch.tv/")).Any(cookie => cookie.Name == "auth-token"));
        try
        {
            await page.CheckAsync(CancellationToken.None);
            throw new InvalidOperationException("A rejected Twitch session was accepted.");
        }
        catch (TwitchBonusSessionExpiredException) { }
        Assert.Equal(3, confirmed.Count);

        async Task ClickAgainAsync()
        {
            await core.ExecuteScriptAsync("window.__streamStudioBonusState.lastAttempt = -Infinity");
            await page.CheckAsync(CancellationToken.None);
            await WaitForBrowserAsync(core, "window.responseDone === true");
            await Task.Delay(50);
        }
    });

    private static Task BrowserBackgroundAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        using var fixture = await NativeBrowserFixture.CreateAsync();
        var foreground = NativeWindowTest.GetForegroundWindow();
        using var page = await fixture.Browser.OpenChannelAsync("alpha", CancellationToken.None);
        var controller = fixture.Controllers.Single();
        var core = controller.CoreWebView2;
        await WaitForBrowserAsync(core, "window.fixtureReady === true");
        Assert.Equal(TwitchBonusBrowser.ChannelChatUrl("alpha"), core.Source);

        // The old host reports hidden here, preventing animation-driven page
        // initialization. A static pre-rendered button cannot catch this bug.
        Assert.Equal("\"visible\"", await core.ExecuteScriptAsync("document.visibilityState"));
        Assert.Equal(false, NativeWindowTest.IsWindowVisible(controller.ParentWindow));
        Assert.Equal(false, NativeWindowTest.IsWindowVisible(new WindowInteropHelper(fixture.Owner).Handle));
        Assert.Equal(0, fixture.Owner.OwnedWindows.Count);
        Assert.Equal(foreground, NativeWindowTest.GetForegroundWindow());
        Assert.True(core.IsMuted);

        await WaitForBrowserAsync(core, "window.framesRun > 2 && !!document.getElementById('bonus')");
        Assert.Contains("Bonus claim clicked", await page.CheckAsync(CancellationToken.None));
        Assert.Equal("1", await core.ExecuteScriptAsync("window.claims"));
        Assert.Equal("0", await core.ExecuteScriptAsync("document.querySelectorAll('video, audio').length"));
        Assert.Contains("Checking chat", await page.CheckAsync(CancellationToken.None));
        var backgroundHost = controller.ParentWindow;
        page.Dispose();
        Assert.Equal(false, NativeWindowTest.IsWindow(backgroundHost));
    });

    private static Task BrowserInspectionAsync() => TestSta.RunAsync(async () =>
    {
        using var fixture = await NativeBrowserFixture.CreateAsync();
        fixture.Owner.Show();
        using var page = await fixture.Browser.OpenChannelAsync("alpha", CancellationToken.None);
        var controller = fixture.Controllers.Single();
        var core = controller.CoreWebView2;
        var backgroundHost = controller.ParentWindow;
        await WaitForBrowserAsync(core, "!!document.getElementById('bonus')");
        await page.CheckAsync(CancellationToken.None);

        page.Show();
        var inspection = fixture.Owner.OwnedWindows.Cast<Window>().Single();
        Assert.Equal(new WindowInteropHelper(inspection).Handle, controller.ParentWindow);
        inspection.Close();
        Assert.Equal(backgroundHost, controller.ParentWindow);
        Assert.Equal(false, NativeWindowTest.IsWindowVisible(backgroundHost));
        Assert.Equal(0, fixture.Owner.OwnedWindows.Count);

        fixture.Owner.WindowState = WindowState.Minimized;
        await core.ExecuteScriptAsync("window.framesRun = 0; document.getElementById('bonus').remove(); window.__streamStudioBonusState.lastAttempt = -Infinity");
        await WaitForBrowserAsync(core, "window.framesRun > 2 && !!document.getElementById('bonus')");
        Assert.Equal("\"visible\"", await core.ExecuteScriptAsync("document.visibilityState"));
        Assert.Contains("Bonus claim clicked", await page.CheckAsync(CancellationToken.None));
        Assert.Equal("2", await core.ExecuteScriptAsync("window.claims"));
        Assert.True(core.IsMuted);
        fixture.Owner.Hide();
        await core.ExecuteScriptAsync("window.framesRun = 0");
        await WaitForBrowserAsync(core, "window.framesRun > 2");
        Assert.Equal("0", await core.ExecuteScriptAsync("document.querySelectorAll('video, audio').length"));

        // Disposal while the inspection window is open must close both hosts.
        fixture.Owner.WindowState = WindowState.Normal;
        page.Show();
        inspection = fixture.Owner.OwnedWindows.Cast<Window>().Single();
        var inspectionHost = new WindowInteropHelper(inspection).Handle;
        fixture.Browser.Dispose();
        Assert.Equal(false, NativeWindowTest.IsWindow(backgroundHost));
        Assert.Equal(false, NativeWindowTest.IsWindow(inspectionHost));
    });

    private static Task BrowserCanceledCreationAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        using var fixture = await NativeBrowserFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.CreationGate = release.Task;
        var opening = fixture.Browser.OpenChannelAsync("alpha", cancellation.Token);
        await Eventually(() => fixture.Controllers.Count == 1);
        var backgroundHost = fixture.Controllers.Single().ParentWindow;
        cancellation.Cancel();
        try
        {
            await opening;
            throw new InvalidOperationException("Expected page creation to be canceled.");
        }
        catch (OperationCanceledException) { }
        finally { release.TrySetResult(); }
        await Eventually(() => !NativeWindowTest.IsWindow(backgroundHost));
    });

    private static Task BrowserMediaAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        using var fixture = await NativeBrowserFixture.CreateAsync();
        fixture.ProbeMedia = true;
        using var page = await fixture.Browser.OpenChannelAsync("alpha", CancellationToken.None);
        var core = fixture.Controllers.Single().CoreWebView2;
        // Probes run during page startup, before CheckAsync can run. The fixture
        // transport supplies every response locally; no request reaches Twitch.
        await WaitForBrowserAsync(core, "window.probesDone === true && window.workerStatus !== undefined && !!probeVideo.error");
        foreach (var uri in new[] { "https://www.twitch.tv/unexpected-preview", "https://www.twitch.tv/playlist.m3u8", "https://www.twitch.tv/worker-segment.m4s", "https://usher.ttvnw.net/api/channel/hls/alpha.m3u8", "https://video-weaver.test.hls.ttvnw.net/opaque-segment", "https://player.twitch.tv/?channel=alpha" })
            Assert.True(fixture.BlockedRequests.Contains(uri));
        // WebView2 does not report worker/CORS-rejected responses through the
        // document's ResponseReceived event. Verify the worker's actual status
        // as well as the host's final pre-network decision for those requests.
        Assert.Equal("403", await core.ExecuteScriptAsync("window.workerStatus"));
        Assert.Equal("403", await core.ExecuteScriptAsync("window.playlistStatus"));
        await Eventually(() => fixture.Responses.Any(response => response.Uri == "https://www.twitch.tv/unexpected-preview" && response.Status == 403));
        Assert.Equal("200", await core.ExecuteScriptAsync("window.chatApiStatus"));
        Assert.Equal("0", await core.ExecuteScriptAsync("probeVideo.readyState"));
        Assert.Equal("0", await core.ExecuteScriptAsync("probeVideo.currentTime"));
        await WaitForBrowserAsync(core, "!!document.getElementById('bonus')");
        Assert.Contains("Bonus claim clicked", await page.CheckAsync(CancellationToken.None));
        Assert.Equal("1", await core.ExecuteScriptAsync("window.claims"));
    });

    private static Task BrowserNavigationAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        using var fixture = await NativeBrowserFixture.CreateAsync();
        using var page = await fixture.Browser.OpenChannelAsync("alpha", CancellationToken.None);
        var core = fixture.Controllers.Single().CoreWebView2;
        await WaitForBrowserAsync(core, "window.fixtureReady === true");
        var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        core.NavigationStarting += (_, args) =>
        {
            if (args.Uri == "https://www.twitch.tv/alpha") blocked.TrySetResult(args.Cancel);
        };
        core.Navigate("https://www.twitch.tv/alpha");
        Assert.True(await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(TwitchBonusBrowser.ChannelChatUrl("alpha"), core.Source);

        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess && core.Source == TwitchBonusBrowser.ChannelChatUrl("alpha")) returned.TrySetResult();
        };
        // A raid/SPA route must be sent back to chat, never the full channel.
        await core.ExecuteScriptAsync("history.pushState(null, '', '/bravo')");
        await returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TwitchBonusBrowser.ChannelChatUrl("alpha"), core.Source);
        await WaitForBrowserAsync(core, "!!document.getElementById('bonus')");
        Assert.Contains("Bonus claim clicked", await page.CheckAsync(CancellationToken.None));
    });

    private static async Task WaitForBrowserAsync(CoreWebView2 core, string predicate)
    {
        var deadline = Stopwatch.StartNew();
        while (await core.ExecuteScriptAsync(predicate) != "true")
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException($"Browser did not reach: {predicate}");
            await Task.Delay(25);
        }
    }

    private sealed class NativeBrowserFixture : IDisposable
    {
        private readonly CoreWebView2Environment environment;
        internal Window Owner { get; } = new()
        {
            Width = 1280,
            Height = 850,
            Left = -16000,
            Top = -16000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        internal TwitchBonusBrowser Browser { get; }
        internal List<CoreWebView2Controller> Controllers { get; } = [];
        internal List<(string Uri, int Status)> Responses { get; } = [];
        internal HashSet<string> BlockedRequests { get; } = new(StringComparer.Ordinal);
        internal bool ProbeMedia { get; set; }
        internal string GraphQlResponse { get; set; } = "{}";
        internal int GraphQlStatusCode { get; set; } = 200;
        internal Task? CreationGate { get; set; }

        private NativeBrowserFixture(CoreWebView2Environment environment)
        {
            this.environment = environment;
            new WindowInteropHelper(Owner).EnsureHandle();
            Browser = new TwitchBonusBrowser(Owner, CreateControllerAsync);
        }

        internal static async Task<NativeBrowserFixture> CreateAsync()
        {
            var profile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bonus-background-test-{Guid.NewGuid():N}");
            return new NativeBrowserFixture(await CoreWebView2Environment.CreateAsync(userDataFolder: profile));
        }

        private async Task<CoreWebView2Controller> CreateControllerAsync(nint parent)
        {
            var controller = await environment.CreateCoreWebView2ControllerAsync(parent);
            var core = controller.CoreWebView2;
            // Synthetic cookie in a unique test profile; all network requests
            // are intercepted below, so it is never sent to Twitch.
            core.CookieManager.AddOrUpdateCookie(core.CookieManager.CreateCookie("auth-token", "fixture", ".twitch.tv", "/"));
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, args) =>
            {
                var isGraphQl = TwitchBonusClaimResponse.IsGraphQlEndpoint(args.Request.Uri);
                args.Response = environment.CreateWebResourceResponse(
                    new System.IO.MemoryStream(Encoding.UTF8.GetBytes(isGraphQl ? GraphQlResponse : Html + (ProbeMedia ? MediaProbes : ""))),
                    isGraphQl ? GraphQlStatusCode : 200, "Fixture response",
                    "Content-Type: " + (isGraphQl ? "application/json" : "text/html") + "\r\nAccess-Control-Allow-Origin: *");
            };
            core.WebResourceResponseReceived += (_, args) => Responses.Add((args.Request.Uri, args.Response.StatusCode));
            // Register the observer after production configuration, but before
            // the first document request, so it sees the final response choice.
            void ObserveRequests(object? sender, CoreWebView2NavigationStartingEventArgs args)
            {
                core.NavigationStarting -= ObserveRequests;
                core.WebResourceRequested += (_, request) =>
                {
                    if (request.Response?.StatusCode == 403) BlockedRequests.Add(request.Request.Uri);
                };
            }
            core.NavigationStarting += ObserveRequests;
            Controllers.Add(controller);
            if (CreationGate is not null) await CreationGate;
            return controller;
        }

        public void Dispose()
        {
            Browser.Dispose();
            Owner.Close();
        }

        private const string Html = """
            <!doctype html><html><body>
            <script>
                window.claims = 0;
                window.framesRun = 0;
                function render() {
                    window.framesRun++;
                    if (!document.getElementById('bonus')) {
                        const button = document.createElement('button');
                        button.id = 'bonus';
                        button.innerHTML = '<span class="claimable-bonus__icon"></span>';
                        button.onclick = () => window.claims++;
                        document.body.append(button);
                    }
                    requestAnimationFrame(render);
                }
                requestAnimationFrame(render);
                window.fixtureReady = true;
            </script></body></html>
            """;

        private const string MediaProbes = """
            <video id="probeVideo" autoplay muted preload="auto" src="/unexpected-preview"></video>
            <iframe src="https://player.twitch.tv/?channel=alpha"></iframe>
            <script>
                Promise.all([
                    fetch('/playlist.m3u8').then(response => window.playlistStatus = response.status),
                    fetch('https://usher.ttvnw.net/api/channel/hls/alpha.m3u8').catch(() => {}),
                    fetch('https://video-weaver.test.hls.ttvnw.net/opaque-segment').catch(() => {}),
                    fetch('/gql').then(response => window.chatApiStatus = response.status)
                ]).then(() => window.probesDone = true);
                const workerUrl = URL.createObjectURL(new Blob([
                    "fetch('https://www.twitch.tv/worker-segment.m4s').then(response => postMessage(response.status))"
                ], { type: 'text/javascript' }));
                const worker = new Worker(workerUrl);
                worker.onmessage = event => {
                    window.workerStatus = event.data;
                    worker.terminate();
                    URL.revokeObjectURL(workerUrl);
                };
            </script>
            """;
    }
}

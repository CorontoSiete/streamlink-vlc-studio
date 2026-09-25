using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.App.Wpf.Twitch;

internal static partial class TwitchChannelPointsTestCatalog
{
    // Explicit opt-in: loads Twitch's real public chat using an empty, isolated
    // profile. Never uses the user's account, sends messages, or claims a bonus.
    private static Task BrowserLiveChatAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var profile = Path.Combine(Path.GetTempPath(), $"bonus-live-chat-test-{Guid.NewGuid():N}");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
        var owner = new Window { ShowActivated = false, ShowInTaskbar = false };
        new WindowInteropHelper(owner).EnsureHandle();
        CoreWebView2? core = null;
        var streamRequests = new HashSet<string>(StringComparer.Ordinal);
        var blockedRequests = new HashSet<string>(StringComparer.Ordinal);
        var responses = new List<(string Uri, int Status)>();
        using var browser = new TwitchBonusBrowser(owner, async parent =>
        {
            var controller = await environment.CreateCoreWebView2ControllerAsync(parent);
            core = controller.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (TwitchBonusBrowser.IsStreamResource(args.Request.Uri, args.ResourceContext))
                    streamRequests.Add(args.Request.Uri);
            };
            core.WebResourceResponseReceived += (_, args) => responses.Add((args.Request.Uri, args.Response.StatusCode));
            void ObserveRequests(object? sender, CoreWebView2NavigationStartingEventArgs args)
            {
                core.NavigationStarting -= ObserveRequests;
                core.WebResourceRequested += (_, request) =>
                {
                    if (request.Response?.StatusCode == 403) blockedRequests.Add(request.Request.Uri);
                };
            }
            core.NavigationStarting += ObserveRequests;
            return controller;
        });
        try
        {
            using var page = await browser.OpenChannelAsync("twitchdev", CancellationToken.None);
            var deadline = Stopwatch.StartNew();
            while (await core!.ExecuteScriptAsync("!!document.querySelector('[data-a-target=chat-input]')") != "true")
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(30))
                    throw new InvalidOperationException("Twitch's live public chat did not load within 30 seconds.");
                await Task.Delay(100);
            }
            // Observe the mounted chat after initial scripts/requests settle.
            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.Equal(TwitchBonusBrowser.ChannelChatUrl("twitchdev"), core.Source);
            Assert.Equal("0", await core.ExecuteScriptAsync("document.querySelectorAll('video, audio').length"));
            Assert.True(responses.Any(response => response.Uri == core.Source && response.Status == 200));
            Assert.True(streamRequests.IsSubsetOf(blockedRequests));
            Assert.True(responses.Where(response => streamRequests.Contains(response.Uri)).All(response => response.Status == 403));
            Console.WriteLine($"Live Twitch chat loaded: 0 media elements; {streamRequests.Count} stream requests attempted; no successful stream responses.");
        }
        finally
        {
            browser.Dispose();
            owner.Close();
        }
    });
}

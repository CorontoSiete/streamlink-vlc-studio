using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

internal static partial class CredentialSecurityTestCatalog
{
    private static Task OAuthBrowserRelayAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var profile = Path.Combine(Path.GetTempPath(), "StreamStudioTests", "oauth-browser-" + Guid.NewGuid().ToString("N"));
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
        var window = new Window { ShowActivated = false, ShowInTaskbar = false };
        var controller = await environment.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(window).EnsureHandle());
        var streams = new List<MemoryStream>();
        try
        {
            var core = controller.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = new List<string>();
            core.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess) received.TrySetException(new InvalidOperationException($"OAuth fixture navigation failed: {args.WebErrorStatus}"));
            };
            core.ProcessFailed += (_, args) => received.TrySetException(
                new InvalidOperationException($"OAuth fixture browser process failed: {args.ProcessFailedKind}"));
            var nonce = OAuthTokenHelpers.CreateBase64UrlSecret(32);
            core.WebResourceRequested += (_, args) =>
            {
                try
                {
                    requests.Add(args.Request.Method + " " + args.Request.Uri);
                    var uri = new Uri(args.Request.Uri);
                    Assert.Equal("oauth-relay.example", uri.Host);
                    Assert.Equal("", uri.Query);
                    string html;
                    string headers;
                    if (uri.AbsolutePath == "/")
                    {
                        Assert.Equal("GET", args.Request.Method);
                        html = TwitchOAuthService.BuildFragmentCaptureHtml(nonce) +
                            "<script>window.untrustedScriptRan = true;</script>";
                        headers = "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n" +
                            "Referrer-Policy: no-referrer\r\nContent-Security-Policy: " +
                            LoopbackOAuthReceiver.GetContentSecurityPolicy(nonce);
                    }
                    else
                    {
                        Assert.Equal("/twitch-oauth-token", uri.AbsolutePath);
                        Assert.Equal("POST", args.Request.Method);
                        Assert.Equal(false, args.Request.Headers.Contains("Referer"));
                        Assert.Contains("application/x-www-form-urlencoded", args.Request.Headers.GetHeader("Content-Type"));
                        using var reader = new StreamReader(args.Request.Content, Encoding.UTF8, leaveOpen: true);
                        Assert.True(OAuthTokenHelpers.TryParseQueryString(reader.ReadToEnd(), out var values));
                        Assert.Equal("expected-state", values["state"]);
                        Assert.Equal("private-browser-token", values["access_token"]);
                        html = "<!doctype html><p>Twitch authorization finished. You can close this window.</p>";
                        headers = "Content-Type: text/html; charset=utf-8\r\nContent-Security-Policy: " +
                            LoopbackOAuthReceiver.GetContentSecurityPolicy();
                        received.TrySetResult();
                    }
                    var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));
                    streams.Add(stream);
                    args.Response = environment.CreateWebResourceResponse(stream, 200, "OK", headers);
                }
                catch (Exception ex)
                {
                    received.TrySetException(ex);
                    args.Response = environment.CreateWebResourceResponse(null, 400, "Invalid test request", "");
                }
            };
            core.Navigate("https://oauth-relay.example/#state=expected-state&access_token=private-browser-token");
            try { await received.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException ex)
            {
                throw new InvalidOperationException($"OAuth fixture received no POST. Requests: {string.Join(", ", requests)}", ex);
            }
            var ready = Stopwatch.StartNew();
            while (await core.ExecuteScriptAsync("document.getElementById('status')?.textContent") !=
                JsonSerializer.Serialize("Twitch authorization finished. You can close this window."))
            {
                if (ready.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The browser did not display the OAuth result.");
                await Task.Delay(25);
            }
            Assert.Equal("\"https://oauth-relay.example/\"", await core.ExecuteScriptAsync("location.href"));
            Assert.Equal("false", await core.ExecuteScriptAsync("window.untrustedScriptRan === true"));
        }
        finally
        {
            controller.Close();
            window.Close();
            foreach (var stream in streams) stream.Dispose();
        }
    });
}

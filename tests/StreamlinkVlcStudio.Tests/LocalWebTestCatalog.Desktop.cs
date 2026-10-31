using System.Net.Http.Json;
using System.Windows.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.App.Wpf.Themes;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    // Exercise the real request handler independently of Windows' persisted TLS-key support.
    private static async Task LoginPipelineAsync()
    {
        using var key = RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Pipeline fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var workspaces = new List<TestWorkspace>();
        await using var server = await Server(certificate, () => { var workspace = new TestWorkspace(); workspaces.Add(workspace); return workspace; });
        var handler = typeof(LocalWebServer).GetMethod("HandleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        async Task<DefaultHttpContext> Send(string path, object body, string? cookie = null, string? csrf = null,
            string? origin = null, string? host = null, string? peer = null, string scheme = "https", string query = "", string method = "POST")
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Scheme = scheme;
            context.Request.Host = new HostString(host ?? server.Address.Authority);
            context.Request.Path = path;
            context.Request.QueryString = new QueryString(query);
            if (method == "POST") context.Request.ContentType = "application/json";
            context.Request.Headers.Origin = origin ?? server.Address.GetLeftPart(UriPartial.Authority);
            context.Request.Headers["X-StreamStudio"] = "1";
            if (cookie is not null) context.Request.Headers.Cookie = cookie;
            if (csrf is not null) context.Request.Headers["X-CSRF-Token"] = csrf;
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer ?? "127.0.0.1");
            context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(body));
            context.Response.Body = new MemoryStream();
            await (Task)handler.Invoke(server, [context])!;
            return context;
        }
        var credentials = new { username = TestUsername, password = TestPassword };
        Assert.Equal(403, (await Send("/api/login", credentials, origin: "https://attacker.example")).Response.StatusCode);
        Assert.Equal(403, (await Send("/api/login", credentials, host: "attacker.example")).Response.StatusCode);
        Assert.Equal(403, (await Send("/api/login", credentials, peer: "127.0.0.2")).Response.StatusCode);
        Assert.Equal(403, (await Send("/api/login", credentials, scheme: "http")).Response.StatusCode);
        Assert.Equal(403, (await Send("/api/login", credentials, query: "?password=" + TestPassword)).Response.StatusCode);
        Assert.Equal(401, (await Send("/api/login", new { username = TestUsername, password = "wrong password" })).Response.StatusCode);
        Assert.Equal(401, (await Send("/api/desktop-login", new { token = "legacy" })).Response.StatusCode);
        var login = await Send("/api/login", credentials);
        Assert.Equal(200, login.Response.StatusCode);
        var cookieHeader = login.Response.Headers.SetCookie.ToString();
        foreach (var flag in new[] { "__host-streamstudio=", "secure", "httponly", "samesite=strict", "path=/" })
            Assert.Contains(flag, cookieHeader.ToLowerInvariant());
        var cookie = cookieHeader.Split(';')[0];
        login.Response.Body.Position = 0;
        var result = await JsonSerializer.DeserializeAsync<JsonElement>(login.Response.Body);
        var csrf = result.GetProperty("csrf").GetString()!;
        Assert.Equal(TestUsername, result.GetProperty("username").GetString());
        Assert.Equal(false, result.ToString().Contains(TestPassword, StringComparison.Ordinal));
        Assert.Equal(403, (await Send("/api/settings", new { }, cookie)).Response.StatusCode);
        Assert.Equal(200, (await Send("/api/settings", new { }, cookie, csrf)).Response.StatusCode);
        Assert.Equal(200, (await Send("/api/session", new { }, cookie, method: "GET")).Response.StatusCode);
        await server.DisconnectAllAsync();
        Assert.True(workspaces.Single().Disposed);
        Assert.Equal(401, (await Send("/api/settings", new { }, cookie, csrf)).Response.StatusCode);
        var secondLogin = await Send("/api/login", credentials);
        Assert.Equal(200, secondLogin.Response.StatusCode);
        var secondCookie = secondLogin.Response.Headers.SetCookie.ToString().Split(';')[0];
        secondLogin.Response.Body.Position = 0;
        var secondResult = await JsonSerializer.DeserializeAsync<JsonElement>(secondLogin.Response.Body);
        var secondCsrf = secondResult.GetProperty("csrf").GetString()!;
        var user = server.Users.ListUsers().Single();
        await server.Users.ChangePasswordAsync(user.Id, "Changed web password 456!");
        Assert.Equal(401, (await Send("/api/settings", new { }, secondCookie, secondCsrf)).Response.StatusCode);
        await server.DisconnectUserAsync(user.Id);
        Assert.True(workspaces.Last().Disposed);
        Assert.Equal(401, (await Send("/api/login", credentials)).Response.StatusCode);
        Assert.Equal(200, (await Send("/api/login", new { username = TestUsername, password = "Changed web password 456!" })).Response.StatusCode);
    }

    private static async Task DesktopLaunchAsync()
    {
        using var certificate = Certificate();
        await using var server = await Server(certificate);
        ProcessStartInfo? launch = null;
        MainWindow.OpenLocalWebBrowser(server, info => launch = info);
        Assert.True(launch!.UseShellExecute);
        Assert.Equal(server.Address.AbsoluteUri, launch.FileName);
        var address = new Uri(launch.FileName);
        Assert.Equal("", address.Query);
        Assert.Equal("", address.Fragment);
        using var http = Client(certificate, server.Address);
        using (var anonymous = await http.GetAsync("/api/session")) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await Login(http);
        var failed = false;
        try { MainWindow.OpenLocalWebBrowser(server, _ => throw new InvalidOperationException("No browser registered.")); }
        catch (InvalidOperationException) { failed = true; }
        Assert.True(failed);
    }

    private static async Task CheckDesktopBrowserSignIn(CoreWebView2 core, LocalWebServer server)
    {
        Uri? launched = null;
        MainWindow.OpenLocalWebBrowser(server, info => { launched = new Uri(info.FileName); core.Navigate(info.FileName); });
        await WaitBrowser(core, "document.readyState==='complete' && document.querySelector('#login')?.hidden===false");
        Assert.Equal("true", await core.ExecuteScriptAsync("location.hash==='' && location.search==='' && document.querySelector('#password').value===''"));
        await core.ExecuteScriptAsync($"document.querySelector('#username').value={JsonSerializer.Serialize(TestUsername)};document.querySelector('#password').value={JsonSerializer.Serialize(TestPassword)};document.querySelector('#login-form').requestSubmit()");
        await WaitBrowser(core, "document.querySelector('#studio')?.hidden===false && document.querySelectorAll('.card').length===1");
        var cookies = await core.CookieManager.GetCookiesAsync(server.Address.AbsoluteUri);
        var cookie = cookies.Single(c => c.Name == LocalWebServer.CookieName);
        Assert.True(cookie.IsSecure && cookie.IsHttpOnly && cookie.SameSite == CoreWebView2CookieSameSiteKind.Strict);
        await CaptureBrowser(core, "desktop-sign-in");
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReload(object? sender, CoreWebView2NavigationCompletedEventArgs args) => reloaded.TrySetResult();
        core.NavigationCompleted += OnReload;
        try { core.Reload(); await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { core.NavigationCompleted -= OnReload; }
        await WaitBrowser(core, "document.querySelector('#studio')?.hidden===false && document.querySelectorAll('.card').length===1");
        await core.ExecuteScriptAsync("document.querySelector('#logout').click()");
        await WaitBrowser(core, "document.querySelector('#login')?.hidden===false");
        core.Navigate(launched!.AbsoluteUri);
        await WaitBrowser(core, "document.querySelector('#login')?.hidden===false && document.querySelector('#studio')?.hidden===true");
        Assert.Equal("[]", await core.ExecuteScriptAsync("window.testErrors"));
    }

    private static Task ServerWindowThemeAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var window = new LocalWebServerWindow
        {
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        window.Networks.Items.Add("Ethernet — 192.168.0.19/24");
        window.Networks.SelectedIndex = 0;
        window.Users.ItemsSource = new[] { new LocalWebUserSummary("fixture", "webtester") };
        window.Users.SelectedIndex = 0;
        window.Status.Text = "Stopped. The browser server is off by default and does not start automatically.";
        window.Stop.IsEnabled = window.Rotate.IsEnabled = window.Export.IsEnabled = false;
        try
        {
            window.Show();
            foreach (var theme in Enum.GetValues<AppTheme>())
            {
                ThemeManager.ApplyTheme(theme);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert.Equal(((SolidColorBrush)window.FindResource("StudioBaseBrush")).Color, ((SolidColorBrush)window.Background).Color);
                Assert.Equal(((SolidColorBrush)window.FindResource("StudioTextBrush")).Color, ((SolidColorBrush)window.Foreground).Color);
                Assert.Equal(((SolidColorBrush)window.FindResource("StudioSurface0Brush")).Color, ((SolidColorBrush)window.Users.Background).Color);
                Assert.Equal(((SolidColorBrush)window.FindResource("StudioTextBrush")).Color, ((SolidColorBrush)window.Users.Foreground).Color);
                Assert.Equal(((SolidColorBrush)window.FindResource("StudioBorderBrush")).Color, ((SolidColorBrush)window.Users.BorderBrush).Color);
                var userRow = (ListBoxItem)window.Users.ItemContainerGenerator.ContainerFromIndex(0)!;
                Assert.Equal(((SolidColorBrush)window.FindResource("StudioAccentPressedBrush")).Color, ((SolidColorBrush)userRow.Background).Color);
                Assert.Equal(((SolidColorBrush)window.FindResource("StudioSurface0Brush")).Color, ((SolidColorBrush)window.Port.Background).Color);
                Assert.Equal(((SolidColorBrush)window.FindResource("StudioTextBrush")).Color, ((SolidColorBrush)window.Port.Foreground).Color);
                Assert.Equal(((SolidColorBrush)window.FindResource("StudioPrimaryBrush")).Color, ((SolidColorBrush)window.Start.Background).Color);
                foreach (var width in new[] { 520d, 660d })
                {
                    window.Width = width;
                    window.UpdateLayout();
                    Assert.True(window.Status.ActualWidth > 400 && window.Status.ActualWidth < width);
                    if (theme is not (AppTheme.Dark or AppTheme.Light)) continue;
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    var repository = Directory.GetCurrentDirectory();
                    while (!File.Exists(Path.Combine(repository, "StreamlinkVlcStudio.sln")))
                        repository = Directory.GetParent(repository)?.FullName ?? throw new DirectoryNotFoundException();
                    var directory = Path.Combine(repository, ".artifacts", "local-web-design");
                    Directory.CreateDirectory(directory);
                    using var file = File.Create(Path.Combine(directory, $"server-window-{theme}-{width}.png"));
                    encoder.Save(file);
                }
            }
        }
        finally { window.Close(); ThemeManager.ApplyTheme(AppTheme.Dark); }
    });
}

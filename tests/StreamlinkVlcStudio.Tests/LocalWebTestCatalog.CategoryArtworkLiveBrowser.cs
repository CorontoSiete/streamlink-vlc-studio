using System.Security.Cryptography.X509Certificates;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private sealed record BrowserArtworkResult(string Id, string Name, bool ExpectedMissing, bool Loaded, int Width, int Height, bool Unavailable);

    private static Task CheckLiveCategoryArtworkBrowserAsync(LocalWebDependencies dependencies, IReadOnlyDictionary<string, bool> availability,
        IReadOnlyDictionary<string, BrowseCategory> metadata, HttpClient cdn, string? directory) => TestSta.RunOffscreenAsync(async () =>
    {
        using var certificate = Certificate();
        // A fresh cache makes the actual HTTPS/browser path exercise all initial image downloads.
        await using var server = await Server(certificate, () => new LocalWebWorkspace(dependencies with { CategoryArtwork = new() }));
        var profile = Path.Combine(Path.GetTempPath(), "StreamStudioTests", "live-category-artwork-" + Guid.NewGuid().ToString("N"));
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
        var window = new Window
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Width = 1440,
            Height = 1000,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        window.Show();
        var controller = await environment.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(window).EnsureHandle());
        controller.Bounds = new System.Drawing.Rectangle(0, 0, 1440, 1000);
        controller.IsVisible = true;
        var core = controller.CoreWebView2;
        core.ServerCertificateErrorDetected += (_, args) =>
        {
            using var presented = X509Certificate2.CreateFromPem(args.ServerCertificate.ToPemEncoding());
            if (new Uri(args.RequestUri).Authority == server.Address.Authority && LocalWebCertificate.Fingerprint(presented) == LocalWebCertificate.Fingerprint(certificate))
                args.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
        };
        var responses = new List<int>();
        core.WebResourceResponseReceived += (_, args) =>
        {
            if (new Uri(args.Request.Uri).AbsolutePath.StartsWith("/category-artwork/", StringComparison.Ordinal)) responses.Add(args.Response.StatusCode);
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync("window.testErrors=[];window.addEventListener('error',e=>testErrors.push(e.message));window.addEventListener('securitypolicyviolation',e=>testErrors.push(e.violatedDirective));");
        try
        {
            core.Navigate(server.Address.AbsoluteUri);
            await WaitBrowser(core, "document.readyState==='complete' && !!document.querySelector('#password')");
            await core.ExecuteScriptAsync($"document.querySelector('#username').value={JsonSerializer.Serialize(TestUsername)};document.querySelector('#password').value={JsonSerializer.Serialize(TestPassword)};document.querySelector('#login-form').requestSubmit()");
            await WaitBrowser(core, "!document.querySelector('#studio').hidden && document.querySelector('#cards').getAttribute('aria-busy')==='false'");
            await core.ExecuteScriptAsync("document.querySelector('[data-page=discover]').click()");
            await WaitBrowser(core, "document.querySelector('#cards').getAttribute('aria-busy')==='false' && document.querySelectorAll('.card').length===50");
            var ids = JsonSerializer.Deserialize<string[]>(await core.ExecuteScriptAsync("[...document.querySelectorAll('.card')].map(c=>c.dataset.key)"))!;
            var rows = new List<BrowserArtworkResult>();
            foreach (var id in ids)
            {
                var selector = ".card[data-key=" + JsonSerializer.Serialize(id) + "]";
                var card = "document.querySelector(" + JsonSerializer.Serialize(selector) + ")";
                await core.ExecuteScriptAsync(card + ".scrollIntoView({block:'center'})");
                await WaitBrowser(core, "(()=>{const c=" + card + ";const i=c.querySelector('.card-media>img');return i?.complete&&i.naturalWidth>0||!!c.querySelector('.artwork-unavailable-label')})()");
                var rendered = JsonSerializer.Deserialize<JsonElement>(await core.ExecuteScriptAsync("(()=>{const c=" + card + ";const i=c.querySelector('.card-media>img');return {loaded:!!i,width:i?.naturalWidth||0,height:i?.naturalHeight||0,unavailable:!!c.querySelector('.artwork-unavailable-label')}})()"));
                var expectedMissing = availability.TryGetValue(id, out var missing) && missing;
                if (!availability.ContainsKey(id))
                {
                    using var upstream = await cdn.GetAsync(metadata[id].ThumbnailUrl);
                    expectedMissing = upstream.RequestMessage!.RequestUri!.AbsolutePath.StartsWith("/ttv-static/404_boxart-", StringComparison.Ordinal);
                }
                rows.Add(new(id, metadata[id].Name, expectedMissing, rendered.GetProperty("loaded").GetBoolean(),
                    rendered.GetProperty("width").GetInt32(), rendered.GetProperty("height").GetInt32(), rendered.GetProperty("unavailable").GetBoolean()));
            }
            await core.ExecuteScriptAsync("document.querySelector('#cards').scrollIntoView({block:'start'})");
            await CaptureBrowser(core, "category-artwork-live-directory-desktop");
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 390, 844);
            await WaitBrowser(core, "innerWidth===390");
            Assert.Equal("true", await core.ExecuteScriptAsync("document.documentElement.scrollWidth<=innerWidth"));
            await CaptureBrowser(core, "category-artwork-live-directory-mobile");
            if (!string.IsNullOrEmpty(directory))
                await File.WriteAllTextAsync(Path.Combine(directory, "live-browser-category-artwork.json"), JsonSerializer.Serialize(new { categories = rows, responseStatuses = responses }, new JsonSerializerOptions { WriteIndented = true }));
            Assert.Equal(50, rows.Count);
            Assert.True(rows.All(row => row.ExpectedMissing ? !row.Loaded && row.Unavailable : row.Loaded && row.Width == 285 && row.Height == 380 && !row.Unavailable));
            Assert.True(responses.All(status => status is 200 or 404));
            Assert.Equal("[]", await core.ExecuteScriptAsync("window.testErrors"));
            Console.WriteLine($"LIVE browser directory: {rows.Count} cards; {rows.Count(row => row.Loaded)} decoded covers; {rows.Count(row => row.Unavailable)} confirmed upstream missing covers; no false unavailable cards.");
        }
        finally { controller.Close(); window.Close(); }
    });
}

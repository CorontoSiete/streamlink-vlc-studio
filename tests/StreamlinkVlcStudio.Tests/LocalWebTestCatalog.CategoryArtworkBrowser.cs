using Microsoft.AspNetCore.Http;
using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private sealed record ArtworkExample(string Id, string Name, string File, char Key);
    private static readonly ArtworkExample[] MislabeledArtworkExamples =
    [
        new("21779", "League of Legends", "league-of-legends-2026-10-22.jpg", '4'),
        new("18122", "World of Warcraft", "world-of-warcraft-2026-10-22.jpg", '5'),
        new("32399", "Counter-Strike", "counter-strike-2026-10-22.jpg", '6'),
        new("29595", "Dota 2", "dota-2-2026-10-22.jpg", '7'),
        new("516575", "VALORANT", "valorant-2026-10-22.jpg", '8'),
        new("509672", "IRL", "irl-2026-10-22.jpg", '9')
    ];

    private static async Task CheckBrowserCategoryArtwork(CoreWebView2 core, CoreWebView2Controller controller, BrowserWorkspace workspace, string password)
    {
        const string settled = "document.querySelector('#cards').getAttribute('aria-busy')==='false'";
        string Card(string id) => $"document.querySelector('.card[data-key=\"{id}\"]')";
        string Image(string id) => Card(id) + "?.querySelector('.card-media > img')";
        string Missing(string id) => Card(id) + "?.querySelector('.artwork-unavailable-label')?.textContent==='Artwork unavailable'";
        await core.ExecuteScriptAsync($"document.querySelector('#password').value={JsonSerializer.Serialize(password)};document.querySelector('#login-form').requestSubmit()");
        await WaitBrowser(core, "!document.querySelector('#studio').hidden && " + settled);
        await core.ExecuteScriptAsync("document.querySelector('[data-page=discover]').click()");
        await WaitBrowser(core, settled + " && document.querySelectorAll('.card').length===10 && " + Missing("66082") + " && " + Image("509658") + "?.naturalWidth===285");
        foreach (var example in MislabeledArtworkExamples)
        {
            await core.ExecuteScriptAsync(Card(example.Id) + ".scrollIntoView({block:'nearest'})");
            await WaitBrowser(core, Image(example.Id) + "?.naturalWidth===285 && " + Image(example.Id) + "?.naturalHeight===380");
            Assert.Equal("false", await core.ExecuteScriptAsync(Missing(example.Id)));
        }
        await core.ExecuteScriptAsync(Card("498566") + ".scrollIntoView({block:'nearest'})");
        Assert.Equal("false", await core.ExecuteScriptAsync(Missing("498566"))); // Slow artwork is still pending.
        Assert.Equal("true", await core.ExecuteScriptAsync(Missing("empty")));
        workspace.ArtworkRelease.TrySetResult();
        await WaitBrowser(core, Missing("498566"));
        Assert.Equal("false", await core.ExecuteScriptAsync("!!" + Image("498566")));
        Assert.Equal("380", await core.ExecuteScriptAsync(Image("509658") + ".naturalHeight"));
        Assert.Equal("329,518 viewers", JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(Card("498566") + ".querySelector('.media-meta').textContent")));
        await CaptureBrowser(core, "category-artwork-desktop");
        controller.Bounds = new System.Drawing.Rectangle(0, 0, 390, 844);
        await WaitBrowser(core, "innerWidth===390");
        Assert.Equal("true", await core.ExecuteScriptAsync("document.documentElement.scrollWidth<=innerWidth"));
        await CaptureBrowser(core, "category-artwork-mobile");

        workspace.RecoverArtwork = true;
        workspace.ArtworkTime = workspace.ArtworkTime.AddSeconds(31);
        await core.ExecuteScriptAsync("document.querySelector('#refresh').click()");
        await WaitBrowser(core, settled + " && " + Image("498566") + "?.naturalWidth===285");
        Assert.Equal("false", await core.ExecuteScriptAsync(Missing("498566")));
        Assert.Equal("true", await core.ExecuteScriptAsync(Missing("66082")));
        await CaptureBrowser(core, "category-artwork-recovered");
        await core.ExecuteScriptAsync(Card("498566") + ".querySelector('.card-main').click()");
        await WaitBrowser(core, settled + " && document.querySelector('#breadcrumbs').textContent.includes('Slots') && !document.querySelector('#cards').classList.contains('categories')");
        await core.ExecuteScriptAsync("document.querySelector('#breadcrumbs button').click();document.querySelector('#query').value='Games';document.querySelector('#search-form').requestSubmit()");
        await WaitBrowser(core, settled + " && document.querySelectorAll('.card').length===1 && " + Missing("66082"));
        Assert.Equal("[]", await core.ExecuteScriptAsync("window.testErrors"));
    }

    private sealed partial class BrowserWorkspace
    {
        public bool CategoryArtwork { get; init; }
        public bool RecoverArtwork { get; set; }
        public DateTimeOffset ArtworkTime { get; set; } = DateTimeOffset.UtcNow;
        public TaskCompletionSource ArtworkRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private HttpClient? artworkHttp;
        private LocalWebCategoryArtwork? artworkClient;
        private static string ArtworkId(char digit) => new(digit, 64);

        private object? CategoryArtworkResult(string action, JsonElement input)
        {
            if (action != "categories") return null;
            object Category(string id, string name, char key) => new
            {
                id,
                name,
                viewerCount = 329518,
                thumbnailUrl = key == '0' ? "" : "/category-artwork/" + ArtworkId(key)
            };
            var slots = Category("498566", "Slots", '1');
            var demos = Category("66082", "Games + Demos", '2');
            var chat = Category("509658", "Just Chatting", '3');
            return new
            {
                items = input.TryGetProperty("query", out var query) && query.GetString() == "Games"
                ? new[] { demos } : new[] { slots, demos, chat }.Concat(MislabeledArtworkExamples.Select(example => Category(example.Id, example.Name, example.Key)))
                    .Append(Category("empty", "No cover", '0')).ToArray()
            };
        }

        public Task ServeCategoryArtworkAsync(HttpContext context, string assetId)
        {
            if (artworkClient is null)
            {
                artworkHttp = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
                {
                    var slots = request.RequestUri!.AbsolutePath.Contains("498566", StringComparison.Ordinal);
                    if (slots) await ArtworkRelease.Task.WaitAsync(token);
                    if (slots && !RecoverArtwork)
                    {
                        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                        redirect.Headers.Location = new Uri("https://static-cdn.jtvnw.net/ttv-static/404_boxart-285x380.jpg");
                        return redirect;
                    }
                    var missing = request.RequestUri.AbsolutePath.Contains("66082", StringComparison.Ordinal);
                    var example = MislabeledArtworkExamples.FirstOrDefault(example => request.RequestUri.AbsolutePath.Contains("/" + example.Id + "-", StringComparison.Ordinal));
                    var file = Path.Combine(AppContext.BaseDirectory, "Fixtures", "category-artwork", example?.File ?? (missing ? "twitch-missing-2026-10-22.jpg" : "just-chatting-2026-10-22.jpg"));
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(await File.ReadAllBytesAsync(file, token)) };
                    response.Content.Headers.ContentType = new(example is null ? "image/jpeg" : "image/png");
                    if (missing) response.RequestMessage = new(HttpMethod.Get, "https://static-cdn.jtvnw.net/ttv-static/404_boxart-285x380.jpg");
                    return response;
                }));
                artworkClient = new(artworkHttp, () => ArtworkTime);
            }
            var id = assetId == ArtworkId('1') ? "498566" : assetId == ArtworkId('2') ? "66082" :
                MislabeledArtworkExamples.FirstOrDefault(example => assetId == ArtworkId(example.Key))?.Id ?? "509658";
            return artworkClient.ServeAsync(context, ArtworkCdn + id + "-285x380.jpg");
        }
    }
}

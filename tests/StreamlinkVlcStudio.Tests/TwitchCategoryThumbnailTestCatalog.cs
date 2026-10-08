internal static class TwitchCategoryThumbnailTestCatalog
{
    private const string BoxArt = "https://static-cdn.jtvnw.net/ttv-boxart/";

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Twitch category thumbnails: fixed-size search artwork matches the browse resolution", () => VerifyUrlsAsync(
        [
            (BoxArt + "33214-52x72.jpg", BoxArt + "33214-285x380.jpg"),
            (BoxArt + "27471_IGDB-52x72.jpg", BoxArt + "27471_IGDB-285x380.jpg"),
            (BoxArt + "33214-{width}x{height}.jpg", BoxArt + "33214-285x380.jpg"),
            (BoxArt + "27471_IGDB-%{width}x%{height}.jpg", BoxArt + "27471_IGDB-285x380.jpg"),
            (BoxArt + "33214-285x380.jpg", BoxArt + "33214-285x380.jpg"),
            (BoxArt + "33214-188x250.jpg", BoxArt + "33214-285x380.jpg"),
            (" //static-cdn.jtvnw.net/ttv-boxart/33214-52x72.jpg ", BoxArt + "33214-285x380.jpg")
        ])),
        ("Twitch category thumbnails: artwork names query strings and fragments survive resizing", () => VerifyUrlsAsync(
        [
            (BoxArt + "Call%20of%20Duty%3A%20Warzone-52x72.jpg", BoxArt + "Call%20of%20Duty%3A%20Warzone-285x380.jpg"),
            (BoxArt + "Game-52x72-special_IGDB-52x72.jpg", BoxArt + "Game-52x72-special_IGDB-285x380.jpg"),
            (BoxArt + "27471_IGDB-52x72.jpg?v=-52x72.jpg#preview-52x72.jpg", BoxArt + "27471_IGDB-285x380.jpg?v=-52x72.jpg#preview-52x72.jpg")
        ])),
        ("Twitch category thumbnails: unrelated image URLs retain their supplied dimensions", () => VerifyUrlsAsync(
        [
            ("https://example.invalid/ttv-boxart/33214-52x72.jpg", "https://example.invalid/ttv-boxart/33214-52x72.jpg"),
            ("https://static-cdn.jtvnw.net.example.invalid/ttv-boxart/33214-52x72.jpg", "https://static-cdn.jtvnw.net.example.invalid/ttv-boxart/33214-52x72.jpg"),
            ("https://static-cdn.jtvnw.net/jtv_user_pictures/avatar-52x72.jpg", "https://static-cdn.jtvnw.net/jtv_user_pictures/avatar-52x72.jpg"),
            (BoxArt + "cover.jpg?v=-52x72.jpg", BoxArt + "cover.jpg?v=-52x72.jpg"),
            (BoxArt + "cover.jpg", BoxArt + "cover.jpg"),
            (BoxArt + "cover-52x72.png", BoxArt + "cover-52x72.png"),
            ("", "")
        ]))
    ];

    private static async Task VerifyUrlsAsync(IReadOnlyList<(string Input, string Expected)> cases)
    {
        var categoryRequests = 0;
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
                return Json("""{"client_id":"thumbnail-client","login":"viewer","user_id":"1234","expires_in":3600,"scopes":[]}""");

            Assert.Equal("api.twitch.tv", request.RequestUri.Host);
            Assert.Equal("/helix/search/categories", request.RequestUri.AbsolutePath);
            Assert.Contains("query=cover", request.RequestUri.Query);
            categoryRequests++;
            return Json(JsonSerializer.Serialize(new
            {
                data = cases.Select((item, index) => new
                {
                    id = index.ToString(CultureInfo.InvariantCulture),
                    name = "Cover " + index,
                    box_art_url = item.Input
                }),
                pagination = new { cursor = "next-cover-page" }
            }));
        }));
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "thumbnail-client";
        settings.Chat.TwitchOAuthToken = "thumbnail-token";
        var service = new BrowseService(new MemoryLogger(), http);
        var result = await service.GetCategoriesAsync(new(PlatformKind.Twitch, "cover"), settings);

        Assert.Equal(BrowseResultStatus.Available, result.Status);
        Assert.Equal("next-cover-page", result.NextCursor);
        Assert.Equal(cases.Count, result.Items.Count);
        Assert.Equal(1, categoryRequests);
        for (var index = 0; index < cases.Count; index++)
        {
            var category = result.Items[index];
            Assert.Equal(index.ToString(CultureInfo.InvariantCulture), category.Id);
            Assert.Equal("Cover " + index, category.Name);
            Assert.Equal(cases[index].Expected, category.ThumbnailUrl);
            var card = new BrowseCategoryViewModel(category, _ => Task.CompletedTask);
            Assert.Equal(cases[index].Expected, card.ThumbnailUrl);
            Assert.Equal(!string.IsNullOrWhiteSpace(cases[index].Expected), card.HasThumbnail);
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
}

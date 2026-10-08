internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> TwitchCategoryThumbnailLiveTests =>
        Environment.GetEnvironmentVariable("SVS_TEST_TWITCH_CATEGORY_THUMBNAILS_LIVE") == "1"
            ? [
                ("Twitch category thumbnails: live search artwork loads at browse resolution in actual cards", TwitchCategoryThumbnailLiveAsync),
                ("Twitch category thumbnails: live war search distinguishes missing artwork from loaded covers", TwitchCategoryMissingArtworkLiveAsync)
            ]
            : [];

    private static async Task TwitchCategoryMissingArtworkLiveAsync()
    {
        var settings = await new JsonSettingsService().LoadAsync();
        using var handler = new TwitchCategoryThumbnailCaptureHandler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var cdn = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var service = new BrowseService(new MemoryLogger(), http);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await service.GetCategoriesAsync(new(PlatformKind.Twitch, "war", PageSize: 100), settings, cancellation.Token);
        Assert.Equal(BrowseResultStatus.Available, result.Status);
        var categories = new[] { "15188", "1050305122", "1086918202", "1594951771", "18122" }
            .Select(id => result.Items.Single(category => category.Id == id)).ToArray();
        var responses = new List<(string Id, string FinalUrl, bool IsAvailable)>();
        foreach (var category in categories)
        {
            using var response = await cdn.GetAsync(category.ThumbnailUrl, cancellation.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var uri = response.RequestMessage!.RequestUri!;
            var isAvailable = !uri.AbsolutePath.StartsWith("/ttv-static/404_boxart-", StringComparison.Ordinal);
            responses.Add((category.Id, uri.AbsoluteUri, isAvailable));
            AnimatedEmoteImage.RemoveCachedImageForTest(category.ThumbnailUrl, AnimatedEmoteImage.DefaultMaxImageBytes);
            Console.WriteLine($"LIVE Twitch category {category.Name}: artwork available={isAvailable}; {category.ThumbnailUrl} -> {uri}");
        }

        await WithStudioPolishWindowAsync(async (window, main, _) =>
        {
            foreach (var category in categories)
                main.BrowseCategories.Add(new BrowseCategoryViewModel(category, _ => Task.CompletedTask));
            main.ShowBrowseHomePageCommand.Execute(null);
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            var items = FindVisualDescendants<ItemsControl>((FrameworkElement)window.Content)
                .Single(control => ReferenceEquals(control.ItemsSource, main.BrowseCategories));
            var images = FindVisualDescendants<AnimatedEmoteImage>(items).ToArray();
            Assert.Equal(categories.Length, images.Length);
            await TestWait.UntilAsync(() => images.All(image => !image.IsImageLoadPending),
                TimeSpan.FromSeconds(15), "live war category image completion");
            LayoutStudioPolishWindow(window, new Size(1320, 820));

            var directory = Environment.GetEnvironmentVariable("SVS_TEST_ARTIFACT_DIR");
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "twitch-category-missing-art.json"),
                    JsonSerializer.Serialize(new
                    {
                        TimestampUtc = DateTimeOffset.UtcNow,
                        RawPages = handler.Pages,
                        Rendered = images.Select(image =>
                        {
                            var card = (BrowseCategoryViewModel)image.DataContext;
                            var response = responses.Single(response => response.Id == card.Id);
                            return new
                            {
                                card.Id,
                                card.Name,
                                card.ThumbnailUrl,
                                response.FinalUrl,
                                ExpectedAvailable = response.IsAvailable,
                                image.HasImageLoadFailed,
                                ImageSourcePresent = image.Source is BitmapSource
                            };
                        }).ToArray()
                    }, new JsonSerializerOptions { WriteIndented = true }));
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                    drawing.DrawRectangle(new VisualBrush(items), null, new Rect(new Point(), items.RenderSize));
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(items.ActualWidth), (int)Math.Ceiling(items.ActualHeight),
                    96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(directory, "twitch-category-missing-art.png"));
                encoder.Save(output);
            }

            foreach (var image in images)
            {
                var card = (BrowseCategoryViewModel)image.DataContext;
                var response = responses.Single(response => response.Id == card.Id);
                Assert.True(response.IsAvailable == (image.Source is BitmapSource),
                    $"{card.Name}: Twitch's missing-art redirect must not be treated as a loaded category cover.");
                Assert.Equal(!response.IsAvailable, image.HasImageLoadFailed);
                var label = FindVisualDescendants<TextBlock>(items)
                    .Single(text => ReferenceEquals(text.DataContext, card) && text.Text == "Artwork unavailable");
                Assert.Equal(response.IsAvailable ? Visibility.Collapsed : Visibility.Visible, ((StackPanel)label.Parent).Visibility);
                if (!response.IsAvailable)
                    Assert.True(AnimatedEmoteImage.ExpireFailedImageLoadForTest(card.ThumbnailUrl, AnimatedEmoteImage.DefaultMaxImageBytes));
            }
        });
        foreach (var category in categories)
            AnimatedEmoteImage.RemoveCachedImageForTest(category.ThumbnailUrl, AnimatedEmoteImage.DefaultMaxImageBytes);
    }

    private static async Task TwitchCategoryThumbnailLiveAsync()
    {
        var settings = await new JsonSettingsService().LoadAsync();
        using var handler = new TwitchCategoryThumbnailCaptureHandler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var service = new BrowseService(new MemoryLogger(), http);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var categories = new List<BrowseCategory>();
        foreach (var query in new[] { "", "Just Chatting", "Rust", "Minecraft" })
        {
            var result = await service.GetCategoriesAsync(new(PlatformKind.Twitch, query, PageSize: 20), settings, cancellation.Token);
            Assert.Equal(BrowseResultStatus.Available, result.Status);
            Assert.True(result.Items.Count > 0, $"The live category query '{query}' must return artwork to inspect.");
            categories.Add(string.IsNullOrEmpty(query)
                ? result.Items[0]
                : result.Items.First(category => category.Name.Equals(query, StringComparison.OrdinalIgnoreCase)));
        }

        await WithStudioPolishWindowAsync(async (window, main, _) =>
        {
            foreach (var category in categories)
                main.BrowseCategories.Add(new BrowseCategoryViewModel(category, _ => Task.CompletedTask));
            main.ShowBrowseHomePageCommand.Execute(null);
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            var items = FindVisualDescendants<ItemsControl>((FrameworkElement)window.Content)
                .Single(control => ReferenceEquals(control.ItemsSource, main.BrowseCategories));
            var images = FindVisualDescendants<AnimatedEmoteImage>(items).ToArray();
            Assert.Equal(categories.Count, images.Length);
            await TestWait.UntilAsync(() => images.All(image => !image.IsImageLoadPending && image.Source is BitmapSource),
                TimeSpan.FromSeconds(15), "live Twitch category artwork in the production card template");
            LayoutStudioPolishWindow(window, new Size(1320, 820));

            var rows = images.Select(image =>
            {
                var source = (BitmapSource)image.Source!;
                var card = (BrowseCategoryViewModel)image.DataContext;
                Console.WriteLine($"LIVE Twitch artwork {card.Name}: {source.PixelWidth}x{source.PixelHeight}; card image {image.ActualWidth:0.0}x{image.ActualHeight:0.0}; {card.ThumbnailUrl}");
                return new
                {
                    card.Name,
                    card.ThumbnailUrl,
                    source.PixelWidth,
                    source.PixelHeight,
                    RenderWidth = image.ActualWidth,
                    RenderHeight = image.ActualHeight
                };
            }).ToArray();

            var directory = Environment.GetEnvironmentVariable("SVS_TEST_ARTIFACT_DIR");
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "twitch-category-thumbnails.json"),
                    JsonSerializer.Serialize(new { TimestampUtc = DateTimeOffset.UtcNow, RawPages = handler.Pages, Rendered = rows },
                        new JsonSerializerOptions { WriteIndented = true }));
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                    drawing.DrawRectangle(new VisualBrush(items), null, new Rect(new Point(), items.RenderSize));
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(items.ActualWidth), (int)Math.Ceiling(items.ActualHeight),
                    96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(directory, "twitch-category-thumbnails.png"));
                encoder.Save(output);
            }

            foreach (var row in rows)
            {
                Assert.Equal(285, row.PixelWidth);
                Assert.Equal(380, row.PixelHeight);
                Assert.True(row.PixelWidth >= row.RenderWidth && row.PixelHeight >= row.RenderHeight,
                    "Category search artwork must not be enlarged from a smaller decoded source.");
            }
        });
    }

    private sealed class TwitchCategoryThumbnailCaptureHandler : DelegatingHandler
    {
        internal List<object> Pages { get; } = [];

        internal TwitchCategoryThumbnailCaptureHandler()
            : base(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri is { Host: "api.twitch.tv" } uri && response.IsSuccessStatusCode &&
                uri.AbsolutePath is "/helix/games/top" or "/helix/search/categories")
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                // Retain only public category metadata; never record credentials or headers.
                Pages.Add(new
                {
                    Endpoint = uri.AbsolutePath,
                    Query = uri.Query,
                    Categories = document.RootElement.GetProperty("data").EnumerateArray().Select(item => new
                    {
                        Id = item.GetProperty("id").GetString(),
                        Name = item.GetProperty("name").GetString(),
                        BoxArtUrl = item.GetProperty("box_art_url").GetString()
                    }).ToArray()
                });
            }
            return response;
        }
    }
}

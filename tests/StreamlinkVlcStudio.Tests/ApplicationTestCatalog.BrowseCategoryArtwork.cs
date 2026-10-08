using System.Windows.Threading;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> TwitchCategoryArtworkTests { get; } =
    [
        ("Twitch category thumbnails: missing-art redirects expire and retry the same URL", TwitchMissingArtworkCacheAsync),
        ("Twitch category thumbnails: placeholder detection preserves valid covers and unrelated hosts", TwitchArtworkResponseClassificationAsync),
        ("Twitch category thumbnails: actual cards distinguish pending unavailable and recovered artwork", TwitchArtworkCardStatesAsync),
        ("Twitch category thumbnails: retained cards recover the same URL when a category refresh completes", TwitchArtworkRefreshRecoveryAsync),
        ("Twitch category thumbnails: an older failed request cannot overwrite recovered artwork", TwitchArtworkStaleFailureAsync)
    ];

    private const string TwitchMissingArtworkUrl = "https://static-cdn.jtvnw.net/ttv-static/404_boxart-285x380.jpg";

    private static Task TwitchMissingArtworkCacheAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var url = "https://static-cdn.jtvnw.net/ttv-boxart/15188-285x380.jpg?regression=" + Guid.NewGuid().ToString("N");
        var bytes = CategoryArtworkPngBytes();
        var requests = 0;
        var available = false;
        using var http = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            requests++;
            return CategoryArtworkResponse(available ? url : TwitchMissingArtworkUrl, bytes);
        }));
        try
        {
            Assert.Equal(false, await AnimatedEmoteImage.LoadImageForTestAsync(url, http));
            Assert.Equal(false, await AnimatedEmoteImage.LoadImageForTestAsync(url, http));
            Assert.Equal(1, requests);
            var image = new AnimatedEmoteImage { ImageUrl = url };
            Assert.Equal(false, image.IsImageLoadPending);
            Assert.True(image.HasImageLoadFailed);
            Assert.Equal<ImageSource?>(null, image.Source);
            Assert.Equal(Visibility.Collapsed, image.Visibility);
            Assert.True(AnimatedEmoteImage.ExpireFailedImageLoadForTest(url, AnimatedEmoteImage.DefaultMaxImageBytes),
                "HTTP 200 missing artwork must enter the expiring failure cache instead of the successful image cache.");

            available = true;
            Assert.True(await AnimatedEmoteImage.LoadImageForTestAsync(url, http));
            Assert.Equal(2, requests);
            image.ImageUrl = "";
            Assert.Equal(false, image.HasImageLoadFailed);
            image.ImageUrl = url;
            Assert.Equal(false, image.HasImageLoadFailed);
            Assert.Equal(Visibility.Visible, image.Visibility);
            Assert.Equal(2, ((BitmapSource)image.Source).PixelWidth);
        }
        finally
        {
            AnimatedEmoteImage.RemoveCachedImageForTest(url, AnimatedEmoteImage.DefaultMaxImageBytes);
        }
    });

    private static Task TwitchArtworkResponseClassificationAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var bytes = CategoryArtworkPngBytes();
        var cases = new (string FinalUrl, HttpStatusCode Status, bool Available)[]
        {
            (TwitchMissingArtworkUrl, HttpStatusCode.OK, false),
            ("https://static-cdn.jtvnw.net/ttv-static/404_boxart-52x72.jpg?cache=1#preview", HttpStatusCode.OK, false),
            ("https://static-cdn.jtvnw.net/ttv-boxart/18122-285x380.jpg", HttpStatusCode.OK, true),
            ("https://static-cdn.jtvnw.net/ttv-boxart/cover.jpg?v=404_boxart-285x380.jpg", HttpStatusCode.OK, true),
            ("https://static-cdn.jtvnw.net.example.invalid/ttv-static/404_boxart-285x380.jpg", HttpStatusCode.OK, true),
            ("https://example.invalid/ttv-static/404_boxart-285x380.jpg", HttpStatusCode.OK, true),
            ("https://static-cdn.jtvnw.net/ttv-boxart/15188-285x380.jpg", HttpStatusCode.NotFound, false)
        };
        foreach (var test in cases)
        {
            var url = "https://static-cdn.jtvnw.net/ttv-boxart/15188-285x380.jpg?classification=" + Guid.NewGuid().ToString("N");
            using var http = new HttpClient(new FakeHttpMessageHandler(_ => CategoryArtworkResponse(test.FinalUrl, bytes, test.Status)));
            try
            {
                Assert.Equal(test.Available, await AnimatedEmoteImage.LoadImageForTestAsync(url, http));
                Assert.Equal(!test.Available, AnimatedEmoteImage.ExpireFailedImageLoadForTest(url, AnimatedEmoteImage.DefaultMaxImageBytes));
            }
            finally
            {
                AnimatedEmoteImage.RemoveCachedImageForTest(url, AnimatedEmoteImage.DefaultMaxImageBytes);
            }
        }
    });

    private static Task TwitchArtworkCardStatesAsync() => WithStudioPolishWindowAsync(async (window, main, _) =>
    {
        var prefix = "https://example.invalid/category-artwork-" + Guid.NewGuid().ToString("N");
        var validUrl = prefix + "-valid.png";
        var failedUrl = prefix + "-failed.png";
        var pendingUrl = prefix + "-pending.png";
        var pending = AnimatedEmoteImage.SetPendingImageLoadForTest(pendingUrl, AnimatedEmoteImage.DefaultMaxImageBytes);
        var bytes = CategoryArtworkPngBytes();
        using var http = new HttpClient(new FakeHttpMessageHandler(_ => CategoryArtworkResponse(TwitchMissingArtworkUrl, bytes)));
        try
        {
            Assert.Equal(false, await AnimatedEmoteImage.LoadImageForTestAsync(failedUrl, http));
            AnimatedEmoteImage.SetCachedSolidColorImageForTest(validUrl, AnimatedEmoteImage.DefaultMaxImageBytes,
                [Colors.MediumPurple], [TimeSpan.FromSeconds(1)], width: 285, height: 380);
            foreach (var (id, name, url) in new[]
            {
                ("available", "Available artwork", validUrl),
                ("missing", "Missing artwork", failedUrl),
                ("empty", "No supplied artwork", ""),
                ("invalid", "Invalid image URL", "invalid-url"),
                ("pending", "Loading artwork", pendingUrl)
            })
                main.BrowseCategories.Add(new BrowseCategoryViewModel(new(PlatformKind.Twitch, id, name, url, []), _ => Task.CompletedTask));
            main.ShowBrowseHomePageCommand.Execute(null);
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            var items = FindVisualDescendants<ItemsControl>((FrameworkElement)window.Content)
                .Single(control => ReferenceEquals(control.ItemsSource, main.BrowseCategories));
            var images = FindVisualDescendants<AnimatedEmoteImage>(items)
                .ToDictionary(image => ((BrowseCategoryViewModel)image.DataContext).Id);
            var labels = FindVisualDescendants<TextBlock>(items).Where(text => text.Text == "Artwork unavailable")
                .ToDictionary(text => ((BrowseCategoryViewModel)text.DataContext).Id, text => (StackPanel)text.Parent);
            Assert.Equal(5, images.Count);
            Assert.Equal(5, labels.Count);
            Assert.NotNull(images["available"].Source);
            Assert.Equal(false, images["available"].HasImageLoadFailed);
            Assert.Equal(Visibility.Collapsed, labels["available"].Visibility);
            Assert.True(images["missing"].HasImageLoadFailed);
            Assert.Equal(Visibility.Visible, labels["missing"].Visibility);
            Assert.Equal(Visibility.Visible, labels["empty"].Visibility);
            Assert.True(images["invalid"].HasImageLoadFailed);
            Assert.Equal(Visibility.Visible, labels["invalid"].Visibility);
            Assert.True(images["pending"].IsImageLoadPending);
            Assert.Equal(false, images["pending"].HasImageLoadFailed);
            Assert.Equal(Visibility.Collapsed, labels["pending"].Visibility);

            pending.TrySetResult(null);
            await TestWait.UntilAsync(() => !images["pending"].IsImageLoadPending, TimeSpan.FromSeconds(1));
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            Assert.True(images["pending"].HasImageLoadFailed);
            Assert.Equal(Visibility.Visible, labels["pending"].Visibility);

            var missing = main.BrowseCategories.Single(card => card.Id == "missing");
            missing.Update(missing.Category with { ThumbnailUrl = validUrl });
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            Assert.NotNull(images["missing"].Source);
            Assert.Equal(false, images["missing"].HasImageLoadFailed);
            Assert.Equal(Visibility.Collapsed, labels["missing"].Visibility);
        }
        finally
        {
            pending.TrySetResult(null);
            foreach (var url in new[] { validUrl, failedUrl, pendingUrl })
                AnimatedEmoteImage.RemoveCachedImageForTest(url, AnimatedEmoteImage.DefaultMaxImageBytes);
        }
    });

    private static Task TwitchArtworkRefreshRecoveryAsync() => WithStudioPolishWindowAsync(async (window, main, _) =>
    {
        var prefix = "https://example.invalid/category-artwork-refresh-" + Guid.NewGuid().ToString("N");
        var failedUrl = prefix + "-failed.png";
        var validUrl = prefix + "-valid.png";
        var bytes = CategoryArtworkPngBytes();
        var available = false;
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
            CategoryArtworkResponse(request.RequestUri!.AbsoluteUri == failedUrl && !available
                ? TwitchMissingArtworkUrl : request.RequestUri.AbsoluteUri, bytes)));
        try
        {
            Assert.Equal(false, await AnimatedEmoteImage.LoadImageForTestAsync(failedUrl, http));
            Assert.True(await AnimatedEmoteImage.LoadImageForTestAsync(validUrl, http));
            var card = new BrowseCategoryViewModel(new(PlatformKind.Twitch, "missing", "Missing artwork", failedUrl, []), _ => Task.CompletedTask);
            main.BrowseCategories.Add(card);
            main.BrowseCategories.Add(new BrowseCategoryViewModel(new(PlatformKind.Twitch, "available", "Available artwork", validUrl, []), _ => Task.CompletedTask));
            main.ShowBrowseHomePageCommand.Execute(null);
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            var items = FindVisualDescendants<ItemsControl>((FrameworkElement)window.Content)
                .Single(control => ReferenceEquals(control.ItemsSource, main.BrowseCategories));
            var images = FindVisualDescendants<AnimatedEmoteImage>(items)
                .ToDictionary(image => ((BrowseCategoryViewModel)image.DataContext).Id);
            var image = images["missing"];
            var healthySource = images["available"].Source;
            Assert.True(image.HasImageLoadFailed);

            var loading = typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsBrowseCategoriesLoading))!;
            loading.SetValue(main, true);
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            Assert.True(image.IsRefreshRunning);
            Assert.True(AnimatedEmoteImage.ExpireFailedImageLoadForTest(failedUrl, AnimatedEmoteImage.DefaultMaxImageBytes));
            available = true;
            Assert.True(await AnimatedEmoteImage.LoadImageForTestAsync(failedUrl, http));
            Assert.True(image.HasImageLoadFailed);

            loading.SetValue(main, false);
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            await TestWait.UntilAsync(() => !image.IsImageLoadPending && image.Source is BitmapSource,
                TimeSpan.FromSeconds(1), "recovered artwork on the retained category card");
            Assert.Equal(false, image.HasImageLoadFailed);
            Assert.True(ReferenceEquals(card, main.BrowseCategories[0]));
            Assert.True(ReferenceEquals(image, FindVisualDescendants<AnimatedEmoteImage>(items)
                .Single(candidate => ReferenceEquals(candidate.DataContext, card))));
            Assert.Equal(failedUrl, card.ThumbnailUrl);
            Assert.True(ReferenceEquals(healthySource, images["available"].Source));
            var label = FindVisualDescendants<TextBlock>(items)
                .Single(text => ReferenceEquals(text.DataContext, card) && text.Text == "Artwork unavailable");
            Assert.Equal(Visibility.Collapsed, ((StackPanel)label.Parent).Visibility);
        }
        finally
        {
            AnimatedEmoteImage.RemoveCachedImageForTest(failedUrl, AnimatedEmoteImage.DefaultMaxImageBytes);
            AnimatedEmoteImage.RemoveCachedImageForTest(validUrl, AnimatedEmoteImage.DefaultMaxImageBytes);
        }
    });

    private static Task TwitchArtworkStaleFailureAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var prefix = "https://example.invalid/category-artwork-race-" + Guid.NewGuid().ToString("N");
        var oldUrl = prefix + "-old.png";
        var newUrl = prefix + "-new.png";
        var completion = AnimatedEmoteImage.SetPendingImageLoadForTest(oldUrl, AnimatedEmoteImage.DefaultMaxImageBytes);
        AnimatedEmoteImage.SetCachedSolidColorImageForTest(newUrl, AnimatedEmoteImage.DefaultMaxImageBytes,
            [Colors.Lime], [TimeSpan.FromSeconds(1)], width: 285, height: 380);
        try
        {
            var image = new AnimatedEmoteImage { ImageUrl = oldUrl };
            Assert.True(image.IsImageLoadPending);
            image.ImageUrl = newUrl;
            var source = image.Source;
            Assert.NotNull(source);
            completion.TrySetResult(null);
            var oldKey = new AnimatedEmoteImageCacheKey(oldUrl, AnimatedEmoteImage.DefaultMaxImageBytes, 0);
            await TestWait.UntilAsync(() => AnimatedEmoteImage.IsCacheEntryCompleted(oldKey), TimeSpan.FromSeconds(1));
            await image.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(ReferenceEquals(source, image.Source));
            Assert.Equal(false, image.HasImageLoadFailed);
        }
        finally
        {
            completion.TrySetResult(null);
            AnimatedEmoteImage.RemoveCachedImageForTest(oldUrl, AnimatedEmoteImage.DefaultMaxImageBytes);
            AnimatedEmoteImage.RemoveCachedImageForTest(newUrl, AnimatedEmoteImage.DefaultMaxImageBytes);
        }
    });

    private static byte[] CategoryArtworkPngBytes()
    {
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 128, 64, 192, 255, 128, 64, 192, 255, 128, 64, 192, 255, 128, 64, 192, 255 }, 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static HttpResponseMessage CategoryArtworkResponse(string finalUrl, byte[] bytes, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUrl),
            Content = new ByteArrayContent(bytes)
        };
}

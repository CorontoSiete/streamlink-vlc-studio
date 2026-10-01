using StreamlinkVlcStudio.App.Wpf.Themes;

internal static class ProfileAvatarTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
        new[] { "live card", "offline card", "VOD card", "tab" }
            .SelectMany(surface => new (string Name, Func<Task> Run)[]
            {
                ($"profile avatars: NiceWigg's transparent PNG paints correctly in the {surface}",
                    () => VerifyAvatarAsync(surface)),
                ($"profile avatars: {surface} restores the fallback during pending, failed and empty loads",
                    () => VerifyFallbackAsync(surface))
            }).ToArray();

    private static string FixturePath => Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "profile-images", "nicewigg-transparent-150.png");

    private static Task VerifyAvatarAsync(string surface) => WithAvatarAsync(surface, async (host, image, border) =>
    {
        image.ImageUrl = new Uri(FixturePath).AbsoluteUri;
        await WaitForImageAsync(image);
        var source = (BitmapSource)image.Source!;
        Assert.Equal(150, source.PixelWidth);
        Assert.Equal(150, source.PixelHeight);
        var decoded = Pixels(source);
        Assert.Equal(19942, Enumerable.Range(0, 150 * 150).Count(index => decoded[index * 4 + 3] == 0));

        // Independently load the saved CDN PNG, then render only that image over the same
        // avatar chrome. Checking the complete border catches fallback pixels showing
        // through transparency; checking the Image alone would miss the original bug.
        var reference = new BitmapImage();
        reference.BeginInit();
        reference.CacheOption = BitmapCacheOption.OnLoad;
        reference.UriSource = new Uri(FixturePath);
        reference.EndInit();
        reference.Freeze();
        Assert.True(Pixels(reference).SequenceEqual(decoded), "The loader must preserve the CDN image's RGBA pixels.");

        ResourceDictionary? previousPalette = null;
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            // This host is disconnected from the application window tree. Attach the
            // production palette locally so resource invalidation reaches its templates.
            if (previousPalette is not null) host.Resources.MergedDictionaries.Remove(previousPalette);
            var palette = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/StreamStudio;component/Themes/Colors/{theme}Colors.xaml")
            };
            host.Resources.MergedDictionaries.Add(palette);
            previousPalette = palette;
            Layout(host);
            var surfaceColorKey = surface switch
            {
                "live card" => "StudioSurface3Color",
                "offline card" => "StudioSurface2Color",
                _ => "StudioSurface0Color"
            };
            WpfVisualTest.AssertSolidBrushColor(((Color)palette[surfaceColorKey]).ToString(), border.Background);
            var actual = RenderAvatar(border);
            var expectedBorder = new RoundedClipBorder
            {
                Width = border.ActualWidth,
                Height = border.ActualHeight,
                CornerRadius = border.CornerRadius,
                Background = border.Background,
                BorderBrush = border.BorderBrush,
                BorderThickness = border.BorderThickness,
                UseLayoutRounding = border.UseLayoutRounding,
                SnapsToDevicePixels = border.SnapsToDevicePixels,
                Child = new Grid { Children = { new Image { Source = reference, Stretch = Stretch.UniformToFill } } }
            };
            Layout(expectedBorder, border.RenderSize);
            var expected = RenderAvatar(expectedBorder);
            Save(actual, $"{surface}-{theme}-actual");
            Save(expected, $"{surface}-{theme}-expected");
            var actualPixels = Pixels(actual);
            var expectedPixels = Pixels(expected);
            var differences = Enumerable.Range(0, expected.PixelWidth * expected.PixelHeight)
                .Count(index => !actualPixels.AsSpan(index * 4, 4).SequenceEqual(expectedPixels.AsSpan(index * 4, 4)));
            Console.WriteLine($"{surface} {theme}: {differences} avatar pixels differ from the PNG-only reference.");
            Assert.Equal(0, differences);
        }
    });

    private static Task VerifyFallbackAsync(string surface) => WithAvatarAsync(surface, async (host, image, border) =>
    {
        Layout(host);
        var fallback = Pixels(RenderAvatar(border));
        image.ImageUrl = new Uri(FixturePath).AbsoluteUri;
        await WaitForImageAsync(image);
        Layout(host);
        Assert.True(!fallback.SequenceEqual(Pixels(RenderAvatar(border))), "A decoded avatar must replace the fallback.");

        var pendingUrl = $"https://example.invalid/profile-avatar-{Guid.NewGuid():N}.png";
        var pending = AnimatedEmoteImage.SetPendingImageLoadForTest(pendingUrl, image.MaxImageBytes);
        try
        {
            image.ImageUrl = pendingUrl;
            Assert.True(image.IsImageLoadPending);
            Assert.True(image.Source is null);
            AssertFallback();
            pending.SetResult(null);
            await TestWait.UntilAsync(() => !image.IsImageLoadPending, TimeSpan.FromSeconds(5));
            AssertFallback();

            // A nonempty URL that cannot be decoded must keep the fallback. Reusing the
            // already-loaded PNG then exercises the synchronous completed-cache path.
            image.ImageUrl = new Uri(FixturePath + ".missing").AbsoluteUri;
            await TestWait.UntilAsync(() => !image.IsImageLoadPending, TimeSpan.FromSeconds(5));
            AssertFallback();
            image.ImageUrl = new Uri(FixturePath).AbsoluteUri;
            await WaitForImageAsync(image);
            Layout(host);
            Assert.True(!fallback.SequenceEqual(Pixels(RenderAvatar(border))));
            image.ImageUrl = "";
            AssertFallback();
        }
        finally
        {
            pending.TrySetResult(null);
            AnimatedEmoteImage.RemoveCachedImageForTest(pendingUrl, image.MaxImageBytes);
            AnimatedEmoteImage.RemoveCachedImageForTest(new Uri(FixturePath + ".missing").AbsoluteUri, image.MaxImageBytes);
        }

        void AssertFallback()
        {
            Layout(host);
            Assert.True(fallback.SequenceEqual(Pixels(RenderAvatar(border))),
                $"The {surface} must paint its original fallback while no decoded image is available.");
        }
    });

    private static Task WithAvatarAsync(string surface,
        Func<FrameworkElement, AnimatedEmoteImage, RoundedClipBorder, Task> verify) => TestSta.RunOffscreenAsync(async () =>
    {
        var window = new MainWindow();
        ApplicationTestCatalog.RemoveMainWindowAutomaticStartup(window);
        var tab = TestViewModels.CreateTab(StreamInputParser.Parse("nicewigg", PlatformKind.Twitch), "best",
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
            new MemoryLogger(), action => action());
        using var stripItem = new TabStripItemViewModel([tab], tab);
        try
        {
            var template = surface switch
            {
                "live card" => (DataTemplate)window.Resources["LiveStreamCardTemplate"],
                "offline card" => (DataTemplate)window.Resources["OfflineFollowedChannelTemplate"],
                "VOD card" => ((ItemsControl)window.FindName("VodCardsItemsControl")).ItemTemplate,
                "tab" => ((ListBox)window.FindName("TabListBox")).ItemTemplate,
                _ => throw new ArgumentOutOfRangeException(nameof(surface))
            };
            var host = new ContentControl
            {
                ContentTemplate = template,
                Resources = window.Resources,
                FontFamily = window.FontFamily,
                UseLayoutRounding = true
            };
            host.Content = surface switch
            {
                "live card" => new LiveStreamCardViewModel(new LiveStreamCardData(LiveStreamCardSource.Followed,
                    tab.Target, PlatformKind.Twitch, "nicewigg", "NiceWigg", "Test stream", "Games", 100, "", "",
                    DateTimeOffset.UnixEpoch, false, "en"), (_, _) => Task.CompletedTask),
                "offline card" => new OfflineFollowedChannelViewModel(new FollowedChannel(PlatformKind.Twitch,
                    "nicewigg", "NiceWigg", "https://www.twitch.tv/nicewigg"), _ => Task.CompletedTask),
                "VOD card" => new VodViewModel(new TwitchVodItem("123", "456", "415954300", "nicewigg", "NiceWigg",
                    "Test broadcast", "", "https://www.twitch.tv/videos/123", "", DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch, TimeSpan.FromHours(1), 100, TwitchVodTypeFilter.Archive),
                    (_, _) => Task.CompletedTask),
                "tab" => stripItem,
                _ => throw new ArgumentOutOfRangeException(nameof(surface))
            };
            Layout(host);
            var image = Descendants<AnimatedEmoteImage>(host)
                .Single(item => BindingOperations.GetBinding(item, AnimatedEmoteImage.ImageUrlProperty) is { } binding &&
                    (binding.Path?.Path is null or "" or "ProfileImageUrl"));
            var parent = VisualTreeHelper.GetParent(image);
            while (parent is not RoundedClipBorder) parent = VisualTreeHelper.GetParent(parent);
            await verify(host, image, (RoundedClipBorder)parent);
        }
        finally
        {
            await tab.DisposeAsync();
            window.Close();
            ThemeManager.ApplyTheme(AppTheme.Dark);
        }
    });

    private static Task WaitForImageAsync(AnimatedEmoteImage image) => TestWait.UntilAsync(
        () => !image.IsImageLoadPending && image.Source is BitmapSource, TimeSpan.FromSeconds(5), "real profile PNG load");

    private static void Layout(FrameworkElement element, Size? size = null)
    {
        var available = size ?? new Size(288, 400);
        element.Measure(available);
        element.Arrange(new Rect(new Point(), size ?? element.DesiredSize));
        element.UpdateLayout();
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var bitmap = source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static RenderTargetBitmap RenderAvatar(FrameworkElement element)
    {
        // RenderTargetBitmap.Render retains a nested visual's layout offset. A VisualBrush
        // captures the complete local avatar canvas, including centered offline/tab images.
        var bounds = new Rect(new Point(), element.RenderSize);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(element), null, bounds);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Save(BitmapSource bitmap, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SVS_TEST_ARTIFACT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, "profile-avatar-" + name.Replace(' ', '-') + ".png"));
        encoder.Save(output);
    }
}

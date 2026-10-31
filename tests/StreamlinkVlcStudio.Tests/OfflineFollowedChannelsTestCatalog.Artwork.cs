internal static partial class OfflineFollowedChannelsTestCatalog
{
    private static Task OfflineBannerRenderingAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "svs-offline-banner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var bannerPath = Environment.GetEnvironmentVariable("SVS_OFFLINE_BANNER_FIXTURE");
        if (string.IsNullOrWhiteSpace(bannerPath))
        {
            bannerPath = Path.Combine(directory, "banner.png");
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(new LinearGradientBrush(Color.FromRgb(15, 80, 140), Color.FromRgb(60, 180, 200), 0),
                    null, new Rect(0, 0, 960, 240));
                for (var index = 0; index < 8; index++)
                    context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(90, 180, 235, 255)), 24),
                        new Point(index * 160, 0), new Point(index * 160 - 200, 240));
            }
            var bitmap = new RenderTargetBitmap(960, 240, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(bannerPath);
            encoder.Save(file);
        }
        var window = new MainWindow();
        ApplicationTestCatalog.RemoveMainWindowAutomaticStartup(window);
        var channel = Offline("shroud") with
        {
            ProfileImageUrl = new Uri(Environment.GetEnvironmentVariable("SVS_OFFLINE_AVATAR_FIXTURE") ??
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "profile-images", "nicewigg-transparent-150.png")).AbsoluteUri,
            BannerImageUrl = new Uri(bannerPath).AbsoluteUri,
            LastLiveAtUtc = DateTimeOffset.UtcNow.AddDays(-1)
        };
        var card = new OfflineFollowedChannelViewModel(channel, _ => Task.CompletedTask, togglePin: _ => { }, isPinned: true);
        var host = new ContentControl
        {
            ContentTemplate = (DataTemplate)window.Resources["OfflineFollowedChannelTemplate"],
            Content = card,
            Resources = window.Resources,
            FontFamily = window.FontFamily,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        ResourceDictionary? previousPalette = null;
        try
        {
            // Loading the same URL at its original size must not reuse a banner-sized cache
            // entry: avatars and other callers retain their existing pixel-preserving behavior.
            var original = new AnimatedEmoteImage { ImageUrl = channel.BannerImageUrl };
            await TestWait.UntilAsync(() => !original.IsImageLoadPending && original.Source is BitmapSource,
                TimeSpan.FromSeconds(5), "original banner dimensions");
            var originalWidth = ((BitmapSource)original.Source!).PixelWidth;
            foreach (var theme in Enum.GetValues<AppTheme>())
            {
                if (previousPalette is not null) host.Resources.MergedDictionaries.Remove(previousPalette);
                var palette = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/StreamStudio;component/Themes/Colors/{theme}Colors.xaml")
                };
                host.Resources.MergedDictionaries.Add(palette);
                previousPalette = palette;
                foreach (var width in new[] { 288.0, 240.0 })
                {
                    card.Update(channel);
                    ArrangeCard();
                    var image = Descendants<AnimatedEmoteImage>(host).Single(item => item.Name == "OfflineBannerImage");
                    await TestWait.UntilAsync(() => !image.IsImageLoadPending && image.Source is BitmapSource,
                        TimeSpan.FromSeconds(5), "offline banner image load");
                    ArrangeCard();
                    var banner = Descendants<RoundedClipBorder>(host).Single(border => border.CornerRadius.TopLeft == 11);
                    Assert.Equal(64.0, banner.ActualHeight);
                    Assert.Equal(Math.Min(640, originalWidth), ((BitmapSource)image.Source!).PixelWidth);
                    Assert.Equal(Stretch.UniformToFill, image.Stretch);
                    Assert.True(Descendants<TextBlock>(host).Any(text => text.Text == "OFFLINE"));
                    var lastLive = Descendants<TextBlock>(host).Single(text => text.Text == "Last live 1 day ago");
                    var pin = Descendants<Button>(host).Single(button => ReferenceEquals(button.Command, card.TogglePinCommand));
                    Assert.True(Bounds(lastLive, host).Right <= Bounds(pin, host).Left + 0.5);
                    Assert.Equal(card.LastLiveText, System.Windows.Automation.AutomationProperties.GetHelpText(
                        Descendants<Button>(host).Single(button => ReferenceEquals(button.Command, card.OpenCommand))));
                    SaveImage(host, $"offline-banner-{theme}-{width:0}");
                    var loaded = CardPixels();
                    card.Update(channel with { BannerImageUrl = "", LastLiveAtUtc = null });
                    ArrangeCard();
                    Assert.Equal(null, image.Source);
                    Assert.True(Descendants<TextBlock>(host).Any(text => text.Text == "Last live unavailable"));
                    var fallback = CardPixels();
                    Assert.Equal(false, loaded.SequenceEqual(fallback));
                    SaveImage(host, $"offline-banner-{theme}-{width:0}-fallback");
                    card.Update(channel with { BannerImageUrl = new Uri(Path.Combine(directory, "missing.png")).AbsoluteUri, LastLiveAtUtc = null });
                    await TestWait.UntilAsync(() => !image.IsImageLoadPending, TimeSpan.FromSeconds(5), "offline banner failure");
                    ArrangeCard();
                    Assert.True(image.HasImageLoadFailed);
                    Assert.True(fallback.SequenceEqual(CardPixels()), "A failed banner must restore the same theme fallback as an empty banner.");

                    void ArrangeCard()
                    {
                        host.Measure(new Size(width, 146));
                        host.Arrange(new Rect(0, 0, width, 146));
                        host.UpdateLayout();
                    }

                    byte[] CardPixels()
                    {
                        var rendered = WpfVisualTest.Render(host);
                        var pixels = new byte[rendered.PixelWidth * rendered.PixelHeight * 4];
                        rendered.CopyPixels(pixels, rendered.PixelWidth * 4, 0);
                        return pixels;
                    }
                }
            }
        }
        finally
        {
            host.Content = null;
            window.Close();
            Directory.Delete(directory, recursive: true);
        }
    });
}

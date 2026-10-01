using System.Windows.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

internal static class EmojiTitleTestCatalog
{
    private const string LongLockedTitle =
        "\U0001F512 LIVE\U0001F512LOCK IN\U0001F512GTA V\U0001F512NOPIXEL V\U0001F512MEGA DAY\U0001F512" +
        "ULTRA DAY\U0001F512DO NOT MISS IT\U0001F512QUICK REACT BEFORE GTA\U0001F512BIG DAY\U0001F512" +
        "MORE REACTS\U0001F512LOCK IN\U0001F512";

    // Captured from Twitch VOD 2888300423 on 2026-10-01. Retain its VS16 selectors.
    private const string SearchVodTitle =
        "\U0001F512\uFE0F LIVE\U0001F512\uFE0FLOCK IN\U0001F512\uFE0FGTA V\U0001F512\uFE0FNOPIXEL V" +
        "\U0001F512\uFE0FMEGA DAY\U0001F512\uFE0FULTRA DAY\U0001F512\uFE0FDO NOT MISS IT" +
        "\U0001F512\uFE0FQUICK REACT BEFORE\U0001F512\uFE0FALSO" +
        "\U0001F512\uFE0FSECRET ROCKSTAR PACKAGE??\U0001F512\uFE0F??";

    private static readonly string[] ExampleTitles =
    [
        "\u26F5LIVE\u26F5QUICK\u26F5GTA 5\u26F5NOPIXEL 5\u26F5BOAT TIME\u26F5BOAT HEIST POGGERS\u26F5DONT",
        "\U0001F698LIVE\U0001F698HERE\U0001F698LOCK IN\U0001F698MEGA DAY\U0001F698GTA 5\U0001F698NOPIXEL V\U0001F698BUT ALSO\U0001F698CHILL REACT",
        "\U0001F479LIVE\U0001F479DRAMA\U0001F479NEWS\U0001F479GTA V\U0001F479NOPIXEL V\U0001F479 5+5 = 10\U0001F479BIG DAY"
    ];

    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("emoji titles: Twitch and Kick VOD cards render color glyphs", VodCardsAsync),
        ("emoji titles: pasted Twitch VOD search title and summary render color glyphs", SearchPopupAsync),
        ("emoji titles: followed and browse live cards render color glyphs", LiveCardsAsync),
        ("emoji titles: graphemes and presentation selectors retain original text", UnicodeSequencesAsync),
        ("emoji titles: title bindings survive updates nulls and recycled data contexts", BindingUpdatesAsync),
        ("emoji titles: inherited fonts and monitor DPI refresh cached images", FontAndDpiAsync),
        ("emoji titles: native wrapping and ellipsis stay inside title bounds", TitleLayoutAsync),
        ("emoji titles: long card titles cannot paint emoji over metadata", LongCardTitleBoundsAsync),
        ("emoji titles: trimmed emoji do not cover the ellipsis and return after resizing", TrimmedEmojiAsync),
        ("emoji titles: downloaded VOD titles use color glyphs", DownloadsAsync),
        ("emoji titles: regular compact and picture-in-picture tab titles use color glyphs", TabTitlesAsync),
        ("emoji titles: full stream details tooltips render color glyphs", ToolTipsAsync)
    ];

    private static Task SearchPopupAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var target = StreamInputParser.Parse("https://www.twitch.tv/videos/2888300423", PlatformKind.Twitch) with
        {
            DisplayTitle = SearchVodTitle,
            Channel = "xqc",
            CategoryName = "Just Chatting"
        };
        var result = new StreamSearchResultViewModel(target, new StreamlinkProbeResult(true, "Twitch VOD"),
            new StreamMetadataResult(StreamMetadataState.Available, "", "xQc", "", "Just Chatting"),
            (_, _) => Task.CompletedTask);
        var status = "Twitch VOD found: " + SearchVodTitle;
        var window = new MainWindow();
        ApplicationTestCatalog.RemoveMainWindowAutomaticStartup(window);
        try
        {
            var popup = (Popup)window.FindName("HomeStreamSearchPopup");
            var host = (Border)popup.Child;
            popup.Child = null;
            host.ClearValue(FrameworkElement.WidthProperty);
            host.Resources = window.Resources;
            host.DataContext = new
            {
                StreamSearchResultsTitle = "1 search result",
                StreamSearchStatus = status,
                StreamSearchResults = new[] { result },
                IsStreamSearchRunning = false,
                IsStreamSearchEmptyVisible = false,
                IsStreamSearchResultsVisible = true
            };
            TextElement.SetFontFamily(host, window.FontFamily);
            TextElement.SetFontSize(host, window.FontSize);
            TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
            foreach (var width in new[] { 560.0, 360.0 })
            {
                Layout(host, width);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Layout(host, width);
                var title = Descendants<TextBlock>(host).Single(block => block.FontSize == 14);
                var content = (Grid)window.FindName("HomeSearchPopupContent");
                var header = content.Children.OfType<DockPanel>().Single(panel => Grid.GetRow(panel) == 0);
                var summary = Descendants<TextBlock>(header).Single(block => block.FontSize == 11);
                SavePreview(host, $"search-popup-{width:0}");
                Assert.True(BitmapAssert.CountColoredPixels(RenderAtOrigin(title)) > 15,
                    $"The pasted VOD search title must render color locks at width {width}.");
                Assert.True(BitmapAssert.CountColoredPixels(RenderAtOrigin(summary)) > 15,
                    $"The search summary must render color locks at width {width}.");
                Assert.Equal(SearchVodTitle, UIElementAutomationPeer.CreatePeerForElement(title)!.GetName());
                Assert.Equal(status, UIElementAutomationPeer.CreatePeerForElement(summary)!.GetName());
                Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
                Assert.Equal(TextTrimming.CharacterEllipsis, summary.TextTrimming);
            }
        }
        finally
        {
            window.Close();
        }
    });

    private static Task VodCardsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var window = new MainWindow();
        ApplicationTestCatalog.RemoveMainWindowAutomaticStartup(window);
        try
        {
            var template = ((ItemsControl)window.FindName("VodCardsItemsControl")).ItemTemplate;
            foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
            {
                for (var index = 0; index < ExampleTitles.Length; index++)
                {
                    var title = ExampleTitles[index];
                    var vod = CreateVod(platform, title);
                    var presenter = CreatePresenter(window, template, vod);
                    Layout(presenter, 324);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Layout(presenter, 324);
                    var titleBlock = Descendants<TextBlock>(presenter).Single(block =>
                        block.FontSize == 14 && block.MaxHeight == 38);
                    SavePreview(presenter, $"vod-{platform}-{index}");
                    Assert.True(BitmapAssert.CountColoredPixels(RenderAtOrigin(titleBlock)) > 15,
                        $"The {platform} VOD title '{title}' must contain rendered color emoji, not monochrome glyphs.");
                    AssertHeightBounds(titleBlock, 38);
                }
            }
        }
        finally
        {
            window.Close();
        }
    });

    private static Task LiveCardsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var window = new MainWindow();
        ApplicationTestCatalog.RemoveMainWindowAutomaticStartup(window);
        try
        {
            var template = (DataTemplate)window.FindResource("LiveStreamCardTemplate");
            foreach (var source in new[] { LiveStreamCardSource.Followed, LiveStreamCardSource.Browse })
            {
                var card = new LiveStreamCardViewModel(new LiveStreamCardData(source,
                    StreamInputParser.Parse("xqc", PlatformKind.Twitch), PlatformKind.Twitch,
                    "xqc", "xQc", ExampleTitles[0], "Grand Theft Auto V", 12345, "", "", null, false, "en"),
                    (_, _) => Task.CompletedTask);
                var presenter = CreatePresenter(window, template, card);
                Layout(presenter, 324);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Layout(presenter, 324);
                var titleBlock = Descendants<TextBlock>(presenter).Single(block =>
                    block.FontSize == 13 && block.MaxHeight == 36);
                SavePreview(presenter, $"live-{source}");
                Assert.True(BitmapAssert.CountColoredPixels(RenderAtOrigin(titleBlock)) > 15,
                    $"The {source} live title must contain rendered color emoji, not monochrome glyphs.");
                Assert.Equal(TextWrapping.Wrap, titleBlock.TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, titleBlock.TextTrimming);
                AssertHeightBounds(titleBlock, 36);
            }
        }
        finally
        {
            window.Close();
        }
    });

    private static Task UnicodeSequencesAsync() => TestSta.RunOffscreenAsync(() =>
    {
        string[] emoji =
        [
            "\u26F5", "\U0001F698", "\U0001F479", "\U0001F422", "\U0001F3B2", "\u2764\uFE0F",
            "\U0001F44D\U0001F3FD", "\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466", "1\uFE0F\u20E3"
        ];
        var text = "caf\u00E9 \u4E2D\u6587 \u00A9 \u2122 \u26F5\uFE0E " + string.Concat(emoji) + " suffix";
        var block = new EmojiTextBlock { SourceText = text, FontSize = 14, Foreground = Brushes.White };
        Layout(block, 640);
        var images = EmojiImages(block);
        SavePreview(block, "unicode-graphemes");
        Assert.Equal(emoji.Length, images.Length);
        Assert.Equal(text, ReconstructText(block));
        for (var index = 0; index < emoji.Length; index++)
        {
            Assert.Equal(emoji[index], AutomationProperties.GetName(images[index]));
            Assert.True(images[index].Source.IsFrozen);
            Assert.True(BitmapAssert.CountPixels(images[index].Source, (_, _, _) => true) > 20);
            if (index != 4)
                Assert.True(BitmapAssert.CountColoredPixels(images[index].Source) > 20,
                    $"Emoji grapheme {index} must retain its font's actual color palette.");
        }
        Assert.True(BitmapAssert.CountPixels(images[0].Source, (red, green, blue) =>
            red > 160 && green > 100 && blue < 120) > 20, "The sailboat must retain its yellow sail.");
        Assert.True(BitmapAssert.CountPixels(images[1].Source, (red, green, blue) =>
            blue > 140 && green > 100 && red < 140) > 20, "The oncoming car must retain its blue body.");
        Assert.True(BitmapAssert.CountPixels(images[2].Source, (red, green, blue) =>
            red > 140 && green < 120 && blue < 140) > 20, "The ogre must retain its red face.");
        return Task.CompletedTask;
    });

    private static Task LongCardTitleBoundsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var window = new MainWindow();
        ApplicationTestCatalog.RemoveMainWindowAutomaticStartup(window);
        try
        {
            var template = (DataTemplate)window.FindResource("LiveStreamCardTemplate");
            foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
                foreach (var source in new[] { LiveStreamCardSource.Followed, LiveStreamCardSource.Browse })
                {
                    var card = new LiveStreamCardViewModel(new LiveStreamCardData(source,
                        StreamInputParser.Parse("xqc", platform), platform,
                        "xqc", "xQc", LongLockedTitle, "Grand Theft Auto V", 12345, "", "", null, false, "en"),
                        (_, _) => Task.CompletedTask);
                    var presenter = CreatePresenter(window, template, card);
                    foreach (var dpi in new[] { 1.0, 1.25, 1.5, 2.0 })
                    {
                        VisualTreeHelper.SetRootDpi(presenter, new DpiScale(dpi, dpi));
                        // Reuse the same card while narrowing it again to catch stale layout state.
                        foreach (var width in new[] { 244.0, 324.0, 424.0, 244.0 })
                        {
                            Layout(presenter, width);
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            Layout(presenter, width);
                            var title = Descendants<EmojiTextBlock>(presenter).Single();
                            if (platform == PlatformKind.Twitch && source == LiveStreamCardSource.Followed && dpi == 1 && width == 324)
                                SavePreview(presenter, "long-live-title");
                            Assert.Equal(LongLockedTitle, title.SourceText);
                            Assert.Equal(LongLockedTitle, ReconstructText(title));
                            Assert.Equal(36.0, title.ActualHeight);
                            Assert.Equal(dpi, VisualTreeHelper.GetDpi(title).DpiScaleX);
                            Assert.True(BitmapAssert.CountColoredPixels(RenderAtOrigin(title)) > 15,
                                "Visible title emoji must survive clipping and resizing.");
                            var titleOrigin = title.TranslatePoint(new Point(), presenter);
                            var bitmap = RenderAtOrigin(presenter);
                            var bottom = (int)Math.Ceiling(titleOrigin.Y + title.ActualHeight);
                            var belowTitle = new CroppedBitmap(bitmap, new Int32Rect(0, bottom,
                                bitmap.PixelWidth, bitmap.PixelHeight - bottom));
                            var leakedPixels = BitmapAssert.CountPixels(belowTitle,
                                (red, green, blue) => red > 140 && green > 95 && blue < 130);
                            Assert.True(leakedPixels == 0,
                                $"{source}/{platform}, width {width}, DPI {dpi}: emoji must not paint below the " +
                                $"title's {title.ActualHeight}-DIP bounds. Found {leakedPixels} yellow pixels below Y={bottom}.");
                        }
                    }
                }
        }
        finally
        {
            window.Close();
        }
    });

    private static Task TrimmedEmojiAsync() => TestSta.RunOffscreenAsync(() =>
    {
        const string text = "A VERY LONG TITLE BEFORE THE FIRST EMOJI \U0001F512\U0001F512";
        var block = new EmojiTextBlock
        {
            SourceText = text,
            FontSize = 13,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        foreach (var trimming in new[] { TextTrimming.CharacterEllipsis, TextTrimming.WordEllipsis })
            foreach (var direction in new[] { FlowDirection.LeftToRight, FlowDirection.RightToLeft })
                foreach (var dpi in new[] { 1.0, 1.25, 1.5, 2.0 })
                {
                    block.TextTrimming = trimming;
                    block.FlowDirection = direction;
                    VisualTreeHelper.SetRootDpi(block, new DpiScale(dpi, dpi));
                    Layout(block, 75);
                    Assert.Equal(0, BitmapAssert.CountColoredPixels(RenderAtOrigin(block)));
                    Layout(block, 750);
                    Assert.True(BitmapAssert.CountColoredPixels(RenderAtOrigin(block)) > 15,
                        "Emoji hidden by ellipsis must return when the complete title fits.");
                    Layout(block, 75);
                    Assert.Equal(0, BitmapAssert.CountColoredPixels(RenderAtOrigin(block)));
                    block.SourceText = "\U0001F512 short";
                    Layout(block, 75);
                    Assert.True(BitmapAssert.CountColoredPixels(RenderAtOrigin(block)) > 15);
                    block.SourceText = text;
                    Layout(block, 75);
                    Assert.Equal(0, BitmapAssert.CountColoredPixels(RenderAtOrigin(block)));
                }
        block.FlowDirection = FlowDirection.LeftToRight;
        VisualTreeHelper.SetRootDpi(block, new DpiScale(1, 1));
        Layout(block, 75);
        SavePreview(block, "hidden-emoji-ellipsis");
        Assert.Equal(text, ReconstructText(block));
        return Task.CompletedTask;
    });

    private static Task BindingUpdatesAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var first = new TitleSource(ExampleTitles[0]);
        var block = new EmojiTextBlock { FontSize = 14, Foreground = Brushes.White };
        block.SetBinding(EmojiTextBlock.SourceTextProperty, new Binding(nameof(TitleSource.Title)));
        var host = new Border { DataContext = first, Child = block };
        Layout(host, 324);
        var peer = UIElementAutomationPeer.CreatePeerForElement(block);
        Assert.NotNull(peer);
        Assert.Equal(first.Title, block.SourceText);
        Assert.Equal(first.Title, peer!.GetName());
        foreach (var text in new string?[] { ExampleTitles[1], "plain title", "", null, ExampleTitles[2] })
        {
            first.Title = text;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Layout(host, 324);
            Assert.True(BindingOperations.IsDataBound(block, EmojiTextBlock.SourceTextProperty));
            Assert.Equal(text, block.SourceText);
            Assert.Equal(text ?? "", ReconstructText(block));
            Assert.Equal(text ?? "", peer.GetName());
            if (string.IsNullOrEmpty(text))
                Assert.Equal(0, block.Inlines.Count);
        }
        var second = new TitleSource(ExampleTitles[0]);
        host.DataContext = second;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        first.Title = "stale title";
        Layout(host, 324);
        Assert.Equal(second.Title, block.SourceText);
        Assert.Equal(second.Title, ReconstructText(block));
        AutomationProperties.SetName(block, "custom accessible title");
        Assert.Equal("custom accessible title", peer.GetName());
    });

    private static Task FontAndDpiAsync() => TestSta.RunOffscreenAsync(() =>
    {
        var block = new EmojiTextBlock { SourceText = "title \u26F5", FontWeight = FontWeights.SemiBold };
        var host = new Border { Child = block };
        TextElement.SetFontSize(host, 13);
        TextElement.SetForeground(host, Brushes.White);
        VisualTreeHelper.SetRootDpi(host, new DpiScale(1, 1));
        Layout(host, 324);
        var first = EmojiImages(block).Single();
        Assert.Equal(13.0, first.Height);
        Assert.True(ReferenceEquals(first.Source, UnicodeEmojiRenderer.GetEmojiImageSource("\u26F5", 13)));
        var plainRun = block.Inlines.OfType<Run>().Single();
        Assert.Equal(13.0, plainRun.FontSize);
        Assert.Equal(FontWeights.SemiBold, plainRun.FontWeight);
        TextElement.SetForeground(host, Brushes.LightGreen);
        Assert.True(ReferenceEquals(Brushes.LightGreen, plainRun.Foreground));

        VisualTreeHelper.SetRootDpi(host, new DpiScale(4, 4));
        Layout(host, 324);
        var highDpi = EmojiImages(block).Single();
        var directHighDpi = (BitmapSource)UnicodeEmojiRenderer.GetEmojiImageSource("\u26F5", 13, 4)!;
        Assert.Equal(4.0, VisualTreeHelper.GetDpi(block).PixelsPerDip);
        Assert.Equal(13.0, highDpi.Height);
        Assert.True(((BitmapSource)highDpi.Source).PixelHeight > ((BitmapSource)first.Source).PixelHeight,
            $"A monitor DPI increase must regenerate a higher-resolution emoji bitmap. " +
            $"Old height: {((BitmapSource)first.Source).PixelHeight}; new height: {((BitmapSource)highDpi.Source).PixelHeight}; " +
            $"direct high-DPI height: {directHighDpi.PixelHeight}; cached old source: {ReferenceEquals(first.Source, highDpi.Source)}.");
        VisualTreeHelper.SetRootDpi(host, new DpiScale(1, 1));
        Layout(host, 324);
        Assert.True(ReferenceEquals(first.Source, EmojiImages(block).Single().Source));
        TextElement.SetFontSize(host, 24);
        Layout(host, 324);
        Assert.Equal(24.0, EmojiImages(block).Single().Height);
        Assert.Equal(24.0, block.Inlines.OfType<Run>().Single().FontSize);
        Assert.Equal("title \u26F5", ReconstructText(block));
        return Task.CompletedTask;
    });

    private static Task TitleLayoutAsync() => TestSta.RunOffscreenAsync(() =>
    {
        var singleLine = new EmojiTextBlock
        {
            SourceText = ExampleTitles[1],
            FontSize = 14,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = 170,
            Margin = new Thickness(8)
        };
        var wide = new EmojiTextBlock
        {
            SourceText = ExampleTitles[1],
            FontSize = 14,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = 620,
            Margin = new Thickness(8)
        };
        var wrapped = new EmojiTextBlock
        {
            SourceText = ExampleTitles[0],
            FontSize = 13,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 18,
            MaxHeight = 36,
            Width = 280,
            Margin = new Thickness(8)
        };
        var stack = new StackPanel();
        stack.Children.Add(singleLine);
        stack.Children.Add(wide);
        stack.Children.Add(wrapped);
        var host = new Border { Background = new SolidColorBrush(Color.FromRgb(36, 36, 39)), Child = stack };
        TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
        Layout(host, 640);
        SavePreview(host, "wrapping-and-ellipsis");
        SavePreview(singleLine, "single-line-ellipsis");
        Assert.Equal(170.0, singleLine.ActualWidth);
        Assert.Equal(wide.ActualHeight, singleLine.ActualHeight);
        Assert.Equal(36.0, wrapped.ActualHeight);
        Assert.Equal(ExampleTitles[1], ReconstructText(singleLine));
        Assert.Equal(ExampleTitles[0], ReconstructText(wrapped));
        var singleLineColorCount = BitmapAssert.CountColoredPixels(RenderAtOrigin(singleLine));
        var wrappedColorCount = BitmapAssert.CountColoredPixels(RenderAtOrigin(wrapped));
        Assert.True(singleLineColorCount > 15, $"Single-line color pixels: {singleLineColorCount}; " +
            $"image sizes: {string.Join(", ", EmojiImages(singleLine).Select(image => image.RenderSize))}.");
        Assert.True(wrappedColorCount > 15, $"Wrapped color pixels: {wrappedColorCount}.");
        return Task.CompletedTask;
    });

    private static Task DownloadsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var target = StreamInputParser.Parse("https://www.twitch.tv/videos/123", PlatformKind.Twitch) with
        {
            DisplayTitle = ExampleTitles[0]
        };
        var item = new VodDownloadItem(Guid.NewGuid(), target, "best", DateTimeOffset.UtcNow);
        var download = new VodDownloadViewModel(item, (execute, enabled) => new AsyncRelayCommand(execute, enabled),
            _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask);
        var view = new VodDownloadsView { DataContext = new { VodDownloads = new[] { download } } };
        Layout(view, 640);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Layout(view, 640);
        var title = Descendants<EmojiTextBlock>(view).Single();
        Assert.Equal(download.Title, title.SourceText);
        Assert.Equal(download.Title, ReconstructText(title));
        Assert.True(BitmapAssert.CountColoredPixels(RenderAtOrigin(title)) > 15);
        SavePreview(view, "downloaded-vod");
    });

    private static Task TabTitlesAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var target = StreamInputParser.Parse("https://www.twitch.tv/videos/123", PlatformKind.Twitch) with
        {
            DisplayTitle = ExampleTitles[0]
        };
        await using var tab = TestViewModels.CreateTab(target, "best", new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(), action => action());
        using var strip = new TabStripItemViewModel([tab], tab);
        var window = new MainWindow();
        ApplicationTestCatalog.RemoveMainWindowAutomaticStartup(window);
        try
        {
            DataTemplate[] templates =
            [
                ((ListBox)window.FindName("TabListBox")).ItemTemplate,
                ((ComboBox)window.FindName("CompactTabSelector")).ItemTemplate
            ];
            for (var index = 0; index < templates.Length; index++)
            {
                var presenter = CreatePresenter(window, templates[index], strip);
                Layout(presenter, 324);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Layout(presenter, 324);
                var title = Descendants<EmojiTextBlock>(presenter).Single();
                Assert.Equal(tab.Title, title.SourceText);
                Assert.Equal(tab.Title, ReconstructText(title));
                Assert.True(BitmapAssert.CountColoredPixels(RenderAtOrigin(title)) > 15);
                SavePreview(presenter, $"tab-{index}");
            }
            var detached = new DetachedVideoWindow([tab]);
            try
            {
                var titleBar = (Border)detached.FindName("TitleBar");
                ((Panel)titleBar.Parent).Children.Remove(titleBar);
                var host = new Border { Resources = detached.Resources, DataContext = detached, Child = titleBar };
                Layout(host, 640);
                var title = Descendants<EmojiTextBlock>(host).Single();
                Assert.Equal(tab.Title, title.SourceText);
                Assert.Equal(tab.Title, ReconstructText(title));
                Assert.True(BitmapAssert.CountColoredPixels(RenderAtOrigin(title)) > 15);
                SavePreview(host, "picture-in-picture-title");
            }
            finally
            {
                detached.Close();
            }
        }
        finally
        {
            window.Close();
        }
    });

    private static Task ToolTipsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var target = StreamInputParser.Parse("https://www.twitch.tv/videos/123", PlatformKind.Twitch) with
        {
            DisplayTitle = ExampleTitles[0]
        };
        await using var tab = TestViewModels.CreateTab(target, "best", new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(), action => action());
        using var strip = new TabStripItemViewModel([tab], tab);
        var window = new MainWindow();
        ApplicationTestCatalog.RemoveMainWindowAutomaticStartup(window);
        try
        {
            var selector = (ComboBox)window.FindName("CompactTabSelector");
            selector.DataContext = new TabSelectionSource(strip);
            var tabPresenter = CreatePresenter(window, ((ListBox)window.FindName("TabListBox")).ItemTemplate, strip);
            Layout(tabPresenter, 324);
            var tabChrome = Descendants<Border>(tabPresenter).Single(border => border.Name == "TabChrome");
            foreach (var targetElement in new FrameworkElement[] { tabChrome, selector })
            {
                Assert.True(targetElement.ToolTip is ToolTip);
                var tooltip = (ToolTip)targetElement.ToolTip;
                tooltip.PlacementTarget = targetElement;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(strip.ToolTip, tooltip.Content);
                var presenter = CreatePresenter(window, tooltip.ContentTemplate, tooltip.Content);
                Layout(presenter, 324);
                var title = Descendants<EmojiTextBlock>(presenter).Single();
                Assert.Equal(strip.ToolTip, title.SourceText);
                Assert.Equal(strip.ToolTip, ReconstructText(title));
                Assert.True(BitmapAssert.CountColoredPixels(RenderAtOrigin(title)) > 15);
                SavePreview(presenter, $"tooltip-{targetElement.Name}");
            }
        }
        finally
        {
            window.Close();
        }
    });

    private static Image[] EmojiImages(EmojiTextBlock block) => block.Inlines.OfType<InlineUIContainer>()
        .Select(container => container.Child).OfType<Image>().ToArray();

    private static string ReconstructText(EmojiTextBlock block) => string.Concat(block.Inlines.Select(inline => inline switch
    {
        Run run => run.Text,
        InlineUIContainer { Child: Image image } => AutomationProperties.GetName(image),
        _ => ""
    }));

    private static void AssertHeightBounds(FrameworkElement element, double maximumHeight)
    {
        var clip = VisualTreeHelper.GetClip(element)?.Bounds;
        var visibleHeight = clip is { } bounds ? Math.Min(bounds.Height, element.RenderSize.Height) : element.RenderSize.Height;
        Assert.True(visibleHeight <= maximumHeight + 0.1,
            $"Visible title height is {visibleHeight:G17}; render height is {element.RenderSize.Height:G17}; clip is {clip}.");
    }

    private static RenderTargetBitmap RenderAtOrigin(FrameworkElement element)
    {
        var bounds = new Rect(element.RenderSize);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var brush = new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            context.DrawRectangle(brush, null, bounds);
        }
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(bounds.Height)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private sealed class TitleSource(string? initialTitle) : ObservableObject
    {
        private string? title = initialTitle;
        public string? Title
        {
            get => title;
            set => SetProperty(ref title, value);
        }
    }

    private sealed class TabSelectionSource(TabStripItemViewModel initialSelection)
    {
        public TabStripItemViewModel SelectedTabStripItem { get; set; } = initialSelection;
        public IReadOnlyList<TabStripItemViewModel> TabStripItems { get; } = [initialSelection];
    }

    private static VodViewModel CreateVod(PlatformKind platform, string title) => platform == PlatformKind.Twitch
        ? new VodViewModel(new TwitchVodItem("123", "456", "789", "xqc", "xQc", title, "",
            "https://www.twitch.tv/videos/123", "", null, null, TimeSpan.FromHours(2), 100,
            TwitchVodTypeFilter.Archive), (_, _) => Task.CompletedTask)
        : new VodViewModel(new KickVodItem("123", "456", "789", "xqc", "xQc", title,
            "https://kick.com/xqc/videos/123", "", "", "Grand Theft Auto V", null, null,
            TimeSpan.FromHours(2), 100), (_, _) => Task.CompletedTask);

    private static ContentPresenter CreatePresenter(MainWindow window, DataTemplate template, object content) => new()
    {
        Content = content,
        ContentTemplate = template,
        Resources = window.Resources,
        Width = 324
    };

    private static void Layout(FrameworkElement element, double width)
    {
        element.Width = width;
        element.Measure(new Size(width, double.PositiveInfinity));
        element.Arrange(new Rect(new Point(), element.DesiredSize));
        element.UpdateLayout();
    }

    private static IEnumerable<TElement> Descendants<TElement>(DependencyObject parent) where TElement : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TElement element)
                yield return element;
            foreach (var descendant in Descendants<TElement>(child))
                yield return descendant;
        }
    }

    private static void SavePreview(FrameworkElement element, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SVS_EMOJI_TITLE_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(directory))
            return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(RenderAtOrigin(element)));
        using var stream = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(stream);
    }
}

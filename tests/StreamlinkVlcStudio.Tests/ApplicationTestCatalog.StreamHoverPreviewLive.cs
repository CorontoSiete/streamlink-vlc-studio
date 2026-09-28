internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> StreamHoverPreviewLiveUiTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_HOVER_CHANNEL"))
            ? []
            : [("stream hover preview: real live video renders in the production card on hover", HoverPreviewLiveCardAsync)];

    private static Task HoverPreviewLiveCardAsync() => TestSta.RunAsync(async () =>
    {
        var settings = await new JsonSettingsService().LoadAsync();
        settings.EnableStreamHoverPreviews = true;
        settings.StreamlinkPath ??= ExecutableResolver.FindStreamlink();
        settings.VlcDirectory ??= ExecutableResolver.FindVlcDirectory();
        var target = StreamInputParser.Parse(Environment.GetEnvironmentVariable("SVS_TEST_HOVER_CHANNEL")!, PlatformKind.Twitch);
        var logger = new MemoryLogger();
        var hoverElapsed = new Stopwatch();
        logger.EntryWritten += (_, entry) =>
        {
            var phase = entry.Message switch
            {
                var message when message.StartsWith("Starting Streamlink for", StringComparison.Ordinal) => "transport starting",
                var message when message.Contains("Found matching plugin", StringComparison.Ordinal) => "provider plugin ready",
                var message when message.Contains("Available streams:", StringComparison.Ordinal) => "provider qualities resolved",
                var message when message.StartsWith("Streamlink HTTP transport ready", StringComparison.Ordinal) => "transport ready",
                var message when message.Contains("Got HTTP request from", StringComparison.Ordinal) => "VLC connected",
                var message when message.Contains("Opening stream:", StringComparison.Ordinal) => "provider stream opening",
                _ when entry.Source == "Hover preview" => entry.Message,
                _ => null
            };
            if (phase is not null) Console.WriteLine($"Live card {hoverElapsed.ElapsedMilliseconds} ms: {phase}");
        };
        await using var controller = new StreamHoverPreviewController(settings, new StreamlinkService(logger), logger);
        var resources = new MainWindow();
        RemoveMainWindowAutomaticStartup(resources);
        var card = (Button)((DataTemplate)resources.Resources["LiveStreamCardTemplate"]).LoadContent();
        card.Width = 316;
        card.Height = 314;
        card.DataContext = new LiveStreamCardViewModel(new(LiveStreamCardSource.Followed, target, target.Platform,
            target.Channel, target.Channel, "Live stream hover preview", "", null, "", "", null, false, ""), (_, _) => Task.CompletedTask);
        var panel = new Grid();
        panel.SetResourceReference(Panel.BackgroundProperty, "StudioBaseBrush");
        panel.Children.Add(card);
        var window = new Window
        {
            Title = "Live hover preview verification",
            Content = panel,
            Width = 460,
            Height = 420,
            Topmost = true,
            DataContext = new { Settings = settings, HoverPreviews = controller }
        };
        window.Resources.MergedDictionaries.Add(resources.Resources);
        NativeWindowTest.TryGetCursorPosition(out var previousCursor);
        try
        {
            window.Show();
            NativeWindowTest.ActivateWindow(new System.Windows.Interop.WindowInteropHelper(window).Handle);
            var outside = panel.PointToScreen(new Point(4, 4));
            NativeWindowTest.SetCursorPosition((int)outside.X, (int)outside.Y);
            await Task.Delay(100);
            var inside = card.PointToScreen(new Point(140, 80));
            hoverElapsed.Restart();
            NativeWindowTest.SetCursorPosition((int)inside.X, (int)inside.Y);
            var preview = FindHoverVisual<StreamHoverPreview>(card)!;
            byte[]? first = null;
            long firstFrameAt = -1;
            var changed = false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (!changed)
            {
                await Task.Delay(100, timeout.Token);
                if (FindHoverVisual<Image>(preview)?.Source is not BitmapSource source) continue;
                if (firstFrameAt < 0)
                {
                    firstFrameAt = hoverElapsed.ElapsedMilliseconds;
                    Console.WriteLine($"Live card first visible frame: {firstFrameAt} ms.");
                }
                var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
                source.CopyPixels(pixels, source.PixelWidth * 4, 0);
                first ??= pixels;
                changed = !first.AsSpan().SequenceEqual(pixels);
            }
            var screenshot = new RenderTargetBitmap(316, 314, 96, 96, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
                context.DrawRectangle(new VisualBrush(card), null, new Rect(0, 0, 316, 314));
            screenshot.Render(drawing);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(screenshot));
            var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".tmp"));
            Directory.CreateDirectory(directory);
            using (var file = File.Create(Path.Combine(directory, $"hover-preview-{target.Platform}.png"))) png.Save(file);
            settings.EnableStreamHoverPreviews = false;
            await Task.Delay(100);
            Assert.True(FindHoverVisual<Image>(preview)?.Source is null);
            await controller.DisposeAsync();
            Console.WriteLine($"Live {target.Platform} card first visible frame {firstFrameAt} ms after hover; " +
                "rendered changing video; settings toggle cleared the image and stopped playback.");
        }
        finally
        {
            window.Close();
            resources.Close();
            NativeWindowTest.SetCursorPosition(previousCursor.X, previousCursor.Y);
        }
    });
}

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> ChatTextSizeTests { get; } =
    [
        ("chat text size is available in overlay settings without a selected stream", () => TestSta.RunAsync(async () =>
        {
            var settings = new AppSettings();
            await using var main = TestViewModels.CreateMain(settings, new FakeSettingsService(settings),
                new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
                new MemoryLogger(), action => action());
            var window = new MainWindow { DataContext = main };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, main);
            try
            {
                main.IsSettingsOpen = true;
                main.ShowChatSettingsCommand.Execute(null);
                window.Show();
                window.UpdateLayout();
                var slider = (Slider)window.FindName("ChatTextSizeSlider");
                Assert.True(slider.IsVisible);
                Assert.True(slider.IsEnabled, "Overlay text size must be editable from Home before a stream is selected.");
                slider.SetCurrentValue(Slider.ValueProperty, 24d);
                Assert.Equal(24d, settings.Chat.VlcOverlayFontSize);
                Assert.Equal(ChatSettings.DefaultFontSize, settings.Chat.FontSize);
                SaveChatTextSizeScreenshot(window, "home-overlay-settings.png");

                settings.Chat.Layout = ChatLayout.Docked;
                FlushChatTextSizeBindings(window);
                Assert.Equal(ChatSettings.DefaultFontSize, slider.Value);
                slider.SetCurrentValue(Slider.ValueProperty, 18d);
                Assert.Equal(18d, settings.Chat.FontSize);
                Assert.Equal(24d, settings.Chat.VlcOverlayFontSize);
                settings.Chat.Layout = ChatLayout.Overlay;
                FlushChatTextSizeBindings(window);
                Assert.Equal(24d, slider.Value);

                settings.Chat.Layout = ChatLayout.Hidden;
                FlushChatTextSizeBindings(window);
                Assert.True(!slider.IsEnabled);
                settings.Chat = new ChatSettings { VlcOverlayFontSize = 20 };
                FlushChatTextSizeBindings(window);
                Assert.True(slider.IsEnabled);
                Assert.Equal(20d, slider.Value);
            }
            finally { window.Close(); }
        })),
        ("chat text size follows the selected stream and saves overlay defaults and overrides", () => TestSta.RunAsync(async () =>
        {
            var root = CreateTempTestDirectory();
            var service = new JsonSettingsService(Path.Combine(root, "settings.json"));
            var settings = new AppSettings();
            settings.Chat.ConnectAutomatically = false;
            var streamlink = new FakeStreamlinkService();
            var playback = new FakePlaybackEngineFactory();
            var chat = new FakeChatClientFactory();
            var logger = new MemoryLogger();
            await using var main = TestViewModels.CreateMain(settings, service, streamlink, playback, chat,
                logger, action => action());
            var window = new MainWindow { DataContext = main };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, main);
            try
            {
                main.IsSettingsOpen = true;
                main.ShowChatSettingsCommand.Execute(null);
                window.Show();
                var slider = (Slider)window.FindName("ChatTextSizeSlider");
                slider.SetCurrentValue(Slider.ValueProperty, 20d);
                var twitch = TestViewModels.CreateTab(StreamInputParser.Parse("fixture", PlatformKind.Twitch),
                    "best", streamlink, playback, chat, logger, action => action());
                var kick = TestViewModels.CreateTab(StreamInputParser.Parse("fixture", PlatformKind.Kick),
                    "best", streamlink, playback, chat, logger, action => action());
                main.Tabs.Add(twitch);
                main.Tabs.Add(kick);
                main.SelectedTab = twitch;
                FlushChatTextSizeBindings(window);
                Assert.Equal(20d, slider.Value);
                Assert.Contains("Twitch: fixture", main.ChatTextSizeDescription);
                slider.SetCurrentValue(Slider.ValueProperty, 28d);

                main.SelectedTab = kick;
                FlushChatTextSizeBindings(window);
                Assert.Equal(20d, slider.Value);
                slider.SetCurrentValue(Slider.ValueProperty, 16d);
                main.SelectedTab = twitch;
                FlushChatTextSizeBindings(window);
                Assert.Equal(28d, slider.Value);
                SaveChatTextSizeScreenshot(window, "stream-overlay-settings.png");

                main.SelectHomeCommand.Execute(null);
                FlushChatTextSizeBindings(window);
                Assert.True(slider.IsEnabled);
                Assert.Equal(20d, slider.Value);
                slider.SetCurrentValue(Slider.ValueProperty, 22d);
                await main.SaveSettingsCommand.ExecuteAsync();
                var loaded = await service.LoadAsync();
                Assert.Equal(22d, loaded.Chat.VlcOverlayFontSize);
                Assert.Equal(28d, loaded.StreamVlcOverlayFontSizes[twitch.Target.StateKey]);
                Assert.Equal(16d, loaded.StreamVlcOverlayFontSizes[kick.Target.StateKey]);
                Assert.Equal(ChatSettings.DefaultFontSize, loaded.Chat.FontSize);
                Assert.Equal(0, playback.CreateCount);

                main.SelectedTab = twitch;
                settings.StreamVlcOverlayFontSizes = new(StringComparer.OrdinalIgnoreCase)
                {
                    [twitch.Target.StateKey] = 30
                };
                FlushChatTextSizeBindings(window);
                Assert.Equal(30d, slider.Value);
            }
            finally { window.Close(); DeleteTempTestDirectory(root); }
        })),
        ("chat text size rerenders replay pixels without blanking chat or rebuilding playback", ChatTextSizeReplayPixelsAsync)
    ];

    private static void FlushChatTextSizeBindings(Window window) => window.Dispatcher.Invoke(
        () => { }, System.Windows.Threading.DispatcherPriority.DataBind);

    private static void SaveChatTextSizeScreenshot(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SVS_TEST_CHAT_SETTINGS_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        FlushChatTextSizeBindings(window);
        window.UpdateLayout();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(WpfVisualTest.Render(window)));
        using var output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
    }

    private static Task ChatTextSizeReplayPixelsAsync() => TestSta.RunAsync(async () =>
    {
        var root = CreateTempTestDirectory();
        var pipeName = $"chat-font-{Guid.NewGuid():N}";
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var playback = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            UsesNativeOverlayOverride = true,
            NativeOverlayPipeNameOverride = pipeName,
            NativeOverlayPositionStatePathOverride = Path.Combine(root, "position")
        });
        var streamlink = new FakeStreamlinkService();
        var chat = new FakeChatClientFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings { StreamlinkPath = "streamlink.exe", VlcDirectory = @"C:\VLC" };
        settings.Chat.VlcOverlayFontSize = 12;
        await using var main = TestViewModels.CreateMain(settings, new FakeSettingsService(settings),
            streamlink, playback, chat, logger, action => dispatcher.Invoke(action));
        var target = new StreamTarget(PlatformKind.Kick, "fixture", "https://vod.kick.com/fixture/index.m3u8",
            StreamTargetKind.KickVod, "fixture-vod", "Chat font fixture", "", TimeSpan.FromMinutes(30),
            DateTimeOffset.UnixEpoch, ChatRoomId: "668");
        var tab = TestViewModels.CreateTab(target, "best", streamlink, playback, chat, logger,
            action => dispatcher.Invoke(action), vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once(
                [new VodChatMessage(TimeSpan.Zero, new ChatMessage(PlatformKind.Kick, "fixture", "Viewer",
                    "Agjp TEXT", DateTimeOffset.UnixEpoch))], TimeSpan.Zero, TimeSpan.FromMinutes(4))));
        main.Tabs.Add(tab);
        main.SelectedTab = tab;
        var window = new MainWindow { DataContext = main };
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, main);
        try
        {
            main.IsSettingsOpen = true;
            main.ShowChatSettingsCommand.Execute(null);
            window.Show();
            var slider = (Slider)window.FindName("ChatTextSizeSlider");
            tab.SetVideoHandle(new IntPtr(42));
            var initial = ReadNativeOverlayPipeMatchingMessageAsync(pipeName,
                IsNativeOverlayRenderedChatFrame, TimeSpan.FromSeconds(4));
            await tab.StartAsync(settings);
            var original = GetNativeOverlayAlphaBounds(await initial);
            var playCount = playback.Engine!.PlayCount;
            var startCount = streamlink.StartCount;
            foreach (var fontSize in new[] { 24d, 36d, 8d, 12d })
            {
                var received = ReceiveSettledChatTextSizeFramesAsync(pipeName);
                slider.SetCurrentValue(Slider.ValueProperty, fontSize);
                var frames = await received;
                var rendered = frames.Where(IsNativeOverlayRenderedChatFrame).ToArray();
                Assert.True(rendered.Length > 0, $"Changing the slider to {fontSize} must render cached chat.");
                Assert.True(frames.All(message => !message.SequenceEqual(
                    NativeOverlayChatFrameRenderer.BuildTransparentBlankFrameMessage())),
                    "A text-size change must not send a blank frame that can erase the refreshed chat.");
                var bounds = GetNativeOverlayAlphaBounds(rendered[^1]);
                if (fontSize > 12)
                    Assert.True(bounds.Height > original.Height * 1.5, "Larger settings must produce larger glyphs.");
                else if (fontSize == 8)
                    Assert.True(bounds.Height < original.Height, "Smaller settings must produce smaller glyphs.");
                else
                    Assert.Equal(original.Height, bounds.Height);
            }
            Assert.Equal(1, playback.CreateCount);
            Assert.Equal(playCount, playback.Engine.PlayCount);
            Assert.Equal(startCount, streamlink.StartCount);
        }
        finally
        {
            window.Close();
            await main.DisposeAsync();
            DeleteTempTestDirectory(root);
        }
    });

    private static async Task<IReadOnlyList<byte[]>> ReceiveSettledChatTextSizeFramesAsync(string pipeName)
    {
        var frames = new List<byte[]>();
        frames.AddRange(await ReadNativeOverlayPipeConnectionMessagesAsync(pipeName, TimeSpan.FromSeconds(4)));
        while (true)
        {
            try
            {
                frames.AddRange(await ReadNativeOverlayPipeConnectionMessagesAsync(pipeName, TimeSpan.FromMilliseconds(500)));
            }
            catch (TimeoutException) { return frames; }
        }
    }
}

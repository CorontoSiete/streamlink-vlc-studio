internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> ReplayChatAvailabilityTests { get; } =
    [
        ("DVR chat status: native overlay renders the notice and resumes captured chat", () => TestSta.RunOffscreenAsync(NativeHistoryStatusAsync)),
        ("DVR chat status: docked header displays and wraps the notice", () => TestSta.RunOffscreenAsync(DockedHistoryStatusAsync))
    ];

    private static async Task NativeHistoryStatusAsync()
    {
        var pipeName = $"svs_history_status_{Guid.NewGuid():N}";
        var startedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var playback = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            UsesNativeOverlayOverride = true,
            NativeOverlayPipeNameOverride = pipeName
        });
        var chats = new FakeChatClientFactory();
        await using var tab = CreateHistoryStatusTab(startedAt, playback, chats);
        var settings = HistoryStatusSettings(ChatLayout.Overlay);
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(settings);
        await TestWait.UntilAsync(() => tab.CanSeekReplay && chats.Client.Connected, TimeSpan.FromSeconds(3));

        var statusFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
            pipeName, IsNativeOverlayRenderedChatFrame, TimeSpan.FromSeconds(5));
        await tab.SeekReplayAsync(TimeSpan.FromHours(1));
        var statusFrame = await statusFrameTask;
        Assert.True(tab.HasReplayChatStatus);
        AssertNativeOverlayChatFrame(statusFrame);
        SaveHistoryStatusFrame(statusFrame);

        chats.Client.Receive(new ChatMessage(PlatformKind.Twitch, "streamer", "viewer",
            "Captured chat at the correct playback time", startedAt.AddMinutes(115), MessageId: "captured-status-test"));
        Assert.True(tab.HasReplayChatStatus);
        Assert.Equal(false, tab.ChatMessages.Any(message => message.MessageId == "captured-status-test"));

        var capturedFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(pipeName,
            frame => IsNativeOverlayRenderedChatFrame(frame) && !frame.SequenceEqual(statusFrame), TimeSpan.FromSeconds(5));
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(115));
        AssertNativeOverlayChatFrame(await capturedFrameTask);
        Assert.Equal(false, tab.HasReplayChatStatus);
        Assert.True(tab.ChatMessages.Any(message => message.MessageId == "captured-status-test"));
    }

    private static async Task DockedHistoryStatusAsync()
    {
        await using var tab = CreateHistoryStatusTab(DateTimeOffset.UtcNow.AddHours(-2),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory());
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(HistoryStatusSettings(ChatLayout.Docked));
        await TestWait.UntilAsync(() => tab.CanSeekReplay, TimeSpan.FromSeconds(3));
        await tab.SeekReplayAsync(TimeSpan.FromHours(1));

        var window = new MainWindow();
        RemoveMainWindowAutomaticStartup(window);
        try
        {
            var status = (TextBlock)window.FindName("DockedReplayChatStatus");
            var header = (Border)((FrameworkElement)status.Parent).Parent;
            ((Panel)header.Parent).Children.Remove(header);
            header.DataContext = new { SelectedTab = tab };
            await header.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            Assert.Equal(tab.ReplayChatStatusText, status.Text);
            Assert.Equal(Visibility.Visible, status.Visibility);
            Assert.Equal(TextWrapping.Wrap, status.TextWrapping);
            var surface = new Border { Width = 240, Background = Brushes.Black, Child = header };
            TextElement.SetForeground(surface, window.Foreground);
            surface.Measure(new Size(240, double.PositiveInfinity));
            surface.Arrange(new Rect(new Point(), surface.DesiredSize));
            surface.UpdateLayout();
            Assert.True(status.ActualHeight > 30,
                $"Expected wrapped status: status={status.ActualWidth}x{status.ActualHeight}, header={header.DesiredSize}, surface={surface.DesiredSize}.");
            Assert.True(status.ActualWidth <= 240);
            Assert.True(FindVisualDescendants<TextBlock>(header).Any(text => text.Text == "REPLAY CHAT"));
            var bitmap = new RenderTargetBitmap(240, (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            SaveHistoryStatusBitmap(bitmap, "docked-status.png");

            await tab.ReturnToLiveAsync();
            await header.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            Assert.Equal(Visibility.Collapsed, status.Visibility);
        }
        finally
        {
            window.Close();
        }
    }

    private static StreamTabViewModel CreateHistoryStatusTab(DateTimeOffset startedAt,
        FakePlaybackEngineFactory playback, FakeChatClientFactory chats)
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        return TestViewModels.CreateTab(StreamInputParser.Parse("streamer", PlatformKind.Twitch), "source",
            new FakeStreamlinkService(), playback, chats, new MemoryLogger(), action =>
            {
                if (dispatcher.CheckAccess()) action();
                else dispatcher.BeginInvoke(action);
            },
            replayResolver: new FakeReplayResolver(new ReplaySessionInfo(
                PlatformKind.Twitch, "streamer", "https://example.invalid/index-dvr.m3u8", "live-dvr-123",
                startedAt, TimeSpan.FromHours(2), true, "", "best", ReplayMediaKind.CurrentLiveDvr)),
            vodChatProvider: new FakeVodChatProvider(VodChatFetchResult.Unsupported("No published comments.")));
    }

    private static AppSettings HistoryStatusSettings(ChatLayout layout) => new()
    {
        StreamlinkPath = "streamlink.exe",
        VlcDirectory = @"C:\VLC",
        Chat = new ChatSettings { Layout = layout, ConnectAutomatically = true }
    };

    private static void SaveHistoryStatusFrame(byte[] frame)
    {
        var width = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(24, 4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(28, 4));
        var pixels = frame[36..];
        // The VLC protocol uses straight RGBA; WPF's bitmap decoder expects BGRA.
        for (var index = 0; index < pixels.Length; index += 4)
        {
            (pixels[index], pixels[index + 2]) = (pixels[index + 2], pixels[index]);
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        SaveHistoryStatusBitmap(bitmap, "native-status.png");
    }

    private static void SaveHistoryStatusBitmap(BitmapSource bitmap, string name)
    {
        if (Environment.GetEnvironmentVariable("SVS_TEST_ARTIFACT_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(output);
    }
}

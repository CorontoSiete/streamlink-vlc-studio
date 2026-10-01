using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StreamlinkVlcStudio.App.Wpf;
using StreamlinkVlcStudio.App.Wpf.Chat;
using StreamlinkVlcStudio.App.Wpf.ViewModels;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Settings;

internal static class NativeInputFocusTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("native input focus: Past broadcasts remains responsive with six unavailable overlays", PastBroadcastsFocusAsync),
        ("native input focus: connected nonreading pipe respects the complete write deadline", StalledPipeWriteAsync),
        ("native input focus: chat shutdown yields before waiting for an unavailable pipe", ShutdownYieldsAsync),
        ("native input focus: a late pipe receives the unchanged release packet", LatePipeReceivesReleaseAsync),
        ("native input focus: repeated requests coalesce without suppressing a new player", CoalescedReleaseAsync),
        ("native input focus: disposal drains pending releases and rejects new requests", DisposalDrainsAsync)
    ];

    private static Task PastBroadcastsFocusAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = new AppSettings();
        settings.Chat.ConnectAutomatically = false;
        var logger = new MemoryLogger();
        var videos = Enumerable.Range(1, 50).Select(index => new TwitchVodItem(
            index.ToString(), "", "71092938", "xqc", "xQc", "🔒 LIVE 🔒 LOCK IN 🔒", "",
            $"https://www.twitch.tv/videos/{index}", "", null, null, TimeSpan.FromHours(5), 100,
            TwitchVodTypeFilter.Archive)).ToArray();
        var vods = new FakeTwitchVodService(new TwitchVodSearchResult(
            TwitchVodSearchStatus.Available, new TwitchVodBroadcaster("71092938", "xqc", "xQc"),
            videos, "", "50 Twitch VODs found for xQc."));
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        await using var main = TestViewModels.CreateMain(settings, new FakeSettingsService(settings),
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(), logger,
            action => dispatcher.BeginInvoke(action), twitchVodService: vods);
        var engineField = typeof(StreamTabViewModel).GetField("playbackEngine", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var engines = new List<(StreamTabViewModel Tab, FakePlaybackEngine Engine)>();
        for (var index = 0; index < 6; index++)
        {
            var tab = CreateTab($"focus{index}", logger);
            var engine = new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = $"svs_missing_focus_{Guid.NewGuid():N}"
            };
            main.Tabs.Add(tab);
            engines.Add((tab, engine));
        }

        var window = new MainWindow
        {
            DataContext = main,
            Left = -8000,
            Top = -8000,
            Width = 1200,
            Height = 800,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        ApplicationTestCatalog.RemoveMainWindowAutomaticStartup(window);
        typeof(MainWindow).GetField("viewModel", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, main);
        try
        {
            window.Show();
            main.SelectHomeCommand.Execute(null);
            main.ShowTwitchVodsHomePageCommand.Execute(null);
            window.UpdateLayout();
            var search = Descendants<TextBox>(window).Single(box => AutomationProperties.GetName(box) == "VOD streamer search");
            Keyboard.ClearFocus();
            foreach (var (tab, engine) in engines) engineField.SetValue(tab, engine);
            var elapsed = Stopwatch.StartNew();
            Assert.True(search.Focus(), "The actual Past broadcasts input did not acquire focus.");
            elapsed.Stop();
            Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(150),
                $"Focusing the VOD search blocked the dispatcher for {elapsed.Elapsed.TotalMilliseconds:F0} ms.");
            search.Text = "xqc";
            await TestWait.UntilAsync(() => main.HasTwitchVodSearchCompleted && !main.IsTwitchVodSearchRunning,
                TimeSpan.FromSeconds(5), "Past broadcasts xqc search did not complete.");
            Assert.Equal(50, main.TwitchVods.Count);
            Assert.Equal("xqc", search.Text);
        }
        finally
        {
            foreach (var (tab, _) in engines) engineField.SetValue(tab, null);
            window.Close();
        }
    });

    private static async Task StalledPipeWriteAsync()
    {
        var pipeName = $"svs_stalled_focus_{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 16, outBufferSize: 16);
        var connected = server.WaitForConnectionAsync();
        var message = NativeOverlayProtocolCodec.BuildEventMessage(NativeOverlayProtocolCodec.ChatInputFocusEventType, 0);
        var batch = new byte[message.Length * 65536];
        for (var offset = 0; offset < batch.Length; offset += message.Length) message.CopyTo(batch, offset);
        var write = NativeChatOverlayController.TryWriteNativeOverlayEventAsync(
            pipeName, batch, TimeSpan.FromMilliseconds(250));
        try
        {
            await connected.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(600);
            Assert.True(write.IsCompleted, "A connected overlay stopped reading and the write outlived its 250 ms deadline.");
            Assert.Equal(false, await write);
        }
        finally
        {
            server.Dispose();
            await write.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static Task ShutdownYieldsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var logger = new MemoryLogger();
        await using var tab = CreateTab("focus_shutdown", logger);
        var field = typeof(StreamTabViewModel).GetField("playbackEngine", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(tab, new FakePlaybackEngine
        {
            UsesNativeOverlayOverride = true,
            NativeOverlayPipeNameOverride = $"svs_missing_shutdown_{Guid.NewGuid():N}"
        });
        try
        {
            var elapsed = Stopwatch.StartNew();
            var stop = tab.NativeOverlay.StopNativeOverlayChatAsync();
            elapsed.Stop();
            Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(100),
                $"Chat shutdown blocked its caller for {elapsed.Elapsed.TotalMilliseconds:F0} ms before yielding.");
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { field.SetValue(tab, null); }
    });

    private static async Task LatePipeReceivesReleaseAsync()
    {
        var pipeName = $"svs_late_focus_{Guid.NewGuid():N}";
        var message = NativeOverlayProtocolCodec.BuildEventMessage(NativeOverlayProtocolCodec.ChatInputFocusEventType, 0);
        var write = NativeChatOverlayController.TryWriteNativeOverlayEventAsync(pipeName, message, TimeSpan.FromSeconds(1));
        await Task.Delay(75);
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var received = new byte[message.Length];
        await server.ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(await write.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(message.AsSpan().SequenceEqual(received), "The focus-release protocol packet changed.");
    }

    private static Task CoalescedReleaseAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var tab = CreateTab("focus_coalescing", new MemoryLogger());
        var field = typeof(StreamTabViewModel).GetField("playbackEngine", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(tab, new FakePlaybackEngine
        {
            UsesNativeOverlayOverride = true,
            NativeOverlayPipeNameOverride = $"svs_missing_coalesced_focus_{Guid.NewGuid():N}"
        });
        try
        {
            var pending = tab.TryReleaseNativeOverlayChatInputFocusAsync();
            Assert.Equal(false, pending.IsCompleted);
            for (var index = 0; index < 100; index++)
            {
                Assert.True(ReferenceEquals(pending, tab.TryReleaseNativeOverlayChatInputFocusAsync()),
                    "Repeated focus events started additional pipe connections.");
            }

            var nextPipeName = $"svs_new_player_focus_{Guid.NewGuid():N}";
            using var server = new NamedPipeServerStream($"{nextPipeName}_events", PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var connected = server.WaitForConnectionAsync();
            field.SetValue(tab, new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = nextPipeName
            });
            var next = tab.TryReleaseNativeOverlayChatInputFocusAsync();
            Assert.Equal(false, ReferenceEquals(pending, next));
            await connected.WaitAsync(TimeSpan.FromSeconds(2));
            var expected = NativeOverlayProtocolCodec.BuildEventMessage(NativeOverlayProtocolCodec.ChatInputFocusEventType, 0);
            var received = new byte[expected.Length];
            await server.ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(expected.AsSpan().SequenceEqual(received));
            Assert.Equal(false, await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { field.SetValue(tab, null); }
    });

    private static Task DisposalDrainsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var tab = CreateTab("focus_disposal", new MemoryLogger());
        var field = typeof(StreamTabViewModel).GetField("playbackEngine", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(tab, new FakePlaybackEngine
        {
            UsesNativeOverlayOverride = true,
            NativeOverlayPipeNameOverride = $"svs_missing_disposal_focus_{Guid.NewGuid():N}"
        });
        var pending = tab.TryReleaseNativeOverlayChatInputFocusAsync();
        Assert.Equal(false, pending.IsCompleted);
        field.SetValue(tab, null);
        await tab.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(pending.IsCompleted, "Disposal left a pipe operation running.");
        Assert.Equal(false, await pending);
        field.SetValue(tab, new FakePlaybackEngine
        {
            UsesNativeOverlayOverride = true,
            NativeOverlayPipeNameOverride = $"svs_rejected_disposal_focus_{Guid.NewGuid():N}"
        });
        try
        {
            var rejected = tab.TryReleaseNativeOverlayChatInputFocusAsync();
            Assert.True(rejected.IsCompleted, "A disposed controller started a new pipe operation.");
            Assert.Equal(false, await rejected);
        }
        finally { field.SetValue(tab, null); }
    });

    private static StreamTabViewModel CreateTab(string channel, MemoryLogger logger) => TestViewModels.CreateTab(
        StreamInputParser.Parse(channel, PlatformKind.Twitch), "best", new FakeStreamlinkService(),
        new FakePlaybackEngineFactory(), new FakeChatClientFactory(), logger, action => action());

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}

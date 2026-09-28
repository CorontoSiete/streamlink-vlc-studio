using System.Windows.Interop;
using System.Windows.Threading;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> MultistreamUiResourceTests { get; } =
    [
        ("multistream UI resources: hidden docked chat releases rows and restores current history", HiddenDockedChatResourcesAsync),
        ("multistream UI resources: unchanged layout does not resize native renderer children", StableRendererLayoutResourcesAsync)
    ];

    private static Task HiddenDockedChatResourcesAsync() => TestSta.RunAsync(async () =>
    {
        var settings = new AppSettings { MultiStreamEnabled = true };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Overlay;
        var logger = new MemoryLogger();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var viewModel = TestViewModels.CreateMain(settings, new FakeSettingsService(settings), new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory(), logger, action => dispatcher.BeginInvoke(action));
        var tab = TestViewModels.CreateTab(StreamInputParser.FromChannel(PlatformKind.Kick, "resourcefixture"), "best",
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(), logger, action => dispatcher.BeginInvoke(action));
        viewModel.Tabs.Add(tab);
        viewModel.VideoTabs.Add(tab);
        viewModel.SelectedTab = tab;
        var window = new MainWindow { DataContext = viewModel, Width = 1000, Height = 700 };
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, viewModel);
        try
        {
            window.Show();
            var list = (ListBox)window.FindName("DockedChatListBox");
            for (var index = 0; index < 100; index++) Append(index);
            await FlushAsync();
            Assert.Equal(false, viewModel.IsDockedChatVisible);
            Console.WriteLine($"Hidden docked chat: {list.Items.Count} bound items, {FindVisualDescendants<DockedChatMessageTextBlock>(list).Count()} realized rows.");
            Assert.Equal(0, list.Items.Count);
            Assert.Equal(0, FindVisualDescendants<DockedChatMessageTextBlock>(list).Count());

            settings.Chat.Layout = ChatLayout.Docked;
            tab.IsChatVisible = true;
            tab.IsDockedChatPanelVisible = true;
            await FlushAsync();
            Assert.True(viewModel.IsDockedChatVisible);
            Assert.Equal(100, list.Items.Count);
            Assert.True(FindVisualDescendants<DockedChatMessageTextBlock>(list).Any());

            tab.IsDockedChatPanelVisible = false;
            await FlushAsync();
            Assert.Equal(0, list.Items.Count);
            for (var index = 100; index < 120; index++)
            {
                tab.DockedChatFeedItems.RemoveAt(0);
                Append(index);
            }
            await FlushAsync();
            Assert.Equal(0, FindVisualDescendants<DockedChatMessageTextBlock>(list).Count());

            tab.IsDockedChatPanelVisible = true;
            await FlushAsync();
            Assert.Equal(100, list.Items.Count);
            Assert.True(ReferenceEquals(tab.DockedChatFeedItems[^1], list.Items[^1]));
            var scroll = FindVisualDescendants<ScrollViewer>(list).First();
            Assert.True(scroll.ScrollableHeight - scroll.VerticalOffset <= 3, "Restored chat must follow its current last message.");
        }
        finally { await viewModel.DisposeAsync(); window.Close(); }

        void Append(int index) => tab.DockedChatFeedItems.Add(new DockedChatMessageFeedItem(
            new ChatMessage(PlatformKind.Kick, "resourcefixture", "system", "history " + index, DateTimeOffset.UnixEpoch)));

        async Task FlushAsync()
        {
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        }
    });

    private static Task StableRendererLayoutResourcesAsync() => TestSta.RunAsync(async () =>
    {
        var surface = new VideoSurface();
        var window = new Window { Content = surface, Width = 700, Height = 450, ShowInTaskbar = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            using var child = new HwndSource(new HwndSourceParameters("resource renderer child")
            {
                ParentWindow = surface.Handle,
                WindowStyle = 0x50000000,
                Width = 10,
                Height = 10
            });
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            typeof(VideoSurface).GetMethod("StopRendererWindowRepairTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, []);
            var changes = 0;
            var notifications = 0;
            surface.NativeBoundsChanged += (_, _) => notifications++;
            child.AddHook(ObservePosition);
            var positionChanged = (Action<Rect>)typeof(VideoSurface).GetMethod("OnWindowPositionChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate(typeof(Action<Rect>), surface);
            var size = NativeWindowTest.GetWindowBounds(surface.Handle).Size;
            var bounds = new Rect(0, 0, size.Width, size.Height);
            positionChanged(bounds);
            changes = 0;
            notifications = 0;
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 100; index++) positionChanged(bounds);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Console.WriteLine($"Unchanged host layout: {notifications} bounds notifications, {changes} native child position messages, {allocated} managed bytes in 100 updates.");
            Assert.Equal(0, notifications);
            Assert.Equal(0, changes);

            window.Width += 137;
            window.Height += 73;
            window.UpdateLayout();
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(NativeWindowTest.GetWindowBounds(surface.Handle).Size, NativeWindowTest.GetWindowBounds(child.Handle).Size);
            Assert.True(changes > 0, "A real size change must reach the renderer synchronously.");

            using var late = new HwndSource(new HwndSourceParameters("late resource renderer child")
            {
                ParentWindow = surface.Handle,
                WindowStyle = 0x50000000,
                Width = 11,
                Height = 13
            });
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(NativeWindowTest.GetWindowBounds(surface.Handle).Size, NativeWindowTest.GetWindowBounds(late.Handle).Size);

            IntPtr ObservePosition(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                if (message == 0x0047) changes++; // WM_WINDOWPOSCHANGED
                return IntPtr.Zero;
            }
        }
        finally { window.Close(); surface.Dispose(); }
    });
}

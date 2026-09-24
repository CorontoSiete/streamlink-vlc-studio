internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> ResponsiveLayoutTests { get; } =
    [
        ("Back hotkey routes native video and records mouse input in settings", () =>
            WithResponsiveWindowAsync(withVideo: true, async (window, viewModel) =>
            {
                var selected = viewModel.SelectedTab;
                var surface = FindVisualDescendants<VideoSurface>(window).Single();
                Assert.True(surface.Handle != IntPtr.Zero);
                await NativeWindowTest.RequireForegroundAsync(
                    new System.Windows.Interop.WindowInteropHelper(window).Handle,
                    TimeSpan.FromSeconds(2), "Back hotkey native input");
                PumpResponsiveLayout(window);
                var mouse4 = new IntPtr(unchecked((int)0x80010000));
                var mouse5 = new IntPtr(unchecked((int)0x80020000));
                Assert.Equal(false, window.TryExecuteNativeMouseAppCommand(IntPtr.Zero, mouse4));
                Assert.Equal(false, window.TryExecuteNativeMouseAppCommand(
                    new System.Windows.Interop.WindowInteropHelper(window).Handle, mouse4));
                Assert.Equal(false, window.TryExecuteNativeMouseAppCommand(surface.Handle, new IntPtr(0x00010000)));
                Assert.Equal(false, window.TryExecuteNativeMouseAppCommand(surface.Handle, new IntPtr(unchecked((int)0x80010008))));
                Assert.Equal(false, window.TryExecuteNativeMouseAppCommand(surface.Handle, mouse5));
                Assert.Equal(false, viewModel.IsHomeSelected);
                Assert.Equal(new IntPtr(1), NativeWindowTest.SendMessage(
                    new System.Windows.Interop.WindowInteropHelper(window).Handle, 0x0319, surface.Handle, mouse4));
                Assert.True(viewModel.IsHomeSelected);
                Assert.Equal(false, viewModel.CanGoBack);

                viewModel.SelectedTab = selected;
                viewModel.ToggleSettingsCommand.Execute(null);
                viewModel.ShowHotkeysSettingsCommand.Execute(null);
                PumpResponsiveLayout(window);
                SaveResponsiveWindowImage(window, "back-hotkey-settings");
                var recorder = (HotkeyRecorderButton)window.FindName("GoBackHotkeyRecorder");
                typeof(HotkeyRecorderButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(recorder, null);
                Assert.True(ReferenceEquals(recorder, Keyboard.FocusedElement));
                var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.XButton2)
                {
                    RoutedEvent = Mouse.PreviewMouseDownEvent
                };
                window.RaiseEvent(down);
                Assert.True(down.Handled);
                Assert.True(viewModel.IsSettingsOpen);
                Assert.Equal("Mouse5", viewModel.Settings.Hotkeys.GoBack);
                Assert.True(recorder.IsMouseCaptured);
                var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.XButton2)
                {
                    RoutedEvent = Mouse.PreviewMouseUpEvent
                };
                window.RaiseEvent(up);
                Assert.True(up.Handled);
                Assert.Equal(false, recorder.IsCapturingInput);
                Assert.Equal(false, recorder.IsMouseCaptured);
                typeof(HotkeyRecorderButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(recorder, null);
                window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.XButton2)
                {
                    RoutedEvent = Mouse.PreviewMouseDownEvent
                });
                Assert.True(recorder.IsCapturingInput);
                Mouse.Capture(null);
                Assert.Equal(false, recorder.IsCapturingInput);
                window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.XButton2)
                {
                    RoutedEvent = Mouse.PreviewMouseUpEvent
                });
                Assert.True(viewModel.IsSettingsOpen, "Losing capture must not turn the recorded button release into navigation.");
                window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.XButton2)
                {
                    RoutedEvent = Mouse.PreviewMouseUpEvent
                });
                Assert.Equal(false, viewModel.IsSettingsOpen);
                Assert.True(ReferenceEquals(selected, viewModel.SelectedTab));
                PumpResponsiveLayout(window);
                SaveResponsiveWindowImage(window, "back-hotkey-toolbar");
            })),
        ("responsive window native resize keeps WPF and video HWND inside the client", () =>
            WithResponsiveWindowAsync(withVideo: true, async (window, _) =>
            {
                foreach (var size in ResponsiveWindowSizes)
                {
                    var client = ResizeResponsiveWindow(window, size.Width, size.Height);
                    var root = (FrameworkElement)window.Content;
                    var viewport = (FrameworkElement)window.FindName("VideoViewport");
                    var surface = FindVisualDescendants<VideoSurface>(window).Single();
                    Console.WriteLine($"RESIZE {size.Width}x{size.Height}: client {client.Width:0.##}x{client.Height:0.##}, " +
                        $"WPF root {root.ActualWidth:0.##}x{root.ActualHeight:0.##}, video {surface.ActualWidth:0.##}x{surface.ActualHeight:0.##} DIPs.");
                    SaveResponsiveWindowImage(window, $"player-{size.Width}x{size.Height}");
                    AssertResponsiveElementInside(root, client, "workspace root");
                    AssertResponsiveElementInside(viewport, client, "video viewport");
                    AssertResponsiveElementInside(surface, client, "WPF video surface");
                    var title = (FrameworkElement)window.FindName("TitleBar");
                    var titleHeight = title.TransformToAncestor(root)
                        .TransformBounds(new Rect(title.RenderSize)).Height;
                    Assert.True(System.Windows.Shell.WindowChrome.GetWindowChrome(window)!.CaptionHeight <= titleHeight,
                        "The native caption extends into the controls below the resized title bar.");
                    Assert.True(surface.ActualWidth > 0 && surface.ActualHeight > 0,
                        $"Video lost all usable space at {size.Width}x{size.Height}.");
                    Assert.True(surface.Handle != IntPtr.Zero);
                    var nativeVideo = NativeWindowTest.GetWindowBounds(surface.Handle);
                    AssertResponsiveBoundsInside(
                        new Rect(nativeVideo.X, nativeVideo.Y, nativeVideo.Width, nativeVideo.Height),
                        client,
                        "native video HWND");
                    var wpfVideo = GetResponsiveScreenBounds(surface);
                    AssertNear(wpfVideo.Width, nativeVideo.Width, 2);
                    AssertNear(wpfVideo.Height, nativeVideo.Height, 2);
                    AssertResponsiveActionAccessible(window, (Button)window.FindName("CloseWindowButton"), client);
                    foreach (var name in new[] { "TopMultiStreamButton", "TopPlayPauseButton", "TopMuteButton", "TopNeverMuteButton", "TopFullscreenButton", "TopTheatreButton" })
                    {
                        AssertResponsiveActionAccessible(window, (ButtonBase)window.FindName(name), client);
                    }
                    await Task.Yield();
                }
                Assert.Equal(0d, window.MinWidth);
                Assert.Equal(0d, window.MinHeight);
            })),
        ("responsive window native minimum tracking permits arbitrarily small main windows", () =>
            WithResponsiveWindowAsync(withVideo: false, (window, _) =>
            {
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<ResponsiveMinMaxInfo>());
                try
                {
                    Marshal.StructureToPtr(new ResponsiveMinMaxInfo
                    {
                        MinTrackSize = new ResponsiveNativePoint { X = 980, Y = 620 }
                    }, pointer, false);
                    NativeWindowTest.SendMessage(handle, 0x0024, IntPtr.Zero, pointer);
                    var limits = Marshal.PtrToStructure<ResponsiveMinMaxInfo>(pointer);
                    Assert.True(limits.MinTrackSize.X <= 1 && limits.MinTrackSize.Y <= 1,
                        $"Main window still imposes a native minimum of {limits.MinTrackSize.X}x{limits.MinTrackSize.Y}.");
                }
                finally
                {
                    Marshal.FreeHGlobal(pointer);
                }
                return Task.CompletedTask;
            })),
        ("responsive home and settings keep search navigation and save reachable after resizing", () =>
            WithResponsiveWindowAsync(withVideo: false, (window, viewModel) =>
            {
                foreach (var size in ResponsiveWindowSizes)
                {
                    viewModel.IsSettingsOpen = false;
                    var client = ResizeResponsiveWindow(window, size.Width, size.Height);
                    AssertResponsiveElementInside((FrameworkElement)window.Content, client, "home workspace");
                    var navigation = (ToolBar)window.FindName("HomeNavigation");
                    foreach (var button in navigation.Items.OfType<Button>())
                    {
                        AssertResponsiveActionAccessible(window, button, client);
                    }
                    AssertResponsiveReachable(window,
                        (FrameworkElement)window.FindName("HomeStreamSearchTextBox"),
                        (FrameworkElement)window.FindName("HomeViewport"), client);
                    SaveResponsiveWindowImage(window, $"home-{size.Width}x{size.Height}");

                    viewModel.IsSettingsOpen = true;
                    PumpResponsiveLayout(window);
                    var settingsViewport = (FrameworkElement)window.FindName("SettingsViewport");
                    AssertResponsiveElementInside(settingsViewport, client, "settings viewport");
                    var selector = (ComboBox)window.FindName("CompactSettingsCategorySelector");
                    if (selector.IsVisible)
                    {
                        AssertResponsiveReachable(window, selector, settingsViewport, client);
                        selector.SelectedItem = SettingsCategory.Chat;
                        Assert.Equal(SettingsCategory.Chat, viewModel.SelectedSettingsCategory);
                        selector.SelectedItem = SettingsCategory.General;
                        Assert.Equal(SettingsCategory.General, viewModel.SelectedSettingsCategory);
                    }
                    var save = (Button)window.FindName("SettingsSaveButton");
                    AssertResponsiveReachable(window, save, settingsViewport, client);
                    SaveResponsiveWindowImage(window, $"settings-{size.Width}x{size.Height}");
                }
                return Task.CompletedTask;
            })),
        ("responsive compact tabs select and close the same tab after shrink and grow", () =>
            WithResponsiveWindowAsync(withVideo: true, (window, viewModel) =>
            {
                var first = viewModel.SelectedTab!;
                var second = TestViewModels.CreateTab(
                    StreamInputParser.Parse("secondfixture", PlatformKind.Twitch), "best",
                    new FakeStreamlinkService(), new FakePlaybackEngineFactory(),
                    new FakeChatClientFactory(), new MemoryLogger(), action => action());
                viewModel.Tabs.Add(second);
                var client = ResizeResponsiveWindow(window, 320, 240);
                var selector = (ComboBox)window.FindName("CompactTabSelector");
                Assert.True(selector.IsVisible);
                AssertResponsiveElementInside(selector, client, "compact tab selector");
                var secondItem = viewModel.TabStripItems.Single(item => item.Contains(second));
                selector.SelectedItem = secondItem;
                PumpResponsiveLayout(window);
                Assert.Equal(second, viewModel.SelectedTab);
                AssertResponsiveSelectedTabTitle(selector, viewModel.SelectedTabStripItem!.Title);
                ResizeResponsiveWindow(window, 1100, 760);
                var tabs = (ListBox)window.FindName("TabListBox");
                Assert.True(tabs.IsVisible);
                Assert.True(tabs.SelectedItem is TabStripItemViewModel wideSelection &&
                    ReferenceEquals(second, wideSelection.ActiveTab) && wideSelection.Contains(second),
                    "The expanded tab strip must retain the selected stream after rebuilding tab items.");
                client = ResizeResponsiveWindow(window, 200, 160);
                Assert.True(selector.SelectedItem is TabStripItemViewModel compactSelection &&
                    ReferenceEquals(second, compactSelection.ActiveTab) && compactSelection.Contains(second),
                    "The compact selector must retain the same selected stream after shrinking again.");
                AssertResponsiveSelectedTabTitle(selector, viewModel.SelectedTabStripItem!.Title);
                var close = FindVisualDescendants<Button>(window).Single(button =>
                    Equals(button.ToolTip, "Close selected tab"));
                AssertResponsiveActionAccessible(window, close, client);
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpResponsiveLayout(window);
                Assert.Equal(1, viewModel.Tabs.Count);
                Assert.Equal(first, viewModel.Tabs.Single());
                Assert.Equal(first, viewModel.SelectedTab);
                return Task.CompletedTask;
            })),
        ("responsive docked chat leaves video visible and chat input reachable while shrinking and growing", () =>
            WithResponsiveWindowAsync(withVideo: true, (window, viewModel) =>
            {
                viewModel.Settings.Chat.Layout = ChatLayout.Docked;
                viewModel.SelectedTab!.IsChatVisible = true;
                viewModel.SelectedTab.IsDockedChatPanelVisible = true;
                var savedDockWidth = viewModel.Settings.Chat.DockWidth;
                foreach (var size in ResponsiveWindowSizes)
                {
                    var client = ResizeResponsiveWindow(window, size.Width, size.Height);
                    var chat = (FrameworkElement)window.FindName("DockedChatPanel");
                    var viewport = (FrameworkElement)window.FindName("DockedChatViewport");
                    var video = FindVisualDescendants<VideoSurface>(window).Single();
                    Assert.True(chat.IsVisible);
                    AssertResponsiveElementInside(chat, client, "docked chat");
                    AssertResponsiveElementInside(video, client, "video beside docked chat");
                    Assert.True(video.ActualWidth > 0 && video.ActualHeight > 0,
                        $"Docked chat consumed all video space at {size.Width}x{size.Height}.");
                    AssertResponsiveReachable(window,
                        (FrameworkElement)window.FindName("ChatInputTextBox"), viewport, client);
                    AssertNear(savedDockWidth, viewModel.Settings.Chat.DockWidth);
                    SaveResponsiveWindowImage(window, $"chat-{size.Width}x{size.Height}");
                }
                return Task.CompletedTask;
            })),
        ("responsive home cards shrink within narrow available widths in both gap modes", () => TestSta.RunAsync(() =>
        {
            foreach (var keepRightGap in new[] { true, false })
            {
                foreach (var width in new[] { 0d, 80d, 200d, 315d, 316d, 700d })
                {
                    var panel = new HomeCardWrapPanel { KeepRightGap = keepRightGap };
                    var cards = AddHomeCardPanelChildren(panel, 4);
                    panel.Measure(new Size(width, double.PositiveInfinity));
                    panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
                    AssertNear(width, panel.DesiredSize.Width);
                    foreach (var card in cards)
                    {
                        var bounds = card.TransformToAncestor(panel).TransformBounds(new Rect(card.RenderSize));
                        Assert.True(bounds.Left >= -0.001 && bounds.Right <= width + 0.001,
                            $"Card bounds {bounds} exceed width {width} (KeepRightGap={keepRightGap}).");
                    }
                }
            }
        })),
        .. ResponsiveHomeControlTests,
        .. ResponsivePopupTests,
        .. ResponsiveScrollbarTests,
        .. ResponsiveVlcResizeTests,
        .. ResponsiveClippingTests
    ];

    private static IReadOnlyList<(int Width, int Height)> ResponsiveWindowSizes =>
    [
        (623, 800),
        (480, 320),
        (320, 240),
        (200, 160),
        (120, 100),
        (80, 80),
        (1100, 180),
        (1100, 760)
    ];

    private static Task WithResponsiveWindowAsync(bool withVideo, Func<MainWindow, MainViewModel, Task> test) =>
        TestSta.RunAsync(async () =>
        {
            var settings = new AppSettings();
            settings.Chat.ConnectAutomatically = false;
            settings.Chat.Layout = ChatLayout.Overlay;
            var streamlink = new FakeStreamlinkService();
            var playback = new FakePlaybackEngineFactory();
            var chats = new FakeChatClientFactory();
            var logger = new MemoryLogger();
            var viewModel = TestViewModels.CreateMain(
                settings, new FakeSettingsService(settings), streamlink, playback, chats, logger, action => action());
            if (withVideo)
            {
                var tab = TestViewModels.CreateTab(
                    StreamInputParser.Parse("responsivefixture", PlatformKind.Twitch),
                    "best", streamlink, playback, chats, logger, action => action());
                viewModel.Tabs.Add(tab);
                viewModel.SelectedTab = tab;
                if (!viewModel.VideoTabs.Contains(tab))
                {
                    viewModel.VideoTabs.Add(tab);
                }
            }
            var window = new MainWindow
            {
                Width = 1100,
                Height = 760,
                Left = 100,
                Top = 100,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);
            try
            {
                window.Show();
                SetMainWindowHandle(window);
                AttachMainWindowMessageHook(window);
                PumpResponsiveLayout(window);
                await test(window, viewModel);
            }
            finally
            {
                window.Close();
                await viewModel.DisposeAsync();
            }
        });

    private static Rect ResizeResponsiveWindow(MainWindow window, int width, int height)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        // SetWindowPos exercises the actual native resize path that exposed the WPF
        // minimum-size mismatch. Setting Window.Width alone coerces to WPF MinWidth.
        NativeWindowTest.SetWindowBounds(handle, 100, 100, width, height);
        PumpResponsiveLayout(window);
        var outer = NativeWindowTest.GetWindowBounds(handle);
        Assert.Equal(width, outer.Width);
        Assert.Equal(height, outer.Height);
        Assert.True(ResponsiveGetClientRect(handle, out var client));
        var origin = new ResponsiveNativePoint();
        Assert.True(ResponsiveClientToScreen(handle, ref origin));
        return new Rect(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
    }

    private static void PumpResponsiveLayout(MainWindow window)
    {
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static Rect GetResponsiveScreenBounds(FrameworkElement element)
    {
        var topLeft = element.PointToScreen(new Point());
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return new Rect(topLeft, bottomRight);
    }

    private static void AssertResponsiveElementInside(FrameworkElement element, Rect client, string name) =>
        AssertResponsiveBoundsInside(GetResponsiveScreenBounds(element), client, name);

    private static void AssertResponsiveBoundsInside(Rect actual, Rect client, string name)
    {
        const double roundingTolerance = 2;
        Assert.True(actual.Left >= client.Left - roundingTolerance &&
                    actual.Top >= client.Top - roundingTolerance &&
                    actual.Right <= client.Right + roundingTolerance &&
                    actual.Bottom <= client.Bottom + roundingTolerance,
            $"{name} bounds {actual} extend outside client {client}.");
    }

    private static void AssertResponsiveActionAccessible(MainWindow window, ButtonBase button, Rect client)
    {
        Assert.NotNull(button);
        if (!ToolBar.GetIsOverflowItem(button))
        {
            Assert.True(button.IsVisible, $"Action {button.Name}/{button.ToolTip} is hidden.");
            AssertResponsiveElementInside(button, client, $"action {button.Name}/{button.ToolTip}");
            Assert.True(button.ActualWidth > 0 && button.ActualHeight > 0);
            return;
        }

        var toolbar = ItemsControl.ItemsControlFromItemContainer(button) as ToolBar ?? button.Parent as ToolBar;
        Assert.NotNull(toolbar);
        Assert.True(toolbar!.HasOverflowItems);
        var overflowButton = toolbar.Template.FindName("OverflowButton", toolbar) as FrameworkElement;
        Assert.NotNull(overflowButton);
        AssertResponsiveElementInside(overflowButton!, client, "toolbar overflow button");
        toolbar.IsOverflowOpen = true;
        try
        {
            PumpResponsiveLayout(window);
            Assert.True(button.IsVisible && button.IsHitTestVisible && button.ActualWidth > 0 && button.ActualHeight > 0,
                $"Overflow action {button.Name}/{button.ToolTip} is inaccessible.");
        }
        finally
        {
            toolbar.IsOverflowOpen = false;
        }
    }

    private static void AssertResponsiveReachable(MainWindow window, FrameworkElement element, FrameworkElement viewport, Rect client)
    {
        element.BringIntoView();
        PumpResponsiveLayout(window);
        var visibleBounds = Rect.Intersect(GetResponsiveScreenBounds(element), GetResponsiveScreenBounds(viewport));
        visibleBounds.Intersect(client);
        Assert.True(!visibleBounds.IsEmpty && visibleBounds.Width > 1 && visibleBounds.Height > 1,
            $"{element.Name} cannot be reached by scrolling; element {GetResponsiveScreenBounds(element)}, " +
            $"viewport {GetResponsiveScreenBounds(viewport)}, client {client}.");
        var point = window.PointFromScreen(new Point(
            visibleBounds.Left + visibleBounds.Width / 2,
            visibleBounds.Top + visibleBounds.Height / 2));
        var hit = window.InputHitTest(point) as DependencyObject;
        Assert.True(hit is not null && (ReferenceEquals(hit, element) || element.IsAncestorOf(hit)),
            $"{element.Name} has visible bounds {visibleBounds}, but a pointer at its visible center hits {hit?.GetType().Name ?? "nothing"}.");
    }

    private static void AssertResponsiveSelectedTabTitle(ComboBox selector, string expectedTitle)
    {
        var visibleLabels = FindVisualDescendants<TextBlock>(selector)
            .Where(label => label.IsVisible && label.ActualWidth > 0 && label.ActualHeight > 0)
            .Select(label => label.Text)
            .ToArray();
        Assert.True(visibleLabels.Contains(expectedTitle, StringComparer.Ordinal),
            $"The compact tab selector should display '{expectedTitle}', but visible text was '{string.Join(" | ", visibleLabels)}'.");
        Assert.True(visibleLabels.All(text => !text.Contains(nameof(TabStripItemViewModel), StringComparison.Ordinal)),
            "The compact tab selector rendered a view-model type name instead of the stream title.");
    }

    private static void SaveResponsiveWindowImage(MainWindow window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SVS_RESPONSIVE_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        System.IO.Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(WpfVisualTest.Render((FrameworkElement)window.Content)));
        using var output = System.IO.File.Create(System.IO.Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ResponsiveNativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ResponsiveNativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ResponsiveMinMaxInfo
    {
        internal ResponsiveNativePoint Reserved;
        internal ResponsiveNativePoint MaxSize;
        internal ResponsiveNativePoint MaxPosition;
        internal ResponsiveNativePoint MinTrackSize;
        internal ResponsiveNativePoint MaxTrackSize;
    }

    [DllImport("user32", EntryPoint = "GetClientRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ResponsiveGetClientRect(IntPtr handle, out ResponsiveNativeRect client);

    [DllImport("user32", EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ResponsiveClientToScreen(IntPtr handle, ref ResponsiveNativePoint point);
}

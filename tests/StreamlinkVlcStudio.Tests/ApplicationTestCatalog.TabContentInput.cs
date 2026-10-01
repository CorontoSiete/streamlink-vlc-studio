internal static partial class ApplicationTestCatalog
{
    internal const string TabContentInputTestName = "tab input: routed and physical title clicks select streams and preserve close controls";

    internal static IReadOnlyList<(string Name, Func<Task> Run)> TabContentInputTests { get; } =
    [
        (TabContentInputTestName, TabContentClicksAsync),
        ("tab input: middle-click resolves nested title text to its stream card", () => HomeTitleContentAsync(useEmoji: false)),
        ("tab input: middle-click resolves inline emoji content to its stream card", () => HomeTitleContentAsync(useEmoji: true)),
        ("tab input: unattached content and nonvisual sources safely have no stream command", UnattachedInputContentAsync),
        ("tab input: rich text content retains text-input focus protection", RichTextInputContentAsync)
    ];

    private static Task TabContentClicksAsync() => TestSta.RunAsync(async () =>
    {
        var settings = new AppSettings();
        settings.Chat.ConnectAutomatically = false;
        await using var main = TestViewModels.CreateMain(settings, new FakeSettingsService(settings),
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
            new MemoryLogger(), action => action());
        var targets = new[]
        {
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            StreamInputParser.Parse("xqc", PlatformKind.Kick) with { DisplayTitle = "xQc 🔴" },
            StreamInputParser.Parse("https://www.twitch.tv/videos/123", PlatformKind.Twitch) with { DisplayTitle = "VOD 🎮" }
        };
        var tabs = targets.Select(target => TestViewModels.CreateTab(target, "best",
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
            new MemoryLogger(), action => action())).ToArray();
        foreach (var tab in tabs) main.Tabs.Add(tab);
        main.SelectedTab = tabs[0];
        var window = new MainWindow
        {
            Width = 1400,
            Height = 760,
            Left = 100,
            Top = 100,
            Topmost = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            DataContext = main
        };
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, main);
        SetMainWindowControlModifierProvider(window, () => false);
        var list = (ListBox)window.FindName("TabListBox");
        var dragField = typeof(MainWindow).GetField("tabDetachDragTab", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var restoreCursor = NativeWindowTest.TryGetCursorPosition(out var originalCursor);
        try
        {
            // Mount an existing set of streamer tabs before the first native layout,
            // as in a session that is already playing when the user switches tabs.
            window.Show();
            SetMainWindowHandle(window);
            AttachMainWindowMessageHook(window);
            PumpResponsiveLayout(window);
            // Raise from the actual template's Run, rather than assigning SelectedTab or
            // raising from TabChrome: those bypass the original crash's input source.
            for (var pass = 0; pass < 4; pass++)
            {
                foreach (var tab in tabs.Reverse())
                {
                    var title = FindVisualDescendants<EmojiTextBlock>(FindTabStripChrome(window, tab)).Single();
                    var run = title.Inlines.OfType<Run>().First();
                    RaiseTitleClick(run, tab);
                    title = FindVisualDescendants<EmojiTextBlock>(FindTabStripChrome(window, tab)).Single();
                    foreach (var inline in title.Inlines.OfType<InlineUIContainer>()) RaiseTitleClick(inline, tab);
                }
            }

            var sources = new List<object>();
            window.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler((_, args) => sources.Add(args.OriginalSource)), handledEventsToo: true);
            await NativeWindowTest.RequireForegroundAsync(
                new System.Windows.Interop.WindowInteropHelper(window).Handle,
                TimeSpan.FromSeconds(2), "tab title click regression");
            foreach (var tab in tabs.Reverse())
            {
                var title = FindVisualDescendants<EmojiTextBlock>(FindTabStripChrome(window, tab)).Single();
                var run = title.Inlines.OfType<Run>().First();
                var rectangle = run.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                var next = run.ContentStart.GetPositionAtOffset(1)!.GetCharacterRect(LogicalDirection.Forward);
                var point = title.PointToScreen(new Point((rectangle.Left + next.Left) / 2, rectangle.Top + rectangle.Height / 2));
                var hit = window.InputHitTest(window.PointFromScreen(point));
                Assert.True(ReferenceEquals(hit, run),
                    $"Physical title target hit {hit?.GetType().Name ?? "null"}/{(hit as FrameworkElement)?.Name}, expected Run. " +
                    $"Point={point}; character={rectangle}; next={next}; title size={title.RenderSize}; " +
                    $"visible={title.IsVisible}; hit-test-visible={title.IsHitTestVisible}; enabled={title.IsEnabled}; " +
                    $"detached={string.Join(",", tabs.Select(candidate => candidate.IsDetached))}.");
                Assert.True(NativeWindowTest.IsRootWindowAtPoint(
                    new System.Windows.Interop.WindowInteropHelper(window).Handle,
                    (int)Math.Round(point.X), (int)Math.Round(point.Y)),
                    $"Physical tab click was occluded: {NativeWindowTest.DescribeWindowAtPoint((int)Math.Round(point.X), (int)Math.Round(point.Y))}.");
                var count = sources.Count;
                await Task.Run(() => NativeWindowTest.SendLeftClick((int)Math.Round(point.X), (int)Math.Round(point.Y)));
                try
                {
                    await TestWait.UntilAsync(() => sources.Count > count && ReferenceEquals(main.SelectedTab, tab) &&
                        dragField.GetValue(window) is null, TimeSpan.FromSeconds(2), "Physical title click did not finish selecting its stream.");
                }
                catch (InvalidOperationException exception)
                {
                    throw new InvalidOperationException($"Physical click at {point} for {tab.Title}: " +
                        $"sources={string.Join(",", sources.Skip(count).Select(source => source.GetType().Name))}; " +
                        $"selected={main.SelectedTab?.Title}; drag={(dragField.GetValue(window) as StreamTabViewModel)?.Title}; " +
                        $"captured={Mouse.Captured?.GetType().Name}; " +
                        NativeWindowTest.DescribeWindowAtPoint((int)Math.Round(point.X), (int)Math.Round(point.Y)), exception);
                }
                Assert.True(sources.Skip(count).Any(source => ReferenceEquals(source, run)),
                    "The physical click did not hit the title's Run, so it did not exercise the crash path.");
                Assert.True(ReferenceEquals(list.SelectedItem, main.SelectedTabStripItem));
                PumpResponsiveLayout(window);
            }

            main.SelectHomeCommand.Execute(null);
            PumpResponsiveLayout(window);
            var homeTitle = FindVisualDescendants<EmojiTextBlock>(FindTabStripChrome(window, tabs[0])).Single();
            RaiseTitleClick(homeTitle.Inlines.OfType<Run>().First(), tabs[0]);
            Assert.Equal(false, main.IsHomeSelected);

            Assert.True(main.TryMergeTabsIntoMultiView([tabs[1]], tabs[0]));
            main.SelectedTab = tabs[2];
            PumpResponsiveLayout(window);
            var groupChrome = FindTabStripChrome(window, tabs[0]);
            var group = (TabStripItemViewModel)groupChrome.DataContext;
            var groupTitle = FindVisualDescendants<EmojiTextBlock>(groupChrome).Single();
            RaiseTitleClick(groupTitle.Inlines.OfType<Run>().First(), group.ActiveTab);
            Assert.Equal(2, main.SelectedTabStripItem!.Tabs.Count);

            // Inline text inside a close button must remain a button click, without
            // selecting its tab or starting a detach drag in the enclosing TabChrome.
            main.SelectedTab = tabs[2];
            PumpResponsiveLayout(window);
            groupChrome = FindTabStripChrome(window, tabs[0]);
            var close = FindVisualDescendants<Button>(groupChrome).Single(button => button.Name == "TabCloseControl");
            var closeText = new TextBlock();
            var closeRun = new Run("Close");
            closeText.Inlines.Add(new Span(closeRun));
            close.Content = closeText;
            var closeDown = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            };
            closeRun.RaiseEvent(closeDown);
            Assert.True(ReferenceEquals(closeRun, closeDown.OriginalSource));
            Assert.Equal(false, closeDown.Handled);
            Assert.True(ReferenceEquals(main.SelectedTab, tabs[2]));
            Assert.True(dragField.GetValue(window) is null);
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.SequenceEqual(new[] { tabs[2] }, main.Tabs);
            Assert.True(ReferenceEquals(main.SelectedTab, tabs[2]));
            Assert.True(tabs.All(tab => !tab.IsDetached));
        }
        finally
        {
            Mouse.Capture(null);
            window.Close();
            if (restoreCursor) NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
        }

        void RaiseTitleClick(ContentElement source, StreamTabViewModel expected)
        {
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            };
            source.RaiseEvent(down);
            Assert.True(ReferenceEquals(source, down.OriginalSource));
            Assert.Equal(true, down.Handled);
            Assert.True(ReferenceEquals(main.SelectedTab, expected));
            Assert.True(ReferenceEquals(list.SelectedItem, main.SelectedTabStripItem));
            Assert.True(ReferenceEquals(dragField.GetValue(window), expected));
            Assert.True(ReferenceEquals(Mouse.Captured, window));
            // Selection can rebuild the tab template. The release goes to the
            // capturing window, rather than the now-unmounted original title.
            window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseUpEvent
            });
            Assert.True(dragField.GetValue(window) is null);
            Assert.True(!ReferenceEquals(Mouse.Captured, window));
            PumpResponsiveLayout(window);
        }
    });

    private static Task HomeTitleContentAsync(bool useEmoji) => TestSta.RunOffscreenAsync(() =>
    {
        var calls = 0;
        var item = new StreamSearchResultViewModel(
            StreamInputParser.Parse("xqc", PlatformKind.Twitch), new StreamlinkProbeResult(true, "Playable"), null,
            (_, stayOnHome) => { Assert.Equal(true, stayOnHome); calls++; return Task.CompletedTask; });
        TextBlock title = useEmoji ? new EmojiTextBlock { SourceText = "Streamer 🔴" } : new TextBlock();
        var nestedRun = new Run(" nested title");
        if (!useEmoji)
        {
            title.Inlines.Add(new Run("Streamer"));
            title.Inlines.Add(new Span(new Bold(nestedRun)));
        }
        var button = new Button { DataContext = item, Content = title };
        button.SetBinding(Button.CommandProperty, new Binding("OpenCommand"));
        button.Measure(new Size(400, 100));
        button.Arrange(new Rect(0, 0, 400, 100));
        button.UpdateLayout();
        DependencyObject[] sources;
        if (useEmoji)
        {
            var inline = title.Inlines.OfType<InlineUIContainer>().Single();
            sources = [inline, inline.Child];
        }
        else
        {
            sources = [title.Inlines.OfType<Run>().First(), nestedRun];
        }
        foreach (var source in sources)
        {
            Assert.True(MainWindow.TryResolveHomeStreamOpenAndStayOnHomeCommand(source, out var command),
                $"Could not resolve the stream command from {source.GetType().Name}.");
            Assert.True(ReferenceEquals(command, item.OpenAndStayOnHomeCommand));
            Assert.True(MainWindow.TryHandleHomeStreamOpenAndStayOnHomeCommand(source));
        }
        Assert.Equal(sources.Length, calls);
        return Task.CompletedTask;
    });

    private static Task UnattachedInputContentAsync() => TestSta.RunOffscreenAsync(() =>
    {
        DependencyObject?[] sources = [null, new Run("orphan"), new Span(new Run("nested orphan")),
            new InlineUIContainer(new Image()), new ContentElement(), new DependencyObject(), new TextBlock()];
        foreach (var source in sources)
            Assert.Equal(false, MainWindow.TryResolveHomeStreamOpenAndStayOnHomeCommand(source, out _));
        return Task.CompletedTask;
    });

    private static Task RichTextInputContentAsync() => TestSta.RunOffscreenAsync(() =>
    {
        var run = new Run("editable text");
        var input = new RichTextBox(new FlowDocument(new Paragraph(new Span(run))));
        var method = typeof(MainWindow).GetMethod("IsWpfTextInput", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(true, (bool)method.Invoke(null, [input])!);
        Assert.Equal(true, (bool)method.Invoke(null, [run])!);
        Assert.Equal(false, (bool)method.Invoke(null, [new Run("unattached")])!);
        Assert.Equal(false, (bool)method.Invoke(null, [new DependencyObject()])!);
        return Task.CompletedTask;
    });
}

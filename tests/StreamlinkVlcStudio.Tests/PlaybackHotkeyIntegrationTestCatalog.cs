internal static class PlaybackHotkeyIntegrationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Playback hotkeys dispatch defaults and remapped keys through main window input", DefaultsAndRemappingAsync),
        ("Playback hotkeys preserve existing tab bindings when new defaults overlap", ExistingTabBindingPrecedenceAsync),
        ("Playback hotkeys repeat volume clamp bounds and preserve wheel targeting", RepeatBoundsAndWheelAsync),
        ("Playback hotkeys preserve text editing recording and settings navigation", InputGuardsAsync),
        ("Playback hotkey settings record swap and reset the new bindings", RecorderBindingsAsync),
        ("Playback toolbar grid button and back hotkey execute their commands", ToolbarCommandsAsync),
        ("Back hotkey mouse and keyboard remapping restores history without repeating", BackNavigationAsync),
        ("Back hotkey recording captures side buttons swaps conflicts and protects input", BackRecorderAsync),
        ("Back hotkey mouse gestures parse exact modifiers and stay distinct from keys", MouseGesturesAsync)
    ];

    private static Task DefaultsAndRemappingAsync() => RunOnHeadlessStaAsync(() =>
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Press(Key.M).Handled);
        Assert.True(fixture.ViewModel.IsMultiStreamEnabled);
        Assert.True(fixture.Press(Key.Up).Handled);
        Assert.Equal(85, fixture.First.Volume);
        Assert.True(fixture.Press(Key.Down).Handled);
        Assert.Equal(80, fixture.First.Volume);
        Assert.Equal(80, fixture.Second.Volume);

        fixture.Settings.Hotkeys.ToggleMultiStream = "F9";
        fixture.Settings.Hotkeys.VolumeUp = "F10";
        fixture.Settings.Hotkeys.VolumeDown = "F11";
        foreach (var oldKey in new[] { Key.M, Key.Up, Key.Down })
        {
            Assert.Equal(false, fixture.Press(oldKey).Handled);
        }

        Assert.True(fixture.Press(Key.F9).Handled);
        Assert.Equal(false, fixture.ViewModel.IsMultiStreamEnabled);
        Assert.True(fixture.Press(Key.F10).Handled);
        Assert.Equal(85, fixture.First.Volume);
        Assert.True(fixture.Press(Key.F11).Handled);
        Assert.Equal(80, fixture.First.Volume);

        fixture.Settings.Hotkeys.VolumeUp = "Ctrl+Up";
        Assert.Equal(false, fixture.Window.TryExecutePlaybackHotkey(Key.Up, ModifierKeys.None, null));
        Assert.Equal(false, fixture.Window.TryExecutePlaybackHotkey(
            Key.Up, ModifierKeys.Control | ModifierKeys.Shift, null));
        Assert.Equal(80, fixture.First.Volume);
        Assert.True(fixture.Window.TryExecutePlaybackHotkey(Key.Up, ModifierKeys.Control, null));
        Assert.Equal(85, fixture.First.Volume);
        var osd = (VolumeOverlay)fixture.Window.FindName("VolumeOsd");
        Assert.Equal("85%", ((TextBlock)osd.FindName("Percent")).Text);
    });

    private static Task ExistingTabBindingPrecedenceAsync() => RunOnHeadlessStaAsync(() =>
    {
        using var fixture = new Fixture();
        fixture.ViewModel.SelectedTab = fixture.Second;
        fixture.Settings.Hotkeys.PreviousTab = "Up";

        Assert.True(fixture.Press(Key.Up).Handled);
        Assert.True(ReferenceEquals(fixture.First, fixture.ViewModel.SelectedTab));
        Assert.Equal(80, fixture.First.Volume);
        Assert.Equal(80, fixture.Second.Volume);

        fixture.Settings.Hotkeys.NextTab = "M";
        Assert.True(fixture.Press(Key.M).Handled);
        Assert.True(ReferenceEquals(fixture.Second, fixture.ViewModel.SelectedTab));
        Assert.Equal(false, fixture.ViewModel.IsMultiStreamEnabled);

        Assert.True(fixture.ViewModel.CloseTab(fixture.First));
        Assert.Equal(false, fixture.Press(Key.Up).Handled);
        Assert.Equal(false, fixture.Press(Key.M).Handled);
        Assert.Equal(80, fixture.Second.Volume);
        Assert.Equal(false, fixture.ViewModel.IsMultiStreamEnabled);
        fixture.ViewModel.SelectHomeCommand.Execute(null);
        Assert.Equal(false, fixture.Press(Key.M).Handled);
        Assert.Equal(false, fixture.ViewModel.IsMultiStreamEnabled);
    });

    private static Task RepeatBoundsAndWheelAsync() => RunOnHeadlessStaAsync(() =>
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Window.TryExecutePlaybackHotkey(Key.M, ModifierKeys.None, null));
        Assert.True(fixture.ViewModel.IsMultiStreamEnabled);
        for (var repeat = 0; repeat < 3; repeat++)
        {
            Assert.True(fixture.Window.TryExecutePlaybackHotkey(Key.M, ModifierKeys.None, null, isRepeat: true));
            Assert.True(fixture.ViewModel.IsMultiStreamEnabled);
        }

        Assert.True(fixture.Window.TryExecutePlaybackHotkey(Key.M, ModifierKeys.None, null));
        Assert.Equal(false, fixture.ViewModel.IsMultiStreamEnabled);
        Assert.True(fixture.Window.TryExecutePlaybackHotkey(Key.Up, ModifierKeys.None, null));
        Assert.True(fixture.Window.TryExecutePlaybackHotkey(Key.Up, ModifierKeys.None, null, isRepeat: true));
        Assert.Equal(90, fixture.First.Volume);
        Assert.True(fixture.Window.TryExecutePlaybackHotkey(Key.Down, ModifierKeys.None, null, isRepeat: true));
        Assert.Equal(85, fixture.First.Volume);

        fixture.First.Volume = 123;
        Assert.True(fixture.Press(Key.Up).Handled);
        Assert.Equal(125, fixture.First.Volume);
        Assert.True(fixture.Press(Key.Up).Handled);
        Assert.Equal(125, fixture.First.Volume);
        fixture.First.Volume = 2;
        Assert.True(fixture.Press(Key.Down).Handled);
        Assert.Equal(0, fixture.First.Volume);
        Assert.True(fixture.Press(Key.Down).Handled);
        Assert.Equal(0, fixture.First.Volume);
        Assert.Equal(80, fixture.Second.Volume);

        // The wheel continues to change the surface's tab, independently of keyboard selection.
        var hoveredSurface = new Border { Tag = fixture.Second };
        InvokePrivate(fixture.Window, "VideoSurface_MouseWheelScrolled", hoveredSurface,
            new VideoSurfaceMouseWheelEventArgs(Mouse.MouseWheelDeltaForOneLine * 2));
        Assert.Equal(90, fixture.Second.Volume);
        InvokePrivate(fixture.Window, "VideoSurface_MouseWheelScrolled", hoveredSurface,
            new VideoSurfaceMouseWheelEventArgs(-Mouse.MouseWheelDeltaForOneLine / 2));
        Assert.Equal(85, fixture.Second.Volume);
        InvokePrivate(fixture.Window, "VideoSurface_MouseWheelScrolled", hoveredSurface,
            new VideoSurfaceMouseWheelEventArgs(0));
        Assert.Equal(85, fixture.Second.Volume);
        Assert.Equal(0, fixture.First.Volume);
        Assert.True(ReferenceEquals(fixture.First, fixture.ViewModel.SelectedTab));
    });

    private static Task InputGuardsAsync() => RunOnHeadlessStaAsync(() =>
    {
        using var fixture = new Fixture();
        foreach (var editor in new IInputElement[] { new TextBox(), new RichTextBox(), new PasswordBox() })
        {
            foreach (var key in new[] { Key.M, Key.Up, Key.Down })
            {
                Assert.Equal(false, fixture.Window.TryExecutePlaybackHotkey(key, ModifierKeys.None, editor));
            }
        }

        var recorder = (HotkeyRecorderButton)fixture.Window.FindName("VolumeUpHotkeyRecorder");
        InvokePrivate(recorder, "OnClick");
        Assert.True(recorder.IsCapturingInput);
        foreach (var key in new[] { Key.M, Key.Up, Key.Down })
        {
            Assert.Equal(false, fixture.Window.TryExecutePlaybackHotkey(key, ModifierKeys.None, recorder));
        }

        Assert.Equal(false, fixture.ViewModel.IsMultiStreamEnabled);
        Assert.Equal(80, fixture.First.Volume);
        fixture.ViewModel.ToggleSettingsCommand.Execute(null);
        Assert.True(fixture.ViewModel.IsSettingsOpen);
        foreach (var key in new[] { Key.M, Key.Up, Key.Down })
        {
            Assert.Equal(false, fixture.Press(key).Handled);
        }

        Assert.Equal(false, fixture.ViewModel.IsMultiStreamEnabled);
        Assert.Equal(80, fixture.First.Volume);
        fixture.ViewModel.GoBackCommand.Execute(null);
        fixture.ViewModel.SelectHomeCommand.Execute(null);
        Assert.Equal(false, fixture.Window.TryExecutePlaybackHotkey(Key.Up, ModifierKeys.None, null));
        Assert.Equal(false, fixture.Window.TryExecutePlaybackHotkey(Key.Down, ModifierKeys.None, null));
        Assert.Equal(80, fixture.First.Volume);
        Assert.Equal(80, fixture.Second.Volume);
    });

    private static Task RecorderBindingsAsync() => RunOnHeadlessStaAsync(() =>
    {
        using var fixture = new Fixture();
        fixture.ViewModel.ToggleSettingsCommand.Execute(null);
        fixture.ViewModel.ShowHotkeysSettingsCommand.Execute(null);
        fixture.FlushBindings();
        var grid = (HotkeyRecorderButton)fixture.Window.FindName("ToggleMultiStreamHotkeyRecorder");
        var up = (HotkeyRecorderButton)fixture.Window.FindName("VolumeUpHotkeyRecorder");
        var down = (HotkeyRecorderButton)fixture.Window.FindName("VolumeDownHotkeyRecorder");
        AssertRecorderBinding(grid, "Settings.Hotkeys.ToggleMultiStream", "M");
        AssertRecorderBinding(up, "Settings.Hotkeys.VolumeUp", "Up");
        AssertRecorderBinding(down, "Settings.Hotkeys.VolumeDown", "Down");

        // Recording M for volume must move the previous volume gesture to the grid action.
        InvokePrivate(up, "OnClick");
        var captured = fixture.CreateKeyEvent(Key.M);
        InvokePrivate(up, "OnPreviewKeyDown", captured);
        fixture.FlushBindings();
        Assert.True(captured.Handled);
        Assert.Equal("M", fixture.Settings.Hotkeys.VolumeUp);
        Assert.Equal("Up", fixture.Settings.Hotkeys.ToggleMultiStream);
        Assert.True(BindingOperations.IsDataBound(up, HotkeyRecorderButton.GestureProperty));

        InvokePrivate(down, "OnClick");
        InvokePrivate(down, "OnPreviewKeyDown", fixture.CreateKeyEvent(Key.F11));
        fixture.FlushBindings();
        Assert.Equal("F11", fixture.Settings.Hotkeys.VolumeDown);
        Assert.True(BindingOperations.IsDataBound(down, HotkeyRecorderButton.GestureProperty));

        var reset = (Button)fixture.Window.FindName("ResetHotkeysButton");
        reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        fixture.FlushBindings();
        Assert.Equal("M", fixture.Settings.Hotkeys.ToggleMultiStream);
        Assert.Equal("Up", fixture.Settings.Hotkeys.VolumeUp);
        Assert.Equal("Down", fixture.Settings.Hotkeys.VolumeDown);
        AssertRecorderBinding(grid, "Settings.Hotkeys.ToggleMultiStream", "M");
        AssertRecorderBinding(up, "Settings.Hotkeys.VolumeUp", "Up");
        AssertRecorderBinding(down, "Settings.Hotkeys.VolumeDown", "Down");
    });

    private static Task ToolbarCommandsAsync() => RunOnHeadlessStaAsync(() =>
    {
        using var fixture = new Fixture();
        fixture.FlushBindings();
        var grid = (Button)fixture.Window.FindName("TopMultiStreamButton");
        Assert.Equal<object?>(null, fixture.Window.FindName("TopBackButton"));
        BindingOperations.GetBindingExpression(grid, Button.CommandProperty)!.UpdateTarget();
        Assert.Equal("Toggle multi-stream grid", System.Windows.Automation.AutomationProperties.GetName(grid));
        Assert.True(ReferenceEquals(fixture.ViewModel.ToggleMultiStreamCommand, grid.Command));
        InvokePrivate(grid, "OnClick");
        fixture.FlushBindings();
        Assert.True(fixture.ViewModel.IsMultiStreamEnabled);
        InvokePrivate(grid, "OnClick");
        fixture.FlushBindings();
        Assert.Equal(false, fixture.ViewModel.IsMultiStreamEnabled);

        fixture.ViewModel.ToggleSettingsCommand.Execute(null);
        fixture.FlushBindings();
        Assert.True(fixture.ViewModel.IsSettingsOpen);
        var mouseDown = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.XButton1)
        {
            RoutedEvent = Mouse.PreviewMouseUpEvent
        };
        InvokePrivate(fixture.Window, "MainWindowPreviewMouseUp", fixture.Window, mouseDown);
        Assert.True(mouseDown.Handled);
        fixture.FlushBindings();
        Assert.Equal(false, fixture.ViewModel.IsSettingsOpen);
        Assert.Equal(false, fixture.ViewModel.IsHomeSelected);
        Assert.True(ReferenceEquals(fixture.First, fixture.ViewModel.SelectedTab));
    });

    private static Task BackNavigationAsync() => RunOnHeadlessStaAsync(() =>
    {
        using var fixture = new Fixture();
        var back = HotkeyGesture.FromMouseButton(MouseButton.XButton1, ModifierKeys.None);
        fixture.ViewModel.SelectHomeCommand.Execute(null);
        fixture.ViewModel.ShowRecentHomePageCommand.Execute(null);
        fixture.ViewModel.ToggleSettingsCommand.Execute(null);
        Assert.Equal(false, fixture.Window.TryExecuteMouseHotkey(MouseButton.XButton2, ModifierKeys.None, null));
        Assert.Equal(false, fixture.Window.TryExecuteMouseHotkey(MouseButton.XButton1, ModifierKeys.Control, null));
        Assert.True(fixture.Window.TryExecuteMouseHotkey(MouseButton.XButton1, ModifierKeys.None, null));
        Assert.True(fixture.ViewModel.IsRecentHomePageSelected);
        Assert.Equal(false, fixture.ViewModel.IsSettingsOpen);
        Assert.True(fixture.Window.TryExecuteGoBackHotkey(back, null, isRepeat: true));
        Assert.True(fixture.ViewModel.IsHomeSelected);
        Assert.True(fixture.Window.TryExecuteMouseHotkey(MouseButton.XButton1, ModifierKeys.None, new TextBox()));
        Assert.True(fixture.ViewModel.IsFollowedHomePageSelected);
        Assert.True(fixture.Window.TryExecuteMouseHotkey(MouseButton.XButton1, ModifierKeys.None, null));
        Assert.Equal(false, fixture.ViewModel.IsHomeSelected);
        Assert.True(ReferenceEquals(fixture.First, fixture.ViewModel.SelectedTab));

        fixture.ViewModel.ToggleSettingsCommand.Execute(null);
        fixture.Settings.Hotkeys.GoBack = "F9";
        Assert.Equal(false, fixture.Window.TryExecuteMouseHotkey(MouseButton.XButton1, ModifierKeys.None, null));
        Assert.True(fixture.Press(Key.F9).Handled);
        Assert.Equal(false, fixture.ViewModel.IsSettingsOpen);
        Assert.True(ReferenceEquals(fixture.First, fixture.ViewModel.SelectedTab));

        fixture.ViewModel.ToggleSettingsCommand.Execute(null);
        fixture.Settings.Hotkeys.GoBack = "Back";
        foreach (var editor in new IInputElement[] { new TextBox(), new RichTextBox(), new PasswordBox() })
        {
            Assert.Equal(false, fixture.Window.TryExecuteHotkey(new HotkeyGesture(Key.Back, ModifierKeys.None), editor));
            Assert.True(fixture.ViewModel.IsSettingsOpen);
        }

        Assert.True(fixture.Press(Key.Back).Handled);
        fixture.Settings.Hotkeys.GoBack = "invalid-gesture";
        Assert.True(fixture.Window.TryExecuteMouseHotkey(MouseButton.XButton1, ModifierKeys.None, null));
        Assert.Equal(false, fixture.ViewModel.CanGoBack);
        Assert.Equal(false, fixture.Window.TryExecuteMouseHotkey(MouseButton.XButton1, ModifierKeys.None, null));
        Assert.Equal(false, fixture.Window.TryExecuteNativeMouseAppCommand(IntPtr.Zero, new IntPtr(unchecked((int)0x80010000))));
    });

    private static Task BackRecorderAsync() => RunOnHeadlessStaAsync(() =>
    {
        using var fixture = new Fixture();
        fixture.ViewModel.ToggleSettingsCommand.Execute(null);
        fixture.ViewModel.ShowHotkeysSettingsCommand.Execute(null);
        fixture.FlushBindings();
        var back = (HotkeyRecorderButton)fixture.Window.FindName("GoBackHotkeyRecorder");
        AssertRecorderBinding(back, "Settings.Hotkeys.GoBack", "Mouse4");
        Assert.Equal("Mouse4", back.Content?.ToString());

        InvokePrivate(back, "OnClick");
        InvokePrivate(back, "OnPreviewKeyDown", fixture.CreateKeyEvent(Key.M));
        fixture.FlushBindings();
        Assert.Equal("M", fixture.Settings.Hotkeys.GoBack);
        Assert.Equal("Mouse4", fixture.Settings.Hotkeys.ToggleMultiStream);
        Assert.True(fixture.ViewModel.IsSettingsOpen);
        Assert.True(back.IsCapturingInput);
        Assert.True(fixture.Window.TryExecuteMouseHotkey(MouseButton.XButton1, ModifierKeys.None, back));
        Assert.True(fixture.ViewModel.IsSettingsOpen);
        InvokePrivate(back, "OnPreviewKeyUp", fixture.CreateKeyEvent(Key.M));

        fixture.ViewModel.GoBackCommand.Execute(null);
        Assert.True(fixture.Window.TryExecuteMouseHotkey(MouseButton.XButton1, ModifierKeys.None, null));
        Assert.True(fixture.ViewModel.IsMultiStreamEnabled);
        Assert.Equal(false, fixture.ViewModel.IsHomeSelected);
        fixture.ViewModel.ToggleSettingsCommand.Execute(null);
        InvokePrivate(back, "OnClick");
        Assert.True(fixture.Window.TryExecuteMouseHotkey(MouseButton.XButton2, ModifierKeys.Control, back));
        fixture.FlushBindings();
        Assert.Equal("Ctrl+Mouse5", fixture.Settings.Hotkeys.GoBack);
        Assert.Equal("Ctrl + Mouse5", back.Content?.ToString());
        Assert.True(fixture.ViewModel.IsSettingsOpen);
        Assert.True(back.IsCapturingInput);
        Assert.True(back.TryCaptureMouseButton(MouseButton.XButton2, ModifierKeys.Control, isRelease: true));
        Assert.Equal(false, back.IsCapturingInput);
        Assert.Equal(false, fixture.Window.TryExecuteMouseHotkey(MouseButton.XButton2, ModifierKeys.None, null));
        Assert.True(fixture.Window.TryExecuteMouseHotkey(MouseButton.XButton2, ModifierKeys.Control, null));
        Assert.Equal(false, fixture.ViewModel.IsSettingsOpen);

        ((Button)fixture.Window.FindName("ResetHotkeysButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        fixture.FlushBindings();
        AssertRecorderBinding(back, "Settings.Hotkeys.GoBack", "Mouse4");
        Assert.Equal("M", fixture.Settings.Hotkeys.ToggleMultiStream);
    });

    private static Task MouseGesturesAsync() => RunOnHeadlessStaAsync(() =>
    {
        Assert.True(HotkeyGesture.TryParse("control+xbutton1", out var mouse));
        Assert.Equal("Ctrl+Mouse4", mouse.Serialize());
        Assert.Equal("Ctrl + Mouse4", mouse.ToDisplayString());
        Assert.True(HotkeyGesture.Matches("Ctrl+Mouse4", "Mouse4", mouse));
        Assert.Equal(false, HotkeyGesture.Matches("Ctrl+Mouse4", "Mouse4", Key.None, ModifierKeys.Control));
        Assert.Equal(false, HotkeyGesture.TryParse("Mouse3", out _));
        Assert.Equal(false, HotkeyGesture.TryParse("Ctrl+Ctrl+Mouse4", out _));
    });

    private static void AssertRecorderBinding(HotkeyRecorderButton recorder, string path, string gesture)
    {
        var binding = BindingOperations.GetBinding(recorder, HotkeyRecorderButton.GestureProperty);
        Assert.NotNull(binding);
        Assert.Equal(path, binding!.Path.Path);
        Assert.Equal(BindingMode.TwoWay, binding.Mode);
        BindingOperations.GetBindingExpression(recorder, HotkeyRecorderButton.GestureProperty)!.UpdateTarget();
        Assert.Equal(gesture, recorder.Gesture);
    }

    private static void InvokePrivate(object target, string methodName, params object[] arguments)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(target, arguments);
    }

    private static Task RunOnHeadlessStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        { IsBackground = true, Name = "PlaybackHotkeyIntegrationTests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var chatFactory = new FakeChatClientFactory();
            var logger = new MemoryLogger();
            Settings = new AppSettings { MultiStreamEnabled = false };
            ViewModel = TestViewModels.CreateMain(Settings, new FakeSettingsService(Settings), streamlink,
                playbackFactory, chatFactory, logger, action => action());
            First = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best", streamlink, playbackFactory, chatFactory, logger, action => action());
            Second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best", streamlink, playbackFactory, chatFactory, logger, action => action());
            ViewModel.Tabs.Add(First);
            ViewModel.Tabs.Add(Second);
            ViewModel.SelectedTab = First;
            Window = new MainWindow { DataContext = ViewModel };
            typeof(MainWindow).GetField("viewModel", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Window, ViewModel);
            Source = new HeadlessPresentationSource { RootVisual = Window };
        }

        public AppSettings Settings { get; }
        public MainViewModel ViewModel { get; }
        public MainWindow Window { get; }
        public StreamTabViewModel First { get; }
        public StreamTabViewModel Second { get; }
        private HeadlessPresentationSource Source { get; }

        public void FlushBindings()
        {
            // Window construction defers binding attachment until the dispatcher runs.
            // Updating one target cannot resolve a still-pending inherited DataContext.
            Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
        }

        public KeyEventArgs CreateKeyEvent(Key key) => new(Keyboard.PrimaryDevice, Source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };

        public KeyEventArgs Press(Key key)
        {
            var args = CreateKeyEvent(key);
            InvokePrivate(Window, "MainWindowPreviewKeyDown", Window, args);
            return args;
        }

        public void Dispose()
        {
            Window.DataContext = null;
            typeof(MainWindow).GetField("closeConfirmed", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Window, true);
            Window.Close();
            ViewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class HeadlessPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}

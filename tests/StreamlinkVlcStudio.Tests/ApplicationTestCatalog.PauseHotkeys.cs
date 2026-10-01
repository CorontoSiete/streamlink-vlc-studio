using System.Windows.Automation;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> PauseHotkeyTests { get; } =
    [
        ("pause hotkeys: Space pauses and resumes only the selected VOD", () => PauseHotkeyPlaybackAsync(false)),
        ("pause hotkeys: Space pauses and resumes live playback through the toolbar command", () => PauseHotkeyPlaybackAsync(true)),
        ("pause hotkeys: typing recording settings Home and stopped playback remain protected", PauseHotkeyInputGuardsAsync),
        ("pause hotkeys: existing Space bindings retain their navigation volume and skip actions", PauseHotkeyExistingBindingsAsync),
        ("pause hotkeys: repeats and pending transitions cannot enqueue extra toggles across windows", PauseHotkeyPendingTransitionAsync),
        ("pause hotkeys: detached players and replay overlay accept remapped keyboard and mouse input", PauseHotkeyOtherSurfacesAsync),
        ("pause hotkeys: settings record swap autosave reset and replace through real bindings", PauseHotkeySettingsAsync),
        .. string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY"))
            ? Array.Empty<(string, Func<Task>)>()
            : [
                ("pause hotkeys: physical Space pauses real Direct3D11 VLC after clicking video and in fullscreen", () => PauseHotkeyNativeVlcAsync(VideoRendererMode.Direct3D11)),
                ("pause hotkeys: physical Space pauses real GDI VLC after clicking video and in fullscreen", () => PauseHotkeyNativeVlcAsync(VideoRendererMode.Gdi))
            ]
    ];

    private static Task PauseHotkeyPlaybackAsync(bool live) => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new SkipHotkeyFixture(live);
        await fixture.StartAsync();
        if (!live) await fixture.PositionAsync(600);
        Assert.True(fixture.Press(Key.Space).Handled);
        await WaitForPauseHotkeyAsync(fixture, PlaybackStatus.Paused);
        Assert.True(fixture.Engine.Paused);
        Assert.Equal(false, fixture.First.PausedByTabSwitch);
        Assert.Equal(PlaybackStatus.Playing, fixture.Other.Status);
        Assert.Equal(0, fixture.OtherEngine.PauseCount);
        Assert.Equal($"{fixture.First.Target.DisplayName}: {fixture.First.StatusText}", fixture.Main.StatusMessage);

        for (var repeat = 0; repeat < 3; repeat++)
            Assert.True(fixture.Window.TryExecuteHotkey(new HotkeyGesture(Key.Space, ModifierKeys.None), null, isRepeat: true));
        Assert.Equal(1, fixture.Engine.PauseCount);
        Assert.Equal(0, fixture.Engine.ResumeCount);
        Assert.Equal(PlaybackStatus.Paused, fixture.First.Status);

        Assert.True(fixture.Press(Key.Space).Handled);
        await WaitForPauseHotkeyAsync(fixture, PlaybackStatus.Playing);
        Assert.Equal(false, fixture.Engine.Paused);
        if (!live) Assert.Equal(TimeSpan.FromSeconds(600), fixture.Engine.Position);
        Assert.Equal(0, fixture.OtherEngine.ResumeCount);

        // The same command still backs the toolbar after introducing the shortcut.
        await fixture.Main.PauseSelectedCommand.ExecuteAsync();
        Assert.Equal(PlaybackStatus.Paused, fixture.First.Status);
        Assert.True(fixture.Engine.Paused);
    });

    private static Task PauseHotkeyInputGuardsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new SkipHotkeyFixture();
        Assert.Equal(false, fixture.Press(Key.Space).Handled);
        await fixture.StartAsync();
        var space = new HotkeyGesture(Key.Space, ModifierKeys.None);
        foreach (var editor in new IInputElement[] { new TextBox(), new RichTextBox(), new PasswordBox() })
            Assert.Equal(false, fixture.Window.TryExecuteHotkey(space, editor));
        var recorder = (HotkeyRecorderButton)fixture.Window.FindName("TogglePauseHotkeyRecorder");
        InvokeSkipRecorder(recorder, "OnClick");
        Assert.Equal(false, fixture.Window.TryExecuteHotkey(space, recorder));
        InvokeSkipRecorder(recorder, "OnPreviewKeyDown", fixture.KeyEvent(Key.Space));
        Assert.True(recorder.IsCapturingInput);
        Assert.Equal(false, fixture.Window.TryExecuteHotkey(space, recorder));
        InvokeSkipRecorder(recorder, "OnPreviewKeyUp", fixture.KeyEvent(Key.Space));
        Assert.Equal(false, recorder.IsCapturingInput);
        Assert.Equal(0, fixture.Engine.PauseCount);

        fixture.Main.IsSettingsOpen = true;
        Assert.Equal(false, fixture.Press(Key.Space).Handled);
        fixture.Main.IsSettingsOpen = false;
        fixture.Main.SelectHomeCommand.Execute(null);
        Assert.Equal(false, fixture.Press(Key.Space).Handled);
        fixture.Main.SelectedTab = fixture.First;

        fixture.Settings.Hotkeys.TogglePause = "Ctrl+P";
        Assert.Equal(false, fixture.Press(Key.Space).Handled);
        Assert.Equal(false, fixture.Window.TryExecuteHotkey(new HotkeyGesture(Key.P, ModifierKeys.None), null));
        Assert.Equal(false, fixture.Window.TryExecuteHotkey(new HotkeyGesture(Key.P, ModifierKeys.Control | ModifierKeys.Shift), null));
        Assert.True(fixture.Window.TryExecuteHotkey(new HotkeyGesture(Key.P, ModifierKeys.Control), new TextBox()));
        await WaitForPauseHotkeyAsync(fixture, PlaybackStatus.Paused);

        fixture.Settings.Hotkeys.TogglePause = "Mouse5";
        Assert.True(fixture.Window.TryExecuteMouseHotkey(MouseButton.XButton2, ModifierKeys.None, null));
        await WaitForPauseHotkeyAsync(fixture, PlaybackStatus.Playing);
        await fixture.First.StopAsync();
        Assert.Equal(false, fixture.Window.TryExecuteMouseHotkey(MouseButton.XButton2, ModifierKeys.None, null));
    });

    private static Task PauseHotkeyExistingBindingsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new SkipHotkeyFixture();
        await fixture.StartAsync();
        fixture.Settings.Hotkeys.VolumeUp = "Space";
        Assert.True(fixture.Press(Key.Space).Handled);
        Assert.Equal(85, fixture.First.Volume);
        fixture.Settings.Hotkeys.ResetToDefaults();
        fixture.Settings.Hotkeys.SkipBackward = "Space";
        await fixture.PositionAsync(600);
        await fixture.PressAndWaitAsync(Key.Space, 570);
        fixture.Settings.Hotkeys.ResetToDefaults();
        fixture.Main.SelectedTab = fixture.Other;
        fixture.Settings.Hotkeys.PreviousTab = "Space";
        Assert.True(fixture.Press(Key.Space).Handled);
        Assert.True(ReferenceEquals(fixture.First, fixture.Main.SelectedTab));
        fixture.Settings.Hotkeys.ResetToDefaults();
        fixture.Main.SelectHomeCommand.Execute(null);
        fixture.Main.SelectedTab = fixture.First;
        fixture.Settings.Hotkeys.GoBack = "Space";
        Assert.True(fixture.Press(Key.Space).Handled);
        Assert.True(fixture.Main.IsHomeSelected);
        fixture.Settings.Hotkeys.ResetToDefaults();
        fixture.Main.SelectedTab = fixture.First;
        fixture.Settings.Hotkeys.DismissFullscreenOrAutoScroll = "Space";
        Assert.Equal(false, fixture.Press(Key.Space).Handled);
        Assert.Equal(0, fixture.Engine.PauseCount);
        Assert.Equal(0, fixture.OtherEngine.PauseCount);
    });

    private static Task PauseHotkeyPendingTransitionAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new SkipHotkeyFixture(pauseCompletion: completion.Task);
        await fixture.StartAsync();
        var detached = new DetachedVideoWindow(fixture.First);
        try
        {
            Assert.True(fixture.Press(Key.Space).Handled);
            await TestWait.UntilAsync(() => fixture.Engine.PauseCount == 1, TimeSpan.FromSeconds(3));
            Assert.Equal(false, fixture.First.PauseOrResumeCommand.CanExecute(null));
            for (var repeat = 0; repeat < 3; repeat++)
            {
                Assert.True(fixture.Press(Key.Space).Handled);
                var args = fixture.KeyEvent(Key.Space);
                detached.RaiseEvent(args);
                Assert.True(args.Handled);
            }
            completion.TrySetResult();
            await WaitForPauseHotkeyAsync(fixture, PlaybackStatus.Paused);
            Assert.Equal(1, fixture.Engine.PauseCount);
            Assert.Equal(0, fixture.Engine.ResumeCount);
            Assert.True(fixture.Press(Key.Space).Handled);
            await WaitForPauseHotkeyAsync(fixture, PlaybackStatus.Playing);
        }
        finally
        {
            completion.TrySetResult();
            detached.CloseForTabDisposal();
        }
    });

    private static Task PauseHotkeyOtherSurfacesAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new SkipHotkeyFixture();
        await fixture.StartAsync();
        var detached = new DetachedVideoWindow(fixture.Other);
        try
        {
            var args = fixture.KeyEvent(Key.Space);
            detached.RaiseEvent(args);
            Assert.True(args.Handled);
            await TestWait.UntilAsync(() => fixture.Other.Status == PlaybackStatus.Paused &&
                fixture.Other.PauseOrResumeCommand.CanExecute(null), TimeSpan.FromSeconds(3));
            Assert.Equal(PlaybackStatus.Playing, fixture.First.Status);
            Assert.Equal(0, fixture.Engine.PauseCount);
            fixture.Settings.Hotkeys.TogglePause = "F8";
            args = fixture.KeyEvent(Key.Space);
            detached.RaiseEvent(args);
            Assert.Equal(false, args.Handled);
            args = fixture.KeyEvent(Key.F8);
            detached.RaiseEvent(args);
            Assert.True(args.Handled);
            await TestWait.UntilAsync(() => fixture.Other.Status == PlaybackStatus.Playing &&
                fixture.Other.PauseOrResumeCommand.CanExecute(null), TimeSpan.FromSeconds(3));

            var overlay = new ReplaySeekOverlay { DataContext = fixture.First };
            fixture.Flush();
            var chrome = (FrameworkElement)overlay.FindName("OverlayChrome");
            args = fixture.KeyEvent(Key.F8);
            chrome.RaiseEvent(args);
            Assert.True(args.Handled);
            await WaitForPauseHotkeyAsync(fixture, PlaybackStatus.Paused);

            fixture.Settings.Hotkeys.TogglePause = "Mouse5";
            var mouse = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.XButton2)
            { RoutedEvent = Mouse.PreviewMouseUpEvent };
            detached.RaiseEvent(mouse);
            Assert.True(mouse.Handled);
            await TestWait.UntilAsync(() => fixture.Other.Status == PlaybackStatus.Paused &&
                fixture.Other.PauseOrResumeCommand.CanExecute(null), TimeSpan.FromSeconds(3));
            Assert.Equal(PlaybackStatus.Paused, fixture.First.Status);
        }
        finally { detached.CloseForTabDisposal(); }
    });

    private static Task PauseHotkeySettingsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Directory.CreateTempSubdirectory("svs-pause-hotkey-ui-");
        try
        {
            var service = new JsonSettingsService(Path.Combine(directory.FullName, "settings.json"));
            await using var fixture = new SkipHotkeyFixture(settingsService: service);
            fixture.Main.IsSettingsOpen = true;
            fixture.Main.ShowHotkeysSettingsCommand.Execute(null);
            fixture.Layout();
            var pause = (HotkeyRecorderButton)fixture.Window.FindName("TogglePauseHotkeyRecorder");
            var up = (HotkeyRecorderButton)fixture.Window.FindName("VolumeUpHotkeyRecorder");
            Assert.Equal("Space", pause.Content.ToString());
            Assert.Equal("Space", pause.DefaultGesture);
            Assert.Equal("Pause or resume playback hotkey: Space", AutomationProperties.GetName(pause));
            Assert.Equal("Settings.Hotkeys.TogglePause", pause.GetBindingExpression(HotkeyRecorderButton.GestureProperty)!.ParentBinding.Path.Path);
            InvokeSkipRecorder(pause, "OnClick");
            InvokeSkipRecorder(pause, "OnPreviewKeyDown", fixture.KeyEvent(Key.Up));
            InvokeSkipRecorder(pause, "OnPreviewKeyUp", fixture.KeyEvent(Key.Up));
            fixture.Flush();
            Assert.Equal("Up", fixture.Settings.Hotkeys.TogglePause);
            Assert.Equal("Space", fixture.Settings.Hotkeys.VolumeUp);
            Assert.Equal("Space", up.Content.ToString());
            var loaded = await WaitForPauseHotkeySettingsAsync(service.SettingsPath, "Up");
            Assert.Equal("Up", loaded.Hotkeys.TogglePause);
            Assert.Equal("Space", loaded.Hotkeys.VolumeUp);

            ((Button)fixture.Window.FindName("ResetHotkeysButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            fixture.Flush();
            Assert.Equal("Space", pause.Content.ToString());
            Assert.Equal("Up", up.Content.ToString());
            _ = await WaitForPauseHotkeySettingsAsync(service.SettingsPath, "Space");
            foreach (var width in new[] { 1320d, 700d })
            {
                fixture.Layout(width);
                Assert.True(pause.ActualWidth > 0 && pause.ActualWidth <= 178);
                SaveResponsiveWindowImage(fixture.Window, $"pause-hotkey-settings-{width}");
            }
            fixture.Settings.Hotkeys = new HotkeySettings { TogglePause = "F9" };
            fixture.Flush();
            Assert.Equal("F9", pause.Content.ToString());
            _ = await WaitForPauseHotkeySettingsAsync(service.SettingsPath, "F9");
        }
        finally { directory.Delete(recursive: true); }
    });

    private static Task WaitForPauseHotkeyAsync(SkipHotkeyFixture fixture, PlaybackStatus expected) =>
        TestWait.UntilAsync(() => fixture.First.Status == expected && fixture.First.PauseOrResumeCommand.CanExecute(null) &&
            fixture.Main.PauseSelectedCommand.CanExecute(null), TimeSpan.FromSeconds(3));

    private static async Task<AppSettings> WaitForPauseHotkeySettingsAsync(string path, string expected)
    {
        var service = new JsonSettingsService(path);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (true)
        {
            var loaded = await service.LoadAsync(timeout.Token);
            if (File.Exists(path) && loaded.Hotkeys.TogglePause == expected) return loaded;
            await Task.Delay(50, timeout.Token);
        }
    }
}

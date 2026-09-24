internal static class HotkeySettingsExpansionTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Extra hotkey defaults normalize reset and notify settings bindings", DefaultsResetAndNotifications),
        ("Extra hotkeys match configured gestures and exchange conflicting bindings", MatchingAndConflictSwaps),
        ("Extra hotkeys preserve typing and arrow navigation in text editors", TextEditingSuppressionAsync),
        ("Extra hotkeys persist across a settings service reload", PersistenceAsync),
        ("Extra hotkeys default safely when loading older settings", OlderSettingsCompatibilityAsync)
    ];

    private static Task DefaultsResetAndNotifications()
    {
        var settings = new HotkeySettings();
        AssertExtraDefaults(settings);
        var changed = new List<string?>();
        settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.ToggleMultiStream = " Ctrl+M ";
        settings.VolumeUp = " Alt+Up ";
        settings.VolumeDown = " Alt+Down ";
        settings.GoBack = " Ctrl+Mouse5 ";
        Assert.Equal("Ctrl+M", settings.ToggleMultiStream);
        Assert.Equal("Alt+Up", settings.VolumeUp);
        Assert.Equal("Alt+Down", settings.VolumeDown);
        Assert.Equal("Ctrl+Mouse5", settings.GoBack);

        changed.Clear();
        settings.ResetToDefaults();
        AssertExtraDefaults(settings);
        Assert.True(changed.SequenceEqual(new[]
        {
            nameof(HotkeySettings.ToggleMultiStream),
            nameof(HotkeySettings.VolumeUp),
            nameof(HotkeySettings.VolumeDown),
            nameof(HotkeySettings.GoBack)
        }));

        settings.ToggleMultiStream = " ";
        settings.VolumeUp = null!;
        settings.VolumeDown = "";
        settings.GoBack = " ";
        AssertExtraDefaults(settings);
        return Task.CompletedTask;
    }

    private static Task MatchingAndConflictSwaps()
    {
        var settings = new HotkeySettings();
        foreach (var (action, key) in new[]
        {
            (AppHotkeyAction.ToggleMultiStream, Key.M),
            (AppHotkeyAction.VolumeUp, Key.Up),
            (AppHotkeyAction.VolumeDown, Key.Down)
        })
        {
            Assert.True(HotkeyBindingPolicy.Matches(settings, action, key, ModifierKeys.None));
            Assert.Equal(false, HotkeyBindingPolicy.Matches(settings, action, key, ModifierKeys.Shift));
            HotkeyBindingPolicy.SetConfiguredGesture(settings, action, "invalid-hotkey");
            Assert.True(HotkeyBindingPolicy.Matches(settings, action, key, ModifierKeys.None));
            HotkeyBindingPolicy.SetConfiguredGesture(settings, action, "Ctrl+F9");
            Assert.Equal("Ctrl+F9", HotkeyBindingPolicy.GetEffectiveGesture(settings, action));
            Assert.True(HotkeyBindingPolicy.Matches(settings, action, Key.F9, ModifierKeys.Control));
            Assert.Equal(false, HotkeyBindingPolicy.Matches(settings, action, key, ModifierKeys.None));
            settings.ResetToDefaults();
        }

        // Exercise conflicts with both existing actions and the new actions,
        // regardless of which side is being edited in the recorder.
        foreach (var (action, conflictingAction) in new[]
        {
            (AppHotkeyAction.ToggleMultiStream, AppHotkeyAction.PreviousTab),
            (AppHotkeyAction.PreviousTab, AppHotkeyAction.ToggleMultiStream),
            (AppHotkeyAction.VolumeUp, AppHotkeyAction.VolumeDown),
            (AppHotkeyAction.VolumeDown, AppHotkeyAction.NextTab),
            (AppHotkeyAction.GoBack, AppHotkeyAction.PreviousTab),
            (AppHotkeyAction.VolumeUp, AppHotkeyAction.GoBack)
        })
        {
            settings.ResetToDefaults();
            var previous = HotkeyBindingPolicy.GetEffectiveGesture(settings, action);
            var replacement = HotkeyBindingPolicy.GetEffectiveGesture(settings, conflictingAction);
            var swapped = HotkeyBindingPolicy.SwapConflictingBinding(settings, action, previous, replacement);
            HotkeyBindingPolicy.SetConfiguredGesture(settings, action, replacement);

            Assert.Equal<AppHotkeyAction?>(conflictingAction, swapped);
            Assert.Equal(previous, HotkeyBindingPolicy.GetEffectiveGesture(settings, conflictingAction));
            Assert.Equal(replacement, HotkeyBindingPolicy.GetEffectiveGesture(settings, action));
            var gestures = Enum.GetValues<AppHotkeyAction>()
                .Select(candidate => HotkeyBindingPolicy.GetEffectiveGesture(settings, candidate))
                .ToArray();
            Assert.Equal(gestures.Length, gestures.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        return Task.CompletedTask;
    }

    private static Task TextEditingSuppressionAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var settings = new HotkeySettings();
                var editors = new IInputElement[] { new TextBox(), new RichTextBox(), new PasswordBox() };
                foreach (var action in new[]
                {
                    AppHotkeyAction.ToggleMultiStream,
                    AppHotkeyAction.VolumeUp,
                    AppHotkeyAction.VolumeDown
                })
                {
                    foreach (var editor in editors)
                    {
                        Assert.True(HotkeyBindingPolicy.ShouldSuppressForTextInput(settings, action, editor));
                    }

                    Assert.Equal(false, HotkeyBindingPolicy.ShouldSuppressForTextInput(settings, action, null));
                    Assert.Equal(false, HotkeyBindingPolicy.ShouldSuppressForTextInput(settings, action, new Button()));
                }

                settings.VolumeUp = "Shift+Up";
                settings.VolumeDown = "Ctrl+Down";
                settings.ToggleMultiStream = "Shift+M";
                foreach (var editor in editors)
                {
                    Assert.True(HotkeyBindingPolicy.ShouldSuppressForTextInput(settings, AppHotkeyAction.VolumeUp, editor));
                    Assert.True(HotkeyBindingPolicy.ShouldSuppressForTextInput(settings, AppHotkeyAction.ToggleMultiStream, editor));
                    Assert.Equal(false, HotkeyBindingPolicy.ShouldSuppressForTextInput(settings, AppHotkeyAction.VolumeDown, editor));
                }

                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "ExtraHotkeyTextEditingTests"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static async Task PersistenceAsync()
    {
        var directory = Directory.CreateTempSubdirectory("svs-extra-hotkey-settings-");
        var path = Path.Combine(directory.FullName, "settings.json");
        try
        {
            var settings = new AppSettings();
            settings.Hotkeys.ToggleMultiStream = "Ctrl+M";
            settings.Hotkeys.VolumeUp = "Alt+Up";
            settings.Hotkeys.VolumeDown = "Alt+Down";
            settings.Hotkeys.GoBack = "Ctrl+Mouse5";
            await new JsonSettingsService(path).SaveAsync(settings);

            var loaded = await new JsonSettingsService(path).LoadAsync();
            Assert.Equal("Ctrl+M", loaded.Hotkeys.ToggleMultiStream);
            Assert.Equal("Alt+Up", loaded.Hotkeys.VolumeUp);
            Assert.Equal("Alt+Down", loaded.Hotkeys.VolumeDown);
            Assert.Equal("Ctrl+Mouse5", loaded.Hotkeys.GoBack);
            loaded.Hotkeys.ResetToDefaults();
            await new JsonSettingsService(path).SaveAsync(loaded);
            AssertExtraDefaults((await new JsonSettingsService(path).LoadAsync()).Hotkeys);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task OlderSettingsCompatibilityAsync()
    {
        var directory = Directory.CreateTempSubdirectory("svs-older-hotkey-settings-");
        var path = Path.Combine(directory.FullName, "settings.json");
        try
        {
            foreach (var hotkeys in new[]
            {
                "",
                "\"Hotkeys\": null,",
                "\"Hotkeys\": { \"PreviousTab\": \"Ctrl+Prior\", \"ToggleReplaySeekBar\": \"F8\" },"
            })
            {
                await File.WriteAllTextAsync(path, "{" + hotkeys + "\"DefaultQuality\": \"480p\"}");
                var service = new JsonSettingsService(path);
                var loaded = await service.LoadAsync();
                AssertExtraDefaults(loaded.Hotkeys);
                Assert.Equal("480p", loaded.DefaultQuality);
                Assert.Equal<string?>(null, service.LastLoadWarning);
                if (hotkeys.Contains("Ctrl+Prior", StringComparison.Ordinal))
                {
                    Assert.Equal("Ctrl+Prior", loaded.Hotkeys.PreviousTab);
                    Assert.Equal("F8", loaded.Hotkeys.ToggleReplaySeekBar);
                }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void AssertExtraDefaults(HotkeySettings settings)
    {
        Assert.Equal("M", settings.ToggleMultiStream);
        Assert.Equal("Up", settings.VolumeUp);
        Assert.Equal("Down", settings.VolumeDown);
        Assert.Equal("Mouse4", settings.GoBack);
    }
}

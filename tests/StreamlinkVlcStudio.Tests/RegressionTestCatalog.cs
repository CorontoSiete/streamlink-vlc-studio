internal static class RegressionTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Kick identity snapshots remain serializable during concurrent updates", KickIdentitySnapshotsAreAtomicAsync),
        ("Live chat supervisor retries forever resets after stability and drains", LiveChatSupervisorRetriesAndDrainsAsync),
        ("Async command releases its gate when predicates and events throw", AsyncCommandReleasesGateOnCallbackFailureAsync),
        ("Clipboard service retries contention three times without surfacing UI failures", ClipboardRetriesAreBoundedAsync),
        ("Overlay scheduler timeout shuts down a dispatcher that starts later", OverlaySchedulerTimeoutDrainsLateDispatcherAsync),
        ("Native overlay codec bounds the complete encoded message", NativeOverlayCodecBoundsCompleteMessage),
        ("Native overlay write loop isolates callbacks and logger failures", NativeOverlayWriteLoopIsolatesCallbacksAsync),
        ("Stale native overlay resize callback cannot replace a newer session", NativeOverlayResizeRejectsStaleCallbackAsync),
        ("Native overlay probe single-flights transient failures and keys definitive results by identity", NativeOverlayProbeCachingAsync),
        ("Stream input channel try parser rejects invalid input without throwing", StreamInputTryFromChannel),
        ("Kick subscription disposal cancels drains and closes admission", KickSubscriptionDisposalDrainsAsync),
        ("Kick replay cache enforces age and byte retention while preserving current day", KickReplayCacheRetention),
        ("TwitchDownloader cache scans beyond 5000 and backfills a later seek", TwitchCacheScansAndBackfillsAsync),
        ("VLC audio requests publish atomic immutable snapshots", VlcAudioRequestsPublishAtomicSnapshots),
        ("Hotkey gestures accept arbitrary keys and require exact modifiers", HotkeyGesturesAcceptArbitraryKeys),
        ("Hotkey policy falls back swaps duplicates and protects text input", HotkeyPolicyFallsBackSwapsAndSuppresses),
        ("Hotkey settings expose stable defaults and reset every binding", HotkeySettingsDefaultsAndReset),
        ("Hotkey recorder captures without native input and preserves two-way binding", HotkeyRecorderCapturesAndUpdatesBindingAsync),
        ("Configured hotkeys route through main window preview input", ConfiguredHotkeyRoutesThroughMainWindowAsync),
        ("App updater starts bundled installer script when available", AppUpdaterStartsBundledInstallerScriptAsync),
        ("App updater keeps app open when installed version matches GitHub latest", AppUpdaterKeepsAppOpenWhenCurrentVersionMatchesLatestAsync),
        ("App updater downloads and launches checksum-verified MSI", AppUpdaterDownloadsAndLaunchesVerifiedMsiAsync),
        ("App updater rejects MSI checksum mismatch before launch", AppUpdaterRejectsMsiChecksumMismatchAsync),
        ("Update command reports status and requests shutdown after launch", UpdateCommandReportsStatusAndRequestsShutdownAsync),
        ("Update command keeps app open when updater reports latest version", UpdateCommandKeepsAppOpenWhenUpdaterReportsLatestAsync),
        ("Installer OS gates allow 64-bit Windows 10 and 11 without build-specific blocks", InstallerOsGatesAllow64BitWindows10And11)
    ];

    private static Task InstallerOsGatesAllow64BitWindows10And11()
    {
        var repoRoot = FindRepoRoot();
        var installerDirectory = Path.Combine(repoRoot, "scripts", "installer");
        var wixNamespace = System.Xml.Linq.XNamespace.Get("http://wixtoolset.org/schemas/v4/wxs");

        var msiDocument = System.Xml.Linq.XDocument.Load(
            Path.Combine(installerDirectory, "StreamlinkVlcStudio.wxs"));
        var launch = msiDocument.Descendants(wixNamespace + "Launch").Single();
        var launchCondition = (string?)launch.Attribute("Condition") ?? "";
        Assert.Equal("Installed OR VersionNT64 >= 603", launchCondition);
        Assert.Equal(
            "Streamlink VLC Studio requires 64-bit Windows 10 or later.",
            (string?)launch.Attribute("Message") ?? "");
        Assert.DoesNotContain("WindowsBuild", launchCondition);
        Assert.DoesNotContain("17763", launchCondition);

        var bundleDocument = System.Xml.Linq.XDocument.Load(
            Path.Combine(installerDirectory, "StreamlinkVlcStudio.Bundle.wxs"));
        var bundle = bundleDocument.Descendants(wixNamespace + "Bundle").Single();
        var bundleCondition = (string?)bundle.Attribute("Condition") ?? "";
        Assert.Equal("VersionNT64 >= v10.0", bundleCondition);
        Assert.DoesNotContain("WindowsBuildNumber", bundleCondition);
        Assert.DoesNotContain("17763", bundleCondition);

        return Task.CompletedTask;
    }

    private static Task HotkeyGesturesAcceptArbitraryKeys()
    {
        Assert.True(HotkeyGesture.TryParse("K", out var plain));
        Assert.Equal(Key.K, plain.Key);
        Assert.Equal(ModifierKeys.None, plain.Modifiers);
        Assert.Equal("K", plain.Serialize());
        Assert.Equal("K", plain.ToDisplayString());

        Assert.True(HotkeyGesture.TryParse("Shift+K", out var shifted));
        Assert.Equal(Key.K, shifted.Key);
        Assert.Equal(ModifierKeys.Shift, shifted.Modifiers);
        Assert.Equal("Shift+K", shifted.Serialize());
        Assert.True(HotkeyGesture.Matches("Shift+K", "Ctrl+S", Key.K, ModifierKeys.Shift));
        Assert.Equal(false, HotkeyGesture.Matches("Shift+K", "Ctrl+S", Key.K, ModifierKeys.None));
        Assert.Equal(
            false,
            HotkeyGesture.Matches(
                "Shift+K",
                "Ctrl+S",
                Key.K,
                ModifierKeys.Control | ModifierKeys.Shift));

        Assert.True(HotkeyGesture.TryParse("windows+control+alt+shift+OemPlus", out var combined));
        Assert.Equal(
            ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows,
            combined.Modifiers);
        Assert.Equal("Ctrl+Alt+Shift+Win+OemPlus", combined.Serialize());
        Assert.Equal("Ctrl + Alt + Shift + Win + =/+", combined.ToDisplayString());

        Assert.True(HotkeyGesture.TryParse("D7", out var digit));
        Assert.Equal("7", digit.ToDisplayString());
        Assert.Equal("Alt+F8", HotkeyGesture.ParseOrDefault("not-a-key", "Alt+F8").Serialize());
        Assert.Equal(
            "K",
            HotkeyGesture.ParseOrDefault("K", "also-not-a-key").Serialize());

        Assert.Equal(false, HotkeyGesture.TryParse("", out _));
        Assert.Equal(false, HotkeyGesture.TryParse("Ctrl", out _));
        Assert.Equal(false, HotkeyGesture.TryParse("Ctrl+Ctrl+S", out _));
        Assert.Equal(false, HotkeyGesture.TryParse("Ctrl++S", out _));
        Assert.Equal(false, HotkeyGesture.TryParse("S+", out _));
        Assert.Equal(false, HotkeyGesture.TryParse("Ctrl+not-a-key", out _));
        Assert.Equal(false, HotkeyGesture.IsBindableKey(Key.LeftCtrl));
        Assert.True(HotkeyGesture.IsBindableKey(Key.Escape));
        Assert.Equal(Key.F4, HotkeyGesture.NormalizeEventKey(Key.System, systemKey: Key.F4));
        Assert.Equal(Key.K, HotkeyGesture.NormalizeEventKey(Key.ImeProcessed, imeProcessedKey: Key.K));
        Assert.Equal(
            Key.OemQuotes,
            HotkeyGesture.NormalizeEventKey(Key.DeadCharProcessed, deadCharProcessedKey: Key.OemQuotes));
        Assert.Throws<ArgumentException>(() => HotkeyGesture.ParseOrDefault("invalid", "still-invalid"));
        return Task.CompletedTask;
    }

    private static Task HotkeyPolicyFallsBackSwapsAndSuppresses()
    {
        return RunOnHeadlessStaAsync(() =>
        {
            var settings = new HotkeySettings
            {
                ToggleReplaySeekBar = "invalid-gesture"
            };

            Assert.Equal(
                HotkeySettings.DefaultToggleReplaySeekBar,
                HotkeyBindingPolicy.GetEffectiveGesture(settings, AppHotkeyAction.ToggleReplaySeekBar));
            Assert.True(HotkeyBindingPolicy.Matches(
                settings,
                AppHotkeyAction.ToggleReplaySeekBar,
                Key.S,
                ModifierKeys.Control));
            Assert.Equal(false, HotkeyBindingPolicy.Matches(
                settings,
                AppHotkeyAction.ToggleReplaySeekBar,
                Key.S,
                ModifierKeys.Control | ModifierKeys.Shift));

            settings.ToggleReplaySeekBar = "K";
            var input = new TextBox();
            Assert.True(HotkeyBindingPolicy.ShouldSuppressForTextInput(
                settings,
                AppHotkeyAction.ToggleReplaySeekBar,
                input));
            settings.ToggleReplaySeekBar = "Shift+K";
            Assert.True(HotkeyBindingPolicy.ShouldSuppressForTextInput(
                settings,
                AppHotkeyAction.ToggleReplaySeekBar,
                input));
            settings.ToggleReplaySeekBar = "Ctrl+K";
            Assert.Equal(false, HotkeyBindingPolicy.ShouldSuppressForTextInput(
                settings,
                AppHotkeyAction.ToggleReplaySeekBar,
                input));
            settings.ToggleReplaySeekBar = "F8";
            Assert.Equal(false, HotkeyBindingPolicy.ShouldSuppressForTextInput(
                settings,
                AppHotkeyAction.ToggleReplaySeekBar,
                input));
            settings.DismissFullscreenOrAutoScroll = "Escape";
            Assert.Equal(false, HotkeyBindingPolicy.ShouldSuppressForTextInput(
                settings,
                AppHotkeyAction.DismissFullscreenOrAutoScroll,
                input));
            settings.DismissFullscreenOrAutoScroll = "K";
            Assert.True(HotkeyBindingPolicy.ShouldSuppressForTextInput(
                settings,
                AppHotkeyAction.DismissFullscreenOrAutoScroll,
                input));
            settings.NextTab = "Ctrl+Prior";
            Assert.Equal(false, HotkeyBindingPolicy.ShouldSuppressForTextInput(
                settings,
                AppHotkeyAction.NextTab,
                input));
            input.Visibility = Visibility.Collapsed;
            settings.ToggleReplaySeekBar = "K";
            Assert.Equal(false, HotkeyBindingPolicy.ShouldSuppressForTextInput(
                settings,
                AppHotkeyAction.ToggleReplaySeekBar,
                input));
            Assert.Equal(false, HotkeyBindingPolicy.ShouldSuppressForTextInput(
                settings,
                AppHotkeyAction.ToggleReplaySeekBar,
                null));

            settings.ResetToDefaults();
            var swapped = HotkeyBindingPolicy.SwapConflictingBinding(
                settings,
                AppHotkeyAction.PreviousTab,
                settings.PreviousTab,
                settings.NextTab);
            Assert.Equal<AppHotkeyAction?>(AppHotkeyAction.NextTab, swapped);
            settings.PreviousTab = HotkeySettings.DefaultNextTab;
            Assert.Equal(HotkeySettings.DefaultNextTab, settings.PreviousTab);
            Assert.Equal(HotkeySettings.DefaultPreviousTab, settings.NextTab);

            var noConflict = HotkeyBindingPolicy.SwapConflictingBinding(
                settings,
                AppHotkeyAction.ToggleReplaySeekBar,
                settings.ToggleReplaySeekBar,
                "Alt+F10");
            Assert.Equal<AppHotkeyAction?>(null, noConflict);
            Assert.Throws<ArgumentException>(() => HotkeyBindingPolicy.SwapConflictingBinding(
                settings,
                AppHotkeyAction.ToggleReplaySeekBar,
                settings.ToggleReplaySeekBar,
                "bad+gesture+value"));
        });
    }

    private static Task HotkeySettingsDefaultsAndReset()
    {
        var settings = new HotkeySettings();
        Assert.Equal(HotkeySettings.DefaultDismissFullscreenOrAutoScroll, settings.DismissFullscreenOrAutoScroll);
        Assert.Equal(HotkeySettings.DefaultToggleReplaySeekBar, settings.ToggleReplaySeekBar);
        Assert.Equal(HotkeySettings.DefaultPreviousTab, settings.PreviousTab);
        Assert.Equal(HotkeySettings.DefaultNextTab, settings.NextTab);

        settings.DismissFullscreenOrAutoScroll = " F12 ";
        settings.ToggleReplaySeekBar = "Shift+T";
        settings.PreviousTab = "Ctrl+PageUp";
        settings.NextTab = "Ctrl+PageDown";
        Assert.Equal("F12", settings.DismissFullscreenOrAutoScroll);

        settings.ResetToDefaults();
        Assert.Equal(HotkeySettings.DefaultDismissFullscreenOrAutoScroll, settings.DismissFullscreenOrAutoScroll);
        Assert.Equal(HotkeySettings.DefaultToggleReplaySeekBar, settings.ToggleReplaySeekBar);
        Assert.Equal(HotkeySettings.DefaultPreviousTab, settings.PreviousTab);
        Assert.Equal(HotkeySettings.DefaultNextTab, settings.NextTab);

        settings.PreviousTab = " ";
        Assert.Equal(HotkeySettings.DefaultPreviousTab, settings.PreviousTab);
        var appSettings = new AppSettings { Hotkeys = null! };
        Assert.Equal(HotkeySettings.DefaultNextTab, appSettings.Hotkeys.NextTab);
        return Task.CompletedTask;
    }

    private static Task HotkeyRecorderCapturesAndUpdatesBindingAsync()
    {
        return RunOnHeadlessStaAsync(() =>
        {
            var settings = new HotkeySettings { PreviousTab = "Shift+J" };
            var recorder = new HotkeyRecorderButton
            {
                DefaultGesture = HotkeySettings.DefaultPreviousTab,
                ActionName = "Previous tab hotkey"
            };
            recorder.SetBinding(
                HotkeyRecorderButton.GestureProperty,
                new Binding(nameof(HotkeySettings.PreviousTab))
                {
                    Source = settings,
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                });

            Assert.Equal("Shift+J", recorder.Gesture);
            Assert.Equal("Shift + J", recorder.Content?.ToString());
            Assert.Equal(
                "Previous tab hotkey: Shift + J",
                System.Windows.Automation.AutomationProperties.GetName(recorder));
            var metadata = (FrameworkPropertyMetadata)HotkeyRecorderButton.GestureProperty.GetMetadata(
                typeof(HotkeyRecorderButton));
            Assert.True(metadata.BindsTwoWayByDefault);

            string? previousGesture = null;
            string? newGesture = null;
            var changingCount = 0;
            recorder.GestureChanging += (_, e) =>
            {
                changingCount++;
                previousGesture = e.PreviousGesture;
                newGesture = e.NewGesture;
            };

            InvokeHotkeyRecorderMethod(recorder, "OnClick");
            Assert.True(recorder.IsCapturing);
            Assert.Contains("Press a key combination", recorder.Content?.ToString() ?? "");
            Assert.Contains(
                "press a key combination",
                System.Windows.Automation.AutomationProperties.GetName(recorder));

            var source = new HeadlessPresentationSource { RootVisual = recorder };
            var keyDown = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                source,
                Environment.TickCount,
                Key.K)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            InvokeHotkeyRecorderMethod(recorder, "OnPreviewKeyDown", keyDown);

            Assert.True(keyDown.Handled);
            Assert.Equal(false, recorder.IsCapturing);
            Assert.True(recorder.IsCapturingInput);
            Assert.Equal(1, changingCount);
            Assert.Equal("Shift+J", previousGesture);
            Assert.Equal("K", newGesture);
            Assert.Equal("K", recorder.Gesture);
            Assert.Equal("K", settings.PreviousTab);
            Assert.Equal("K", recorder.Content?.ToString());
            Assert.Equal(
                "Previous tab hotkey: K",
                System.Windows.Automation.AutomationProperties.GetName(recorder));

            var repeatedKeyDown = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                source,
                Environment.TickCount,
                Key.K)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            InvokeHotkeyRecorderMethod(recorder, "OnPreviewKeyDown", repeatedKeyDown);
            Assert.True(repeatedKeyDown.Handled);
            Assert.Equal(1, changingCount);
            Assert.True(recorder.IsCapturingInput);

            var keyUp = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                source,
                Environment.TickCount,
                Key.K)
            {
                RoutedEvent = Keyboard.PreviewKeyUpEvent
            };
            InvokeHotkeyRecorderMethod(recorder, "OnPreviewKeyUp", keyUp);
            Assert.True(keyUp.Handled);
            Assert.Equal(false, recorder.IsCapturingInput);

            InvokeHotkeyRecorderMethod(recorder, "OnClick");
            var secondKeyDown = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                source,
                Environment.TickCount,
                Key.L)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            InvokeHotkeyRecorderMethod(recorder, "OnPreviewKeyDown", secondKeyDown);
            Assert.True(recorder.IsCapturingInput);
            var focusLost = new KeyboardFocusChangedEventArgs(
                Keyboard.PrimaryDevice,
                Environment.TickCount,
                recorder,
                null)
            {
                RoutedEvent = Keyboard.LostKeyboardFocusEvent
            };
            InvokeHotkeyRecorderMethod(recorder, "OnLostKeyboardFocus", focusLost);
            Assert.Equal(false, recorder.IsCapturingInput);
        });
    }

    private static Task ConfiguredHotkeyRoutesThroughMainWindowAsync()
    {
        return RunOnHeadlessStaAsync(() =>
        {
            var settings = new AppSettings();
            settings.Hotkeys.ToggleReplaySeekBar = "F8";
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                new FakeStreamlinkService(),
                new FakePlaybackEngineFactory(),
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action());
            var window = new MainWindow { DataContext = viewModel };
            var viewModelField = typeof(MainWindow).GetField(
                "viewModel",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(viewModelField);
            viewModelField!.SetValue(window, viewModel);

            var initialVisibility = viewModel.IsReplaySeekBarUiVisible;
            var source = new HeadlessPresentationSource { RootVisual = window };
            var keyDown = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                source,
                Environment.TickCount,
                Key.F8)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            var handler = typeof(MainWindow).GetMethod(
                "MainWindowPreviewKeyDown",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(handler);
            handler!.Invoke(window, [window, keyDown]);

            Assert.True(keyDown.Handled);
            Assert.Equal(!initialVisibility, viewModel.IsReplaySeekBarUiVisible);
            viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        });
    }

    private static async Task AppUpdaterStartsBundledInstallerScriptAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "svs-updater-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "install.ps1");
            await File.WriteAllTextAsync(scriptPath, "# test installer");
            var requests = new List<Uri>();
            var launched = new List<ProcessStartInfo>();
            using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                requests.Add(request.RequestUri!);
                return request.RequestUri!.AbsoluteUri switch
                {
                    "https://api.github.com/repos/owner/repo/releases/latest" => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            CreateLatestReleaseJson(msiSize: 1, checksumSize: 1),
                            Encoding.UTF8,
                            "application/json")
                    },
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                };
            }));
            using var updater = new GitHubReleaseAppUpdateService(
                new MemoryLogger(),
                httpClient,
                "owner/repo",
                root,
                Path.Combine(root, "updates"),
                startInfo =>
                {
                    launched.Add(startInfo);
                    return true;
                },
                getCurrentInstalledVersion: () => "1.0.6");

            var result = await updater.StartLatestReleaseUpdateAsync();

            Assert.True(result.RequestApplicationShutdown);
            Assert.Contains("installer script", result.Message);
            Assert.Equal(1, requests.Count);
            Assert.Equal(1, launched.Count);
            var startInfo = launched[0];
            Assert.Equal("powershell.exe", startInfo.FileName);
            Assert.Equal(false, startInfo.UseShellExecute);
            var arguments = startInfo.ArgumentList.ToArray();
            AssertArgumentValue(arguments, "-File", scriptPath);
            AssertArgumentValue(arguments, "-InstallDir", root);
            AssertArgumentValue(arguments, "-GitHubRepository", "owner/repo");
            AssertArgumentValue(arguments, "-AppSource", "GitHub");
            Assert.True(arguments.Contains("-ForceStopApp"));
            Assert.True(arguments.Contains("-Launch"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task AppUpdaterKeepsAppOpenWhenCurrentVersionMatchesLatestAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "svs-updater-current-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var requests = new List<Uri>();
            var launched = false;
            using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                requests.Add(request.RequestUri!);
                return request.RequestUri!.AbsoluteUri switch
                {
                    "https://api.github.com/repos/owner/repo/releases/latest" => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            CreateLatestReleaseJson(msiSize: 1, checksumSize: 1),
                            Encoding.UTF8,
                            "application/json")
                    },
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                };
            }));
            using var updater = new GitHubReleaseAppUpdateService(
                new MemoryLogger(),
                httpClient,
                "owner/repo",
                root,
                Path.Combine(root, "updates"),
                _ =>
                {
                    launched = true;
                    return true;
                },
                getCurrentInstalledVersion: () => "1.0.7.0");

            var result = await updater.StartLatestReleaseUpdateAsync();

            Assert.Equal(false, result.RequestApplicationShutdown);
            Assert.Contains("latest version", result.Message);
            Assert.Contains("1.0.7", result.Message);
            Assert.Equal(1, requests.Count);
            Assert.Equal(false, launched);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task AppUpdaterDownloadsAndLaunchesVerifiedMsiAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "svs-updater-msi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var msiBytes = Encoding.UTF8.GetBytes("verified msi bytes");
            var checksumText = $"{GetSha256(msiBytes)} *StreamlinkVlcStudio-Setup.msi{Environment.NewLine}";
            var checksumBytes = Encoding.UTF8.GetBytes(checksumText);
            var requests = new List<Uri>();
            var launched = new List<ProcessStartInfo>();
            using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                requests.Add(request.RequestUri!);
                return request.RequestUri!.AbsoluteUri switch
                {
                    "https://api.github.com/repos/owner/repo/releases/latest" => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            CreateLatestReleaseJson(msiBytes.Length, checksumBytes.Length),
                            Encoding.UTF8,
                            "application/json")
                    },
                    "https://downloads.example/StreamlinkVlcStudio-Setup.msi" => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(msiBytes)
                    },
                    "https://downloads.example/SHA256SUMS.txt" => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(checksumBytes)
                    },
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                };
            }));
            using var updater = new GitHubReleaseAppUpdateService(
                new MemoryLogger(),
                httpClient,
                "owner/repo",
                root,
                Path.Combine(root, "updates"),
                startInfo =>
                {
                    launched.Add(startInfo);
                    return true;
                },
                getCurrentInstalledVersion: () => "1.0.6");

            var result = await updater.StartLatestReleaseUpdateAsync();

            Assert.True(result.RequestApplicationShutdown);
            Assert.Contains("downloaded and verified", result.Message);
            Assert.Equal(3, requests.Count);
            Assert.Equal(1, launched.Count);
            var msiPath = launched[0].FileName;
            Assert.True(msiPath.EndsWith("StreamlinkVlcStudio-Setup.msi", StringComparison.Ordinal));
            Assert.True(File.Exists(msiPath));
            Assert.Equal(msiBytes.Length, new FileInfo(msiPath).Length);
            Assert.Equal(true, launched[0].UseShellExecute);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task AppUpdaterRejectsMsiChecksumMismatchAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "svs-updater-bad-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var msiBytes = Encoding.UTF8.GetBytes("tampered msi bytes");
            var checksumText = $"{new string('0', 64)} *StreamlinkVlcStudio-Setup.msi{Environment.NewLine}";
            var checksumBytes = Encoding.UTF8.GetBytes(checksumText);
            var launched = false;
            using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
                request.RequestUri!.AbsoluteUri switch
                {
                    "https://api.github.com/repos/owner/repo/releases/latest" => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            CreateLatestReleaseJson(msiBytes.Length, checksumBytes.Length),
                            Encoding.UTF8,
                            "application/json")
                    },
                    "https://downloads.example/StreamlinkVlcStudio-Setup.msi" => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(msiBytes)
                    },
                    "https://downloads.example/SHA256SUMS.txt" => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(checksumBytes)
                    },
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                }));
            using var updater = new GitHubReleaseAppUpdateService(
                new MemoryLogger(),
                httpClient,
                "owner/repo",
                root,
                Path.Combine(root, "updates"),
                _ =>
                {
                    launched = true;
                    return true;
                },
                getCurrentInstalledVersion: () => "1.0.6");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => updater.StartLatestReleaseUpdateAsync());

            Assert.Contains("checksum mismatch", exception.Message);
            Assert.Equal(false, launched);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task UpdateCommandReportsStatusAndRequestsShutdownAsync()
    {
        var settings = new AppSettings();
        var logger = new MemoryLogger();
        var updater = new FakeAppUpdateService(new AppUpdateStartResult("Updater launched.", true));
        var shutdownRequests = 0;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            logger,
            action => action(),
            appUpdateService: updater,
            requestShutdown: () => shutdownRequests++);

        try
        {
            await viewModel.UpdateAppCommand.ExecuteAsync();

            Assert.Equal(1, updater.CallCount);
            Assert.Equal("Updater launched.", viewModel.AppUpdateStatus);
            Assert.Equal("Updater launched.", viewModel.StatusMessage);
            Assert.Equal(1, shutdownRequests);
            Assert.True(logger.Entries.Any(entry =>
                entry.Level == AppLogLevel.Info &&
                entry.Source == "Updater" &&
                entry.Message == "Updater launched."));
        }
        finally
        {
            await viewModel.DisposeAsync().AsTask();
        }
    }

    private static async Task UpdateCommandKeepsAppOpenWhenUpdaterReportsLatestAsync()
    {
        var settings = new AppSettings();
        var logger = new MemoryLogger();
        var updater = new FakeAppUpdateService(new AppUpdateStartResult("You're on the latest version (1.0.7).", false));
        var shutdownRequests = 0;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            logger,
            action => action(),
            appUpdateService: updater,
            requestShutdown: () => shutdownRequests++);

        try
        {
            await viewModel.UpdateAppCommand.ExecuteAsync();

            Assert.Equal(1, updater.CallCount);
            Assert.Equal("You're on the latest version (1.0.7).", viewModel.AppUpdateStatus);
            Assert.Equal("You're on the latest version (1.0.7).", viewModel.StatusMessage);
            Assert.Equal(0, shutdownRequests);
            Assert.True(logger.Entries.Any(entry =>
                entry.Level == AppLogLevel.Info &&
                entry.Source == "Updater" &&
                entry.Message == "You're on the latest version (1.0.7)."));
        }
        finally
        {
            await viewModel.DisposeAsync().AsTask();
        }
    }

    private static void AssertArgumentValue(IReadOnlyList<string> arguments, string name, string expectedValue)
    {
        var index = Array.IndexOf(arguments.ToArray(), name);
        Assert.True(index >= 0 && index + 1 < arguments.Count);
        Assert.Equal(expectedValue, arguments[index + 1]);
    }

    private static string CreateLatestReleaseJson(int msiSize, int checksumSize)
    {
        return $$"""
        {
          "tag_name": "release-7-run-8",
          "draft": false,
          "prerelease": false,
          "assets": [
            {
              "name": "StreamlinkVlcStudio-Setup.msi",
              "size": {{msiSize}},
              "browser_download_url": "https://downloads.example/StreamlinkVlcStudio-Setup.msi"
            },
            {
              "name": "SHA256SUMS.txt",
              "size": {{checksumSize}},
              "browser_download_url": "https://downloads.example/SHA256SUMS.txt"
            }
          ]
        }
        """;
    }

    private static string GetSha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
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
        {
            IsBackground = true,
            Name = "HeadlessHotkeyTests"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void InvokeHotkeyRecorderMethod(
        HotkeyRecorderButton recorder,
        string methodName,
        RoutedEventArgs? eventArgs = null)
    {
        var method = typeof(HotkeyRecorderButton).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(recorder, eventArgs is null ? null : [eventArgs]);
    }

    private sealed class HeadlessPresentationSource : PresentationSource
    {
        private Visual rootVisual = null!;

        public override Visual RootVisual
        {
            get => rootVisual;
            set => rootVisual = value;
        }

        public override bool IsDisposed => false;

        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private static Task VlcAudioRequestsPublishAtomicSnapshots()
    {
        var controller = new LibVlcAudioStateController();
        var initial = controller.Snapshot;
        Assert.Equal(80, initial.Volume);
        Assert.Equal(PlaybackAudioState.Audible, initial.AudioState);
        Assert.Equal(0, initial.Version);

        var normalized = controller.Update(-10, (PlaybackAudioState)int.MaxValue);
        Assert.Equal(VolumeLimits.Min, normalized.Volume);
        Assert.Equal(PlaybackAudioState.Audible, normalized.AudioState);
        Assert.Equal(1, normalized.Version);

        var muted = controller.Update(VolumeLimits.Max + 10, PlaybackAudioState.HardMuted);
        Assert.Equal(VolumeLimits.Max, muted.Volume);
        Assert.Equal(PlaybackAudioState.HardMuted, muted.AudioState);
        Assert.Equal(2, muted.Version);
        Assert.Equal(80, initial.Volume);
        Assert.Equal(false, controller.IsCurrent(normalized.Version, normalized.AudioState));
        Assert.Equal(true, controller.IsCurrent(muted.Version, muted.AudioState));

        Parallel.For(0, 128, index =>
            controller.Update(index, index % 2 == 0 ? PlaybackAudioState.Audible : PlaybackAudioState.Muted));
        var concurrent = controller.Snapshot;
        Assert.Equal(130, concurrent.Version);
        Assert.True(concurrent.Volume is >= VolumeLimits.Min and <= VolumeLimits.Max);

        var invalidated = controller.Invalidate();
        Assert.Equal(concurrent.Volume, invalidated.Volume);
        Assert.Equal(concurrent.AudioState, invalidated.AudioState);
        Assert.Equal(131, invalidated.Version);
        return Task.CompletedTask;
    }

    private static async Task NativeOverlayProbeCachingAsync()
    {
        NativeOverlayCapabilityProbe.ClearCache();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        var release = new TaskCompletionSource<ProcessExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var probe = new NativeOverlayCapabilityProbe(
            (_, _, _) => Interlocked.Increment(ref calls) == 1
                ? release.Task
                : Task.FromResult(new ProcessExecutionResult(0, "--font-size", "", false)),
            timeProvider: clock);
        var waiters = Enumerable.Range(0, 16)
            .Select(_ => probe.SupportsFontSizeAsync("single-flight-timeout.exe"))
            .ToArray();
        await TestWait.UntilAsync(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(1));
        release.TrySetResult(new ProcessExecutionResult(0, "", "", TimedOut: true));
        var firstResults = await Task.WhenAll(waiters);
        Assert.True(firstResults.All(result => !result));
        Assert.Equal(false, await probe.SupportsFontSizeAsync("single-flight-timeout.exe"));
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(await probe.SupportsFontSizeAsync("single-flight-timeout.exe"));
        Assert.Equal(2, calls);

        foreach (var transient in new[]
                 {
                     new ProcessExecutionResult(0, "--font-size", "", false, StandardOutputTruncated: true),
                     new ProcessExecutionResult(7, "--font-size", "", false)
                 })
        {
            NativeOverlayCapabilityProbe.ClearCache();
            calls = 0;
            probe = new NativeOverlayCapabilityProbe(
                (_, _, _) => Task.FromResult(
                    Interlocked.Increment(ref calls) == 1
                        ? transient
                        : new ProcessExecutionResult(0, "--font-size", "", false)),
                timeProvider: clock);
            Assert.Equal(false, await probe.SupportsFontSizeAsync($"transient-{transient.ExitCode}-{transient.OutputWasTruncated}.exe"));
            Assert.Equal(false, await probe.SupportsFontSizeAsync($"transient-{transient.ExitCode}-{transient.OutputWasTruncated}.exe"));
            Assert.Equal(1, calls);
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.True(await probe.SupportsFontSizeAsync($"transient-{transient.ExitCode}-{transient.OutputWasTruncated}.exe"));
            Assert.Equal(2, calls);
        }

        NativeOverlayCapabilityProbe.ClearCache();
        calls = 0;
        probe = new NativeOverlayCapabilityProbe(
            (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new ProcessExecutionResult(0, "usage", "", false));
            },
            timeProvider: clock);
        Assert.Equal(false, await probe.SupportsFontSizeAsync("definitive-unsupported.exe"));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(false, await probe.SupportsFontSizeAsync("definitive-unsupported.exe"));
        Assert.Equal(1, calls);

        var executablePath = Path.Combine(Path.GetTempPath(), $"overlay-probe-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(executablePath, [1]);
            NativeOverlayCapabilityProbe.ClearCache();
            calls = 0;
            probe = new NativeOverlayCapabilityProbe(
                (_, _, _) => Task.FromResult(
                    Interlocked.Increment(ref calls) == 1
                        ? new ProcessExecutionResult(0, "usage", "", false)
                        : new ProcessExecutionResult(0, "--font-size", "", false)),
                timeProvider: clock);
            Assert.Equal(false, await probe.SupportsFontSizeAsync(executablePath));
            File.WriteAllBytes(executablePath, [1, 2]);
            File.SetLastWriteTimeUtc(executablePath, DateTime.UtcNow.AddSeconds(2));
            Assert.True(await probe.SupportsFontSizeAsync(executablePath));
            Assert.Equal(2, calls);
        }
        finally
        {
            File.Delete(executablePath);
        }
    }

    private static async Task NativeOverlayResizeRejectsStaleCallbackAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "StreamlinkVlcStudioTests", $"resize-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var statePath = Path.Combine(root, "overlay-position");
        var targetPath = $"{statePath}.size";
        File.WriteAllText(targetPath, "reference 300 200");
        var firstTempWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirst = new ManualResetEventSlim(false);
        var callbacks = 0;
        await using var host = new NativeOverlayReplayEventHost(
            new MemoryLogger(),
            action => action(),
            () => { },
            () => 1080,
            resizeDebounceDelay: TimeSpan.FromMilliseconds(1),
            resizeTempWritten: (_, _) =>
            {
                if (Interlocked.Increment(ref callbacks) == 1)
                {
                    firstTempWritten.TrySetResult();
                    releaseFirst.Wait(TimeSpan.FromSeconds(2));
                }
            });
        try
        {
            host.Start($"resize-old-{Guid.NewGuid():N}", statePath);
            host.ResumeResizePersistence();
            host.QueueResizeFlushForTest(600, 400);
            await firstTempWritten.Task.WaitAsync(TimeSpan.FromSeconds(1));

            host.Start($"resize-new-{Guid.NewGuid():N}", statePath);
            host.ResumeResizePersistence();
            host.QueueResizeFlushForTest(900, 600);
            await TestWait.UntilAsync(
                () => File.Exists(targetPath) &&
                    string.Equals(File.ReadAllText(targetPath), "reference 900 600", StringComparison.Ordinal),
                TimeSpan.FromSeconds(1));

            releaseFirst.Set();
            await TestWait.UntilAsync(
                () => !Directory.EnumerateFiles(root, "*.tmp").Any(),
                TimeSpan.FromSeconds(1));
            Assert.Equal("reference 900 600", File.ReadAllText(targetPath));
        }
        finally
        {
            releaseFirst.Set();
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task NativeOverlayCodecBoundsCompleteMessage()
    {
        var maximumPixels = NativeOverlayProtocolCodec.MaximumFramePayloadBytes / 4;
        Assert.True(NativeOverlayProtocolCodec.TryGetFrameMessageSize(
            1,
            maximumPixels,
            out var payloadBytes,
            out var encodedBytes));
        Assert.Equal(NativeOverlayProtocolCodec.MaximumFramePayloadBytes, payloadBytes);
        Assert.Equal(NativeOverlayProtocolCodec.MaximumEncodedMessageBytes, encodedBytes);
        Assert.Equal(false, NativeOverlayProtocolCodec.TryGetFrameMessageSize(
            1,
            maximumPixels + 1,
            out _,
            out _));
        Assert.Equal(false, NativeOverlayProtocolCodec.TryGetFrameMessageSize(
            int.MaxValue,
            int.MaxValue,
            out _,
            out _));

        var fitted = NativeOverlayProtocolCodec.FitFrameDimensions(int.MaxValue, int.MaxValue);
        Assert.True(fitted.Width > 0 && fitted.Height > 0);
        Assert.True(NativeOverlayProtocolCodec.TryGetFrameMessageSize(
            fitted.Width,
            fitted.Height,
            out _,
            out _));

        var fallback = NativeOverlayChatFrameRenderer.BuildTransparentBlankFrameMessage();
        Assert.Equal(NativeOverlayProtocolCodec.HeaderSize + 4, fallback.Length);
        Assert.True(NativeOverlayProtocolCodec.TryValidateEncodedMessage(fallback, out _));
        Assert.Equal(0, fallback[32]);
        return Task.CompletedTask;
    }

    private static async Task NativeOverlayWriteLoopIsolatesCallbacksAsync()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        await using var gate = new NativeReplayOverlayFrameWriteGate(
            new ThrowingTestLogger(),
            async (_, cancellationToken) =>
            {
                var write = Interlocked.Increment(ref writes);
                if (write == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    secondWritten.TrySetResult();
                }

                return new NativeReplayOverlayFrameWriteResult(true, null);
            },
            () => 1,
            _ => throw new InvalidOperationException("failure callback should be isolated"),
            TimeSpan.Zero,
            _ => throw new InvalidOperationException("success callback should be isolated"));
        gate.QueueWrite("callback-test", [1], 1);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        gate.QueueWrite("callback-test", [2], 1);
        releaseFirst.TrySetResult();
        await secondWritten.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, writes);

        var logger = new MemoryLogger();
        using var validatingGate = new NativeReplayOverlayFrameWriteGate(
            logger,
            (_, _) => Task.FromResult(new NativeReplayOverlayFrameWriteResult(true, null)),
            () => 1,
            _ => { },
            TimeSpan.Zero,
            validateProtocolMessages: true);
        validatingGate.QueueWrite("invalid-frame", new byte[NativeOverlayProtocolCodec.HeaderSize], 1);
        Assert.True(logger.Entries.Any(entry =>
            entry.Message.Contains("Rejected invalid", StringComparison.Ordinal)));
    }

    private static async Task OverlaySchedulerTimeoutDrainsLateDispatcherAsync()
    {
        using var startGate = new ManualResetEventSlim(false);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                NativeReplayOverlayFrameScheduler.CreateAsync(
                    new MemoryLogger(),
                    _ => { },
                    startupTimeout: TimeSpan.FromMilliseconds(25),
                    beforeDispatcherInitialization: startGate.Wait,
                    dispatcherStopped: () => stopped.TrySetResult()));
        }
        finally
        {
            startGate.Set();
        }

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static async Task AsyncCommandReleasesGateOnCallbackFailureAsync()
    {
        var predicateCalls = 0;
        var allowPredicate = false;
        var executions = 0;
        var predicateCommand = new AsyncRelayCommand(
            () =>
            {
                Interlocked.Increment(ref executions);
                return Task.CompletedTask;
            },
            () =>
            {
                if (allowPredicate)
                {
                    return true;
                }

                return Interlocked.Increment(ref predicateCalls) == 1
                    ? true
                    : throw new InvalidOperationException("predicate failed after gate acquisition");
            });
        await Assert.ThrowsAsync<InvalidOperationException>(() => predicateCommand.ExecuteAsync());
        allowPredicate = true;
        await predicateCommand.ExecuteAsync();
        Assert.Equal(1, executions);

        var eventExecutions = 0;
        var eventCommand = new AsyncRelayCommand(() =>
        {
            Interlocked.Increment(ref eventExecutions);
            return Task.CompletedTask;
        });
        EventHandler throwing = (_, _) => throw new InvalidOperationException("notification failed");
        eventCommand.CanExecuteChanged += throwing;
        await Assert.ThrowsAsync<InvalidOperationException>(() => eventCommand.ExecuteAsync());
        eventCommand.CanExecuteChanged -= throwing;
        await eventCommand.ExecuteAsync();
        Assert.Equal(1, eventExecutions);
    }

    private static async Task ClipboardRetriesAreBoundedAsync()
    {
        var attempts = 0;
        var delays = 0;
        var clipboard = new ClipboardService(
            _ =>
            {
                if (Interlocked.Increment(ref attempts) < 3)
                {
                    throw new ExternalException("clipboard busy");
                }
            },
            (_, _) =>
            {
                Interlocked.Increment(ref delays);
                return Task.CompletedTask;
            });
        var result = await clipboard.TrySetTextAsync("redirect URL");
        Assert.True(result.Succeeded);
        Assert.Equal(3, attempts);
        Assert.Equal(2, delays);

        attempts = 0;
        clipboard = new ClipboardService(
            _ =>
            {
                Interlocked.Increment(ref attempts);
                throw new ExternalException("clipboard remains busy");
            },
            (_, _) => Task.CompletedTask);
        result = await clipboard.TrySetTextAsync("redirect URL");
        Assert.Equal(false, result.Succeeded);
        Assert.Equal(3, attempts);
        Assert.NotNull(result.Error);
    }

    private static Task StreamInputTryFromChannel()
    {
        Assert.True(StreamInputParser.TryFromChannel(PlatformKind.Twitch, "valid_name", out var target));
        Assert.Equal("valid_name", target!.Channel);
        Assert.Equal(false, StreamInputParser.TryFromChannel(PlatformKind.Twitch, "x", out target));
        Assert.Equal(null, target);
        Assert.Equal(false, StreamInputParser.TryFromChannel((PlatformKind)999, "valid_name", out target));
        Assert.Equal(null, target);
        return Task.CompletedTask;
    }

    private static async Task LiveChatSupervisorRetriesAndDrainsAsync()
    {
        var delays = new List<TimeSpan>();
        var thirdAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourthAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingDelayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockDelay = false;
        var blockedDelayCanceled = false;
        var attempts = 0;
        var supervisor = new LiveChatConnectionSupervisor(
            new MemoryLogger(),
            "TestChat",
            _ => { },
            async (delay, cancellationToken) =>
            {
                delays.Add(delay);
                if (!blockDelay)
                {
                    return;
                }

                blockingDelayEntered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    blockedDelayCanceled = true;
                    throw;
                }
            },
            () => 0.5d);
        supervisor.Start(_ =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt < 3)
            {
                return Task.FromException(new IOException("transient reconnect failure"));
            }

            if (attempt == 3)
            {
                thirdAttempt.TrySetResult();
            }
            else if (attempt == 4)
            {
                fourthAttempt.TrySetResult();
            }

            return Task.CompletedTask;
        });

        supervisor.NotifyConnectionEnded(TimeSpan.FromSeconds(10));
        await thirdAttempt.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.SequenceEqual(
            new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) },
            delays);

        supervisor.NotifyConnectionEnded(TimeSpan.FromSeconds(60));
        await fourthAttempt.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), delays[3]);

        blockDelay = true;
        supervisor.NotifyConnectionEnded(TimeSpan.FromSeconds(1));
        await blockingDelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await supervisor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(blockedDelayCanceled);
    }

    private static async Task KickIdentitySnapshotsAreAtomicAsync()
    {
        var settings = new ChatSettings();
        var writer = Task.Run(() =>
        {
            for (var index = 0; index < 2_000; index++)
            {
                settings.SetKickChatroomId($"channel-{index % 32}", index.ToString(CultureInfo.InvariantCulture));
                settings.SetKickBroadcasterUserId($"channel-{index % 32}", (index + 1).ToString(CultureInfo.InvariantCulture));
            }
        });
        var readers = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() =>
            {
                for (var index = 0; index < 500; index++)
                {
                    var json = JsonSerializer.Serialize(settings);
                    using var document = JsonDocument.Parse(json);
                    Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("KickChatroomIds").ValueKind);
                    Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("KickBroadcasterUserIds").ValueKind);
                }
            }))
            .ToArray();

        await Task.WhenAll(readers.Append(writer));
        Assert.True(settings.TryGetKickChatroomId("channel-15", out _));
        Assert.True(settings.TryGetKickBroadcasterUserId("channel-15", out _));
    }

    private static async Task KickSubscriptionDisposalDrainsAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new KickEventSubscriptionService(
            new MemoryLogger(),
            appAccessTokenProvider: async (_, _, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "unreachable";
            });
        var operation = service.EnsureChatMessageSentSubscriptionAsync(
            StreamInputParser.FromChannel(PlatformKind.Kick, "streamer"),
            new ChatSettings());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            service.EnsureChatMessageSentSubscriptionAsync(
                StreamInputParser.FromChannel(PlatformKind.Kick, "streamer"),
                new ChatSettings()));
    }

    private static Task KickReplayCacheRetention()
    {
        var root = Path.Combine(Path.GetTempPath(), "StreamlinkVlcStudioTests", $"kick-retention-{Guid.NewGuid():N}");
        var channelRoot = Path.Combine(root, "streamer");
        Directory.CreateDirectory(channelRoot);
        var now = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        var expired = Path.Combine(channelRoot, "20260701.jsonl");
        var priorDay = Path.Combine(channelRoot, "20260815.jsonl");
        var currentDay = Path.Combine(channelRoot, "20260816.jsonl");
        File.WriteAllBytes(expired, new byte[8]);
        File.WriteAllBytes(priorDay, new byte[16]);
        File.WriteAllBytes(currentDay, new byte[32]);
        File.SetLastWriteTimeUtc(expired, now.AddDays(-45).UtcDateTime);
        File.SetLastWriteTimeUtc(priorDay, now.AddDays(-1).UtcDateTime);
        File.SetLastWriteTimeUtc(currentDay, now.UtcDateTime);
        var logger = new MemoryLogger();
        try
        {
            var store = new KickOfficialChatReplayStore(
                root,
                logger,
                new ManualTimeProvider(now),
                TimeSpan.FromDays(30),
                maximumCacheBytes: 20,
                pruneInterval: TimeSpan.FromMinutes(15));

            Assert.Equal(false, File.Exists(expired));
            Assert.Equal(false, File.Exists(priorDay));
            Assert.True(File.Exists(currentDay));
            var result = store.PruneForTest();
            Assert.True(result.Ran);
            Assert.True(result.ProtectedDataExceedsLimit);
            Assert.True(logger.Entries.Any(entry =>
                entry.Message.Contains("prevents", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static async Task TwitchCacheScansAndBackfillsAsync()
    {
        var replayId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var cacheDirectory = ReplayChatProvider.GetDefaultReplayChatCacheDirectory(PlatformKind.Twitch);
        Directory.CreateDirectory(cacheDirectory);
        var cachePath = Path.Combine(cacheDirectory, $"{replayId}_chat.json");
        var comments = Enumerable.Range(0, 5_001)
            .Select(index => new
            {
                _id = $"cached-{index}",
                content_offset_seconds = index,
                commenter = new { display_name = $"Viewer{index}" },
                message = new { body = $"cached message {index}" }
            })
            .ToArray();
        await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(new { comments }));

        var requestCount = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref requestCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    [{"data":{"video":{"comments":{"pageInfo":{"hasNextPage":false},"edges":[
                      {"cursor":"later","node":{"id":"gql-later","contentOffsetSeconds":5600,"createdAt":"2026-08-16T12:00:00Z","commenter":{"displayName":"LaterViewer"},"message":{"body":"later seek chat"}}}
                    ]}}}}]
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        var provider = new ReplayChatProvider(httpClient);
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            $"https://www.twitch.tv/videos/{replayId}",
            replayId,
            new DateTimeOffset(2026, 8, 16, 10, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(2),
            true,
            "");
        try
        {
            var cache = ReplayChatProvider.LoadTwitchChat(replay);
            Assert.True(cache.IsAvailable, cache.UnavailableReason);
            Assert.Equal(5_001, cache.Messages.Count);
            Assert.Equal(TimeSpan.Zero, cache.LoadedFromOffset);
            Assert.Equal(TimeSpan.FromSeconds(5_000), cache.LoadedThroughOffset);

            var result = await provider.LoadTwitchChatAsync(
                replay,
                new AppSettings(),
                TimeSpan.FromSeconds(5_600));
            Assert.True(result.IsAvailable, result.UnavailableReason);
            Assert.Equal(5_002, result.Messages.Count);
            Assert.Equal("later seek chat", result.Messages[^1].Message.Message);
            Assert.Equal(TimeSpan.FromSeconds(5_540), result.LoadedFromOffset);
            Assert.Equal(TimeSpan.FromSeconds(5_840), result.LoadedThroughOffset);
            Assert.Equal(1, requestCount);
        }
        finally
        {
            File.Delete(cachePath);
        }
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "scripts", "package-release.ps1")) &&
                    File.Exists(Path.Combine(directory.FullName, "StreamlinkVlcStudio.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed class FakeAppUpdateService(AppUpdateStartResult result) : IAppUpdateService
    {
        public int CallCount { get; private set; }

        public Task<AppUpdateStartResult> StartLatestReleaseUpdateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingTestLogger : IAppLogger
    {
        public event EventHandler<LogEntry>? EntryWritten
        {
            add { }
            remove { }
        }

        public void Write(AppLogLevel level, string source, string message, Exception? exception = null)
        {
            throw new InvalidOperationException("logger failure");
        }
    }
}

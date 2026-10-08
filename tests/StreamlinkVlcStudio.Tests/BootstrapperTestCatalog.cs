extern alias BootstrapperAssembly;

using BootstrapperAssembly::StreamlinkVlcStudio.Bootstrapper;
using WixToolset.BootstrapperApplicationApi;

internal static class BootstrapperTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Bootstrapper entry point leaves COM apartment selection to WiX", EntryPointLeavesComApartmentSelectionToWix),
        ("Bootstrapper read-only version run bindings are one-way", ReadOnlyVersionRunBindingsAreOneWay),
        ("Bootstrapper defaults uninstall data cleanup on", BundleDefaultsPurgeUserData),
        ("Bootstrapper related removals preserve user data and notification registration", RelatedRemovalPreservesUserDataAsync),
        ("Bootstrapper prevents closing during setup preparation", CannotCloseDuringPreparation),
        ("Bootstrapper downgrade refusal terminates passive setup", PassiveDowngradeTerminates),
        ("Bootstrapper expands executable paths before using them", ExpandsExecutablePaths),
        ("Bootstrapper distinguishes installed, outdated, and missing dependencies", DependencyStatusMatchesDetection),
        ("Bootstrapper cancellation never interrupts rollback", CancellationAllowsRollback),
        ("Bootstrapper retry detects again before offering installation actions", RetryDetectsAgain),
        ("Bootstrapper retry is unavailable for downgrades or cleanup warnings", RetryEligibility),
        ("Bootstrapper reboot-required completion does not offer app launch", RebootRequiredDisablesLaunch),
        ("Bootstrapper dependency verification failure prevents success and launch", DependencyVerificationFailureAsync),
        ("Bootstrapper repairs damaged executables and reports uninstall notification warnings", DamagedApplicationMaintenanceAsync),
        ("Bootstrapper failed independent shutdown prevents every setup action", FailedIndependentShutdownAsync),
        ("Bootstrapper install requests repair registered bundles for current and legacy update helpers", InstalledBundleRequestsRepairAsync),
        ("Bootstrapper runtime repair uses the reviewed commands and retains newer shared versions", RuntimeRepairCommands),
        ("Bootstrapper bundles the offline WebView2 runtime as a shared dependency", WebView2DependencyUsesOfflinePackage),
        ("Bootstrapper dependency component text fits at the minimum window size", DependencyPageLayout),
        ("Bootstrapper cancellation during preparation prevents planning", CancelPreparationAsync),
        ("Bootstrapper cancellation before queued apply prevents elevation", CancelQueuedApplyAsync),
        ("Bootstrapper quiet apply uses a real hidden window handle", HiddenApplyUsesWindowHandleAsync),
        ("Bootstrapper installs VLC from EXE package instead of MSI", VlcDependencyUsesExePackage),
        ("Bootstrapper app icon decodes at Windows Search display sizes", SearchIconDecodes),
        ("Bootstrapper clears only Stream Studio search icons across display scales", SearchIconCacheCleanup),
        ("Bootstrapper search icon cleanup continues past a locked bitmap", LockedSearchIconCacheCleanup),
        ("Scripted installer installs VLC without msiexec", ScriptedInstallerUsesVlcExe)
    ];

    private static Task RuntimeRepairCommands()
    {
        var bundle = System.Xml.Linq.XDocument.Load(Path.Combine(FindRepoRoot(), "scripts", "installer", "StreamlinkVlcStudio.Bundle.wxs"));
        var variables = new Dictionary<string, string>
        {
            ["Streamlink"] = "StreamlinkMachineVersion",
            ["Vlc"] = "VlcInstalledVersion",
            ["WebView2"] = "WebView2InstalledVersion"
        };
        foreach (var package in bundle.Descendants().Where(element => element.Name.LocalName == "ExePackage"))
        {
            var id = (string)package.Attribute("Id")!;
            Assert.Equal((string?)package.Attribute("InstallArguments"), (string?)package.Attribute("RepairArguments"));
            Assert.Equal($"{variables[id]} <= v$(var.{id}InstallerVersion)", (string?)package.Attribute("RepairCondition"));
            // A false InstallCondition suppresses repair even if RepairCondition is true.
            Assert.True(package.Attribute("InstallCondition") is null);
            Assert.Equal("yes", (string?)package.Attribute("Permanent"));
        }
        return Task.CompletedTask;
    }

    private static Task DamagedApplicationMaintenanceAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "StreamStudio-damaged-maintenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var missing in new[] { false, true })
                foreach (var action in new[] { LaunchAction.Install, LaunchAction.Repair, LaunchAction.Uninstall })
                {
                    var executable = Path.Combine(root, missing ? "missing.exe" : "corrupt.exe");
                    if (!missing) File.WriteAllText(executable, "damaged executable");
                    var shutdowns = 0;
                    var (app, model, engine) = CreateApplication(RelationType.None, null, Display.Full, timeout =>
                    {
                        shutdowns++;
                        Assert.Equal(TimeSpan.FromSeconds(30), timeout);
                        return true;
                    });
                    SetField(app, "dispatcher", System.Windows.Threading.Dispatcher.CurrentDispatcher);
                    SetField(app, "isInstalled", true);
                    model.PurgeUserData = false;
                    engine.Variables["InstalledApplicationPath"] = executable;
                    engine.FormattedVariables["[InstalledApplicationPath]"] = executable;
                    if (action == LaunchAction.Uninstall) app.Uninstall();
                    else if (action == LaunchAction.Repair) app.Repair();
                    else app.Install();
                    await TestWait.UntilAsync(() => engine.PlannedAction != LaunchAction.Unknown, TimeSpan.FromSeconds(3));
                    Assert.Equal(1, shutdowns);
                    Assert.Equal(action == LaunchAction.Uninstall ? LaunchAction.Uninstall : LaunchAction.Repair, engine.PlannedAction);
                    if (action != LaunchAction.Uninstall) continue;
                    var finished = WhenResult(model);
                    InvokeHandler(app, "OnApplyComplete", new ApplyCompleteEventArgs(0, ApplyRestart.None,
                        BOOTSTRAPPER_APPLYCOMPLETE_ACTION.None, BOOTSTRAPPER_APPLYCOMPLETE_ACTION.None));
                    await finished.WaitAsync(TimeSpan.FromSeconds(3));
                    Assert.True(model.ResultWarning);
                    Assert.Equal(false, model.ResultSucceeded);
                    Assert.Equal(false, model.CanLaunch);
                    Assert.Equal(false, model.CanRetry);
                    Assert.Contains("notification", model.ResultMessage);
                    Assert.Equal(2, (int)GetField(app, "resultCode")!);
                }
        }
        finally { Directory.Delete(root, recursive: true); }
    });

    private static Task FailedIndependentShutdownAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        foreach (var action in new[] { LaunchAction.Install, LaunchAction.Repair, LaunchAction.Uninstall })
        {
            var (app, model, engine) = CreateApplication(RelationType.None, (_, _, _) =>
                throw new InvalidOperationException("No application executable may be launched after shutdown fails."), Display.Full, _ => false);
            SetField(app, "dispatcher", System.Windows.Threading.Dispatcher.CurrentDispatcher);
            model.PurgeUserData = false;
            var finished = WhenResult(model);
            if (action == LaunchAction.Uninstall) app.Uninstall();
            else if (action == LaunchAction.Repair) app.Repair();
            else app.Install();
            await finished.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(LaunchAction.Unknown, engine.PlannedAction);
            Assert.Equal(false, model.ResultSucceeded);
            Assert.True(model.CanRetry);
            Assert.Contains("did not close", model.ResultMessage);
        }
    });

    private static Task InstalledBundleRequestsRepairAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        foreach (var display in new[] { Display.None, Display.Passive, Display.Full })
            foreach (var installed in new[] { false, true })
            {
                var (app, model, engine) = CreateApplication(RelationType.None, (_, _, _) => 0, display);
                SetField(app, "dispatcher", System.Windows.Threading.Dispatcher.CurrentDispatcher);
                SetField(app, "isInstalled", installed);
                SetField(app, "command", new BootstrapperCommand(LaunchAction.Install, display, "", 0,
                    ResumeType.None, nint.Zero, RelationType.None, false, "", "", ""));
                // Do not touch an application already installed on the test workstation.
                var absent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "app.exe");
                engine.Variables["InstalledApplicationPath"] = absent;
                engine.FormattedVariables["[InstalledApplicationPath]"] = absent;
                if (display == Display.Full) app.Install();
                else InvokeHandler(app, "OnDetectComplete", new DetectCompleteEventArgs(0, false));
                await TestWait.UntilAsync(() => engine.PlannedAction != LaunchAction.Unknown, TimeSpan.FromSeconds(3));
                Assert.Equal(installed ? LaunchAction.Repair : LaunchAction.Install, engine.PlannedAction);
                Assert.Equal(installed ? "Repairing Stream Studio" : "Installing Stream Studio", model.OperationTitle);
            }
    });

    private static Task DependencyPageLayout() => TestSta.RunOffscreenAsync(() =>
    {
        var (application, model, _) = CreateApplication(RelationType.None, (_, _, _) => 0);
        model.Page = BootstrapperPage.Install;
        model.StreamlinkStatus = "Already installed";
        model.VlcStatus = "Will be installed";
        model.WebView2Status = "Will be updated";
        var window = new BootstrapperAssembly::StreamlinkVlcStudio.Bootstrapper.MainWindow(application, model);
        try
        {
            var root = (Grid)window.Content;
            root.Background = window.Background;
            // Reserve normal Windows non-client borders and title-bar height.
            var size = new Size(window.MinWidth - 16, window.MinHeight - 40);
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
            var output = Environment.GetEnvironmentVariable("SVS_INSTALLER_SNAPSHOT_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, "installer-minimum-size.png"));
                encoder.Save(stream);
            }
            var body = root.Children.OfType<Grid>().Single();
            var text = VisibleText(body).ToArray();
            Assert.True(text.Any(item => item.Text.Contains("Microsoft Edge WebView2 Runtime", StringComparison.Ordinal)));
            foreach (var item in text)
            {
                var bounds = item.TransformToAncestor(body).TransformBounds(new Rect(new Size(item.ActualWidth, item.ActualHeight)));
                Assert.True(bounds.Bottom <= body.ActualHeight + 1, $"Setup text exceeds the content area: {item.Text}");
                Assert.True(bounds.Right <= body.ActualWidth + 1, $"Setup text exceeds the content width: {item.Text}");
                Assert.True(item.ActualHeight + 1 >= item.DesiredSize.Height - item.Margin.Top - item.Margin.Bottom,
                    $"Setup text is clipped vertically: {item.Text}");
            }
        }
        finally { window.CloseFromApplication(); }
        return Task.CompletedTask;

        static IEnumerable<TextBlock> VisibleText(DependencyObject element)
        {
            if (element is FrameworkElement { Visibility: not Visibility.Visible }) yield break;
            if (element is TextBlock block) yield return block;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
                foreach (var child in VisibleText(VisualTreeHelper.GetChild(element, index))) yield return child;
        }
    });

    private static Task DependencyVerificationFailureAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "StreamStudio-dependency-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "app.exe");
            File.WriteAllText(executable, "dependency verification is simulated");
            var calls = new List<string>();
            var (app, model, engine) = CreateApplication(RelationType.None, (_, argument, _) =>
            {
                calls.Add(argument);
                return 1;
            }, Display.Full);
            engine.Variables["InstalledApplicationPath"] = executable;
            engine.FormattedVariables["[InstalledApplicationPath]"] = executable;
            SetField(app, "plannedAction", LaunchAction.Install);
            SetField(app, "dispatcher", System.Windows.Threading.Dispatcher.CurrentDispatcher);
            model.Page = BootstrapperPage.Progress;
            await (Task)typeof(StudioBootstrapperApplication).GetMethod("CompleteApplyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(app, [0, ApplyRestart.None])!;
            Assert.Equal(1, calls.Count);
            Assert.Equal("--maintenance-verify-dependencies", calls[0]);
            Assert.Equal(false, model.ResultSucceeded);
            Assert.Equal(false, model.CanLaunch);
            Assert.True(model.CanRetry);
            Assert.Equal(unchecked((int)0x80070643), (int)GetField(app, "resultCode")!);
            Assert.Contains("required runtime", model.ResultMessage);
            Assert.Equal(false, model.CancelCommand.CanExecute(null));
            app.RequestCancel();
            Assert.Equal(false, model.CancelRequested);
        }
        finally { Directory.Delete(root, recursive: true); }
    });

    private static Task WebView2DependencyUsesOfflinePackage()
    {
        var bundle = System.Xml.Linq.XDocument.Load(Path.Combine(FindRepoRoot(), "scripts", "installer", "StreamlinkVlcStudio.Bundle.wxs"));
        var package = bundle.Descendants().Single(element => element.Name.LocalName == "ExePackage" && (string?)element.Attribute("Id") == "WebView2");
        Assert.Equal("yes", (string?)package.Attribute("Permanent"));
        Assert.Equal("yes", (string?)package.Attribute("PerMachine"));
        Assert.Equal("/silent /install", (string?)package.Attribute("InstallArguments"));
        Assert.Equal("$(var.WebView2Installer)", (string?)package.Attribute("SourceFile"));
        Assert.Contains("WebView2InstalledVersion", (string)package.Attribute("DetectCondition")!);
        Assert.Equal(false, package.Descendants().Any(element => element.Name.LocalName == "RemotePayload"));
        return Task.CompletedTask;
    }

    private static Task SearchIconDecodes()
    {
        var iconPath = Path.Combine(FindRepoRoot(), "src", "StreamlinkVlcStudio.App.Wpf", "Assets", "Studio.ico");
        foreach (var size in new[] { 64, 96, 128 })
        {
            using var icon = new System.Drawing.Icon(iconPath, size, size);
            using var bitmap = icon.ToBitmap();
            Assert.Equal(size, bitmap.Width);
            Assert.Equal(size, bitmap.Height);
            // Check the mint Studio bars, rather than accepting a generic or old purple icon.
            var bar = bitmap.GetPixel(size / 4, size / 2);
            Assert.True(bar.G > 200 && bar.G > bar.R + 60 && bar.B > 140);
        }
        return Task.CompletedTask;
    }

    private static Task SearchIconCacheCleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "StreamStudio-search-icons-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = Path.Combine(root, "Packages", "Microsoft.Windows.Search_cw5n1h2txyewy", "LocalState", "AppIconCache");
            foreach (var scale in new[] { "100", "150" })
            {
                var directory = Path.Combine(cache, scale);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "{6D809377-6AF0-444B-8957-A3773F02200E}_Streamlink VLC Studio_StreamlinkVlcStudio_exe"), "old icon");
                File.WriteAllText(Path.Combine(directory, "C__Users_test_AppData_Local_Programs_StreamStudio_StreamStudio_exe"), "old icon");
                File.WriteAllText(Path.Combine(directory, "OtherApp_exe"), "unrelated icon");
                File.WriteAllText(Path.Combine(directory, "StreamStudioTools_exe"), "unrelated icon");
            }

            Assert.Equal(4, ShellIconCache.ClearSearchIcons(root));
            foreach (var scale in new[] { "100", "150" })
            {
                var remaining = Directory.GetFiles(Path.Combine(cache, scale));
                Assert.Equal(2, remaining.Length);
                Assert.True(remaining.All(path => File.ReadAllText(path) == "unrelated icon"));
            }
            Assert.Equal(0, ShellIconCache.ClearSearchIcons(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        return Task.CompletedTask;
    }

    private static Task LockedSearchIconCacheCleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "StreamStudio-search-icons-" + Guid.NewGuid().ToString("N"));
        try
        {
            var directory = Path.Combine(root, "Packages", "Microsoft.Windows.Search_cw5n1h2txyewy", "LocalState", "AppIconCache", "150");
            Directory.CreateDirectory(directory);
            var lockedIcon = Path.Combine(directory, "path_StreamStudio_exe");
            var otherIcon = Path.Combine(directory, "path_StreamlinkVlcStudio_exe");
            File.WriteAllText(lockedIcon, "locked icon");
            File.WriteAllText(otherIcon, "old icon");
            using (var stream = new FileStream(lockedIcon, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Equal(1, ShellIconCache.ClearSearchIcons(root));
                Assert.True(File.Exists(lockedIcon));
                Assert.True(!File.Exists(otherIcon));
            }
            Assert.Equal(1, ShellIconCache.ClearSearchIcons(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        return Task.CompletedTask;
    }

    private static Task CancelPreparationAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "StreamStudio-setup-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var executable = Path.Combine(root, "app.exe");
            File.WriteAllText(executable, "not executable: maintenance is simulated");
            var (application, model, engine) = CreateApplication(RelationType.None, (_, _, _) =>
            {
                started.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test did not release preparation.");
                return 0;
            }, Display.Full);
            SetField(application, "dispatcher", System.Windows.Threading.Dispatcher.CurrentDispatcher);
            engine.Variables["InstalledApplicationPath"] = executable;
            engine.FormattedVariables["[InstalledApplicationPath]"] = executable;
            var finished = WhenResult(model);
            application.Install();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            application.RequestCancel();
            release.Set();
            await finished.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(LaunchAction.Unknown, engine.PlannedAction);
            Assert.Equal(0, engine.Applies);
            Assert.Equal(1602, (int)GetField(application, "resultCode")!);
            Assert.True(model.CanRetry);
        }
        finally
        {
            release.Set();
            Directory.Delete(root, recursive: true);
        }
    });

    private static Task CancelQueuedApplyAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var (application, model, engine) = CreateApplication(RelationType.None, (_, _, _) => 0, Display.Full);
        SetField(application, "dispatcher", System.Windows.Threading.Dispatcher.CurrentDispatcher);
        SetField(application, "plannedAction", LaunchAction.Install);
        model.Page = BootstrapperPage.Progress;
        var finished = WhenResult(model);
        // Hold the dispatcher until the engine thread has queued Apply, then cancel before dispatch.
        Task.Run(() => InvokeHandler(application, "OnPlanComplete", new PlanCompleteEventArgs(0))).GetAwaiter().GetResult();
        application.RequestCancel();
        await finished.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, engine.Applies);
        Assert.Equal(1602, (int)GetField(application, "resultCode")!);
    });

    private static Task HiddenApplyUsesWindowHandleAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var (application, _, engine) = CreateApplication(RelationType.None, (_, _, _) => 0, Display.None);
        var window = new BootstrapperAssembly::StreamlinkVlcStudio.Bootstrapper.MainWindow(
            application, new BootstrapperViewModel(application));
        SetField(application, "window", window);
        SetField(application, "dispatcher", System.Windows.Threading.Dispatcher.CurrentDispatcher);
        try
        {
            Assert.Equal(false, window.IsVisible);
            InvokeHandler(application, "OnPlanComplete", new PlanCompleteEventArgs(0));
            await TestWait.UntilAsync(() => engine.Applies == 1, TimeSpan.FromSeconds(3));
            Assert.True(engine.ApplyParent != nint.Zero);
            Assert.Equal(window.WindowHandle, engine.ApplyParent);
            Assert.Equal(false, window.IsVisible);
        }
        finally { window.CloseFromApplication(); }
    });

    private static Task WhenResult(BootstrapperViewModel model)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += (_, _) => { if (model.Page == BootstrapperPage.Result) finished.TrySetResult(); };
        return finished.Task;
    }

    private static void InvokeHandler(StudioBootstrapperApplication app, string name, EventArgs args) =>
        typeof(StudioBootstrapperApplication).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!
            .Invoke(app, [null, args]);

    private static Task CancellationAllowsRollback()
    {
        foreach (var canceled in new[] { false, true })
        {
            var (application, model, _) = CreateApplication(RelationType.None, (_, _, _) => 0);
            SetField(application, "dispatcher", System.Windows.Threading.Dispatcher.CurrentDispatcher);
            model.Page = BootstrapperPage.Progress;
            if (canceled) application.RequestCancel();
            var execute = new ExecutePackageBeginEventArgs("AppMsi", true, ActionState.Install, INSTALLUILEVEL.None, false, false);
            InvokeHandler(application, "OnExecutePackageBegin", execute);
            Assert.Equal(canceled, execute.Cancel);

            var rollback = new ExecutePackageBeginEventArgs("AppMsi", false, ActionState.Uninstall, INSTALLUILEVEL.None, false, false);
            InvokeHandler(application, "OnExecutePackageBegin", rollback);
            Assert.Equal(false, rollback.Cancel);
            Assert.True(model.IsRollingBack);
            Assert.Equal(false, model.CancelCommand.CanExecute(null));
            Assert.Contains("Rolling back", model.StatusText);
            application.RequestCancel();
            Assert.Equal(canceled, model.CancelRequested);

            var progress = new ExecuteProgressEventArgs("AppMsi", 10, 50, false);
            InvokeHandler(application, "OnExecuteProgress", progress);
            Assert.Equal(false, progress.Cancel);
            var overall = new ProgressEventArgs(10, 50, false);
            InvokeHandler(application, "OnProgress", overall);
            Assert.Equal(false, overall.Cancel);
        }
        return Task.CompletedTask;
    }

    private static void ShowFailure(StudioBootstrapperApplication app, bool warning = false) =>
        typeof(StudioBootstrapperApplication).GetMethod("ShowResult", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(app, [false, warning, "Setup did not complete", "Test failure"]);

    private static Task RetryDetectsAgain()
    {
        var (application, model, engine) = CreateApplication(RelationType.None, (_, _, _) => 0, Display.Full);
        SetField(application, "dispatcher", System.Windows.Threading.Dispatcher.CurrentDispatcher);
        SetField(application, "lastError", "old failure");
        model.CancelRequested = true;
        ShowFailure(application);
        Assert.True(model.RetryCommand.CanExecute(null));
        model.RetryCommand.Execute(null);
        Assert.Equal(1, engine.Detections);
        Assert.Equal(false, model.CancelRequested);
        Assert.Equal(BootstrapperPage.Loading, model.Page);
        Assert.Equal(false, model.RetryCommand.CanExecute(null));
        Assert.Equal(LaunchAction.Unknown, engine.PlannedAction);
        InvokeHandler(application, "OnDetectComplete", new DetectCompleteEventArgs(0, false));
        Assert.Equal(BootstrapperPage.Install, model.Page);
        Assert.Equal(LaunchAction.Unknown, engine.PlannedAction);
        Assert.True(GetField(application, "lastError") is null);
        return Task.CompletedTask;
    }

    private static Task RetryEligibility()
    {
        foreach (var scenario in new[] { "passive", "downgrade", "cleanup" })
        {
            var (application, model, _) = CreateApplication(RelationType.None, (_, _, _) => 0,
                scenario == "passive" ? Display.Passive : Display.Full);
            if (scenario == "downgrade") SetField(application, "newerRelatedBundle", true);
            ShowFailure(application, scenario == "cleanup");
            Assert.Equal(false, model.CanRetry);
        }
        return Task.CompletedTask;
    }

    private static Task RebootRequiredDisablesLaunch()
    {
        var (application, model, engine) = CreateApplication(RelationType.None, (_, _, _) => 0, Display.Full);
        SetField(application, "plannedAction", LaunchAction.Install);
        engine.Variables["InstalledApplicationPath"] = Environment.ProcessPath!;
        engine.FormattedVariables["[InstalledApplicationPath]"] = Environment.ProcessPath!;
        var finalize = typeof(StudioBootstrapperApplication).GetMethod("FinalizeApply", BindingFlags.Instance | BindingFlags.NonPublic)!;
        finalize.Invoke(application, [0, 0, ApplyRestart.None, ""]);
        Assert.True(model.CanLaunch);
        finalize.Invoke(application, [0, 0, ApplyRestart.RestartRequired, ""]);
        Assert.Equal(3010, (int)GetField(application, "resultCode")!);
        Assert.True(model.ResultSucceeded);
        Assert.Equal(false, model.CanLaunch);
        return Task.CompletedTask;
    }

    private static async Task RelatedRemovalPreservesUserDataAsync()
    {
        foreach (var relation in new[] { RelationType.Upgrade, RelationType.Update, RelationType.ChainPackage })
        {
            var maintenanceCalls = new List<string>();
            var (application, model, engine) = CreateApplication(relation, (_, argument, _) =>
            {
                maintenanceCalls.Add(argument);
                return 0;
            });
            model.PurgeUserData = true;
            application.Uninstall();
            Assert.Equal(false, model.PurgeUserData);
            Assert.Equal(0L, engine.PurgeUserData);
            Assert.Equal(LaunchAction.Uninstall, engine.PlannedAction);
            Assert.Equal(0, maintenanceCalls.Count);

            var completion = (Task)typeof(StudioBootstrapperApplication)
                .GetMethod("CompleteApplyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(application, [0, ApplyRestart.None])!;
            await completion;
            Assert.Equal(0, maintenanceCalls.Count);
            Assert.Equal(false, engine.Messages.Any(message => message.Contains("Post-uninstall", StringComparison.Ordinal)));
        }
    }

    private static Task CannotCloseDuringPreparation()
    {
        var (application, model, _) = CreateApplication(RelationType.None, (_, _, _) => 0);
        model.Page = BootstrapperPage.Progress;
        Assert.True(application.IsApplying);
        application.Close();
        Assert.Equal(false, (bool)GetField(application, "shutdownRequested")!);
        application.RequestCancel();
        Assert.True(model.CancelRequested);
        return Task.CompletedTask;
    }

    private static Task PassiveDowngradeTerminates()
    {
        var (application, model, engine) = CreateApplication(RelationType.None, (_, _, _) => 0);
        SetField(application, "newerRelatedBundle", true);
        model.Page = BootstrapperPage.Install;
        application.Install();
        Assert.Equal(BootstrapperPage.Result, model.Page);
        Assert.Equal(1638, (int)GetField(application, "resultCode")!);
        Assert.True((bool)GetField(application, "shutdownRequested")!);
        Assert.Equal(LaunchAction.Unknown, engine.PlannedAction);
        return Task.CompletedTask;
    }

    private static Task ExpandsExecutablePaths()
    {
        var (application, _, engine) = CreateApplication(RelationType.None, (_, _, _) => 0);
        engine.Variables["StreamlinkMachineExecutable"] = @"[ProgramFiles64Folder]Streamlink\bin\streamlink.exe";
        engine.Variables["InstalledApplicationPath"] = @"[ProgramFiles64Folder]Stream Studio\StreamStudio.exe";
        engine.FormattedVariables["[StreamlinkMachineExecutable]"] = @"C:\Program Files\Streamlink\bin\streamlink.exe";
        engine.FormattedVariables["[InstalledApplicationPath]"] = @"C:\Program Files\Stream Studio\StreamStudio.exe";

        var readPath = typeof(StudioBootstrapperApplication)
            .GetMethod("SafeFormattedVariable", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(@"C:\Program Files\Streamlink\bin\streamlink.exe",
            (string)readPath.Invoke(application, ["StreamlinkMachineExecutable", ""])!);
        Assert.Equal("fallback", (string)readPath.Invoke(application, ["MissingPath", "fallback"])!);
        Assert.Equal(@"C:\Program Files\Stream Studio\StreamStudio.exe",
            (string)typeof(StudioBootstrapperApplication)
                .GetMethod("GetInstalledApplicationPath", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(application, null)!);
        return Task.CompletedTask;
    }

    private static Task DependencyStatusMatchesDetection()
    {
        var (application, model, engine) = CreateApplication(RelationType.None, (_, _, _) => 0);
        SetField(application, "dispatcher", System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var detected = typeof(StudioBootstrapperApplication)
            .GetMethod("OnDetectPackageComplete", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;

        foreach (var (package, variable, oldVersion, currentVersion) in new[]
                 {
                     ("Streamlink", "StreamlinkMachineVersion", "7.6.0", "8.5.0"),
                     ("Vlc", "VlcInstalledVersion", "3.0.18", "3.0.23.0"),
                     ("WebView2", "WebView2InstalledVersion", "120.0.0.0", "152.0.4191.53")
                 })
        {
            foreach (var (version, state, expected) in new[]
                     {
                         ("0.0.0.0", PackageState.Absent, "Will be installed"),
                         (oldVersion, PackageState.Absent, "Will be updated"),
                         (currentVersion, PackageState.Present, "Already installed"),
                         ("99.0.0", PackageState.Present, "Already installed")
                     })
            {
                engine.Variables[variable] = version;
                detected.Invoke(application, [null, new DetectPackageCompleteEventArgs(package, 0, state, false)]);
                Assert.Equal(expected, package switch { "Streamlink" => model.StreamlinkStatus, "Vlc" => model.VlcStatus, _ => model.WebView2Status });
            }
        }
        return Task.CompletedTask;
    }

    private static (StudioBootstrapperApplication App, BootstrapperViewModel Model, BootstrapperEngineProbe Engine)
        CreateApplication(RelationType relation, Func<string, string, TimeSpan, int>? maintenance, Display display = Display.None,
            Func<TimeSpan, bool>? requestShutdown = null)
    {
        var engine = DispatchProxy.Create<IEngine, BootstrapperEngineProbe>();
        var app = new StudioBootstrapperApplication(maintenance, requestShutdown);
        typeof(BootstrapperApplication).GetField("engine", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, engine);
        var command = new BootstrapperCommand(LaunchAction.Uninstall, display, "", 0,
            ResumeType.None, nint.Zero, relation, false, "", "", "");
        SetField(app, "command", command);
        var model = new BootstrapperViewModel(app) { Page = BootstrapperPage.Maintenance };
        SetField(app, "viewModel", model);
        return (app, model, (BootstrapperEngineProbe)(object)engine);
    }

    private static object? GetField(StudioBootstrapperApplication app, string name) =>
        typeof(StudioBootstrapperApplication).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app);

    private static void SetField(StudioBootstrapperApplication app, string name, object value) =>
        typeof(StudioBootstrapperApplication).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, value);

    private static Task EntryPointLeavesComApartmentSelectionToWix()
    {
        var programPath = Path.Combine(
            FindRepoRoot(),
            "src",
            "StreamlinkVlcStudio.Bootstrapper",
            "Program.cs");
        var lines = File.ReadLines(programPath);

        Assert.Equal(
            false,
            lines.Any(line => string.Equals(line.Trim(), "[STAThread]", StringComparison.Ordinal)));
        return Task.CompletedTask;
    }

    private static Task ReadOnlyVersionRunBindingsAreOneWay()
    {
        var xamlPath = Path.Combine(
            FindRepoRoot(),
            "src",
            "StreamlinkVlcStudio.Bootstrapper",
            "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains("{Binding Version, Mode=OneWay}", xaml);
        Assert.Contains("{Binding StreamlinkVersion, Mode=OneWay}", xaml);
        Assert.Contains("{Binding VlcVersion, Mode=OneWay}", xaml);
        Assert.Contains("{Binding WebView2Version, Mode=OneWay}", xaml);
        return Task.CompletedTask;
    }

    private static Task BundleDefaultsPurgeUserData()
    {
        var root = FindRepoRoot();
        var bundlePath = Path.Combine(root, "scripts", "installer", "StreamlinkVlcStudio.Bundle.wxs");
        var bundle = File.ReadAllText(bundlePath);
        var xaml = File.ReadAllText(Path.Combine(root, "src", "StreamlinkVlcStudio.Bootstrapper", "MainWindow.xaml"));

        Assert.Contains("Name=\"PurgeUserData\"", bundle);
        Assert.Contains("Value=\"1\"", bundle);
        Assert.Contains("Remove my Stream Studio settings, cache, and temporary data", xaml);
        Assert.DoesNotContain("Also remove my Stream Studio settings", xaml);
        return Task.CompletedTask;
    }

    private static Task VlcDependencyUsesExePackage()
    {
        var root = FindRepoRoot();
        var bundlePath = Path.Combine(root, "scripts", "installer", "StreamlinkVlcStudio.Bundle.wxs");
        var bundle = File.ReadAllText(bundlePath);

        Assert.Contains("Id=\"Vlc\"", bundle);
        Assert.Contains("<ExePackage", bundle);
        Assert.Contains("SourceFile=\"$(var.VlcInstaller)\"", bundle);
        Assert.Contains("InstallArguments=\"/L=1033 /S\"", bundle);
        Assert.Contains("DetectCondition=\"VlcInstalledVersion &gt;= v$(var.VlcVersion) AND VlcRuntimeUsable\"", bundle);
        Assert.Contains("Key=\"SOFTWARE\\VideoLAN\\VLC\"", bundle);
        Assert.Contains("Value=\"InstallDir\"", bundle);
        Assert.Contains("Path=\"[VlcInstallDirectory]\\libvlc.dll\"", bundle);
        Assert.Contains("Result=\"version\"", bundle);
        Assert.Contains("After=\"VlcInstallDirectorySearch\"", bundle);
        Assert.DoesNotContain("Value=\"Version\"", bundle);
        Assert.DoesNotContain("SourceFile=\"$(var.VlcMsi)\"", bundle);
        Assert.DoesNotContain("VlcMsi", bundle);

        var manifestPath = Path.Combine(root, "dependencies", "windows-installers.json");
        var manifest = File.ReadAllText(manifestPath);
        Assert.Contains("\"fileName\": \"vlc-3.0.23-win64.exe\"", manifest);
        Assert.DoesNotContain("\"fileName\": \"vlc-3.0.23-win64.msi\"", manifest);

        return Task.CompletedTask;
    }

    private static Task ScriptedInstallerUsesVlcExe()
    {
        var root = FindRepoRoot();
        var installScript = File.ReadAllText(Path.Combine(root, "scripts", "install.ps1"));
        var buildScript = File.ReadAllText(Path.Combine(root, "scripts", "build-installer.ps1"));

        Assert.Contains("Start-Installer $downloadPath @(\"/L=1033\", \"/S\") \"VLC\"", installScript);
        Assert.DoesNotContain("$msiArguments", installScript);
        Assert.DoesNotContain("msiexec.exe", installScript);
        Assert.Contains("\"-d\", (\"VlcInstaller=\" + $vlcInstallerPath)", buildScript);
        Assert.DoesNotContain("VlcMsi=", buildScript);

        return Task.CompletedTask;
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "StreamlinkVlcStudio.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}

public class BootstrapperEngineProbe : DispatchProxy
{
    public long PurgeUserData { get; private set; } = 1;
    public LaunchAction PlannedAction { get; private set; } = LaunchAction.Unknown;
    public int Detections { get; private set; }
    public int Applies { get; private set; }
    public nint ApplyParent { get; private set; }
    public List<string> Messages { get; } = [];
    public Dictionary<string, string> Variables { get; } = [];
    public Dictionary<string, string> FormattedVariables { get; } = [];

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod!.Name)
        {
            case "ContainsVariable": return Variables.ContainsKey((string)args![0]!);
            case "GetVariableString": return Variables[(string)args![0]!];
            case "FormatString": return FormattedVariables[(string)args![0]!];
            case "Log": Messages.Add((string)args![1]!); return null;
            case "Plan": PlannedAction = (LaunchAction)args![0]!; return null;
            case "Detect": Detections++; return null;
            case "Apply": ApplyParent = (nint)args![0]!; Applies++; return null;
            case "SetVariableVersion": Variables[(string)args![0]!] = (string)args[1]!; return null;
            case "SetVariableNumeric":
                if ((string)args![0]! == "PurgeUserData") PurgeUserData = (long)args[1]!;
                return null;
            default: throw new NotSupportedException("Unexpected installer engine operation: " + targetMethod.Name);
        }
    }
}

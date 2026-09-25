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
        ("Bootstrapper cancellation during preparation prevents planning", CancelPreparationAsync),
        ("Bootstrapper cancellation before queued apply prevents elevation", CancelQueuedApplyAsync),
        ("Bootstrapper installs VLC from EXE package instead of MSI", VlcDependencyUsesExePackage),
        ("Scripted installer installs VLC without msiexec", ScriptedInstallerUsesVlcExe)
    ];

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
                     ("Vlc", "VlcInstalledVersion", "3.0.18", "3.0.23.0")
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
                Assert.Equal(expected, package == "Streamlink" ? model.StreamlinkStatus : model.VlcStatus);
            }
        }
        return Task.CompletedTask;
    }

    private static (StudioBootstrapperApplication App, BootstrapperViewModel Model, BootstrapperEngineProbe Engine)
        CreateApplication(RelationType relation, Func<string, string, TimeSpan, int> maintenance, Display display = Display.None)
    {
        var engine = DispatchProxy.Create<IEngine, BootstrapperEngineProbe>();
        var app = new StudioBootstrapperApplication(maintenance);
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
        Assert.Contains("DetectCondition=\"VlcInstalledVersion &gt;= v$(var.VlcVersion)\"", bundle);
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
            case "Apply": Applies++; return null;
            case "SetVariableVersion": Variables[(string)args![0]!] = (string)args[1]!; return null;
            case "SetVariableNumeric":
                if ((string)args![0]! == "PurgeUserData") PurgeUserData = (long)args[1]!;
                return null;
            default: throw new NotSupportedException("Unexpected installer engine operation: " + targetMethod.Name);
        }
    }
}

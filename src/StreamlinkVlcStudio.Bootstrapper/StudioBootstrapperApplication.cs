using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Infrastructure.Processes;
using System.Windows;
using System.Windows.Threading;
using WixToolset.BootstrapperApplicationApi;

namespace StreamlinkVlcStudio.Bootstrapper;

internal sealed class StudioBootstrapperApplication : BootstrapperApplication
{
    private const string ApplicationExecutableName = "StreamlinkVlcStudio.exe";
    private const string MaintenanceExecutableName = "StreamStudio.Maintenance.exe";
    private const int ErrorInstallUserExit = 1602;
    private const int ErrorSuccessRebootRequired = 3010;
    private const int ErrorCancelledHResult = unchecked((int)0x800704C7);
    private const int ErrorInstallUserExitHResult = unchecked((int)0x80070642);
    private IBootstrapperCommand? command;
    private Dispatcher? dispatcher;
    private MainWindow? window;
    private BootstrapperViewModel? viewModel;
    private LaunchAction plannedAction = LaunchAction.Unknown;
    private int resultCode;
    private bool isInstalled;
    private bool newerRelatedBundle;
    private bool notificationsUnregistered;
    private bool isApplying;
    private volatile bool isRollingBack;
    private bool retryingDetection;
    private bool shutdownRequested;
    private string? lastError;
    private string? logPath;
    private readonly Func<string, string, TimeSpan, int>? runMaintenance;

    public StudioBootstrapperApplication() : this(null)
    {
    }

    internal StudioBootstrapperApplication(Func<string, string, TimeSpan, int>? runMaintenance)
    {
        this.runMaintenance = runMaintenance;
        DetectBegin += OnDetectBegin;
        DetectRelatedBundle += OnDetectRelatedBundle;
        DetectPackageComplete += OnDetectPackageComplete;
        DetectComplete += OnDetectComplete;
        PlanComplete += OnPlanComplete;
        ApplyBegin += OnApplyBegin;
        CacheAcquireBegin += OnCacheAcquireBegin;
        CacheAcquireProgress += OnCacheAcquireProgress;
        ExecutePackageBegin += OnExecutePackageBegin;
        ExecuteProgress += OnExecuteProgress;
        Progress += OnProgress;
        Error += OnError;
        ApplyComplete += OnApplyComplete;
    }

    internal bool IsApplying => isApplying || viewModel?.Page == BootstrapperPage.Progress;

    // Burn removes the previous bundle at the end of an upgrade. That invocation
    // must never unregister the new app's notifications or delete the user's data.
    private bool IsRelatedBundleRemoval => plannedAction == LaunchAction.Uninstall &&
        command?.Relation is { } relation && relation != RelationType.None;

    internal string BundleVersion => TrimDisplayVersion(SafeVariable("WixBundleVersion", "1.7.0"));

    internal string StreamlinkVersion => SafeVariable("StreamlinkDependencyVersion", "8.5.0-1");

    internal string VlcVersion => SafeVariable("VlcDependencyVersion", "3.0.23");

    protected override void OnCreate(CreateEventArgs args)
    {
        base.OnCreate(args);
        command = args.Command;
    }

    protected override void Run()
    {
        try
        {
            RunUserInterfaceThread();
        }
        catch (Exception ex)
        {
            resultCode = ex.HResult < 0 ? ex.HResult : 1;
            TryLog(LogLevel.Error, $"Bootstrapper application failed: {ex}");
        }
        finally
        {
            engine.Quit(NormalizeExitCode(resultCode));
        }
    }

    private void RunUserInterfaceThread()
    {
        Exception? uiThreadError = null;
        var uiThread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var bundleData = new BootstrapperApplicationData(new FileInfo(command!.BootstrapperApplicationDataPath));
                command.ParseCommandLine().SetOverridableVariables(bundleData.Bundle.OverridableVariables, engine);
                viewModel = new BootstrapperViewModel(this)
                {
                    PurgeUserData = SafeNumericVariableDefaultTrue("PurgeUserData")
                };
                window = new MainWindow(this, viewModel);

                if (command?.Display is Display.Full or Display.Passive)
                {
                    window.Show();
                }

                engine.Log(LogLevel.Standard, "Managed bootstrapper application initialized.");
                ProbeStreamlinkDependency();
                engine.Detect(window.IsVisible ? window.WindowHandle : nint.Zero);
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                uiThreadError = ex;
                resultCode = ex.HResult < 0 ? ex.HResult : 1;
                TryLog(LogLevel.Error, $"Bootstrapper UI thread failed: {ex}");
            }
        })
        {
            IsBackground = false,
            Name = "Stream Studio Setup UI"
        };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();

        uiThread.Join();

        if (uiThreadError is not null)
        {
            throw uiThreadError;
        }
    }

    internal void Install() => BeginAction(LaunchAction.Install);

    internal void Repair() => BeginAction(LaunchAction.Repair);

    internal void Uninstall()
    {
        if (command?.Display == Display.Full && window is not null)
        {
            var message = viewModel?.PurgeUserData == true
                ? "Uninstall Stream Studio and permanently remove your settings, cache, and temporary data? VLC and Streamlink will be retained."
                : "Uninstall Stream Studio? Your settings, cache, VLC, and Streamlink will be retained.";
            if (MessageBox.Show(
                    window,
                    message,
                    "Confirm uninstall",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }
        }

        BeginAction(LaunchAction.Uninstall);
    }

    internal void RequestCancel()
    {
        if (!IsApplying || isRollingBack || viewModel is null)
        {
            return;
        }

        viewModel.CancelRequested = true;
        viewModel.StatusText = "Canceling and rolling back…";
        TryLog(LogLevel.Standard, "The user requested cancellation.");
    }

    internal void Retry()
    {
        if (IsApplying || viewModel?.CanRetry != true) return;
        retryingDetection = true;
        newerRelatedBundle = false;
        lastError = null;
        resultCode = 0;
        viewModel.CanRetry = false;
        viewModel.CanLaunch = false;
        viewModel.CancelRequested = false;
        viewModel.IsRollingBack = false;
        viewModel.Progress = 0;
        viewModel.StatusText = "Checking installed components again...";
        viewModel.Page = BootstrapperPage.Loading;
        ProbeStreamlinkDependency();
        engine.Detect(window?.IsVisible == true ? window.WindowHandle : nint.Zero);
    }

    internal void Close()
    {
        if (IsApplying)
        {
            return;
        }

        shutdownRequested = true;
        window?.CloseFromApplication();
        dispatcher?.BeginInvokeShutdown(DispatcherPriority.Background);
    }

    internal void NotifyWindowClosing()
    {
        if (!IsApplying)
        {
            shutdownRequested = true;
            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Background);
        }
    }

    internal void OpenLog()
    {
        logPath ??= SafeVariable("WixBundleLog", string.Empty);
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        OpenWithShell(File.Exists(logPath) ? logPath : Path.GetDirectoryName(logPath));
    }

    internal void LaunchApplication()
    {
        var executable = GetInstalledApplicationPath();
        if (!File.Exists(executable))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--setup",
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = true
            });
            Close();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            TryLog(LogLevel.Error, $"Could not launch the installed application: {ex.Message}");
        }
    }

    private void BeginAction(LaunchAction action)
    {
        if (viewModel is null || viewModel.Page == BootstrapperPage.Progress)
        {
            return;
        }

        if (newerRelatedBundle && action is LaunchAction.Install or LaunchAction.Repair)
        {
            ShowResult(
                success: false,
                warning: false,
                "A newer version is already installed",
                "Setup will not replace a newer Stream Studio installation with this version.");
            resultCode = 1638;
            CompleteHeadlessIfNeeded();
            return;
        }

        plannedAction = action;
        lastError = null;
        notificationsUnregistered = false;
        isRollingBack = false;
        viewModel.IsRollingBack = false;
        viewModel.CancelRequested = false;
        viewModel.Progress = 0;
        viewModel.Page = BootstrapperPage.Progress;
        viewModel.OperationTitle = action switch
        {
            LaunchAction.Uninstall => "Uninstalling Stream Studio",
            LaunchAction.Repair => "Repairing Stream Studio",
            _ => isInstalled ? "Updating Stream Studio" : "Installing Stream Studio"
        };
        viewModel.StatusText = action == LaunchAction.Uninstall
            ? "Requesting a graceful application shutdown…"
            : "Preparing the installation plan…";

        if (action == LaunchAction.Uninstall)
        {
            if (IsRelatedBundleRemoval)
            {
                viewModel.PurgeUserData = false;
                engine.SetVariableNumeric("PurgeUserData", 0);
                engine.Plan(action);
                return;
            }
            engine.SetVariableNumeric("PurgeUserData", viewModel.PurgeUserData ? 1 : 0);
        }
        RunDetached(PrepareApplicationAndPlanAsync(action), "Application preparation failed", "Setup could not start");
    }

    private async Task PrepareApplicationAndPlanAsync(LaunchAction action)
    {
        var preparation = await Task.Run(() => PrepareApplication(action)).ConfigureAwait(false);
        await InvokeUiAsync(() =>
        {
            if (viewModel?.CancelRequested == true)
            {
                RunDetached(CompleteApplyAsync(ErrorInstallUserExitHResult, ApplyRestart.None),
                    "Canceled setup preparation could not be completed", "Setup could not stop");
                return;
            }
            if (!preparation.Success)
            {
                resultCode = preparation.ExitCode;
                ShowResult(false, false, "Setup could not start", preparation.Message);
                CompleteHeadlessIfNeeded();
                return;
            }

            if (viewModel is not null)
            {
                viewModel.StatusText = "Preparing the setup plan…";
            }

            engine.Plan(action);
        }).ConfigureAwait(false);
    }

    private MaintenanceResult PrepareApplication(LaunchAction action)
    {
        var executable = GetInstalledApplicationPath();
        if (!File.Exists(executable))
        {
            TryLog(LogLevel.Standard, $"Installed application maintenance executable was not found: {executable}");
            return MaintenanceResult.Ok;
        }

        var shutdown = RunMaintenance(executable, "--maintenance-request-shutdown", TimeSpan.FromSeconds(30));
        if (!shutdown.Success)
        {
            return new MaintenanceResult(
                false,
                shutdown.ExitCode,
                "Stream Studio did not close cleanly. Close the application and try setup again.");
        }

        if (action != LaunchAction.Uninstall) return MaintenanceResult.Ok;

        var unregister = RunMaintenance(executable, "--maintenance-unregister-notifications", TimeSpan.FromSeconds(15));
        if (!unregister.Success)
        {
            return new MaintenanceResult(
                false,
                unregister.ExitCode,
                "Windows notification registration could not be removed. No application files were changed.");
        }

        notificationsUnregistered = true;
        return MaintenanceResult.Ok;
    }

    private void ProbeStreamlinkDependency()
    {
        const string versionVariable = "StreamlinkMachineVersion";
        var executable = SafeFormattedVariable("StreamlinkMachineExecutable", string.Empty);
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            TryLog(LogLevel.Standard, "A machine-wide Streamlink executable was not found under Program Files.");
            engine.SetVariableVersion(versionVariable, "0.0.0.0");
            return;
        }

        try
        {
            var startInfo = BoundedProcessRunner.CreateRedirectedStartInfo(executable, ["--version"]);
            startInfo.WorkingDirectory = Path.GetDirectoryName(executable)!;
            var result = new BoundedProcessRunner().RunAsync(startInfo, TimeSpan.FromSeconds(10))
                .GetAwaiter().GetResult();
            if (result.TimedOut)
            {
                engine.SetVariableVersion(versionVariable, "0.0.0.0");
                TryLog(LogLevel.Standard, "The machine-wide Streamlink version probe timed out.");
                return;
            }

            var output = string.Concat(result.StandardOutput, " ", result.StandardError);
            var match = Regex.Match(
                output,
                @"(?<!\d)(?<version>\d+(?:\.\d+){1,3}(?:-[0-9A-Za-z.-]+)?)(?!\d)",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
            if (result.ExitCode != 0 || result.OutputWasTruncated || !match.Success)
            {
                engine.SetVariableVersion(versionVariable, "0.0.0.0");
                TryLog(LogLevel.Standard, "The machine-wide Streamlink executable did not report a usable version.");
                return;
            }

            var version = match.Groups["version"].Value;
            engine.SetVariableVersion(versionVariable, version);
            TryLog(LogLevel.Standard, $"Detected machine-wide Streamlink {version} at {executable}.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            engine.SetVariableVersion(versionVariable, "0.0.0.0");
            TryLog(LogLevel.Standard, $"The machine-wide Streamlink version probe failed: {ex.Message}");
        }
    }

    private MaintenanceResult RunMaintenance(string executable, string argument, TimeSpan timeout)
    {
        TryLog(LogLevel.Standard, $"Running application maintenance mode: {argument}");
        if (runMaintenance is not null)
        {
            var code = runMaintenance(executable, argument, timeout);
            return new MaintenanceResult(code == 0, code, "Maintenance test result.");
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = argument,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return new MaintenanceResult(false, 1, "The maintenance process could not be started.");
            }

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return new MaintenanceResult(false, ErrorInstallUserExit, "The maintenance process timed out.");
            }

            return process.ExitCode == 0
                ? MaintenanceResult.Ok
                : new MaintenanceResult(false, process.ExitCode, $"The maintenance process returned {process.ExitCode}.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            TryLog(LogLevel.Error, $"Application maintenance mode {argument} failed: {ex.Message}");
            return new MaintenanceResult(false, ex.HResult < 0 ? ex.HResult : 1, ex.Message);
        }
    }

    private void OnDetectBegin(object? sender, DetectBeginEventArgs e)
    {
        isInstalled = e.RegistrationType == RegistrationType.Full;
        UpdateUi(vm => vm.StatusText = "Checking installed components…");
    }

    private void OnDetectRelatedBundle(object? sender, DetectRelatedBundleEventArgs e)
    {
        if (e.RelationType == RelationType.Upgrade && engine.CompareVersions(e.Version, SafeVariable("WixBundleVersion", "0.0.0")) > 0)
        {
            newerRelatedBundle = true;
        }
    }

    private void OnDetectPackageComplete(object? sender, DetectPackageCompleteEventArgs e)
    {
        var present = string.Equals(e.State.ToString(), "Present", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(e.State.ToString(), "Superseded", StringComparison.OrdinalIgnoreCase);
        var versionVariable = e.PackageId switch
        {
            "Streamlink" => "StreamlinkMachineVersion",
            "Vlc" => "VlcInstalledVersion",
            _ => null
        };
        var installedVersion = versionVariable is null ? "0.0.0.0" : SafeVariable(versionVariable, "0.0.0.0");
        var status = present
            ? "Already installed"
            : installedVersion is not ("" or "0" or "0.0.0" or "0.0.0.0")
                ? "Will be updated"
                : "Will be installed";

        UpdateUi(vm =>
        {
            switch (e.PackageId)
            {
                case "Streamlink":
                    vm.StreamlinkStatus = status;
                    break;
                case "Vlc":
                    vm.VlcStatus = status;
                    break;
            }
        });
    }

    private void OnDetectComplete(object? sender, DetectCompleteEventArgs e)
    {
        UpdateUi(vm =>
        {
            if (e.Status < 0)
            {
                resultCode = e.Status;
                ShowResult(false, false, "Setup could not inspect this PC", FormatFailure(e.Status));
                CompleteHeadlessIfNeeded();
                return;
            }

            vm.StreamlinkStatus = ResolvePendingStatus(vm.StreamlinkStatus);
            vm.VlcStatus = ResolvePendingStatus(vm.VlcStatus);

            if (retryingDetection)
            {
                retryingDetection = false;
                vm.Page = isInstalled ? BootstrapperPage.Maintenance : BootstrapperPage.Install;
                vm.StatusText = isInstalled ? "Ready for maintenance." : "Ready to install.";
            }
            else if (command?.Action == LaunchAction.Uninstall && command.Resume != ResumeType.Arp)
            {
                Uninstall();
            }
            else if (command?.Display != Display.Full)
            {
                BeginAction(command?.Action is { } action && action != LaunchAction.Unknown
                    ? action
                    : LaunchAction.Install);
            }
            else
            {
                vm.Page = isInstalled ? BootstrapperPage.Maintenance : BootstrapperPage.Install;
                vm.StatusText = isInstalled ? "Ready for maintenance." : "Ready to install.";
            }
        });
    }

    private void OnPlanComplete(object? sender, PlanCompleteEventArgs e)
    {
        if (e.Status < 0 || viewModel?.CancelRequested == true)
        {
            RunDetached(CompleteApplyAsync(e.Status < 0 ? e.Status : ErrorInstallUserExitHResult, ApplyRestart.None),
                "Setup preparation could not be completed", "Setup could not start");
            return;
        }

        UpdateUi(vm =>
        {
            // Cancellation can arrive after PlanComplete, while this dispatcher callback is queued.
            if (vm.CancelRequested)
            {
                RunDetached(CompleteApplyAsync(ErrorInstallUserExitHResult, ApplyRestart.None),
                    "Canceled setup plan could not be completed", "Setup could not stop");
                return;
            }
            vm.StatusText = "Waiting for Windows permission…";
            // Engine callbacks arrive on the engine's thread; window.IsVisible/WindowHandle are
            // dispatcher-affine, so resolve the parent handle here rather than on that thread.
            // Burn requires a real parent HWND even for quiet or related-bundle removal.
            // EnsureHandle creates the hidden window without displaying setup UI.
            engine.Apply(window?.WindowHandle
                ?? throw new InvalidOperationException("The setup window has not been initialized."));
        });
    }

    private void OnApplyBegin(object? sender, ApplyBeginEventArgs e)
    {
        isApplying = true;
        UpdateUi(vm => vm.StatusText = "Applying changes…");
    }

    private void OnCacheAcquireBegin(object? sender, CacheAcquireBeginEventArgs e)
    {
        UpdateUi(vm => vm.StatusText = $"Acquiring {PackageDisplayName(e.PackageOrContainerId)}…");
    }

    private void OnCacheAcquireProgress(object? sender, CacheAcquireProgressEventArgs e)
    {
        e.Cancel = !isRollingBack && viewModel?.CancelRequested == true;
        UpdateProgress(e.OverallPercentage, $"Downloading {PackageDisplayName(e.PackageOrContainerId)}…");
    }

    private void OnExecutePackageBegin(object? sender, ExecutePackageBeginEventArgs e)
    {
        var rollingBack = !e.ShouldExecute;
        isRollingBack = rollingBack;
        e.Cancel = !rollingBack && viewModel?.CancelRequested == true;
        UpdateUi(vm =>
        {
            vm.IsRollingBack = rollingBack;
            vm.StatusText = rollingBack
                ? $"Rolling back {PackageDisplayName(e.PackageId)}..."
                : vm.CancelRequested ? "Canceling and rolling back..." : $"Configuring {PackageDisplayName(e.PackageId)}…";
        });
    }

    private void OnExecuteProgress(object? sender, ExecuteProgressEventArgs e)
    {
        e.Cancel = !isRollingBack && viewModel?.CancelRequested == true;
        UpdateProgress(e.OverallPercentage, isRollingBack
            ? $"Rolling back {PackageDisplayName(e.PackageId)}..."
            : $"Configuring {PackageDisplayName(e.PackageId)}…");
    }

    private void OnProgress(object? sender, ProgressEventArgs e)
    {
        e.Cancel = !isRollingBack && viewModel?.CancelRequested == true;
        UpdateProgress(e.OverallPercentage, null);
    }

    private void OnError(object? sender, WixToolset.BootstrapperApplicationApi.ErrorEventArgs e)
    {
        lastError = string.IsNullOrWhiteSpace(e.ErrorMessage)
            ? $"Windows Installer error {e.ErrorCode}."
            : e.ErrorMessage;
        TryLog(LogLevel.Error, $"{PackageDisplayName(e.PackageId)}: {lastError}");
    }

    private void OnApplyComplete(object? sender, ApplyCompleteEventArgs e)
    {
        RunDetached(
            CompleteApplyAsync(e.Status, e.Restart),
            "Post-apply processing failed",
            "Setup could not finish");
    }

    private async Task CompleteApplyAsync(int status, ApplyRestart restart)
    {
        var effectiveStatus = status;
        var cleanupWarning = string.Empty;

        if (status < 0 && !IsRelatedBundleRemoval && plannedAction == LaunchAction.Uninstall && notificationsUnregistered)
        {
            var executable = GetInstalledApplicationPath();
            if (File.Exists(executable))
            {
                var restore = await Task.Run(() =>
                    RunMaintenance(executable, "--maintenance-register-notifications", TimeSpan.FromSeconds(15))).ConfigureAwait(false);
                if (!restore.Success)
                {
                    cleanupWarning = " Windows notification registration could not be restored automatically.";
                }
            }
        }
        else if (status >= 0 && !IsRelatedBundleRemoval && plannedAction == LaunchAction.Uninstall && viewModel?.PurgeUserData == true)
        {
            var purge = await Task.Run(RunPurgeHelper).ConfigureAwait(false);
            if (!purge.Success)
            {
                effectiveStatus = purge.ExitCode == 0 ? 1 : purge.ExitCode;
                cleanupWarning = "The application was removed, but personal-data cleanup is incomplete. Open the setup log for details.";
            }
        }

        await InvokeUiAsync(() => FinalizeApply(status, effectiveStatus, restart, cleanupWarning)).ConfigureAwait(false);
    }

    /// <summary>
    /// Observes a detached operation. Without this, a failure inside a discarded task leaves the
    /// progress page frozen with no Close button and Cancel already disabled, so the user has no
    /// way to proceed.
    /// </summary>
    private void RunDetached(Task operation, string logMessage, string failureTitle)
    {
        _ = operation.ContinueWith(
            completed =>
            {
                var error = completed.Exception?.GetBaseException();
                if (error is null)
                {
                    return;
                }

                TryLog(LogLevel.Error, $"{logMessage}: {error}");
                resultCode = error.HResult < 0 ? error.HResult : 1;
                UpdateUi(_ =>
                {
                    ShowResult(false, false, failureTitle, error.Message);
                    CompleteHeadlessIfNeeded();
                });
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private MaintenanceResult RunPurgeHelper()
    {
        var helper = Path.Combine(AppContext.BaseDirectory, MaintenanceExecutableName);
        if (!File.Exists(helper))
        {
            TryLog(LogLevel.Error, $"Post-uninstall maintenance helper was not found: {helper}");
            return new MaintenanceResult(false, 2, "The maintenance helper was not found.");
        }

        return RunMaintenance(helper, "--purge-user-data-only /quiet /purge-user-data", TimeSpan.FromMinutes(2));
    }

    private void FinalizeApply(int applyStatus, int effectiveStatus, ApplyRestart restart, string cleanupWarning)
    {
        logPath = SafeVariable("WixBundleLog", string.Empty);
        resultCode = effectiveStatus;
        var canceled = IsCancellation(applyStatus) || viewModel?.CancelRequested == true;
        var success = applyStatus >= 0 && effectiveStatus == applyStatus;

        if (success && restart != ApplyRestart.None)
        {
            resultCode = ErrorSuccessRebootRequired;
        }

        if (!string.IsNullOrWhiteSpace(cleanupWarning) && applyStatus >= 0)
        {
            ShowResult(false, true, "Uninstalled with a cleanup warning", cleanupWarning);
        }
        else if (success)
        {
            var title = plannedAction switch
            {
                LaunchAction.Uninstall => "Stream Studio was uninstalled",
                LaunchAction.Repair => "Repair completed",
                _ => isInstalled ? "Update completed" : "Installation completed"
            };
            var message = plannedAction == LaunchAction.Uninstall
                ? "Your personal data was " + (viewModel?.PurgeUserData == true
                    ? "removed. VLC and Streamlink were retained."
                    : "preserved. VLC and Streamlink were retained.")
                : restart == ApplyRestart.None
                    ? "Stream Studio is ready to use."
                    : "Windows must be restarted before all changes take effect.";
            ShowResult(true, false, title, message);
        }
        else if (canceled)
        {
            resultCode = ErrorInstallUserExit;
            ShowResult(false, false, "Setup was canceled", "Setup stopped. Open the setup log to review any changes." + cleanupWarning);
        }
        else
        {
            ShowResult(false, false, "Setup did not complete", (lastError ?? FormatFailure(applyStatus)) + cleanupWarning);
        }

        CompleteHeadlessIfNeeded();
    }

    private void ShowResult(bool success, bool warning, string title, string message)
    {
        isApplying = false;
        if (viewModel is null)
        {
            return;
        }

        viewModel.ResultSucceeded = success;
        viewModel.ResultWarning = warning;
        viewModel.ResultTitle = title;
        viewModel.ResultMessage = message;
        viewModel.CanOpenLog = !string.IsNullOrWhiteSpace(logPath ?? SafeVariable("WixBundleLog", string.Empty));
        viewModel.CanLaunch = success && resultCode != ErrorSuccessRebootRequired &&
            plannedAction != LaunchAction.Uninstall && File.Exists(GetInstalledApplicationPath());
        viewModel.CanRetry = !success && !warning && !newerRelatedBundle && command?.Display == Display.Full;
        viewModel.Page = BootstrapperPage.Result;
        viewModel.Progress = success ? 100 : viewModel.Progress;
    }

    private void CompleteHeadlessIfNeeded()
    {
        if (command?.Display != Display.Full)
        {
            shutdownRequested = true;
            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Background);
        }
    }

    private void UpdateProgress(int percentage, string? status)
    {
        UpdateUi(vm =>
        {
            vm.Progress = percentage;
            if (!string.IsNullOrWhiteSpace(status) && !vm.CancelRequested)
            {
                vm.StatusText = status;
            }
        });
    }

    private void UpdateUi(Action<BootstrapperViewModel> update)
    {
        if (dispatcher is null || viewModel is null || shutdownRequested)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            update(viewModel);
        }
        else
        {
            _ = dispatcher.BeginInvoke(() =>
            {
                if (!shutdownRequested && viewModel is not null)
                {
                    update(viewModel);
                }
            });
        }
    }

    private Task InvokeUiAsync(Action action)
    {
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private string GetInstalledApplicationPath()
    {
        var configured = SafeFormattedVariable("InstalledApplicationPath", string.Empty);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Streamlink VLC Studio",
            ApplicationExecutableName);
    }

    private string SafeVariable(string name, string fallback)
    {
        try
        {
            return engine.ContainsVariable(name) ? engine.GetVariableString(name) : fallback;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return fallback;
        }
    }

    private string SafeFormattedVariable(string name, string fallback)
    {
        try
        {
            // GetVariableString returns the raw value, including nested folder tokens.
            // Format the variable reference so Burn also preserves literal paths.
            return engine.ContainsVariable(name) ? engine.FormatString($"[{name}]") : fallback;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return fallback;
        }
    }

    private bool SafeNumericVariableDefaultTrue(string name)
    {
        try
        {
            return !engine.ContainsVariable(name) || engine.GetVariableNumeric(name) != 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return true;
        }
    }

    private void TryLog(LogLevel level, string message)
    {
        try
        {
            engine.Log(level, message);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void OpenWithShell(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
        }
    }

    private static string PackageDisplayName(string? packageId) => packageId switch
    {
        "Streamlink" => "Streamlink",
        "Vlc" => "VLC media player",
        "StreamStudio" => "Stream Studio",
        "StreamlinkVlcStudio" => "Stream Studio",
        null or "" => "setup files",
        _ => packageId
    };

    private static string ResolvePendingStatus(string status) =>
        string.Equals(status, "Checking…", StringComparison.Ordinal) ? "Will be installed" : status;

    private static string TrimDisplayVersion(string version)
    {
        var parts = version.Split('.');
        return parts.Length == 4 && parts[3] == "0" ? string.Join('.', parts, 0, 3) : version;
    }

    private static string FormatFailure(int status) =>
        $"Setup returned 0x{status:X8}. See the setup log for details.";

    private static bool IsCancellation(int status) =>
        status is ErrorInstallUserExit or ErrorCancelledHResult or ErrorInstallUserExitHResult;

    private static int NormalizeExitCode(int status)
    {
        if ((status & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000))
        {
            return status & 0xFFFF;
        }

        return status;
    }

    private readonly record struct MaintenanceResult(bool Success, int ExitCode, string Message)
    {
        public static MaintenanceResult Ok { get; } = new(true, 0, string.Empty);
    }
}

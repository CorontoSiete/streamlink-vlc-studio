using System.Diagnostics;
using Microsoft.Win32;

namespace StreamlinkVlcStudio.Maintenance;

internal static class MaintenanceApplication
{
    private const int ExitSuccess = 0;
    private const int ExitManagedFilesRetained = 1;
    private const int ExitCleanupIncomplete = 2;
    private const int ExitInvalidState = 3;
    private const int ExitLaunchFailed = 4;
    private const int ExitInvalidArguments = 87;
    private const int ExitCancelled = 1602;
    private const string UninstallRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\StreamlinkVlcStudio";

    internal static int Run(string[] args)
    {
        CommandLineOptions options;
        try
        {
            options = CommandLineOptions.Parse(args);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            NativeDialog.ShowError(exception.Message);
            return ExitInvalidArguments;
        }

        try
        {
            if (options.Staged)
            {
                return RunStaged(options);
            }

            using var log = MaintenanceLog.Create();
            log.Write($"Maintenance executable started. Quiet={options.Quiet}; Purge={options.PurgeUserData}; DataOnly={options.PurgeUserDataOnly}");
            if (options.PurgeUserDataOnly)
            {
                return RunDataOnly(options, log);
            }

            var installDirectory = ResolveInstallDirectory();
            _ = InstallOwnership.Load(installDirectory);
            if (!options.Quiet && !NativeDialog.Confirm(
                    "Uninstall Streamlink VLC Studio?\n\nStreamlink and VLC will remain installed."))
            {
                log.Write("The user canceled uninstall before any changes were made.");
                return ExitCancelled;
            }

            var purgeUserData = options.PurgeUserData;
            if (!options.Quiet && !purgeUserData)
            {
                purgeUserData = NativeDialog.Confirm(
                    "Also delete this Windows account's Streamlink VLC Studio settings, cache, and temporary data?\n\n" +
                    "No is the default. Streamlink, VLC, other profiles, and their data are never removed.");
            }

            if (!StageLauncher.TryLaunch(
                    installDirectory,
                    purgeUserData,
                    options.Quiet,
                    log,
                    out var launchError))
            {
                log.Write($"Could not launch staged maintenance: {launchError}");
                if (!options.Quiet)
                {
                    NativeDialog.ShowError($"Uninstall could not start.\n\n{launchError}\n\nLog: {log.Path}");
                }

                return ExitLaunchFailed;
            }

            log.Write("The staged maintenance process was launched successfully.");
            return ExitSuccess;
        }
        catch (Exception exception) when (IsRecoverableMaintenanceFailure(exception))
        {
            if (!options.Quiet)
            {
                NativeDialog.ShowError(
                    $"No application files were removed because the installation ownership state is unsafe or invalid.\n\n{exception.Message}");
            }

            return ExitInvalidState;
        }
    }

    private static int RunStaged(CommandLineOptions options)
    {
        using var log = MaintenanceLog.OpenExisting(options.LogPath!);
        try
        {
            StageLauncher.ValidateStagedHandshake(options);
            log.Write($"Validated staged handoff for installation: {options.InstallDirectory}");
            WaitForOriginalProcess(options.ParentProcessId, log);

            var ownership = InstallOwnership.Load(options.InstallDirectory!);
            var appPath = Path.Combine(ownership.Root, "StreamlinkVlcStudio.exe");
            var shutdownRequested = RunAppMaintenanceMode(
                appPath,
                "--maintenance-request-shutdown",
                TimeSpan.FromSeconds(20),
                log);
            if (!shutdownRequested)
            {
                log.Write("Graceful shutdown did not report success; bounded file deletion retries will determine the retained result.");
            }

            var notificationsUnregistered = RunAppMaintenanceMode(
                appPath,
                "--maintenance-unregister-notifications",
                TimeSpan.FromSeconds(15),
                log);
            var outcome = ManagedInstallationCleaner.Clean(ownership, log);
            if (!outcome.ApplicationRemoved && notificationsUnregistered && File.Exists(appPath))
            {
                _ = RunAppMaintenanceMode(
                    appPath,
                    "--maintenance-register-notifications",
                    TimeSpan.FromSeconds(15),
                    log);
            }

            var personalDataRetained = options.PurgeUserData
                ? UserDataCleaner.PurgeCurrentUserData(log)
                : Array.Empty<string>();
            return ReportResult(options, outcome, notificationsUnregistered, personalDataRetained, log);
        }
        catch (Exception exception) when (IsRecoverableMaintenanceFailure(exception))
        {
            log.Write($"Staged uninstall failed without deleting unvalidated paths: {exception}");
            if (!options.Quiet)
            {
                NativeDialog.ShowError(
                    $"Uninstall could not continue safely. No unvalidated paths were removed.\n\n{exception.Message}\n\nLog: {log.Path}");
            }

            return ExitInvalidState;
        }
        finally
        {
            StageLauncher.ScheduleCurrentStageForCleanup(log);
        }
    }

    private static int RunDataOnly(CommandLineOptions options, MaintenanceLog log)
    {
        var purge = options.PurgeUserData;
        if (!purge && !options.Quiet)
        {
            purge = NativeDialog.Confirm(
                "Delete this Windows account's Streamlink VLC Studio settings, cache, and temporary data?\n\n" +
                "No is the default. Streamlink, VLC, application files, other profiles, and their data are never removed.");
        }

        if (!purge)
        {
            log.Write("Personal-data cleanup was not selected; no data was removed.");
            return options.Quiet ? ExitSuccess : ExitCancelled;
        }

        var retained = UserDataCleaner.PurgeCurrentUserData(log);
        if (retained.Count == 0)
        {
            if (!options.Quiet)
            {
                NativeDialog.ShowInformation($"Personal data for this Windows account was removed.\n\nLog: {log.Path}");
            }

            return ExitSuccess;
        }

        var report = log.WriteRetainedPathReport("Personal-data cleanup incomplete", retained);
        if (!options.Quiet)
        {
            NativeDialog.ShowError(
                $"Some personal data could not be removed.\n\nRetained-path report: {report}\nLog: {log.Path}");
        }

        return ExitCleanupIncomplete;
    }

    /// <summary>
    /// Failures that mean "stop without deleting anything" rather than "the process is broken".
    /// <see cref="PathSafety.Normalize"/> wraps <c>Path.GetFullPath</c>, so malformed paths from
    /// the registry or the command line surface here too and must not crash the uninstaller.
    /// </summary>
    private static bool IsRecoverableMaintenanceFailure(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            ArgumentException or
            NotSupportedException or
            System.Text.Json.JsonException or
            System.Security.Cryptography.CryptographicException;

    private static int ReportResult(
        CommandLineOptions options,
        CleanupOutcome outcome,
        bool notificationsUnregistered,
        IReadOnlyList<string> personalDataRetained,
        MaintenanceLog log)
    {
        if (!outcome.ApplicationRemoved)
        {
            var retained = outcome.RetainedPaths.Concat(personalDataRetained).ToArray();
            var report = log.WriteRetainedPathReport(
                "Application removal incomplete; uninstall registration was preserved for retry",
                retained);
            log.Write("Application removal incomplete. The uninstall registration remains available for retry.");
            if (!options.Quiet)
            {
                NativeDialog.ShowError(
                    $"Some managed application files could not be removed. You can retry uninstall from Windows Settings.\n\n" +
                    $"Retained-path report: {report}\nLog: {log.Path}");
            }

            return ExitManagedFilesRetained;
        }

        if (personalDataRetained.Count > 0)
        {
            var report = log.WriteRetainedPathReport(
                "Application removed; personal-data cleanup incomplete",
                personalDataRetained);
            if (!options.Quiet)
            {
                NativeDialog.ShowError(
                    $"Streamlink VLC Studio was removed, but some personal data could not be cleaned up.\n\n" +
                    $"Retained-path report: {report}\nLog: {log.Path}");
            }

            return ExitCleanupIncomplete;
        }

        var cleanupIncomplete = !notificationsUnregistered || !outcome.RegistrationRemoved;
        if (!options.Quiet)
        {
            var dataMessage = options.PurgeUserData
                ? "Personal data for this Windows account was also removed."
                : "Personal data was preserved.";
            NativeDialog.ShowInformation(
                $"Streamlink VLC Studio was removed.\n\n{dataMessage}\nStreamlink and VLC were retained.\n\nLog: {log.Path}");
        }

        return cleanupIncomplete ? ExitCleanupIncomplete : ExitSuccess;
    }

    private static string ResolveInstallDirectory()
    {
        var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(executableDirectory) &&
            File.Exists(Path.Combine(executableDirectory, InstallOwnership.OwnerFileName)) &&
            File.Exists(Path.Combine(executableDirectory, InstallOwnership.ManifestFileName)))
        {
            return PathSafety.Normalize(executableDirectory);
        }

        using var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryPath, writable: false);
        var registeredLocation = key?.GetValue("InstallLocation") as string;
        if (string.IsNullOrWhiteSpace(registeredLocation))
        {
            throw new InvalidDataException("Could not resolve the registered ZIP installation directory.");
        }

        return PathSafety.Normalize(registeredLocation);
    }

    private static bool RunAppMaintenanceMode(
        string appPath,
        string argument,
        TimeSpan timeout,
        MaintenanceLog log)
    {
        if (!PathSafety.TryGetAttributes(appPath, out var attributes) ||
            !PathSafety.IsPlainFile(attributes) ||
            PathSafety.ContainsReparsePoint(appPath))
        {
            log.Write($"Application maintenance mode is unavailable: {appPath} {argument}");
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = appPath,
                WorkingDirectory = Path.GetDirectoryName(appPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { argument }
            });
            if (process is null)
            {
                log.Write($"Application maintenance process did not start: {argument}");
                return false;
            }

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: false);
                }
                catch (InvalidOperationException)
                {
                }

                log.Write($"Application maintenance mode timed out: {argument}");
                return false;
            }

            log.Write($"Application maintenance mode completed with exit code {process.ExitCode}: {argument}");
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.Write($"Application maintenance mode failed: {argument}. {exception.Message}");
            return false;
        }
    }

    private static void WaitForOriginalProcess(int processId, MaintenanceLog log)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                throw new IOException("The original uninstaller did not exit within 30 seconds.");
            }
        }
        catch (ArgumentException)
        {
            // The handoff process already exited.
        }

        log.Write("Original installed uninstaller exited; staged cleanup may remove it safely.");
    }
}

internal static class StageLauncher
{
    private const string StageDirectoryPrefix = "StreamlinkVlcStudio-Maintenance-Stage-";
    private const string StageTokenFileName = ".stage-token";

    internal static bool TryLaunch(
        string installDirectory,
        bool purgeUserData,
        bool quiet,
        MaintenanceLog log,
        out string error)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            error = "The current maintenance executable path is unavailable.";
            return false;
        }

        var tempRoot = PathSafety.Normalize(Path.GetTempPath());
        if (PathSafety.ContainsReparsePoint(tempRoot))
        {
            error = "The current-user temporary directory contains a reparse point.";
            return false;
        }

        var nonce = Guid.NewGuid().ToString("N");
        var stageDirectory = Path.Combine(tempRoot, StageDirectoryPrefix + nonce);
        var stageExecutable = Path.Combine(stageDirectory, "StreamlinkVlcStudio.Maintenance.exe");
        try
        {
            Directory.CreateDirectory(stageDirectory);
            if (PathSafety.ContainsReparsePoint(stageDirectory))
            {
                throw new IOException("The newly created stage contains a reparse point.");
            }

            File.Copy(processPath, stageExecutable, overwrite: false);
            File.WriteAllText(Path.Combine(stageDirectory, StageTokenFileName), nonce);

            var startInfo = new ProcessStartInfo
            {
                FileName = stageExecutable,
                WorkingDirectory = stageDirectory,
                UseShellExecute = false,
                CreateNoWindow = quiet
            };
            startInfo.ArgumentList.Add("--staged");
            startInfo.ArgumentList.Add("--install-directory");
            startInfo.ArgumentList.Add(installDirectory);
            startInfo.ArgumentList.Add("--parent-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--stage-nonce");
            startInfo.ArgumentList.Add(nonce);
            startInfo.ArgumentList.Add("--log-path");
            startInfo.ArgumentList.Add(log.Path);
            if (quiet)
            {
                startInfo.ArgumentList.Add("/quiet");
            }

            if (purgeUserData)
            {
                startInfo.ArgumentList.Add("/purge-user-data");
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new IOException("The operating system did not return a staged maintenance process.");
            }

            log.Write($"Staged maintenance executable: {stageExecutable}; PID={process.Id}");
            error = "";
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryRemoveFailedStage(stageDirectory);
            error = exception.Message;
            return false;
        }
    }

    internal static void ValidateStagedHandshake(CommandLineOptions options)
    {
        var processPath = PathSafety.Normalize(Environment.ProcessPath ?? "");
        var stageDirectory = Path.GetDirectoryName(processPath)
            ?? throw new InvalidDataException("The staged executable directory is unavailable.");
        var tempRoot = PathSafety.Normalize(Path.GetTempPath());
        var expectedName = StageDirectoryPrefix + options.StageNonce;
        if (!PathSafety.IsSameOrUnder(stageDirectory, tempRoot) ||
            PathSafety.PathsEqual(stageDirectory, tempRoot) ||
            !string.Equals(Path.GetFileName(stageDirectory), expectedName, StringComparison.Ordinal) ||
            PathSafety.ContainsReparsePoint(stageDirectory) ||
            !Guid.TryParseExact(options.StageNonce, "N", out _))
        {
            throw new InvalidDataException("The staged maintenance executable is outside its validated handoff directory.");
        }

        var tokenPath = Path.Combine(stageDirectory, StageTokenFileName);
        if (!PathSafety.TryGetAttributes(tokenPath, out var attributes) ||
            !PathSafety.IsPlainFile(attributes) ||
            !string.Equals(File.ReadAllText(tokenPath), options.StageNonce, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The staged maintenance handoff token is invalid.");
        }

        File.Delete(tokenPath);
    }

    internal static void ScheduleCurrentStageForCleanup(MaintenanceLog log)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return;
        }

        if (NativeDialog.ScheduleDeleteOnReboot(processPath, null, 0x00000004))
        {
            log.Write($"Scheduled staged maintenance executable for deletion at reboot: {processPath}");
        }
        else
        {
            log.Write($"The staged maintenance executable remains in the temporary directory: {processPath}");
        }
    }

    private static void TryRemoveFailedStage(string stageDirectory)
    {
        try
        {
            if (Directory.Exists(stageDirectory) &&
                Path.GetFileName(stageDirectory).StartsWith(StageDirectoryPrefix, StringComparison.Ordinal) &&
                !PathSafety.ContainsReparsePoint(stageDirectory))
            {
                foreach (var fileName in new[] { "StreamlinkVlcStudio.Maintenance.exe", StageTokenFileName })
                {
                    var path = Path.Combine(stageDirectory, fileName);
                    if (PathSafety.TryGetAttributes(path, out var attributes) &&
                        PathSafety.IsPlainFile(attributes))
                    {
                        File.Delete(path);
                    }
                }

                Directory.Delete(stageDirectory, recursive: false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

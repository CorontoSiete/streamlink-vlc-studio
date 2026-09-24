namespace StreamlinkVlcStudio.Core.Services;

public interface IAppUpdateService
{
    event EventHandler<AppUpdateStateChangedEventArgs>? StateChanged { add { } remove { } }
    AppUpdateState State => AppUpdateState.Idle;
    Task<AppUpdateCheckResult> CheckAsync(UpdateCheckReason reason, CancellationToken cancellationToken = default) =>
        Task.FromException<AppUpdateCheckResult>(new NotSupportedException("This updater does not support staged checks."));
    Task<PreparedAppUpdate> DownloadAsync(AppUpdateRelease release, IProgress<AppUpdateProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.FromException<PreparedAppUpdate>(new NotSupportedException("This updater does not support staged downloads."));
    Task<AppUpdateLaunchResult> ApplyAndRestartAsync(PreparedAppUpdate update, CancellationToken cancellationToken = default) =>
        Task.FromException<AppUpdateLaunchResult>(new NotSupportedException("This updater does not support staged installation."));
    Task<AppUpdateCompletion?> ConsumeCompletionAsync(CancellationToken cancellationToken = default) => Task.FromResult<AppUpdateCompletion?>(null);

}

public enum UpdateCheckReason { Startup, Retry, Manual }
public enum AppInstallKind { Managed, LegacyManaged, Zip, Unmanaged }
public enum AppUpdatePhase { Idle, Checking, Available, Downloading, Verifying, Ready, Launching, Completed, NotifyOnly, Failed }

public sealed record AppUpdateState(
    AppUpdatePhase Phase,
    string Message,
    AppUpdateRelease? Release = null,
    PreparedAppUpdate? PreparedUpdate = null,
    AppUpdateProgress? Progress = null)
{
    public static AppUpdateState Idle { get; } = new(AppUpdatePhase.Idle, "Updates have not been checked.");
}

public sealed class AppUpdateStateChangedEventArgs(AppUpdateState state) : EventArgs
{
    public AppUpdateState State { get; } = state;
}

public sealed record AppUpdateAsset(string Name, Uri DownloadUri, long Length, string Sha256);
public sealed record AppUpdateRelease(
    Version Version,
    string Tag,
    string Commit,
    string Repository,
    Uri ReleasePage,
    int ProtocolVersion,
    AppUpdateAsset Setup,
    AppUpdateAsset Zip,
    IReadOnlyDictionary<string, string> DependencyMinimums);
public sealed record AppUpdateCheckResult(
    bool Checked,
    bool IsUpdateAvailable,
    bool IsNotifyOnly,
    AppInstallKind InstallKind,
    AppUpdateRelease? Release,
    string Message,
    DateTimeOffset CheckedAt);
public sealed record AppUpdateProgress(long BytesReceived, long TotalBytes)
{
    public double Percentage => TotalBytes <= 0 ? 0 : Math.Clamp(BytesReceived * 100d / TotalBytes, 0, 100);
}
public sealed record PreparedAppUpdate(
    Guid OperationId,
    AppUpdateRelease Release,
    string OperationDirectory,
    string SetupPath,
    string HelperPath,
    DateTimeOffset VerifiedAt);
public sealed record AppUpdateLaunchResult(bool Started, string Message, string? LogPath = null);
public enum AppUpdateCompletionOutcome { Succeeded, SucceededRebootRequired, Canceled, Failed }
public sealed record AppUpdateCompletion(
    Guid OperationId,
    AppUpdateCompletionOutcome Outcome,
    int InstallerExitCode,
    string? LogPath,
    string Message,
    DateTimeOffset CompletedAt);

// Kept as a data-contract compatibility type for older test doubles and integrations.
// It is intentionally no longer part of IAppUpdateService.
public sealed record AppUpdateStartResult(string Message, bool RequestApplicationShutdown);

namespace StreamlinkVlcStudio.Core.Services;

public interface IAppUpdateService
{
    Task<AppUpdateStartResult> StartLatestReleaseUpdateAsync(CancellationToken cancellationToken = default);
}

public sealed record AppUpdateStartResult(string Message, bool RequestApplicationShutdown);

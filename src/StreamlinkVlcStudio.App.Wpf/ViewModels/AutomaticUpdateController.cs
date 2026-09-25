using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

/// <summary>Keeps update discovery alive for long-running sessions and after network failures.</summary>
internal sealed class AutomaticUpdateController(
    IAppUpdateService service,
    Func<bool> checksEnabled,
    Action<AppUpdateCheckResult> onChecked,
    Action<AppUpdateCompletion> onCompleted,
    IAppLogger logger,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<AppUpdateCheckResult, CancellationToken, Task>? prepareUpdate = null)
{
    internal async Task RunAsync(CancellationToken token)
    {
        var wait = delay ?? Task.Delay;
        try
        {
            try
            {
                if (await service.ConsumeCompletionAsync(token) is { } completion) onCompleted(completion);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                logger.Write(AppLogLevel.Warning, "Updater", "Could not read the previous update result; update checks will continue.", ex);
            }

            var nextDelay = TimeSpan.FromSeconds(20);
            var failures = 0;
            while (true)
            {
                await wait(nextDelay, token);
                token.ThrowIfCancellationRequested();
                if (!checksEnabled() || service.State.Phase is AppUpdatePhase.Checking or AppUpdatePhase.Downloading or AppUpdatePhase.Verifying or AppUpdatePhase.Launching)
                {
                    nextDelay = TimeSpan.FromMinutes(1);
                    continue;
                }

                try
                {
                    var result = await service.CheckAsync(failures == 0 ? UpdateCheckReason.Startup : UpdateCheckReason.Retry, token);
                    onChecked(result);
                    if (checksEnabled() && prepareUpdate is not null)
                        await prepareUpdate(result, token);
                    failures = 0;
                    // The service's signed 24-hour cache controls network cadence. An hourly
                    // wake also makes snoozed versions visible again without restarting the app.
                    nextDelay = TimeSpan.FromHours(1);
                }
                catch (NotSupportedException)
                {
                    return;
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    nextDelay = TimeSpan.FromMinutes(Math.Min(360, 15 * Math.Pow(2, Math.Min(failures++, 5))));
                    logger.Write(AppLogLevel.Warning, "Updater", $"Automatic update failed; retrying in {nextDelay.TotalMinutes:0} minutes.", ex);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }
}

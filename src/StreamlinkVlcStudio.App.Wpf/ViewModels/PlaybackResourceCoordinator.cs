using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

/// <summary>
/// Owns the bounded teardown of a playback engine and its parking surface. The tab remains the
/// state owner, while this coordinator makes the shutdown contract reusable for detached tabs and
/// future playback hosts.
/// </summary>
internal sealed class PlaybackResourceCoordinator
{
    private static readonly TimeSpan CancellationDrainTimeout = TimeSpan.FromSeconds(1);
    private readonly IAppLogger logger;
    private readonly Func<string> displayName;

    public PlaybackResourceCoordinator(IAppLogger logger, Func<string> displayName)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.displayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
    }

    public async Task StopAsync(
        IPlaybackEngine engine,
        TimeSpan? timeout,
        IDisposable? parkingSurface,
        CancellationToken engineCancellationToken,
        Action<Task>? observeBackgroundCleanup = null)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var shutdownCancellation = CancellationTokenSource.CreateLinkedTokenSource(engineCancellationToken);
        var shutdownTask = Task.Run(async () =>
        {
            try
            {
                await engine.StopAsync(shutdownCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                engine.Dispose();
                parkingSurface?.Dispose();
                shutdownCancellation.Dispose();
            }
        });

        try
        {
            if (timeout is null)
            {
                await shutdownTask.ConfigureAwait(false);
            }
            else
            {
                var completed = await Task.WhenAny(
                    shutdownTask,
                    Task.Delay(timeout.Value, CancellationToken.None)).ConfigureAwait(false);
                if (!ReferenceEquals(completed, shutdownTask))
                {
                    throw new TimeoutException();
                }

                await shutdownTask.ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            try
            {
                shutdownCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The shutdown won the race and disposed its linked token source.
            }
            logger.Write(
                AppLogLevel.Warning,
                "Playback",
                $"Timed out stopping playback for {displayName()}; cancellation was requested through the engine token.");

            var drained = await Task.WhenAny(
                shutdownTask,
                Task.Delay(CancellationDrainTimeout, CancellationToken.None)).ConfigureAwait(false);
            if (ReferenceEquals(drained, shutdownTask))
            {
                ObserveCompletedShutdown(shutdownTask);
            }
            else
            {
                logger.Write(
                    AppLogLevel.Warning,
                    "Playback",
                    $"Playback cleanup for {displayName()} ignored cancellation and remains tracked in the background.");
                observeBackgroundCleanup?.Invoke(shutdownTask);
            }
        }
        catch (OperationCanceledException) when (engineCancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Playback",
                $"Failed to stop playback for {displayName()}.",
                ex);
        }
    }

    private static void ObserveCompletedShutdown(Task shutdownTask)
    {
        if (shutdownTask.IsFaulted)
        {
            _ = shutdownTask.Exception;
        }
    }
}

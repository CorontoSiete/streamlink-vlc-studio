using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

internal static class ChatConnectionCleanup
{
    internal static async Task DisconnectAsync(
        SemaphoreSlim lifecycleGate,
        Action throwIfDisposed,
        Func<Task> disconnectAsync,
        CancellationToken cancellationToken)
    {
        throwIfDisposed();
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Requests admitted before disposal starts must finish draining under this gate.
            await disconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    internal static Task StopAndDisconnectAsync(
        ref LiveChatConnectionSupervisor? supervisor,
        Func<Task> disconnectAsync) =>
        StopAndDisconnectCoreAsync(Interlocked.Exchange(ref supervisor, null), disconnectAsync);

    private static async Task StopAndDisconnectCoreAsync(
        LiveChatConnectionSupervisor? supervisor,
        Func<Task> disconnectAsync)
    {
        try
        {
            if (supervisor is not null) await supervisor.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await disconnectAsync().ConfigureAwait(false);
        }
    }

    internal static void DisposeResources(IAppLogger logger, string source, params IDisposable?[] resources)
    {
        foreach (var resource in resources)
        {
            if (resource is null) continue;
            try
            {
                resource.Dispose();
            }
            catch (Exception exception)
            {
                logger.WriteSafely(AppLogLevel.Warning, source,
                    $"Could not dispose {resource.GetType().Name} after disconnecting chat.", exception);
            }
        }
    }
}

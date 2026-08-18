using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

/// <summary>
/// Supervises an already-established live-chat connection. Initial connection errors remain the
/// caller's responsibility; after that first success, every remote termination is retried until
/// explicit shutdown with bounded exponential backoff and jitter.
/// </summary>
internal sealed class LiveChatConnectionSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan StableConnectionReset = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30)
    ];

    private readonly IAppLogger logger;
    private readonly string source;
    private readonly Action<string> statusChanged;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly Func<double> nextJitter;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly SemaphoreSlim reconnectSignal = new(0, 1);
    private readonly object gate = new();
    private Func<CancellationToken, Task>? reconnectAsync;
    private Task? runTask;
    private Task? disposalTask;
    private TimeSpan lastConnectionDuration;
    private bool reconnectPending;
    private bool disposed;

    internal LiveChatConnectionSupervisor(
        IAppLogger logger,
        string source,
        Action<string> statusChanged,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<double>? nextJitter = null)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.source = string.IsNullOrWhiteSpace(source) ? "LiveChat" : source.Trim();
        this.statusChanged = statusChanged ?? throw new ArgumentNullException(nameof(statusChanged));
        this.delayAsync = delayAsync ?? Task.Delay;
        this.nextJitter = nextJitter ?? Random.Shared.NextDouble;
    }

    internal void Start(Func<CancellationToken, Task> reconnect)
    {
        ArgumentNullException.ThrowIfNull(reconnect);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (runTask is not null)
            {
                throw new InvalidOperationException("The live-chat connection supervisor has already started.");
            }

            reconnectAsync = reconnect;
            runTask = RunAsync();
        }
    }

    internal void NotifyConnectionEnded(TimeSpan connectedDuration)
    {
        lock (gate)
        {
            if (disposed || runTask is null)
            {
                return;
            }

            lastConnectionDuration = connectedDuration > TimeSpan.Zero
                ? connectedDuration
                : TimeSpan.Zero;
            if (reconnectPending)
            {
                return;
            }

            reconnectPending = true;
            reconnectSignal.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task task;
        lock (gate)
        {
            if (disposalTask is not null)
            {
                task = disposalTask;
            }
            else
            {
                disposed = true;
                lifetimeCancellation.Cancel();
                task = disposalTask = DrainAndDisposeAsync(runTask);
            }
        }

        await task.ConfigureAwait(false);
    }

    private async Task DrainAndDisposeAsync(Task? task)
    {
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
            {
            }
        }

        lifetimeCancellation.Dispose();
        reconnectSignal.Dispose();
    }

    private async Task RunAsync()
    {
        var failureIndex = 0;
        var cancellationToken = lifetimeCancellation.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            await reconnectSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            TimeSpan connectedDuration;
            lock (gate)
            {
                reconnectPending = false;
                connectedDuration = lastConnectionDuration;
            }

            if (connectedDuration >= StableConnectionReset)
            {
                failureIndex = 0;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var delay = ApplyJitter(Backoff[Math.Min(failureIndex, Backoff.Length - 1)]);
                SafeStatus($"{source} reconnecting in {delay.TotalSeconds:0.#} seconds...");
                await delayAsync(delay, cancellationToken).ConfigureAwait(false);
                failureIndex = Math.Min(failureIndex + 1, Backoff.Length - 1);
                try
                {
                    await reconnectAsync!(cancellationToken).ConfigureAwait(false);
                    SafeStatus($"{source} reconnected.");
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    SafeLog(AppLogLevel.Warning, $"{source} reconnect attempt failed.", ex);
                    SafeStatus($"{source} reconnect failed: {ex.Message}");
                }
            }
        }
    }

    private TimeSpan ApplyJitter(TimeSpan delay)
    {
        double sample;
        try
        {
            sample = nextJitter();
            if (!double.IsFinite(sample))
            {
                throw new InvalidOperationException("The reconnect jitter provider returned a non-finite value.");
            }

            sample = Math.Clamp(sample, 0d, 1d);
        }
        catch (Exception ex)
        {
            SafeLog(AppLogLevel.Debug, $"{source} reconnect jitter provider failed.", ex);
            sample = 0.5d;
        }

        return TimeSpan.FromMilliseconds(delay.TotalMilliseconds * (0.8d + (sample * 0.4d)));
    }

    private void SafeStatus(string message)
    {
        try
        {
            statusChanged(message);
        }
        catch (Exception ex)
        {
            SafeLog(AppLogLevel.Debug, $"{source} status callback failed.", ex);
        }
    }

    private void SafeLog(AppLogLevel level, string message, Exception exception)
    {
        try
        {
            logger.Write(level, source, message, exception);
        }
        catch (Exception)
        {
        }
    }
}

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.Infrastructure.Streamlink;

internal sealed class StreamlinkExternalHttpSession : IStreamTransportSession
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);
    private readonly Process process;
    private readonly IAppLogger logger;
    private readonly ConcurrentQueue<string> recentLogLines = new();
    private readonly object uriGate = new();
    private Uri? playbackUri;
    private bool disposed;

    public StreamlinkExternalHttpSession(Process process, IAppLogger logger)
    {
        this.process = process;
        this.logger = logger;
    }

    public Uri PlaybackUri
    {
        get
        {
            lock (uriGate)
            {
                return playbackUri ?? throw new InvalidOperationException("Streamlink playback URI has not been resolved.");
            }
        }
    }

    internal IReadOnlyList<string> RecentLogLines => recentLogLines.ToArray();

    public event EventHandler<string>? LogLineReceived;

    internal void SetPlaybackUri(Uri uri)
    {
        lock (uriGate)
        {
            playbackUri = uri;
        }
    }

    internal void AddLogLine(string line)
    {
        recentLogLines.Enqueue(line);
        while (recentLogLines.Count > 200 && recentLogLines.TryDequeue(out _))
        {
        }

        LogLineReceived?.Invoke(this, line);
    }

    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (disposed)
        {
            return;
        }

        await StopProcessAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await StopProcessAsync(CancellationToken.None).ConfigureAwait(false);
        process.Dispose();
    }

    private async Task StopProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(StopTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.Write(AppLogLevel.Warning, "Streamlink", "Timed out waiting for the Streamlink process to exit after kill.");
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            logger.Write(AppLogLevel.Warning, "Streamlink", "Failed to stop Streamlink process cleanly.", ex);
        }
    }
}

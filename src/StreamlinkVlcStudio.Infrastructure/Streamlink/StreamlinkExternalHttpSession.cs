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
    private readonly object disposeGate = new();
    private Uri? playbackUri;
    private Task? disposeTask;

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

        var handlers = LogLineReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<string> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, line);
            }
            catch (Exception ex)
            {
                logger.Write(AppLogLevel.Warning, "Streamlink", "A Streamlink log subscriber failed.", ex);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeGate)
        {
            return new ValueTask(disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await StopProcessAsync().ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
        }
    }

    private async Task StopProcessAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(StopTimeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
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

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using static StreamlinkVlcStudio.Infrastructure.Processes.ProcessExtensions;

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
    private Task standardOutputPump = Task.CompletedTask;
    private Task standardErrorPump = Task.CompletedTask;

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

    internal void AttachOutputPumps(Task standardOutput, Task standardError)
    {
        standardOutputPump = standardOutput ?? throw new ArgumentNullException(nameof(standardOutput));
        standardErrorPump = standardError ?? throw new ArgumentNullException(nameof(standardError));
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
            await ObserveOutputReadsAsync(standardOutputPump, standardErrorPump).ConfigureAwait(false);
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
            if (process.HasExited)
            {
                return;
            }

            await KillProcessTreeAsync(process, StopTimeout).ConfigureAwait(false);
            if (!process.HasExited)
            {
                logger.Write(AppLogLevel.Warning, "Streamlink", "Timed out waiting for the Streamlink process to exit after kill.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            logger.Write(AppLogLevel.Warning, "Streamlink", "Failed to stop Streamlink process cleanly.", ex);
        }
    }
}

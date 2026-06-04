using System.Diagnostics;
using System.Threading;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

internal sealed class NativeReplayOverlayFrameWriteGate : IDisposable
{
    private readonly IAppLogger logger;
    private readonly Func<NativeReplayOverlayFrameWriteRequest, CancellationToken, Task<NativeReplayOverlayFrameWriteResult>> writeAsync;
    private readonly Func<long> getCurrentVersion;
    private readonly Action<Exception> currentWriteFailed;
    private readonly Action<NativeReplayOverlayFrameWriteRequest>? currentWriteSucceeded;
    private readonly TimeSpan slowWriteThreshold;
    private readonly object gate = new();
    private NativeReplayOverlayFrameWriteRequest? pendingWrite;
    private bool writeLoopRunning;
    private bool disposed;
    private long generation;
    private long droppedWrites;

    public NativeReplayOverlayFrameWriteGate(
        IAppLogger logger,
        Func<NativeReplayOverlayFrameWriteRequest, CancellationToken, Task<NativeReplayOverlayFrameWriteResult>> writeAsync,
        Func<long> getCurrentVersion,
        Action<Exception> currentWriteFailed,
        TimeSpan slowWriteThreshold,
        Action<NativeReplayOverlayFrameWriteRequest>? currentWriteSucceeded = null)
    {
        this.logger = logger;
        this.writeAsync = writeAsync;
        this.getCurrentVersion = getCurrentVersion;
        this.currentWriteFailed = currentWriteFailed;
        this.slowWriteThreshold = slowWriteThreshold;
        this.currentWriteSucceeded = currentWriteSucceeded;
    }

    public void QueueWrite(
        string pipeName,
        byte[] frame,
        long version,
        string frameKey = "",
        TimeSpan animationClock = default,
        bool hasAnimatedContent = false,
        TimeSpan? nextAnimationFrameDelay = null,
        TimeSpan renderDuration = default)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || frame.Length == 0)
        {
            return;
        }

        var request = new NativeReplayOverlayFrameWriteRequest(
            version,
            pipeName,
            frame,
            Interlocked.Increment(ref generation),
            frameKey,
            animationClock,
            hasAnimatedContent,
            nextAnimationFrameDelay,
            renderDuration);
        var startLoop = false;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (pendingWrite is not null)
            {
                CountDroppedWrite();
            }

            pendingWrite = request;
            if (!writeLoopRunning)
            {
                writeLoopRunning = true;
                startLoop = true;
            }
        }

        if (startLoop)
        {
            _ = RunWriteLoopAsync();
        }
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref generation);
        lock (gate)
        {
            if (pendingWrite is not null)
            {
                pendingWrite = null;
                CountDroppedWrite();
            }
        }
    }

    public void Dispose()
    {
        Interlocked.Increment(ref generation);
        lock (gate)
        {
            disposed = true;
            if (pendingWrite is not null)
            {
                pendingWrite = null;
                CountDroppedWrite();
            }
        }
    }

    private async Task RunWriteLoopAsync()
    {
        try
        {
            while (true)
            {
                NativeReplayOverlayFrameWriteRequest? request;
                lock (gate)
                {
                    request = pendingWrite;
                    pendingWrite = null;
                    if (request is null)
                    {
                        writeLoopRunning = false;
                        return;
                    }
                }

                if (!IsCurrent(request))
                {
                    CountDroppedWrite();
                    continue;
                }

                var stopwatch = Stopwatch.StartNew();
                NativeReplayOverlayFrameWriteResult result;
                try
                {
                    result = await writeAsync(request, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    result = new NativeReplayOverlayFrameWriteResult(false, ex);
                }

                stopwatch.Stop();
                LogWriteDiagnosticsIfUseful(stopwatch.Elapsed);

                if (result.LastException is not null && IsCurrent(request))
                {
                    currentWriteFailed(result.LastException);
                }
                else if (result.Sent && IsCurrent(request))
                {
                    currentWriteSucceeded?.Invoke(request);
                }
            }
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", "Native VLC replay overlay write gate failed.", ex);
            lock (gate)
            {
                writeLoopRunning = false;
            }
        }
    }

    private bool IsCurrent(NativeReplayOverlayFrameWriteRequest request)
    {
        lock (gate)
        {
            if (disposed)
            {
                return false;
            }
        }

        return request.Version == getCurrentVersion() &&
            request.Generation == Volatile.Read(ref generation);
    }

    private void CountDroppedWrite()
    {
        Interlocked.Increment(ref droppedWrites);
    }

    private void LogWriteDiagnosticsIfUseful(TimeSpan elapsed)
    {
        var dropped = Interlocked.Exchange(ref droppedWrites, 0);
        if (elapsed < slowWriteThreshold && dropped == 0)
        {
            return;
        }

        var droppedSuffix = dropped > 0
            ? $" Dropped {dropped} stale replay overlay frame write{(dropped == 1 ? "" : "s")}."
            : "";
        logger.Write(
            AppLogLevel.Debug,
            "ChatOverlay",
            $"Native VLC replay overlay pipe write took {elapsed.TotalMilliseconds:0} ms.{droppedSuffix}");
    }
}

internal sealed record NativeReplayOverlayFrameWriteRequest(
    long Version,
    string PipeName,
    byte[] Frame,
    long Generation,
    string FrameKey,
    TimeSpan AnimationClock,
    bool HasAnimatedContent,
    TimeSpan? NextAnimationFrameDelay,
    TimeSpan RenderDuration);

internal readonly record struct NativeReplayOverlayFrameWriteResult(
    bool Sent,
    Exception? LastException);

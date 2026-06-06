using System.Diagnostics;
using System.IO;
using System.Threading;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

internal sealed class NativeReplayOverlayFrameWriteGate : IDisposable
{
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(100);
    private const int DefaultMaxCurrentFrameRetries = 8;

    private readonly IAppLogger logger;
    private readonly Func<NativeReplayOverlayFrameWriteRequest, CancellationToken, Task<NativeReplayOverlayFrameWriteResult>> writeAsync;
    private readonly Func<long> getCurrentVersion;
    private readonly Func<string?>? getCurrentPipeName;
    private readonly Action<Exception> currentWriteFailed;
    private readonly Action<NativeReplayOverlayFrameWriteRequest>? currentWriteSucceeded;
    private readonly TimeSpan slowWriteThreshold;
    private readonly TimeSpan writeTimeout;
    private readonly TimeSpan retryDelay;
    private readonly int maxCurrentFrameRetries;
    private readonly object gate = new();
    private readonly Queue<NativeReplayOverlayFrameWriteRequest> pendingCriticalWrites = new();
    private NativeReplayOverlayFrameWriteRequest? pendingWrite;
    private bool writeLoopRunning;
    private bool disposed;
    private long generation;
    private long criticalGeneration;
    private long droppedWrites;

    public NativeReplayOverlayFrameWriteGate(
        IAppLogger logger,
        Func<NativeReplayOverlayFrameWriteRequest, CancellationToken, Task<NativeReplayOverlayFrameWriteResult>> writeAsync,
        Func<long> getCurrentVersion,
        Action<Exception> currentWriteFailed,
        TimeSpan slowWriteThreshold,
        Action<NativeReplayOverlayFrameWriteRequest>? currentWriteSucceeded = null,
        Func<string?>? getCurrentPipeName = null,
        TimeSpan? writeTimeout = null,
        int maxCurrentFrameRetries = DefaultMaxCurrentFrameRetries,
        TimeSpan? retryDelay = null)
    {
        this.logger = logger;
        this.writeAsync = writeAsync;
        this.getCurrentVersion = getCurrentVersion;
        this.getCurrentPipeName = getCurrentPipeName;
        this.currentWriteFailed = currentWriteFailed;
        this.slowWriteThreshold = slowWriteThreshold;
        this.currentWriteSucceeded = currentWriteSucceeded;
        this.writeTimeout = writeTimeout ?? TimeSpan.Zero;
        this.maxCurrentFrameRetries = Math.Max(0, maxCurrentFrameRetries);
        this.retryDelay = retryDelay is { } delay && delay >= TimeSpan.Zero
            ? delay
            : DefaultRetryDelay;
    }

    public void QueueWrite(
        string pipeName,
        byte[] frame,
        long version,
        string frameKey = "",
        TimeSpan animationClock = default,
        bool hasAnimatedContent = false,
        TimeSpan? nextAnimationFrameDelay = null,
        TimeSpan renderDuration = default,
        bool isCritical = false,
        string writeKind = "frame",
        string replaySessionKey = "")
    {
        if (string.IsNullOrWhiteSpace(pipeName) || frame.Length == 0)
        {
            return;
        }

        var request = new NativeReplayOverlayFrameWriteRequest(
            version,
            pipeName,
            frame,
            isCritical ? Volatile.Read(ref criticalGeneration) : Interlocked.Increment(ref generation),
            frameKey,
            animationClock,
            hasAnimatedContent,
            nextAnimationFrameDelay,
            renderDuration,
            isCritical,
            string.IsNullOrWhiteSpace(writeKind) ? "frame" : writeKind.Trim(),
            replaySessionKey.Trim(),
            RetryCount: 0);
        var startLoop = false;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (isCritical)
            {
                pendingCriticalWrites.Enqueue(request);
            }
            else
            {
                if (pendingWrite is not null)
                {
                    CountDroppedWrite();
                }

                pendingWrite = request;
            }

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

    public void Invalidate(bool includeCritical = false)
    {
        Interlocked.Increment(ref generation);
        if (includeCritical)
        {
            Interlocked.Increment(ref criticalGeneration);
        }

        lock (gate)
        {
            if (pendingWrite is not null)
            {
                pendingWrite = null;
                CountDroppedWrite();
            }

            if (includeCritical)
            {
                while (pendingCriticalWrites.Count > 0)
                {
                    pendingCriticalWrites.Dequeue();
                    CountDroppedWrite();
                }
            }
        }
    }

    public void Dispose()
    {
        Interlocked.Increment(ref generation);
        Interlocked.Increment(ref criticalGeneration);
        lock (gate)
        {
            disposed = true;
            if (pendingWrite is not null)
            {
                pendingWrite = null;
                CountDroppedWrite();
            }

            while (pendingCriticalWrites.Count > 0)
            {
                pendingCriticalWrites.Dequeue();
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
                    request = TakeNextWriteLocked();
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

                if (!result.Sent && ShouldRetry(request, result.LastException))
                {
                    LogRetryableWriteFailure(request, result.LastException);
                    var retryRequest = request with { RetryCount = request.RetryCount + 1 };
                    await Task.Delay(retryDelay).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (!disposed && IsCurrentLocked(retryRequest))
                        {
                            if (retryRequest.IsCritical)
                            {
                                pendingCriticalWrites.Enqueue(retryRequest);
                            }
                            else
                            {
                                pendingWrite = retryRequest;
                            }
                        }
                    }
                }
                else if (result.LastException is not null && IsCurrent(request))
                {
                    LogFinalWriteFailure(request, result.LastException);
                    currentWriteFailed(result.LastException);
                }
                else if (!result.Sent && IsCurrent(request))
                {
                    var exception = new IOException("Native VLC replay overlay pipe write was not accepted.");
                    LogFinalWriteFailure(request, exception);
                    currentWriteFailed(exception);
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

    private NativeReplayOverlayFrameWriteRequest? TakeNextWriteLocked()
    {
        if (pendingCriticalWrites.Count > 0)
        {
            return pendingCriticalWrites.Dequeue();
        }

        var request = pendingWrite;
        pendingWrite = null;
        return request;
    }

    private bool IsCurrent(NativeReplayOverlayFrameWriteRequest request)
    {
        lock (gate)
        {
            return IsCurrentLocked(request);
        }
    }

    private bool IsCurrentLocked(NativeReplayOverlayFrameWriteRequest request)
    {
        if (disposed)
        {
            return false;
        }

        if (!request.IsCritical)
        {
            return request.Version == getCurrentVersion() &&
                request.Generation == Volatile.Read(ref generation);
        }

        if (request.Generation != Volatile.Read(ref criticalGeneration))
        {
            return false;
        }

        if (!IsPersistentCriticalClear(request) &&
            request.Version != getCurrentVersion())
        {
            return false;
        }

        if (getCurrentPipeName is not { } getPipeName)
        {
            return request.Version == getCurrentVersion();
        }

        return string.Equals(request.PipeName, getPipeName(), StringComparison.Ordinal);
    }

    private static bool IsPersistentCriticalClear(NativeReplayOverlayFrameWriteRequest request)
    {
        return string.Equals(request.WriteKind, "critical-clear", StringComparison.Ordinal);
    }

    private bool ShouldRetry(NativeReplayOverlayFrameWriteRequest request, Exception? exception)
    {
        if (!IsRetryableWriteFailure(exception) ||
            !IsCurrent(request))
        {
            return false;
        }

        return request.IsCritical ||
            request.RetryCount < maxCurrentFrameRetries;
    }

    private static bool IsRetryableWriteFailure(Exception? exception)
    {
        return exception is null or TimeoutException or IOException or UnauthorizedAccessException;
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

    private void LogRetryableWriteFailure(NativeReplayOverlayFrameWriteRequest request, Exception? exception)
    {
        logger.Write(
            AppLogLevel.Debug,
            "ChatOverlay",
            BuildWriteFailureMessage(
                request,
                $"Native VLC replay overlay write failed; retrying in {retryDelay.TotalMilliseconds:0} ms."),
            exception);
    }

    private void LogFinalWriteFailure(NativeReplayOverlayFrameWriteRequest request, Exception exception)
    {
        logger.Write(
            AppLogLevel.Warning,
            "ChatOverlay",
            BuildWriteFailureMessage(
                request,
                "Native VLC replay overlay write failed."),
            exception);
    }

    private string BuildWriteFailureMessage(NativeReplayOverlayFrameWriteRequest request, string prefix)
    {
        return string.Join(
            " ",
            prefix,
            $"kind={request.WriteKind};",
            $"session={FormatDiagnosticValue(request.ReplaySessionKey)};",
            $"pipe={FormatDiagnosticValue(request.PipeName)};",
            $"timeoutMs={FormatTimeout(writeTimeout)};",
            $"retry={request.RetryCount}/{(request.IsCritical ? "unbounded" : maxCurrentFrameRetries.ToString())};",
            $"latestCurrent={IsCurrent(request).ToString().ToLowerInvariant()}");
    }

    private static string FormatDiagnosticValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }

    private static string FormatTimeout(TimeSpan timeout)
    {
        return timeout > TimeSpan.Zero
            ? timeout.TotalMilliseconds.ToString("0")
            : "(unknown)";
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
    TimeSpan RenderDuration,
    bool IsCritical,
    string WriteKind,
    string ReplaySessionKey,
    int RetryCount);

internal readonly record struct NativeReplayOverlayFrameWriteResult(
    bool Sent,
    Exception? LastException);

using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

internal sealed class NativeOverlayReplayEventHost : IAsyncDisposable
{
    private static readonly TimeSpan DefaultResizeDebounceDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PipeBusyRetryDelay = TimeSpan.FromMilliseconds(50);

    private const uint NativeOverlayMagic = 0x564C4F56u;
    private const uint NativeOverlayVersion = 1u;
    private const uint NativeOverlayResizeEventType = 3u;
    private const int NativeOverlayEventMessageSize = 16;
    private const int ErrorPipeBusy = 231;

    private readonly IAppLogger logger;
    private readonly Action<Action> dispatch;
    private readonly Action replayFrameInvalidated;
    private readonly Func<int> getVideoHeight;
    private readonly TimeSpan resizeDebounceDelay;
    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    private Task? listeningTask;
    private string? pipeName;
    private string? positionStatePath;
    private bool stopRequested;
    private Timer? resizeDebounceTimer;
    private ResizeFlush pendingResizeFlush;
    private ResizeFlush lastResizeFlush;
    private bool hasPendingResizeFlush;
    private bool hasLastResizeFlush;
    private bool resizePersistenceSuspended = true;
    private long resizePersistenceGeneration;
    private long resizeSessionId;

    public NativeOverlayReplayEventHost(
        IAppLogger logger,
        Action<Action> dispatch,
        Action replayFrameInvalidated,
        Func<int> getVideoHeight,
        TimeSpan? resizeDebounceDelay = null)
    {
        this.logger = logger;
        this.dispatch = dispatch;
        this.replayFrameInvalidated = replayFrameInvalidated;
        this.getVideoHeight = getVideoHeight;
        this.resizeDebounceDelay = resizeDebounceDelay ?? DefaultResizeDebounceDelay;
        if (this.resizeDebounceDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(resizeDebounceDelay));
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return listeningTask is not null && !stopRequested;
            }
        }
    }

    public string? PipeName
    {
        get
        {
            lock (gate)
            {
                return pipeName;
            }
        }
    }

    public void Start(string activePipeName, string activePositionStatePath)
    {
        if (string.IsNullOrWhiteSpace(activePipeName) ||
            string.IsNullOrWhiteSpace(activePositionStatePath))
        {
            Stop();
            return;
        }

        lock (gate)
        {
            if (listeningTask is not null &&
                !stopRequested &&
                string.Equals(pipeName, activePipeName, StringComparison.Ordinal) &&
                string.Equals(positionStatePath, activePositionStatePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        Stop();

        var nextCancellation = new CancellationTokenSource();
        long activeResizeSessionId;
        lock (gate)
        {
            activeResizeSessionId = NextResizeSessionIdLocked();
            resizePersistenceSuspended = true;
            resizePersistenceGeneration++;
            cancellation = nextCancellation;
            pipeName = activePipeName;
            positionStatePath = activePositionStatePath;
            stopRequested = false;
            listeningTask = Task.Run(() => ListenAsync(
                activePipeName,
                activePositionStatePath,
                nextCancellation,
                activeResizeSessionId));
        }
    }

    public void SuspendResizePersistence()
    {
        Timer? resizeTimerToDispose;
        lock (gate)
        {
            resizePersistenceSuspended = true;
            resizePersistenceGeneration++;
            resizeTimerToDispose = ClearResizeFlushStateLocked();
        }

        resizeTimerToDispose?.Dispose();
    }

    public void ResumeResizePersistence()
    {
        lock (gate)
        {
            if (listeningTask is null ||
                stopRequested ||
                string.IsNullOrWhiteSpace(positionStatePath))
            {
                return;
            }

            resizePersistenceSuspended = false;
            resizePersistenceGeneration++;
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cancellationToStop;
        Timer? resizeTimerToDispose;
        lock (gate)
        {
            cancellationToStop = cancellation;
            resizeTimerToDispose = ClearResizeFlushStateLocked();
            NextResizeSessionIdLocked();
            resizePersistenceSuspended = true;
            resizePersistenceGeneration++;
            pipeName = null;
            positionStatePath = null;
            stopRequested = cancellationToStop is not null;
        }

        resizeTimerToDispose?.Dispose();
        if (cancellationToStop is not null)
        {
            cancellationToStop.Cancel();
        }
    }

    public async Task StopAsync()
    {
        Task? taskToStop;
        CancellationTokenSource? cancellationToStop;
        Timer? resizeTimerToDispose;
        lock (gate)
        {
            taskToStop = listeningTask;
            cancellationToStop = cancellation;
            resizeTimerToDispose = ClearResizeFlushStateLocked();
            NextResizeSessionIdLocked();
            resizePersistenceSuspended = true;
            resizePersistenceGeneration++;
            pipeName = null;
            positionStatePath = null;
            stopRequested = cancellationToStop is not null;
        }

        resizeTimerToDispose?.Dispose();
        if (cancellationToStop is null)
        {
            return;
        }

        try
        {
            cancellationToStop.Cancel();
            if (taskToStop is not null)
            {
                await taskToStop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task ListenAsync(
        string activePipeName,
        string activePositionStatePath,
        CancellationTokenSource activeCancellation,
        long activeResizeSessionId)
    {
        var cancellationToken = activeCancellation.Token;
        var eventPipeName = $"{activePipeName}_events";
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(
                        eventPipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await ReadMessagesAsync(
                        pipe,
                        activePositionStatePath,
                        activeResizeSessionId,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex) when (!cancellationToken.IsCancellationRequested && IsAllPipeInstancesBusy(ex))
                {
                    logger.Write(AppLogLevel.Debug, "ChatOverlay", "Native VLC replay overlay event pipe was busy; retrying listener start.", ex);
                    await Task.Delay(PipeBusyRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException ex) when (cancellationToken.IsCancellationRequested)
        {
            logger.Write(AppLogLevel.Info, "ChatOverlay", "Native VLC replay overlay event listener stopped.", ex);
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", "Native VLC replay overlay event listener stopped unexpectedly.", ex);
        }
        finally
        {
            Timer? resizeTimerToDispose = null;
            lock (gate)
            {
                if (ReferenceEquals(cancellation, activeCancellation))
                {
                    resizeTimerToDispose = ClearResizeFlushStateLocked();
                    NextResizeSessionIdLocked();
                    resizePersistenceSuspended = true;
                    resizePersistenceGeneration++;
                    cancellation = null;
                    listeningTask = null;
                    pipeName = null;
                    positionStatePath = null;
                    stopRequested = false;
                }
            }

            resizeTimerToDispose?.Dispose();
            activeCancellation.Dispose();
        }
    }

    private static bool IsAllPipeInstancesBusy(IOException exception)
    {
        return (exception.HResult & 0xFFFF) == ErrorPipeBusy ||
            exception.Message.Contains("All pipe instances are busy", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ReadMessagesAsync(
        PipeStream pipe,
        string activePositionStatePath,
        long activeResizeSessionId,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[NativeOverlayEventMessageSize];
        while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
        {
            var read = await ReadExactlyOrEndAsync(pipe, buffer, cancellationToken).ConfigureAwait(false);
            if (!read)
            {
                return;
            }

            HandleEvent(buffer, activePositionStatePath, activeResizeSessionId);
        }
    }

    private static async Task<bool> ReadExactlyOrEndAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                    buffer.AsMemory(offset, buffer.Length - offset),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private void HandleEvent(
        ReadOnlySpan<byte> message,
        string activePositionStatePath,
        long activeResizeSessionId)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(message[..4]) != NativeOverlayMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(4, 4)) != NativeOverlayVersion ||
            BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(8, 4)) != NativeOverlayResizeEventType)
        {
            return;
        }

        var packedSize = BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(12, 4));
        var sourceWidth = (int)((packedSize >> 16) & 0xFFFFu);
        var sourceHeight = (int)(packedSize & 0xFFFFu);
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return;
        }

        var videoHeight = getVideoHeight();
        var minimumWidth = NativeOverlaySizing.ScaleReferencePixels(videoHeight, NativeOverlaySizing.MinWidth);
        var minimumHeight = NativeOverlaySizing.ScaleReferencePixels(videoHeight, NativeOverlaySizing.MinHeight);
        if (sourceWidth < minimumWidth || sourceHeight < minimumHeight)
        {
            return;
        }

        var (referenceWidth, referenceHeight) = NativeOverlaySizing.NormalizeToReferenceSize(
            sourceWidth,
            sourceHeight,
            videoHeight);

        QueueResizeFlush(activePositionStatePath, activeResizeSessionId, referenceWidth, referenceHeight);
    }

    private void QueueResizeFlush(
        string activePositionStatePath,
        long activeResizeSessionId,
        int referenceWidth,
        int referenceHeight)
    {
        var persistenceGeneration = 0L;
        lock (gate)
        {
            if (!IsResizePersistenceActiveLocked(activePositionStatePath, activeResizeSessionId))
            {
                return;
            }

            persistenceGeneration = resizePersistenceGeneration;
            pendingResizeFlush = new ResizeFlush(
                activeResizeSessionId,
                persistenceGeneration,
                activePositionStatePath,
                referenceWidth,
                referenceHeight);
            hasPendingResizeFlush = true;
            resizeDebounceTimer ??= new Timer(FlushPendingResize, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            resizeDebounceTimer.Change(resizeDebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void FlushPendingResize(object? state)
    {
        ResizeFlush flush;
        lock (gate)
        {
            if (!hasPendingResizeFlush)
            {
                return;
            }

            flush = pendingResizeFlush;
            hasPendingResizeFlush = false;
            pendingResizeFlush = default;
            if (!IsResizePersistenceActiveLocked(flush.PositionStatePath, flush.SessionId, flush.PersistenceGeneration) ||
                IsDuplicateResizeFlushLocked(flush))
            {
                return;
            }

            if (!TrySaveResizeFlush(flush))
            {
                return;
            }

            lastResizeFlush = flush;
            hasLastResizeFlush = true;
        }

        dispatch(() =>
        {
            if (IsResizeSessionActive(flush.PositionStatePath, flush.SessionId))
            {
                replayFrameInvalidated();
            }
        });
    }

    private bool TrySaveResizeFlush(ResizeFlush flush)
    {
        try
        {
            File.WriteAllText(
                $"{flush.PositionStatePath}.size",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "reference {0} {1}",
                    flush.ReferenceWidth,
                    flush.ReferenceHeight));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", "Could not save native VLC replay overlay size.", ex);
            return false;
        }
    }

    private bool IsResizeSessionActive(string activePositionStatePath, long activeResizeSessionId)
    {
        lock (gate)
        {
            return IsResizeSessionActiveLocked(activePositionStatePath, activeResizeSessionId);
        }
    }

    private bool IsResizeSessionActiveLocked(string activePositionStatePath, long activeResizeSessionId)
    {
        return activeResizeSessionId == resizeSessionId &&
            !stopRequested &&
            string.Equals(positionStatePath, activePositionStatePath, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsResizePersistenceActiveLocked(string activePositionStatePath, long activeResizeSessionId)
    {
        return IsResizeSessionActiveLocked(activePositionStatePath, activeResizeSessionId) &&
            !resizePersistenceSuspended;
    }

    private bool IsResizePersistenceActiveLocked(
        string activePositionStatePath,
        long activeResizeSessionId,
        long activeResizePersistenceGeneration)
    {
        return IsResizePersistenceActiveLocked(activePositionStatePath, activeResizeSessionId) &&
            activeResizePersistenceGeneration == resizePersistenceGeneration;
    }

    private bool IsDuplicateResizeFlushLocked(ResizeFlush flush)
    {
        return hasLastResizeFlush &&
            lastResizeFlush.ReferenceWidth == flush.ReferenceWidth &&
            lastResizeFlush.ReferenceHeight == flush.ReferenceHeight &&
            string.Equals(lastResizeFlush.PositionStatePath, flush.PositionStatePath, StringComparison.OrdinalIgnoreCase);
    }

    private Timer? ClearResizeFlushStateLocked()
    {
        var timer = resizeDebounceTimer;
        resizeDebounceTimer = null;
        pendingResizeFlush = default;
        lastResizeFlush = default;
        hasPendingResizeFlush = false;
        hasLastResizeFlush = false;
        return timer;
    }

    private long NextResizeSessionIdLocked()
    {
        unchecked
        {
            resizeSessionId++;
        }

        return resizeSessionId;
    }

    private readonly record struct ResizeFlush(
        long SessionId,
        long PersistenceGeneration,
        string PositionStatePath,
        int ReferenceWidth,
        int ReferenceHeight);
}

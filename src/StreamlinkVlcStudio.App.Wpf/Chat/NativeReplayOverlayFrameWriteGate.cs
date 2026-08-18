using System.Diagnostics;
using System.IO;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

internal sealed class NativeReplayOverlayFrameWriteGate : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(2);
    private const int DefaultMaxCurrentFrameRetries = 8;
    private const long DefaultMaximumQueuedBytes = 64L * 1024 * 1024;
    private const long DefaultReservedCriticalBytes = 64L * 1024;

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
    private readonly long maximumQueuedBytes;
    private readonly long reservedCriticalBytes;
    private readonly bool validateProtocolMessages;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly LinkedList<NativeReplayOverlayFrameWriteRequest> pendingCriticalWrites = new();
    private readonly Dictionary<string, LinkedListNode<NativeReplayOverlayFrameWriteRequest>> pendingCriticalByKey =
        new(StringComparer.Ordinal);
    private readonly LinkedList<NativeReplayOverlayFrameWriteRequest> parkedWrites = new();
    private readonly Dictionary<string, LinkedListNode<NativeReplayOverlayFrameWriteRequest>> parkedByKey =
        new(StringComparer.Ordinal);
    private NativeReplayOverlayFrameWriteRequest? pendingWrite;
    private NativeReplayOverlayFrameWriteRequest? activeWriteRequest;
    private CancellationTokenSource? activeWriteCancellation;
    private bool writeLoopRunning;
    private bool disposed;
    private Task? writeLoopTask;
    private Task? disposalTask;
    private long generation;
    private long criticalGeneration;
    private long emptyCriticalGeneration;
    private long persistentCriticalClearGeneration;
    private long sequence;
    private long queuedBytes;
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
        TimeSpan? retryDelay = null,
        long maximumQueuedBytes = DefaultMaximumQueuedBytes,
        long? reservedCriticalBytes = null,
        bool validateProtocolMessages = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumQueuedBytes);
        var effectiveReservedCriticalBytes = reservedCriticalBytes ??
            Math.Min(DefaultReservedCriticalBytes, maximumQueuedBytes / 2);
        ArgumentOutOfRangeException.ThrowIfNegative(effectiveReservedCriticalBytes);
        if (effectiveReservedCriticalBytes >= maximumQueuedBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(reservedCriticalBytes));
        }
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
        this.maximumQueuedBytes = maximumQueuedBytes;
        this.reservedCriticalBytes = effectiveReservedCriticalBytes;
        this.validateProtocolMessages = validateProtocolMessages;
    }

    internal long QueuedByteCount
    {
        get
        {
            lock (gate)
            {
                return queuedBytes;
            }
        }
    }

    internal int ParkedWriteCount
    {
        get
        {
            lock (gate)
            {
                return parkedWrites.Count;
            }
        }
    }

    internal int PendingCriticalWriteCount
    {
        get
        {
            lock (gate)
            {
                return pendingCriticalWrites.Count;
            }
        }
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
        string replaySessionKey = "",
        byte[]? followupFrame = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            LogInvalidRequest("pipe name is empty");
            return;
        }

        if (validateProtocolMessages &&
            !NativeOverlayProtocolCodec.TryValidateEncodedMessage(frame, out var invalidReason))
        {
            LogInvalidRequest(invalidReason);
            return;
        }

        if (validateProtocolMessages &&
            followupFrame is not null &&
            !NativeOverlayProtocolCodec.TryValidateEncodedMessage(followupFrame, out invalidReason))
        {
            LogInvalidRequest($"follow-up {invalidReason}");
            return;
        }

        var normalizedWriteKind = string.IsNullOrWhiteSpace(writeKind) ? "frame" : writeKind.Trim();
        var requestGeneration = isCritical
            ? IsPersistentCriticalClear(normalizedWriteKind)
                ? Volatile.Read(ref persistentCriticalClearGeneration)
                : IsEmptyCriticalWrite(normalizedWriteKind)
                    ? Volatile.Read(ref emptyCriticalGeneration)
                    : Volatile.Read(ref criticalGeneration)
            : Interlocked.Increment(ref generation);
        var request = new NativeReplayOverlayFrameWriteRequest(
            version,
            pipeName,
            frame,
            requestGeneration,
            Interlocked.Increment(ref sequence),
            frameKey,
            animationClock,
            hasAnimatedContent,
            nextAnimationFrameDelay,
            renderDuration,
            isCritical,
            normalizedWriteKind,
            replaySessionKey.Trim(),
            followupFrame,
            RetryCount: 0);
        var requestLimit = isCritical
            ? maximumQueuedBytes
            : maximumQueuedBytes - reservedCriticalBytes;
        if (GetRequestByteCount(request) > requestLimit)
        {
            CountDroppedWrite();
            LogInvalidRequest("request exceeds the bounded write-queue admission limit");
            return;
        }

        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            var semanticKey = GetSemanticKey(request);
            if (parkedByKey.TryGetValue(semanticKey, out var parkedNode))
            {
                ReplaceParkedWriteLocked(parkedNode, request);
                TrimQueuedBytesLocked(request);
                StartWriteLoopLocked();
                return;
            }

            QueuePendingWriteLocked(request, preferExisting: false);
            TrimQueuedBytesLocked(request);
            StartWriteLoopLocked();
        }
    }

    public void NotifyReconnected(string? pipeName = null)
    {
        lock (gate)
        {
            if (disposed || parkedWrites.Count == 0)
            {
                return;
            }

            var node = parkedWrites.First;
            while (node is not null)
            {
                var next = node.Next;
                var request = node.Value;
                if (string.IsNullOrWhiteSpace(pipeName) ||
                    string.Equals(request.PipeName, pipeName, StringComparison.Ordinal))
                {
                    RemoveParkedNodeLocked(node, countAsDropped: false);
                    if (IsCurrentLocked(request))
                    {
                        QueuePendingWriteLocked(request with { RetryCount = 0 }, preferExisting: true);
                    }
                    else
                    {
                        CountDroppedWrite();
                    }
                }

                node = next;
            }

            TrimQueuedBytesLocked(protectedRequest: null);
            StartWriteLoopLocked();
        }
    }

    public void Invalidate(bool includeCritical = false)
    {
        Interlocked.Increment(ref generation);
        if (includeCritical)
        {
            Interlocked.Increment(ref criticalGeneration);
            Interlocked.Increment(ref emptyCriticalGeneration);
            Interlocked.Increment(ref persistentCriticalClearGeneration);
        }

        CancellationTokenSource? activeCancellation = null;
        lock (gate)
        {
            RemovePendingWriteLocked(countAsDropped: true);
            if (includeCritical)
            {
                ClearPendingCriticalWritesLocked();
                ClearParkedWritesLocked();
            }

            // A persistent clear survives ordinary render invalidation, but every other active
            // write is stale and must yield to the replacement frame.
            if (includeCritical ||
                activeWriteRequest is not { } activeRequest ||
                !IsPersistentCriticalClear(activeRequest))
            {
                activeCancellation = activeWriteCancellation;
            }
        }

        CancelActiveWrite(activeCancellation);
    }

    // A loaded chat frame supersedes both the startup clear and renderer-produced blank frames.
    public void SupersedePersistentCriticalClears()
    {
        CancellationTokenSource? activeCancellation = null;
        lock (gate)
        {
            var activeIsSupersedableEmptyWrite = activeWriteRequest is { } activeRequest &&
                IsEmptyCriticalWrite(activeRequest);
            RemoveMatchingPendingCriticalWritesLocked(IsEmptyCriticalWrite);
            RemoveMatchingParkedWritesLocked(IsEmptyCriticalWrite);

            Interlocked.Increment(ref persistentCriticalClearGeneration);
            Interlocked.Increment(ref emptyCriticalGeneration);
            if (activeIsSupersedableEmptyWrite)
            {
                activeCancellation = activeWriteCancellation;
            }
        }

        CancelActiveWrite(activeCancellation);
    }

    public void Dispose()
    {
        _ = BeginDisposeAsync();
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(BeginDisposeAsync());
    }

    private Task BeginDisposeAsync()
    {
        Task? activeLoop;
        CancellationTokenSource? activeCancellation;
        TaskCompletionSource completion;
        lock (gate)
        {
            if (disposalTask is not null)
            {
                return disposalTask;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disposalTask = completion.Task;
            disposed = true;
            activeLoop = writeLoopTask;
            activeCancellation = activeWriteCancellation;
            RemovePendingWriteLocked(countAsDropped: true);
            ClearPendingCriticalWritesLocked();
            ClearParkedWritesLocked();
        }

        Interlocked.Increment(ref generation);
        Interlocked.Increment(ref criticalGeneration);
        Interlocked.Increment(ref emptyCriticalGeneration);
        Interlocked.Increment(ref persistentCriticalClearGeneration);
        lifetimeCancellation.Cancel();
        CancelActiveWrite(activeCancellation);
        _ = CompleteDisposalAsync(activeLoop, completion);
        return completion.Task;
    }

    private async Task CompleteDisposalAsync(Task? activeLoop, TaskCompletionSource completion)
    {
        try
        {
            if (activeLoop is not null)
            {
                await activeLoop.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            SafeLog(AppLogLevel.Debug, "Native VLC replay overlay write-loop disposal failed.", ex);
        }
        finally
        {
            lifetimeCancellation.Dispose();
            completion.TrySetResult();
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
                var writeWasCanceled = false;
                var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
                lock (gate)
                {
                    if (disposed)
                    {
                        writeCancellation.Dispose();
                        CountDroppedWrite();
                        continue;
                    }

                    activeWriteRequest = request;
                    activeWriteCancellation = writeCancellation;
                }

                try
                {
                    result = await writeAsync(request, writeCancellation.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    result = new NativeReplayOverlayFrameWriteResult(false, ex);
                }
                finally
                {
                    writeWasCanceled = writeCancellation.IsCancellationRequested;
                    lock (gate)
                    {
                        if (ReferenceEquals(activeWriteCancellation, writeCancellation))
                        {
                            activeWriteRequest = null;
                            activeWriteCancellation = null;
                        }
                    }

                    writeCancellation.Dispose();
                }

                stopwatch.Stop();
                LogWriteDiagnosticsIfUseful(stopwatch.Elapsed);

                if (writeWasCanceled || !IsCurrent(request))
                {
                    continue;
                }

                if (result.Sent)
                {
                    InvokeSucceededSafely(request);
                    HandleSuccessfulReconnect(request);
                    continue;
                }

                if (IsRetryableWriteFailure(result.LastException))
                {
                    if (request.RetryCount < maxCurrentFrameRetries)
                    {
                        var delay = CalculateRetryDelay(request.RetryCount);
                        LogRetryableWriteFailure(request, result.LastException, delay);
                        var retryRequest = request with { RetryCount = request.RetryCount + 1 };
                        try
                        {
                            await Task.Delay(delay, lifetimeCancellation.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
                        {
                            continue;
                        }

                        lock (gate)
                        {
                            if (!disposed && IsCurrentLocked(retryRequest))
                            {
                                QueuePendingWriteLocked(retryRequest, preferExisting: true);
                                TrimQueuedBytesLocked(retryRequest);
                            }
                        }

                        continue;
                    }

                    var parked = false;
                    lock (gate)
                    {
                        if (!disposed && IsCurrentLocked(request))
                        {
                            parked = ParkWriteLocked(request);
                            TrimQueuedBytesLocked(request);
                        }
                    }

                    if (parked)
                    {
                        LogParkedWriteFailure(request, result.LastException);
                    }

                    continue;
                }

                var exception = result.LastException ??
                    new IOException("Native VLC replay overlay pipe write was not accepted.");
                LogFinalWriteFailure(request, exception);
                InvokeFailedSafely(exception);
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SafeLog(AppLogLevel.Warning, "Native VLC replay overlay write gate failed.", ex);
        }
        finally
        {
            lock (gate)
            {
                writeLoopRunning = false;
                writeLoopTask = null;
                StartWriteLoopLocked();
            }
        }
    }

    private void HandleSuccessfulReconnect(NativeReplayOverlayFrameWriteRequest successfulRequest)
    {
        lock (gate)
        {
            var successfulKey = GetSemanticKey(successfulRequest);
            var node = parkedWrites.First;
            while (node is not null)
            {
                var next = node.Next;
                var parked = node.Value;
                if (string.Equals(parked.PipeName, successfulRequest.PipeName, StringComparison.Ordinal))
                {
                    RemoveParkedNodeLocked(node, countAsDropped: false);
                    if (string.Equals(GetSemanticKey(parked), successfulKey, StringComparison.Ordinal) ||
                        (IsLoadedContentWrite(successfulRequest) && IsEmptyCriticalWrite(parked)) ||
                        !IsCurrentLocked(parked))
                    {
                        CountDroppedWrite();
                    }
                    else
                    {
                        QueuePendingWriteLocked(parked with { RetryCount = 0 }, preferExisting: true);
                    }
                }

                node = next;
            }

            TrimQueuedBytesLocked(protectedRequest: null);
        }
    }

    private static void CancelActiveWrite(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void StartWriteLoopLocked()
    {
        if (disposed || writeLoopRunning || !HasPendingWritesLocked())
        {
            return;
        }

        writeLoopRunning = true;
        writeLoopTask = Task.Run(RunWriteLoopAsync);
    }

    private bool HasPendingWritesLocked() => pendingCriticalWrites.Count > 0 || pendingWrite is not null;

    private NativeReplayOverlayFrameWriteRequest? TakeNextWriteLocked()
    {
        if (pendingCriticalWrites.First is { } criticalNode)
        {
            var request = criticalNode.Value;
            RemovePendingCriticalNodeLocked(criticalNode, countAsDropped: false);
            return request;
        }

        var pending = pendingWrite;
        RemovePendingWriteLocked(countAsDropped: false);
        return pending;
    }

    private void QueuePendingWriteLocked(
        NativeReplayOverlayFrameWriteRequest request,
        bool preferExisting)
    {
        var requestLimit = request.IsCritical
            ? maximumQueuedBytes
            : maximumQueuedBytes - reservedCriticalBytes;
        if (GetRequestByteCount(request) > requestLimit)
        {
            CountDroppedWrite();
            return;
        }

        if (!request.IsCritical)
        {
            if (pendingWrite is { } existing)
            {
                if (preferExisting && existing.Sequence > request.Sequence)
                {
                    CountDroppedWrite();
                    return;
                }

                RemovePendingWriteLocked(countAsDropped: true);
            }

            pendingWrite = request;
            queuedBytes += GetRequestByteCount(request);
            return;
        }

        var semanticKey = GetSemanticKey(request);
        if (pendingCriticalByKey.TryGetValue(semanticKey, out var existingNode))
        {
            if (preferExisting && existingNode.Value.Sequence > request.Sequence)
            {
                CountDroppedWrite();
                return;
            }

            queuedBytes -= GetRequestByteCount(existingNode.Value);
            CountDroppedWrite();
            existingNode.Value = request;
            queuedBytes += GetRequestByteCount(request);
            pendingCriticalWrites.Remove(existingNode);
            pendingCriticalWrites.AddLast(existingNode);
            return;
        }

        var node = pendingCriticalWrites.AddLast(request);
        pendingCriticalByKey[semanticKey] = node;
        queuedBytes += GetRequestByteCount(request);
    }

    private bool ParkWriteLocked(NativeReplayOverlayFrameWriteRequest request)
    {
        var requestLimit = request.IsCritical
            ? maximumQueuedBytes
            : maximumQueuedBytes - reservedCriticalBytes;
        if (GetRequestByteCount(request) > requestLimit)
        {
            CountDroppedWrite();
            return false;
        }

        var semanticKey = GetSemanticKey(request);
        if (HasNewerPendingSemanticWriteLocked(request, semanticKey))
        {
            CountDroppedWrite();
            return false;
        }

        if (parkedByKey.TryGetValue(semanticKey, out var existingNode))
        {
            if (existingNode.Value.Sequence > request.Sequence)
            {
                CountDroppedWrite();
                return false;
            }

            ReplaceParkedWriteLocked(existingNode, request);
            return true;
        }

        var node = parkedWrites.AddLast(request);
        parkedByKey[semanticKey] = node;
        queuedBytes += GetRequestByteCount(request);
        return true;
    }

    private bool HasNewerPendingSemanticWriteLocked(
        NativeReplayOverlayFrameWriteRequest request,
        string semanticKey)
    {
        if (request.IsCritical)
        {
            return pendingCriticalByKey.TryGetValue(semanticKey, out var node) &&
                node.Value.Sequence > request.Sequence;
        }

        return pendingWrite is { } pending && pending.Sequence > request.Sequence;
    }

    private void ReplaceParkedWriteLocked(
        LinkedListNode<NativeReplayOverlayFrameWriteRequest> node,
        NativeReplayOverlayFrameWriteRequest request)
    {
        queuedBytes -= GetRequestByteCount(node.Value);
        CountDroppedWrite();
        node.Value = request;
        queuedBytes += GetRequestByteCount(request);
        parkedWrites.Remove(node);
        parkedWrites.AddLast(node);
    }

    private void TrimQueuedBytesLocked(NativeReplayOverlayFrameWriteRequest? protectedRequest)
    {
        var nonCriticalLimit = maximumQueuedBytes - reservedCriticalBytes;
        while (GetNonCriticalQueuedBytesLocked() > nonCriticalLimit)
        {
            var parkedFrame = FindUnprotectedNode(
                parkedWrites,
                protectedRequest,
                static request => !request.IsCritical);
            if (parkedFrame is not null)
            {
                RemoveParkedNodeLocked(parkedFrame, countAsDropped: true);
                continue;
            }

            if (pendingWrite is not null && !ReferenceEquals(pendingWrite, protectedRequest))
            {
                RemovePendingWriteLocked(countAsDropped: true);
                continue;
            }

            if (protectedRequest is { IsCritical: false } &&
                RemoveSpecificQueuedWriteLocked(protectedRequest))
            {
                CountDroppedWrite();
                SafeLog(
                    AppLogLevel.Warning,
                    "A valid native-overlay frame could not be admitted because the reserved control capacity was exhausted.");
                continue;
            }

            break;
        }

        while (queuedBytes > maximumQueuedBytes)
        {
            // Critical clear/control work gets first claim on capacity. Evict stale coalescible
            // frame state before considering any queued critical request.
            var parkedFrame = FindUnprotectedNode(
                parkedWrites,
                protectedRequest,
                static request => !request.IsCritical);
            if (parkedFrame is not null)
            {
                RemoveParkedNodeLocked(parkedFrame, countAsDropped: true);
                continue;
            }

            if (pendingWrite is not null && !ReferenceEquals(pendingWrite, protectedRequest))
            {
                RemovePendingWriteLocked(countAsDropped: true);
                continue;
            }

            var parkedCritical = FindUnprotectedNode(
                parkedWrites,
                protectedRequest,
                static request => request.IsCritical);
            if (parkedCritical is not null)
            {
                RemoveParkedNodeLocked(parkedCritical, countAsDropped: true);
                continue;
            }

            var critical = FindUnprotectedNode(
                pendingCriticalWrites,
                protectedRequest,
                static request => request.IsCritical);
            if (critical is not null)
            {
                RemovePendingCriticalNodeLocked(critical, countAsDropped: true);
                continue;
            }

            if (protectedRequest is not null && RemoveSpecificQueuedWriteLocked(protectedRequest))
            {
                CountDroppedWrite();
                SafeLog(
                    AppLogLevel.Warning,
                    "A valid native-overlay request could not be retained within the 64 MiB write queue.");
                continue;
            }

            break;
        }
    }

    private static LinkedListNode<NativeReplayOverlayFrameWriteRequest>? FindUnprotectedNode(
        LinkedList<NativeReplayOverlayFrameWriteRequest> writes,
        NativeReplayOverlayFrameWriteRequest? protectedRequest,
        Func<NativeReplayOverlayFrameWriteRequest, bool> predicate)
    {
        var node = writes.First;
        while (node is not null &&
               (ReferenceEquals(node.Value, protectedRequest) || !predicate(node.Value)))
        {
            node = node.Next;
        }

        return node;
    }

    private long GetNonCriticalQueuedBytesLocked()
    {
        var total = pendingWrite is null ? 0L : GetRequestByteCount(pendingWrite);
        foreach (var request in parkedWrites)
        {
            if (!request.IsCritical)
            {
                total = checked(total + GetRequestByteCount(request));
            }
        }

        return total;
    }

    private bool RemoveSpecificQueuedWriteLocked(NativeReplayOverlayFrameWriteRequest request)
    {
        if (ReferenceEquals(pendingWrite, request))
        {
            RemovePendingWriteLocked(countAsDropped: false);
            return true;
        }

        var semanticKey = GetSemanticKey(request);
        if (pendingCriticalByKey.TryGetValue(semanticKey, out var criticalNode) &&
            ReferenceEquals(criticalNode.Value, request))
        {
            RemovePendingCriticalNodeLocked(criticalNode, countAsDropped: false);
            return true;
        }

        if (parkedByKey.TryGetValue(semanticKey, out var parkedNode) &&
            ReferenceEquals(parkedNode.Value, request))
        {
            RemoveParkedNodeLocked(parkedNode, countAsDropped: false);
            return true;
        }

        return false;
    }

    private void RemovePendingWriteLocked(bool countAsDropped)
    {
        if (pendingWrite is null)
        {
            return;
        }

        queuedBytes -= GetRequestByteCount(pendingWrite);
        pendingWrite = null;
        if (countAsDropped)
        {
            CountDroppedWrite();
        }
    }

    private void RemovePendingCriticalNodeLocked(
        LinkedListNode<NativeReplayOverlayFrameWriteRequest> node,
        bool countAsDropped)
    {
        pendingCriticalWrites.Remove(node);
        pendingCriticalByKey.Remove(GetSemanticKey(node.Value));
        queuedBytes -= GetRequestByteCount(node.Value);
        if (countAsDropped)
        {
            CountDroppedWrite();
        }
    }

    private void RemoveParkedNodeLocked(
        LinkedListNode<NativeReplayOverlayFrameWriteRequest> node,
        bool countAsDropped)
    {
        parkedWrites.Remove(node);
        parkedByKey.Remove(GetSemanticKey(node.Value));
        queuedBytes -= GetRequestByteCount(node.Value);
        if (countAsDropped)
        {
            CountDroppedWrite();
        }
    }

    private void ClearPendingCriticalWritesLocked()
    {
        while (pendingCriticalWrites.First is { } node)
        {
            RemovePendingCriticalNodeLocked(node, countAsDropped: true);
        }
    }

    private void ClearParkedWritesLocked()
    {
        while (parkedWrites.First is { } node)
        {
            RemoveParkedNodeLocked(node, countAsDropped: true);
        }
    }

    private void RemoveMatchingPendingCriticalWritesLocked(
        Func<NativeReplayOverlayFrameWriteRequest, bool> predicate)
    {
        var node = pendingCriticalWrites.First;
        while (node is not null)
        {
            var next = node.Next;
            if (predicate(node.Value))
            {
                RemovePendingCriticalNodeLocked(node, countAsDropped: true);
            }

            node = next;
        }
    }

    private void RemoveMatchingParkedWritesLocked(
        Func<NativeReplayOverlayFrameWriteRequest, bool> predicate)
    {
        var node = parkedWrites.First;
        while (node is not null)
        {
            var next = node.Next;
            if (predicate(node.Value))
            {
                RemoveParkedNodeLocked(node, countAsDropped: true);
            }

            node = next;
        }
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
            return request.Version == GetCurrentVersionSafely() &&
                request.Generation == Volatile.Read(ref generation);
        }

        var currentCriticalGeneration = IsPersistentCriticalClear(request)
            ? Volatile.Read(ref persistentCriticalClearGeneration)
            : IsEmptyCriticalWrite(request)
                ? Volatile.Read(ref emptyCriticalGeneration)
                : Volatile.Read(ref criticalGeneration);
        if (request.Generation != currentCriticalGeneration)
        {
            return false;
        }

        if (!IsPersistentCriticalClear(request) &&
            request.Version != GetCurrentVersionSafely())
        {
            return false;
        }

        if (getCurrentPipeName is not { } getPipeName)
        {
            return request.Version == GetCurrentVersionSafely();
        }

        return string.Equals(request.PipeName, GetCurrentPipeNameSafely(getPipeName), StringComparison.Ordinal);
    }

    private static bool IsPersistentCriticalClear(string writeKind) =>
        string.Equals(writeKind, "critical-clear", StringComparison.Ordinal);

    private static bool IsPersistentCriticalClear(NativeReplayOverlayFrameWriteRequest request) =>
        IsPersistentCriticalClear(request.WriteKind);

    private static bool IsEmptyCriticalWrite(string writeKind) =>
        IsPersistentCriticalClear(writeKind) ||
        string.Equals(writeKind, "blank-frame", StringComparison.Ordinal);

    private static bool IsEmptyCriticalWrite(NativeReplayOverlayFrameWriteRequest request) =>
        IsEmptyCriticalWrite(request.WriteKind);

    private static bool IsLoadedContentWrite(NativeReplayOverlayFrameWriteRequest request) =>
        string.Equals(request.WriteKind, "chat-frame", StringComparison.Ordinal) ||
        string.Equals(request.WriteKind, "status-frame", StringComparison.Ordinal);

    private static bool IsRetryableWriteFailure(Exception? exception) =>
        exception is null or TimeoutException or IOException or UnauthorizedAccessException;

    private TimeSpan CalculateRetryDelay(int retryCount)
    {
        if (retryDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var multiplier = 1L << Math.Min(retryCount, 10);
        var milliseconds = Math.Min(
            MaximumRetryDelay.TotalMilliseconds,
            retryDelay.TotalMilliseconds * multiplier);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static string GetSemanticKey(NativeReplayOverlayFrameWriteRequest request) =>
        string.Join('\n', request.PipeName, request.ReplaySessionKey, request.WriteKind);

    private static long GetRequestByteCount(NativeReplayOverlayFrameWriteRequest request) =>
        checked(request.Frame.LongLength + (request.FollowupFrame?.LongLength ?? 0L));

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
        SafeLog(
            AppLogLevel.Debug,
            $"Native VLC replay overlay pipe write took {elapsed.TotalMilliseconds:0} ms.{droppedSuffix}");
    }

    private void LogRetryableWriteFailure(
        NativeReplayOverlayFrameWriteRequest request,
        Exception? exception,
        TimeSpan delay)
    {
        SafeLog(
            AppLogLevel.Debug,
            BuildWriteFailureMessage(
                request,
                $"Native VLC replay overlay write failed; retrying in {delay.TotalMilliseconds:0} ms."),
            exception);
    }

    private void LogParkedWriteFailure(
        NativeReplayOverlayFrameWriteRequest request,
        Exception? exception)
    {
        SafeLog(
            AppLogLevel.Warning,
            BuildWriteFailureMessage(
                request,
                "Native VLC replay overlay write retries were exhausted; parked the latest state until reconnect."),
            exception);
    }

    private void LogFinalWriteFailure(NativeReplayOverlayFrameWriteRequest request, Exception exception)
    {
        SafeLog(
            AppLogLevel.Warning,
            BuildWriteFailureMessage(request, "Native VLC replay overlay write failed."),
            exception);
    }

    private void LogInvalidRequest(string reason)
    {
        SafeLog(
            AppLogLevel.Warning,
            $"Rejected invalid native VLC replay overlay write request: {reason}.");
    }

    private void InvokeSucceededSafely(NativeReplayOverlayFrameWriteRequest request)
    {
        try
        {
            currentWriteSucceeded?.Invoke(request);
        }
        catch (Exception ex)
        {
            SafeLog(AppLogLevel.Warning, "Native VLC replay overlay success callback failed.", ex);
        }
    }

    private void InvokeFailedSafely(Exception exception)
    {
        try
        {
            currentWriteFailed(exception);
        }
        catch (Exception ex)
        {
            SafeLog(AppLogLevel.Warning, "Native VLC replay overlay failure callback failed.", ex);
        }
    }

    private long GetCurrentVersionSafely()
    {
        try
        {
            return getCurrentVersion();
        }
        catch (Exception ex)
        {
            SafeLog(AppLogLevel.Warning, "Native VLC replay overlay version callback failed.", ex);
            return long.MinValue;
        }
    }

    private string? GetCurrentPipeNameSafely(Func<string?> getPipeName)
    {
        try
        {
            return getPipeName();
        }
        catch (Exception ex)
        {
            SafeLog(AppLogLevel.Warning, "Native VLC replay overlay pipe callback failed.", ex);
            return null;
        }
    }

    private void SafeLog(AppLogLevel level, string message, Exception? exception = null)
    {
        try
        {
            logger.Write(level, "ChatOverlay", message, exception);
        }
        catch (Exception)
        {
        }
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
            $"retry={request.RetryCount}/{maxCurrentFrameRetries};",
            $"latestCurrent={IsCurrent(request).ToString().ToLowerInvariant()}");
    }

    private static string FormatDiagnosticValue(string value) =>
        string.IsNullOrWhiteSpace(value) ? "(none)" : value;

    private static string FormatTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero
            ? timeout.TotalMilliseconds.ToString("0")
            : "(unknown)";
}

internal sealed record NativeReplayOverlayFrameWriteRequest(
    long Version,
    string PipeName,
    byte[] Frame,
    long Generation,
    long Sequence,
    string FrameKey,
    TimeSpan AnimationClock,
    bool HasAnimatedContent,
    TimeSpan? NextAnimationFrameDelay,
    TimeSpan RenderDuration,
    bool IsCritical,
    string WriteKind,
    string ReplaySessionKey,
    byte[]? FollowupFrame,
    int RetryCount);

internal readonly record struct NativeReplayOverlayFrameWriteResult(
    bool Sent,
    Exception? LastException);

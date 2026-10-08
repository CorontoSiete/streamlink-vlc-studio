using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Threading;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed partial class StreamTabViewModel
{
    private readonly object replaySeekRequestGate = new();
    private ReplaySeekRequest? pendingReplaySeekRequest;
    private ReplaySeekRequest? activeReplaySeekRequest;
    private TimeSpan? requestedReplaySeekOffset;
    private bool replaySeekRequestWorkerRunning;

    private TimeSpan? RequestedReplaySeekOffset
    {
        get { lock (replaySeekRequestGate) return requestedReplaySeekOffset; }
    }

    private bool HasPendingReplaySeekRequest
    {
        get { lock (replaySeekRequestGate) return pendingReplaySeekRequest is not null; }
    }

    public Task SeekReplayAsync(
        TimeSpan offset,
        CancellationToken cancellationToken = default,
        bool forceReload = false,
        bool holdExactPosition = false)
    {
        if (disposed || cancellationToken.IsCancellationRequested) return Task.CompletedTask;

        // Capture the target before yielding to WPF. Repeated skips build on this
        // request, even while VLC's clock still describes the previous position.
        offset = replaySession is { IsAvailable: true } replay
            ? ClampReplayOffset(offset, GetCurrentReplayDuration(replay))
            : offset < TimeSpan.Zero ? TimeSpan.Zero : offset;
        var request = new ReplaySeekRequest(offset, forceReload, holdExactPosition, cancellationToken,
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeCancellation.Token));
        var requestToken = request.Cancellation.Token;
        ReplaySeekRequest? replaced;
        bool startWorker;
        lock (replaySeekRequestGate)
        {
            replaced = pendingReplaySeekRequest;
            request.ResolvedPlayback = replaced?.ResolvedPlayback;
            pendingReplaySeekRequest = request;
            requestedReplaySeekOffset = offset;
            startWorker = !replaySeekRequestWorkerRunning;
            replaySeekRequestWorkerRunning = true;
        }
        replaced?.Complete();
        IsReplaySeekInProgress = true;
        if (!IsReplaySeekPreviewActive) RestoreReplaySeekDisplay();
        if (!request.Completion.Task.IsCompleted)
        {
            request.CancellationRegistration = requestToken.Register(
                () => dispatch(() => RemoveCanceledPendingReplaySeekRequest(request)));
        }
        if (request.Completion.Task.IsCompleted) request.CancellationRegistration.Dispose();
        if (startWorker) _ = DrainReplaySeekRequestsAsync();
        return request.Completion.Task;
    }

    private async Task DrainReplaySeekRequestsAsync()
    {
        // Also coalesce multiple input events delivered before the dispatcher
        // resumes. There is no additional debounce delay on an ordinary seek.
        await Task.Yield();
        while (true)
        {
            ReplaySeekRequest? request;
            lock (replaySeekRequestGate)
            {
                request = pendingReplaySeekRequest;
                pendingReplaySeekRequest = null;
                activeReplaySeekRequest = request;
                if (request is null)
                {
                    replaySeekRequestWorkerRunning = false;
                    requestedReplaySeekOffset = null;
                }
            }
            if (request is null)
            {
                if (!disposed && RequestedReplaySeekOffset is null)
                {
                    IsReplaySeekInProgress = false;
                    if (!IsReplaySeekPreviewActive) RestoreReplaySeekDisplay();
                }
                return;
            }

            try
            {
                // Let an active open/seek finish, retaining its initialized input.
                // Only the newest waiting target is submitted next, in place.
                // Stop, replacement, disposal and caller cancellation still abort it.
                await SeekReplaySerializedAsync(request.Offset, request.Cancellation.Token,
                    request.ForceReload, request.HoldExactPosition, playbackTransitionAlreadyHeld: false,
                    liveTransitionCancellationToken: request.CallerCancellationToken, seekRequest: request);
                CompleteRedundantPendingReplaySeekRequest(request);
            }
            catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                request.Completion.TrySetException(ex);
            }
            finally
            {
                lock (replaySeekRequestGate)
                {
                    if (ReferenceEquals(activeReplaySeekRequest, request)) activeReplaySeekRequest = null;
                    if (pendingReplaySeekRequest is null) requestedReplaySeekOffset = null;
                }
                if (!disposed && RequestedReplaySeekOffset is null && IsReplaySeekInProgress)
                {
                    IsBusy = false;
                    IsReplaySeekInProgress = false;
                    if (!IsReplaySeekPreviewActive) RestoreReplaySeekDisplay();
                    // The last waiting request may have been canceled after the
                    // active seek deferred its checkpoint and overlay refresh.
                    CaptureVodResumePosition();
                    await SaveVodResumePositionAsync(force: true);
                    FlushNativeReplayOverlayRefreshAfterSeek();
                }
                request.Complete();
            }
        }
    }

    private bool IsReplaySeekRequestSuperseded(ReplaySeekRequest? request)
    {
        if (request is null) return false;
        lock (replaySeekRequestGate)
            return ReferenceEquals(activeReplaySeekRequest, request) && pendingReplaySeekRequest is not null;
    }

    private void ConfirmReplaySeekRequest(ReplaySeekRequest? request, TimeSpan offset, long operationVersion,
        IPlaybackEngine engine)
    {
        if (request is null) return;
        lock (replaySeekRequestGate)
            request.Confirmation = new(offset, operationVersion, engine, pendingReplaySeekRequest);
    }

    private void CompleteRedundantPendingReplaySeekRequest(ReplaySeekRequest request)
    {
        if (request.Confirmation is not { } confirmation) return;
        ReplaySeekRequest? redundant = null;
        lock (replaySeekRequestGate)
        {
            // A successful seek satisfies an identical target already waiting at
            // confirmation. Fresh requests, failures, replacement media and explicit
            // reload/live intents still run.
            if (!disposed && ReferenceEquals(activeReplaySeekRequest, request) &&
                ReferenceEquals(playbackEngine, confirmation.Engine) &&
                IsLatestReplaySeekOperation(confirmation.OperationVersion) &&
                pendingReplaySeekRequest is { ForceReload: false } pending &&
                ReferenceEquals(pending, confirmation.WaitingRequest) &&
                pending.Offset == confirmation.Offset && pending.HoldExactPosition == request.HoldExactPosition)
            {
                redundant = pending;
                pendingReplaySeekRequest = null;
                requestedReplaySeekOffset = confirmation.Offset;
            }
        }
        redundant?.Complete();
    }

    private void CacheResolvedReplaySeekPlaybackForPendingRequest(ReplaySeekRequest? request, ResolvedReplaySeekPlayback resolved)
    {
        if (request is null) return;
        lock (replaySeekRequestGate)
        {
            if (ReferenceEquals(activeReplaySeekRequest, request) && pendingReplaySeekRequest is { } pending)
                pending.ResolvedPlayback = resolved;
        }
    }

    private void CancelReplaySeekRequests()
    {
        ReplaySeekRequest? pending;
        ReplaySeekRequest? active;
        lock (replaySeekRequestGate)
        {
            pending = pendingReplaySeekRequest;
            pendingReplaySeekRequest = null;
            active = activeReplaySeekRequest;
            requestedReplaySeekOffset = null;
        }
        pending?.Complete();
        CancellationSourceCleanup.Cancel(active?.Cancellation);
    }

    private void RemoveCanceledPendingReplaySeekRequest(ReplaySeekRequest request)
    {
        bool idle;
        lock (replaySeekRequestGate)
        {
            if (!ReferenceEquals(pendingReplaySeekRequest, request)) return;
            pendingReplaySeekRequest = null;
            requestedReplaySeekOffset = activeReplaySeekRequest?.Offset;
            idle = activeReplaySeekRequest is null;
        }
        if (idle) IsReplaySeekInProgress = false;
        if (!IsReplaySeekPreviewActive) RestoreReplaySeekDisplay();
        request.Complete();
    }

    private void RestoreReplaySeekDisplay()
    {
        // Preserve the clock's original seconds value. Converting it through
        // TimeSpan and back can truncate a tick and desynchronize the slider.
        var seconds = Math.Clamp(RequestedReplaySeekOffset?.TotalSeconds ?? ReplaySeekValue, 0, ReplaySeekMaximum);
        ReplaySeekSliderValue = seconds;
        ReplayElapsedText = StreamViewModelHelpers.FormatClockTime(TimeSpan.FromSeconds(seconds));
    }

    private sealed record ReplaySeekRequest(TimeSpan Offset, bool ForceReload, bool HoldExactPosition,
        CancellationToken CallerCancellationToken, CancellationTokenSource Cancellation)
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration CancellationRegistration;
        internal ReplaySeekConfirmation? Confirmation;
        internal ResolvedReplaySeekPlayback? ResolvedPlayback;

        internal void Complete()
        {
            Completion.TrySetResult();
            CancellationRegistration.Dispose();
            Cancellation.Dispose();
        }
    }
}

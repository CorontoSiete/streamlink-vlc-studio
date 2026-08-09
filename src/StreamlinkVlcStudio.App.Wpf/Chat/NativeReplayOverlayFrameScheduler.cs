using System.Diagnostics;
using System.Windows.Threading;
using StreamlinkVlcStudio.App.Wpf.Controls;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

internal sealed class NativeReplayOverlayFrameScheduler : IAsyncDisposable
{
    private static readonly TimeSpan SlowRenderThreshold = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly IAppLogger logger;
    private readonly Action<NativeReplayOverlayFrameResult> frameRendered;
    private readonly object gate = new();
    private readonly Thread renderThread;
    private readonly Dispatcher dispatcher;
    private NativeReplayOverlayFrameRequest? pendingRequest;
    private bool renderQueued;
    private bool disposed;

    public NativeReplayOverlayFrameScheduler(
        IAppLogger logger,
        Action<NativeReplayOverlayFrameResult> frameRendered)
    {
        this.logger = logger;
        this.frameRendered = frameRendered;

        var dispatcherReady = new TaskCompletionSource<Dispatcher>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        renderThread = new Thread(() => RunDispatcher(dispatcherReady))
        {
            IsBackground = true,
            Name = "Streamlink VLC Studio replay overlay renderer"
        };
        renderThread.SetApartmentState(ApartmentState.STA);
        renderThread.Start();

        dispatcher = dispatcherReady.Task
            .WaitAsync(ShutdownTimeout)
            .GetAwaiter()
            .GetResult();
    }

    public void QueueRender(NativeReplayOverlayFrameRequest request)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            pendingRequest = request;
            if (renderQueued)
            {
                return;
            }

            renderQueued = true;
        }

        try
        {
            dispatcher.BeginInvoke(RenderPendingFrames, DispatcherPriority.Background);
        }
        catch (InvalidOperationException)
        {
            lock (gate)
            {
                renderQueued = false;
                pendingRequest = null;
            }
        }
    }

    public void CancelPending()
    {
        lock (gate)
        {
            pendingRequest = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            disposed = true;
            pendingRequest = null;
        }

        try
        {
            if (!dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }
        catch (InvalidOperationException)
        {
        }

        if (!renderThread.Join(ShutdownTimeout))
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", "Timed out stopping the native VLC replay overlay renderer.");
        }

        return ValueTask.CompletedTask;
    }

    private static void RunDispatcher(TaskCompletionSource<Dispatcher> dispatcherReady)
    {
        try
        {
            var currentDispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(currentDispatcher));
            dispatcherReady.TrySetResult(currentDispatcher);
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            dispatcherReady.TrySetException(ex);
        }
    }

    private void RenderPendingFrames()
    {
        while (true)
        {
            NativeReplayOverlayFrameRequest? request;
            lock (gate)
            {
                request = pendingRequest;
                pendingRequest = null;
                if (request is null)
                {
                    renderQueued = false;
                    return;
                }
            }

            var stopwatch = Stopwatch.StartNew();
            byte[]? frame = null;
            var hasAnimatedContent = false;
            var hasPendingImageLoads = false;
            TimeSpan? nextAnimationFrameDelay = null;
            IReadOnlyCollection<AnimatedEmoteImageCacheKey> pendingImageLoads = [];
            var width = 0;
            var height = 0;
            Exception? exception = null;
            var renderedSelection = NativeReplayOverlayRenderedSelection.Empty;
            try
            {
                var renderedFrame = NativeOverlayChatFrameRenderer.TryBuildFrame(
                    request.Messages,
                    request.Settings,
                    request.FontSize,
                    request.VideoHeight,
                    request.PositionStatePath,
                    request.AnimationClock,
                    out width,
                    out height,
                    request.MessageOffset,
                    request.ImageCachePinOwner);
                frame = renderedFrame?.Frame;
                hasAnimatedContent = renderedFrame?.HasAnimatedContent == true;
                hasPendingImageLoads = renderedFrame?.HasPendingImageLoads == true;
                nextAnimationFrameDelay = renderedFrame?.NextAnimationFrameDelay;
                pendingImageLoads = renderedFrame?.PendingImageLoads ?? [];
                renderedSelection = renderedFrame?.RenderedSelection ?? NativeReplayOverlayRenderedSelection.Empty;
            }
            catch (Exception ex)
            {
                exception = ex;
                logger.Write(AppLogLevel.Warning, "ChatOverlay", "Native VLC replay overlay rendering failed.", ex);
            }

            stopwatch.Stop();
            if (exception is null && stopwatch.Elapsed >= SlowRenderThreshold)
            {
                logger.Write(
                    AppLogLevel.Debug,
                    "ChatOverlay",
                    $"Native VLC replay overlay render took {stopwatch.Elapsed.TotalMilliseconds:0} ms for {request.Messages.Count} messages at {width}x{height}.");
            }

            try
            {
                frameRendered(new NativeReplayOverlayFrameResult(
                    request,
                    frame,
                    width,
                    height,
                    Environment.CurrentManagedThreadId,
                    Thread.CurrentThread.GetApartmentState(),
                    stopwatch.Elapsed,
                    exception,
                    renderedSelection,
                    hasAnimatedContent,
                    hasPendingImageLoads,
                    nextAnimationFrameDelay,
                    pendingImageLoads));
            }
            catch (Exception ex)
            {
                logger.Write(AppLogLevel.Warning, "ChatOverlay", "Native VLC replay overlay render callback failed.", ex);
            }

            lock (gate)
            {
                if (pendingRequest is null)
                {
                    renderQueued = false;
                    return;
                }
            }
        }
    }
}

internal sealed record NativeReplayOverlayFrameRequest(
    long Version,
    string PipeName,
    IReadOnlyList<ChatMessage> Messages,
    ChatSettings Settings,
    double FontSize,
    int VideoHeight,
    string? PositionStatePath,
    string FrameKey,
    int MessageOffset = 0,
    string ScrollSessionKey = "",
    TimeSpan AnimationClock = default,
    object? ImageCachePinOwner = null);

internal sealed record NativeReplayOverlayFrameResult(
    NativeReplayOverlayFrameRequest Request,
    byte[]? Frame,
    int Width,
    int Height,
    int RenderThreadId,
    ApartmentState RenderThreadApartmentState,
    TimeSpan RenderDuration,
    Exception? Exception,
    NativeReplayOverlayRenderedSelection RenderedSelection,
    bool HasAnimatedContent,
    bool HasPendingImageLoads,
    TimeSpan? NextAnimationFrameDelay,
    IReadOnlyCollection<AnimatedEmoteImageCacheKey> PendingImageLoads)
{
    public bool Succeeded => Exception is null && Frame is not null;
}

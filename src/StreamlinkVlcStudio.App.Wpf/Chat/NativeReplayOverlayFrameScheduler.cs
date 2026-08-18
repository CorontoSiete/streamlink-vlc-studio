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
    private readonly Dispatcher dispatcher;
    private readonly Task threadExited;
    private NativeReplayOverlayFrameRequest? pendingRequest;
    private bool renderQueued;
    private bool disposed;
    private Task? disposalTask;
    private NativeReplayOverlayFrameRenderContext? renderContext;

    private NativeReplayOverlayFrameScheduler(
        IAppLogger logger,
        Action<NativeReplayOverlayFrameResult> frameRendered,
        Dispatcher dispatcher,
        Task threadExited)
    {
        this.logger = logger;
        this.frameRendered = frameRendered;
        this.dispatcher = dispatcher;
        this.threadExited = threadExited;
    }

    internal static async Task<NativeReplayOverlayFrameScheduler> CreateAsync(
        IAppLogger logger,
        Action<NativeReplayOverlayFrameResult> frameRendered,
        CancellationToken cancellationToken = default,
        TimeSpan? startupTimeout = null,
        Action? beforeDispatcherInitialization = null,
        Action? dispatcherStopped = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(frameRendered);
        var timeout = startupTimeout ?? ShutdownTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));
        }

        var startup = new DispatcherStartupState(dispatcherStopped);
        var renderThread = new Thread(() => RunDispatcher(startup, beforeDispatcherInitialization))
        {
            IsBackground = true,
            Name = "Streamlink VLC Studio replay overlay renderer"
        };
        renderThread.SetApartmentState(ApartmentState.STA);
        renderThread.Start();

        try
        {
            var dispatcher = await startup.DispatcherReady.Task
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
            return new NativeReplayOverlayFrameScheduler(
                logger,
                frameRendered,
                dispatcher,
                startup.ThreadExited.Task);
        }
        catch
        {
            startup.RequestShutdown();
            throw;
        }
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
            if (disposalTask is not null)
            {
                return new ValueTask(disposalTask);
            }

            disposed = true;
            pendingRequest = null;
            disposalTask = DisposeCoreAsync();
            return new ValueTask(disposalTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
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

        try
        {
            await threadExited.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            SafeLog(AppLogLevel.Warning, "Timed out stopping the native VLC replay overlay renderer.");
        }
    }

    private static void RunDispatcher(
        DispatcherStartupState startup,
        Action? beforeDispatcherInitialization)
    {
        try
        {
            beforeDispatcherInitialization?.Invoke();
            var currentDispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(currentDispatcher));
            startup.DispatcherReady.TrySetResult(currentDispatcher);
            if (startup.ShutdownRequested)
            {
                currentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }

            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            startup.DispatcherReady.TrySetException(ex);
        }
        finally
        {
            startup.SignalStopped();
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
                renderContext ??= new NativeReplayOverlayFrameRenderContext();
                renderContext.EnsureContentVersion(request.RenderContentVersion);
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
                    request.ImageCachePinOwner,
                    renderContext);
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
                SafeLog(AppLogLevel.Warning, "Native VLC replay overlay rendering failed.", ex);
            }

            stopwatch.Stop();
            if (exception is null && stopwatch.Elapsed >= SlowRenderThreshold)
            {
                SafeLog(
                    AppLogLevel.Debug,
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
                SafeLog(AppLogLevel.Warning, "Native VLC replay overlay render callback failed.", ex);
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

    private sealed class DispatcherStartupState(Action? dispatcherStopped)
    {
        private int shutdownRequested;

        internal TaskCompletionSource<Dispatcher> DispatcherReady { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ThreadExited { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool ShutdownRequested => Volatile.Read(ref shutdownRequested) != 0;

        internal void RequestShutdown()
        {
            Interlocked.Exchange(ref shutdownRequested, 1);
            _ = DispatcherReady.Task.ContinueWith(
                static task =>
                {
                    try
                    {
                        if (!task.Result.HasShutdownStarted)
                        {
                            task.Result.BeginInvokeShutdown(DispatcherPriority.Send);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        internal void SignalStopped()
        {
            try
            {
                dispatcherStopped?.Invoke();
            }
            catch (Exception)
            {
            }

            ThreadExited.TrySetResult();
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
    object? ImageCachePinOwner = null,
    long RenderContentVersion = 0);

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

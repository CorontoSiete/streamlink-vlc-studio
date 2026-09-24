using System.Windows.Threading;

namespace StreamlinkVlcStudio.App.Wpf;

internal sealed class LowLevelMouseHookDispatcher
{
    internal static readonly TimeSpan DefaultSynchronousRouteTimeout = TimeSpan.FromMilliseconds(25);

    private readonly Dispatcher dispatcher;
    private readonly Func<LowLevelMouseHookEvent, bool> routeOnUi;
    private readonly Func<bool> hasActiveMouseMoveRoute;
    private readonly TimeSpan synchronousRouteTimeout;
    private readonly Func<LowLevelMouseHookEvent, Action?> captureWheelFallback;
    private readonly Func<LowLevelMouseHookEvent, Action?> captureContextMenuRoute;
    private volatile bool isLeftButtonDown;
    private bool suppressRightButtonUp;

    public LowLevelMouseHookDispatcher(
        Dispatcher dispatcher,
        Func<LowLevelMouseHookEvent, bool> routeOnUi,
        Func<bool> hasActiveMouseMoveRoute,
        TimeSpan? synchronousRouteTimeout = null,
        Func<LowLevelMouseHookEvent, Action?>? captureWheelFallback = null,
        Func<LowLevelMouseHookEvent, Action?>? captureContextMenuRoute = null)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.routeOnUi = routeOnUi ?? throw new ArgumentNullException(nameof(routeOnUi));
        this.hasActiveMouseMoveRoute = hasActiveMouseMoveRoute ?? throw new ArgumentNullException(nameof(hasActiveMouseMoveRoute));
        this.synchronousRouteTimeout = synchronousRouteTimeout ?? DefaultSynchronousRouteTimeout;
        this.captureWheelFallback = captureWheelFallback ?? NativeMouseWheelTarget.CaptureFallback;
        this.captureContextMenuRoute = captureContextMenuRoute ?? NativePictureInPictureContextMenuTarget.CaptureRoute;
    }

    public bool ProcessEvent(LowLevelMouseHookEvent hookEvent)
    {
        if (hookEvent.Message == LowLevelMouseHookEvent.WmMouseWheel)
        {
            return QueueMouseWheel(hookEvent);
        }

        if (hookEvent.Message == LowLevelMouseHookEvent.WmRightButtonDown)
        {
            suppressRightButtonUp = QueueContextMenu(hookEvent);
            return suppressRightButtonUp;
        }

        if (hookEvent.Message == LowLevelMouseHookEvent.WmRightButtonUp)
        {
            // The intercepted press never reached WPF's mouse device. Letting its
            // release through can immediately dismiss the menu, especially when inactive.
            var suppress = suppressRightButtonUp;
            suppressRightButtonUp = false;
            return suppress;
        }

        if (hookEvent.Message == LowLevelMouseHookEvent.WmLeftButtonDown)
        {
            isLeftButtonDown = true;
        }

        if (hookEvent.Message == LowLevelMouseHookEvent.WmMouseMove)
        {
            // Preserve moves after a press even when the UI was too busy to arm its drag
            // candidate within the synchronous hook timeout. Dispatcher input ordering makes
            // sure the press is processed before these moves.
            var followsLeftButtonDown = isLeftButtonDown;
            if (followsLeftButtonDown || hasActiveMouseMoveRoute())
            {
                QueueRoute(hookEvent, discardIfLeftButtonReleased: followsLeftButtonDown);
            }

            return false;
        }

        if (!IsSynchronousRouteMessage(hookEvent.Message))
        {
            return false;
        }

        try
        {
            return RouteSynchronously(
                hookEvent,
                preservePendingOperation: hookEvent.Message is
                    LowLevelMouseHookEvent.WmLeftButtonDown or
                    LowLevelMouseHookEvent.WmLeftButtonUp);
        }
        finally
        {
            if (hookEvent.Message == LowLevelMouseHookEvent.WmLeftButtonUp)
            {
                isLeftButtonDown = false;
            }
        }
    }

    private static bool IsSynchronousRouteMessage(int message)
    {
        return message is
            LowLevelMouseHookEvent.WmLeftButtonDown or
            LowLevelMouseHookEvent.WmLeftButtonUp;
    }

    private bool QueueContextMenu(LowLevelMouseHookEvent hookEvent)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return false;
        }

        // Only a registered native PiP target can claim this click. Capture its
        // owner now and suppress native delivery so it cannot open the menu twice.
        var route = captureContextMenuRoute(hookEvent);
        if (route is null)
        {
            return false;
        }

        try
        {
            var operation = dispatcher.BeginInvoke(DispatcherPriority.Input, route);
            return operation.Status != DispatcherOperationStatus.Aborted;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private bool QueueMouseWheel(LowLevelMouseHookEvent hookEvent)
    {
        if (hookEvent.WheelDelta == 0 || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return false;
        }

        // Decide native ownership on the hook thread, without waiting for WPF or VLC.
        // Once queued, we own this input: letting Windows deliver it as well would
        // double-count it. Unhandled controls receive the original native message once.
        var fallback = captureWheelFallback(hookEvent);
        if (fallback is null)
        {
            return false;
        }

        try
        {
            var operation = dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!routeOnUi(hookEvent))
                {
                    fallback();
                }
            }));
            return operation.Status != DispatcherOperationStatus.Aborted;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private void QueueRoute(
        LowLevelMouseHookEvent hookEvent,
        bool discardIfLeftButtonReleased)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() =>
                {
                    if (!discardIfLeftButtonReleased || isLeftButtonDown)
                    {
                        _ = routeOnUi(hookEvent);
                    }
                }));
        }
        catch (InvalidOperationException)
        {
        }
        catch (TaskCanceledException)
        {
        }
    }

    private bool RouteSynchronously(
        LowLevelMouseHookEvent hookEvent,
        bool preservePendingOperation)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return false;
        }

        if (dispatcher.CheckAccess())
        {
            return routeOnUi(hookEvent);
        }

        DispatcherOperation operation;
        try
        {
            operation = dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Func<bool>(() => routeOnUi(hookEvent)));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }

        try
        {
            var status = operation.Wait(synchronousRouteTimeout);
            if (status == DispatcherOperationStatus.Completed && operation.Result is bool handled)
            {
                return handled;
            }

            if (status == DispatcherOperationStatus.Pending && !preservePendingOperation)
            {
                _ = operation.Abort();
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (TaskCanceledException)
        {
        }
        catch (TimeoutException)
        {
            if (!preservePendingOperation)
            {
                _ = operation.Abort();
            }
        }

        return false;
    }
}

using System.Windows.Threading;

namespace StreamlinkVlcStudio.App.Wpf;

internal sealed class LowLevelMouseHookDispatcher
{
    internal static readonly TimeSpan DefaultSynchronousRouteTimeout = TimeSpan.FromMilliseconds(25);

    private readonly Dispatcher dispatcher;
    private readonly Func<LowLevelMouseHookEvent, bool> routeOnUi;
    private readonly Func<bool> hasActiveMouseMoveRoute;
    private readonly TimeSpan synchronousRouteTimeout;
    private volatile bool isLeftButtonDown;

    public LowLevelMouseHookDispatcher(
        Dispatcher dispatcher,
        Func<LowLevelMouseHookEvent, bool> routeOnUi,
        Func<bool> hasActiveMouseMoveRoute,
        TimeSpan? synchronousRouteTimeout = null)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.routeOnUi = routeOnUi ?? throw new ArgumentNullException(nameof(routeOnUi));
        this.hasActiveMouseMoveRoute = hasActiveMouseMoveRoute ?? throw new ArgumentNullException(nameof(hasActiveMouseMoveRoute));
        this.synchronousRouteTimeout = synchronousRouteTimeout ?? DefaultSynchronousRouteTimeout;
    }

    public bool ProcessEvent(LowLevelMouseHookEvent hookEvent)
    {
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
            LowLevelMouseHookEvent.WmMouseWheel or
            LowLevelMouseHookEvent.WmLeftButtonDown or
            LowLevelMouseHookEvent.WmLeftButtonUp or
            LowLevelMouseHookEvent.WmRightButtonDown;
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

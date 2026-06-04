using System.Windows.Threading;

namespace StreamlinkVlcStudio.App.Wpf;

internal sealed class LowLevelMouseHookDispatcher
{
    internal static readonly TimeSpan DefaultSynchronousRouteTimeout = TimeSpan.FromMilliseconds(25);

    private readonly Dispatcher dispatcher;
    private readonly Func<LowLevelMouseHookEvent, bool> routeOnUi;
    private readonly Func<bool> hasActiveMouseMoveRoute;
    private readonly TimeSpan synchronousRouteTimeout;

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
        if (hookEvent.Message == LowLevelMouseHookEvent.WmMouseMove)
        {
            if (hasActiveMouseMoveRoute())
            {
                QueueRoute(hookEvent);
            }

            return false;
        }

        if (!IsSynchronousRouteMessage(hookEvent.Message))
        {
            return false;
        }

        return RouteSynchronously(hookEvent);
    }

    private static bool IsSynchronousRouteMessage(int message)
    {
        return message is
            LowLevelMouseHookEvent.WmMouseWheel or
            LowLevelMouseHookEvent.WmLeftButtonDown or
            LowLevelMouseHookEvent.WmLeftButtonUp;
    }

    private void QueueRoute(LowLevelMouseHookEvent hookEvent)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() => _ = routeOnUi(hookEvent)));
        }
        catch (InvalidOperationException)
        {
        }
        catch (TaskCanceledException)
        {
        }
    }

    private bool RouteSynchronously(LowLevelMouseHookEvent hookEvent)
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

            if (status == DispatcherOperationStatus.Pending)
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
            _ = operation.Abort();
        }

        return false;
    }
}

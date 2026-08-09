namespace StreamlinkVlcStudio.App.Wpf.Notifications;

/// <summary>
/// Coordinates notification delivery across refresh, settings, and background thumbnail threads.
/// A generation captured before an off/on transition can never be delivered afterward.
/// </summary>
internal sealed class LiveNotificationDeliveryGate : IDisposable
{
    private readonly object syncRoot = new();
    private bool isEnabled = true;
    private bool disposed;
    private long generation;

    public bool IsEnabled
    {
        get
        {
            lock (syncRoot)
            {
                return isEnabled && !disposed;
            }
        }
        set
        {
            lock (syncRoot)
            {
                if (disposed || isEnabled == value)
                {
                    return;
                }

                isEnabled = value;
                generation++;
            }
        }
    }

    public bool TryBegin(out long deliveryGeneration)
    {
        lock (syncRoot)
        {
            if (disposed || !isEnabled)
            {
                deliveryGeneration = 0;
                return false;
            }

            deliveryGeneration = generation;
            return true;
        }
    }

    public bool TryRunIfCurrent(long deliveryGeneration, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (syncRoot)
        {
            if (disposed || !isEnabled || deliveryGeneration != generation)
            {
                return false;
            }

            action();
            return true;
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            isEnabled = false;
            generation++;
        }
    }
}

using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

internal static class SafeEventDispatcher
{
    public static void Invoke<TEventArgs>(
        EventHandler<TEventArgs>? handlers,
        object sender,
        TEventArgs eventArgs,
        IAppLogger logger,
        string source,
        string eventName)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<TEventArgs>)handler)(sender, eventArgs);
            }
            catch (Exception ex)
            {
                LogSubscriberFailure(logger, source, eventName, ex);
            }
        }
    }

    public static void Invoke<T>(
        Action<T>? callback,
        T value,
        IAppLogger logger,
        string source,
        string callbackName)
    {
        if (callback is null)
        {
            return;
        }

        foreach (var handler in callback.GetInvocationList())
        {
            try
            {
                ((Action<T>)handler)(value);
            }
            catch (Exception ex)
            {
                LogSubscriberFailure(logger, source, callbackName, ex);
            }
        }
    }

    private static void LogSubscriberFailure(
        IAppLogger logger,
        string source,
        string eventName,
        Exception exception)
    {
        try
        {
            logger.Write(
                AppLogLevel.Warning,
                source,
                $"The {eventName} subscriber threw; continuing network processing.",
                exception);
        }
        catch
        {
            // Subscriber isolation must not depend on the logger being healthy.
        }
    }
}

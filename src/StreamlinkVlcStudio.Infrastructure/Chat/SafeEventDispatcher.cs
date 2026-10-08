using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

internal static class SafeEventDispatcher
{
    public static void Invoke(
        Action? callback,
        IAppLogger? logger,
        string source,
        string callbackName) =>
        Invoke(callback, false, static (handler, _) => handler(), logger, source, callbackName);

    public static void Invoke<TEventArgs>(
        EventHandler<TEventArgs>? handlers,
        object sender,
        TEventArgs eventArgs,
        IAppLogger? logger,
        string source,
        string eventName) =>
        Invoke(handlers, (sender, eventArgs), static (handler, state) => handler(state.sender, state.eventArgs),
            logger, source, eventName);

    public static void Invoke(
        EventHandler? handlers,
        object sender,
        EventArgs eventArgs,
        IAppLogger? logger,
        string source,
        string eventName) =>
        Invoke(handlers, (sender, eventArgs), static (handler, state) => handler(state.sender, state.eventArgs),
            logger, source, eventName);

    public static void Invoke<T>(
        Action<T>? callback,
        T value,
        IAppLogger? logger,
        string source,
        string callbackName) =>
        Invoke(callback, value, static (handler, state) => handler(state), logger, source, callbackName);

    public static void Invoke<TFirst, TSecond>(
        Action<TFirst, TSecond>? callback,
        TFirst first,
        TSecond second,
        IAppLogger? logger,
        string source,
        string callbackName) =>
        Invoke(callback, (first, second), static (handler, state) => handler(state.first, state.second),
            logger, source, callbackName);

    private static void Invoke<TDelegate, TState>(
        TDelegate? callback,
        TState state,
        Action<TDelegate, TState> invoke,
        IAppLogger? logger,
        string source,
        string callbackName) where TDelegate : Delegate
    {
        if (callback is null)
        {
            return;
        }

        foreach (var handler in Delegate.EnumerateInvocationList(callback))
        {
            try
            {
                invoke(handler, state);
            }
            catch (Exception ex)
            {
                logger.WriteSafely(AppLogLevel.Warning, source,
                    $"The {callbackName} subscriber threw; continuing event dispatch.", ex);
            }
        }
    }
}

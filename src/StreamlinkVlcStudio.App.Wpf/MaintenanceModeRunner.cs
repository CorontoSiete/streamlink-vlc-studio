using Microsoft.Toolkit.Uwp.Notifications;
using StreamStudio.Installation;

namespace StreamlinkVlcStudio.App.Wpf;

internal static class MaintenanceModeRunner
{
    internal const string ShutdownEventName = WindowsApplicationShutdown.ShutdownEventName;

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length > 0 && string.Equals(args[0], ApplicationDependencyVerifier.Command, StringComparison.OrdinalIgnoreCase))
        {
            try { exitCode = ApplicationDependencyVerifier.Run(args); }
            catch (Exception ex) { Console.Error.WriteLine($"Dependency verification failed: {ex.Message}"); exitCode = 1; }
            return true;
        }
        if (args.Length != 1) return false;
        try
        {
            if (string.Equals(args[0], "--maintenance-request-shutdown", StringComparison.OrdinalIgnoreCase))
            {
                exitCode = RequestShutdown() ? 0 : 2;
                return true;
            }
            if (string.Equals(args[0], "--maintenance-unregister-notifications", StringComparison.OrdinalIgnoreCase))
            {
                ToastNotificationManagerCompat.Uninstall();
                return true;
            }
            if (string.Equals(args[0], "--maintenance-register-notifications", StringComparison.OrdinalIgnoreCase))
            {
                _ = ToastNotificationManagerCompat.CreateToastNotifier();
                return true;
            }
            return false;
        }
        catch
        {
            exitCode = 1;
            return true;
        }
    }

    private static bool RequestShutdown() => WindowsApplicationShutdown.Request(TimeSpan.FromSeconds(20));

    internal static bool RequestShutdown(TimeSpan timeout, string shutdownEventName, string instanceMutexName) =>
        WindowsApplicationShutdown.Request(timeout, shutdownEventName, instanceMutexName);

    internal static bool RequestShutdown(TimeSpan timeout, params (string EventName, string MutexName)[] instances) =>
        WindowsApplicationShutdown.Request(timeout, instances);
}

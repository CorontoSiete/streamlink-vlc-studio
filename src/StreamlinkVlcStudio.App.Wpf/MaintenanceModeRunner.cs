using Microsoft.Toolkit.Uwp.Notifications;

namespace StreamlinkVlcStudio.App.Wpf;

internal static class MaintenanceModeRunner
{
    internal const string ShutdownEventName = "Local\\StreamStudio.App.MaintenanceShutdown";

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
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

    private static bool RequestShutdown()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ShutdownEventName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { return true; }
        catch (UnauthorizedAccessException) { return false; }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            using var mutex = new Mutex(false, App.SingleInstanceMutexName);
            try
            {
                if (mutex.WaitOne(TimeSpan.FromMilliseconds(200)))
                {
                    mutex.ReleaseMutex();
                    return true;
                }
            }
            catch (AbandonedMutexException) { return true; }
        }
        return false;
    }
}

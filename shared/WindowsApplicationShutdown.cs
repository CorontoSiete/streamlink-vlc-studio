using System.Diagnostics;
using System.IO;

namespace StreamStudio.Installation;

// Keep shutdown independent of the installed executable: a missing or damaged
// application must still be repairable and removable.
internal static class WindowsApplicationShutdown
{
    internal const string ShutdownEventName = "Local\\StreamStudio.App.MaintenanceShutdown";
    internal const string SingleInstanceMutexName = "Local\\StreamStudio.App.SingleInstance";

    internal static bool Request(TimeSpan timeout) => Request(timeout,
        (ShutdownEventName, SingleInstanceMutexName),
        ("Local\\StreamlinkVlcStudio.App.MaintenanceShutdown", "Local\\StreamlinkVlcStudio.App.SingleInstance"));

    internal static bool Request(TimeSpan timeout, params (string EventName, string MutexName)[] instances)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var elapsed = Stopwatch.StartNew();
        foreach (var (eventName, mutexName) in instances)
        {
            var remaining = timeout - elapsed.Elapsed;
            if (!Request(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1), eventName, mutexName)) return false;
        }
        return true;
    }

    internal static bool Request(
        TimeSpan timeout,
        string shutdownEventName,
        string instanceMutexName)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        try
        {
            using var mutex = new Mutex(false, instanceMutexName);
            var elapsed = Stopwatch.StartNew();
            var signaled = false;
            while (true)
            {
                if (!signaled)
                {
                    try
                    {
                        using var signal = EventWaitHandle.OpenExisting(shutdownEventName);
                        signal.Set();
                        signaled = true;
                    }
                    catch (WaitHandleCannotBeOpenedException)
                    {
                        // Startup can hold the mutex before creating its shutdown
                        // event. Recheck instead of assuming the app is closed.
                    }
                }

                var remaining = timeout - elapsed.Elapsed;
                var wait = remaining <= TimeSpan.Zero ? TimeSpan.Zero
                    : remaining < TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMilliseconds(200);
                var acquired = false;
                try { acquired = mutex.WaitOne(wait); }
                catch (AbandonedMutexException) { acquired = true; }
                if (acquired)
                {
                    mutex.ReleaseMutex();
                    return true;
                }
                if (elapsed.Elapsed >= timeout) return false;
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}

using System.Reflection;
using System.Text.Json;

// Run only by the packaging smoke test, before the actual app entry point.
// This inspects the shipped executable's real default updater, then exits without UI.
internal static class StartupHook
{
    public static void Initialize()
    {
        var restartExecutable = Environment.GetEnvironmentVariable("SVS_UPDATE_PROBE_RESTART_EXECUTABLE");
        // The real helper passes its environment through Setup to the relaunched app.
        // Let every other process run normally; only inspect the installed app.
        if (!string.IsNullOrEmpty(restartExecutable) &&
            !string.Equals(Environment.ProcessPath, restartExecutable, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var assembly = Assembly.Load("StreamlinkVlcStudio.Infrastructure");
            var type = assembly.GetType("StreamlinkVlcStudio.Infrastructure.Updates.StagedAppUpdateService", true)!;
            using var service = (IDisposable)Activator.CreateInstance(type, new object?[] { null })!;
            var directory = (string)type.GetField("applicationDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            var detect = type.GetMethod("DetectInstallKind", BindingFlags.Static | BindingFlags.NonPublic)!;
            var report = JsonSerializer.Serialize(new
            {
                ProcessPath = Environment.ProcessPath,
                BaseDirectory = AppContext.BaseDirectory,
                ConfiguredDirectory = directory,
                InstallKind = detect.Invoke(null, [directory])!.ToString()
            });
            var restartReport = Environment.GetEnvironmentVariable("SVS_UPDATE_PROBE_RESTART_REPORT");
            if (!string.IsNullOrEmpty(restartReport))
            {
                File.WriteAllText(restartReport + ".tmp", report);
                File.Move(restartReport + ".tmp", restartReport, overwrite: true);
            }
            Console.WriteLine(report);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Environment.Exit(1);
        }
    }
}

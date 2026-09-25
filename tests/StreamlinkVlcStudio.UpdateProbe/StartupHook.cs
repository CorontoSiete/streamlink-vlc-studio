using System.Reflection;
using System.Text.Json;

// Run only by the packaging smoke test, before the actual app entry point.
// This inspects the shipped executable's real default updater, then exits without UI.
internal static class StartupHook
{
    public static void Initialize()
    {
        try
        {
            var assembly = Assembly.Load("StreamlinkVlcStudio.Infrastructure");
            var type = assembly.GetType("StreamlinkVlcStudio.Infrastructure.Updates.StagedAppUpdateService", true)!;
            using var service = (IDisposable)Activator.CreateInstance(type, new object?[] { null })!;
            var directory = (string)type.GetField("applicationDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            var detect = type.GetMethod("DetectInstallKind", BindingFlags.Static | BindingFlags.NonPublic)!;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ProcessPath = Environment.ProcessPath,
                BaseDirectory = AppContext.BaseDirectory,
                ConfiguredDirectory = directory,
                InstallKind = detect.Invoke(null, [directory])!.ToString()
            }));
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Environment.Exit(1);
        }
    }
}

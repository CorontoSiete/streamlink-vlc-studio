using System.Reflection;
using System.Text.Json;

// Run only by the packaging smoke test, before the actual app entry point.
// Inspect the shipped executable's default updater and optionally exercise its
// signed release check and download against GitHub, then exit before opening UI.
internal static class StartupHook
{
    public static void Initialize()
    {
        var restartExecutable = Environment.GetEnvironmentVariable("SVS_UPDATE_PROBE_RESTART_EXECUTABLE");
        // The real helper passes its environment through Setup to the relaunched app.
        // Let every other process and installer maintenance invocation run normally;
        // only inspect the final app launch, which has no command-line arguments.
        if (!string.IsNullOrEmpty(restartExecutable) &&
            (!string.Equals(Environment.ProcessPath, restartExecutable, StringComparison.OrdinalIgnoreCase) ||
                Environment.GetCommandLineArgs().Length != 1))
            return;

        try
        {
            var assembly = Assembly.Load("StreamlinkVlcStudio.Infrastructure");
            var type = assembly.GetType("StreamlinkVlcStudio.Infrastructure.Updates.StagedAppUpdateService", true)!;
            var loggerType = assembly.GetType("StreamlinkVlcStudio.Infrastructure.Logging.FileAppLogger", true)!;
            using var logger = (IDisposable)Activator.CreateInstance(loggerType,
                new object?[] { Path.Combine(Path.GetTempPath(), "StreamStudio-update-probe", Guid.NewGuid().ToString("N")) })!;
            using var service = (IDisposable)Activator.CreateInstance(type, new object?[] { logger })!;
            var directory = (string)type.GetField("applicationDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            var detect = type.GetMethod("DetectInstallKind", BindingFlags.Static | BindingFlags.NonPublic)!;
            object? liveUpdate = null;
            var expectedTag = Environment.GetEnvironmentVariable("SVS_UPDATE_PROBE_EXPECTED_TAG");
            if (!string.IsNullOrEmpty(expectedTag))
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var checkMethod = type.GetMethod("CheckAsync")!;
                var reason = Enum.Parse(checkMethod.GetParameters()[0].ParameterType, "Manual");
                var check = AwaitResult(checkMethod.Invoke(service, [reason, cancellation.Token])!);
                var release = ReadProperty(check, "Release");
                if (ReadProperty(check, "Checked") is not true ||
                    ReadProperty(check, "IsNotifyOnly") is not false ||
                    ReadProperty(check, "InstallKind").ToString() != "Managed" ||
                    ReadProperty(release, "Tag").ToString() != expectedTag)
                    throw new InvalidOperationException("The installed updater did not authenticate the expected managed release.");

                var download = Environment.GetEnvironmentVariable("SVS_UPDATE_PROBE_DOWNLOAD") == "true";
                object? prepared = null;
                if (download)
                {
                    if (ReadProperty(check, "IsUpdateAvailable") is not true)
                        throw new InvalidOperationException("The installed updater did not detect the published upgrade.");
                    prepared = AwaitResult(type.GetMethod("DownloadAsync")!.Invoke(service,
                        [release, null, cancellation.Token])!);
                }
                else if (ReadProperty(check, "IsUpdateAvailable") is not false)
                    throw new InvalidOperationException("The upgraded application still offers an update to its installed version.");

                var phase = ReadProperty(ReadProperty(service, "State"), "Phase").ToString();
                if (phase != (download ? "Ready" : "Completed"))
                    throw new InvalidOperationException($"The published updater ended in unexpected phase {phase}.");
                liveUpdate = new { Check = check, PreparedUpdate = prepared, Phase = phase };
            }
            var report = JsonSerializer.Serialize(new
            {
                ProcessPath = Environment.ProcessPath,
                BaseDirectory = AppContext.BaseDirectory,
                ConfiguredDirectory = directory,
                InstallKind = detect.Invoke(null, [directory])!.ToString(),
                LiveUpdate = liveUpdate
            });
            var restartReport = Environment.GetEnvironmentVariable("SVS_UPDATE_PROBE_RESTART_REPORT");
            if (!string.IsNullOrEmpty(restartReport))
            {
                File.WriteAllText(restartReport + ".tmp", report);
                File.Move(restartReport + ".tmp", restartReport, overwrite: true);
            }
            Console.WriteLine(report);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Environment.Exit(1);
        }
        Environment.Exit(0);
    }

    private static object AwaitResult(object value)
    {
        ((Task)value).GetAwaiter().GetResult();
        return ReadProperty(value, "Result");
    }

    private static object ReadProperty(object value, string name) =>
        value.GetType().GetProperty(name)!.GetValue(value)
        ?? throw new InvalidOperationException($"The updater returned no {name}.");
}

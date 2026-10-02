using System.IO;
using System.Text.Json;
using SkiaSharp;
using StreamStudio.Installation;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Processes;
using StreamlinkVlcStudio.Infrastructure.Vlc;

namespace StreamlinkVlcStudio.App.Wpf;

internal static class ApplicationDependencyVerifier
{
    internal const string Command = "--maintenance-verify-dependencies";

    internal static int Run(string[] args)
    {
        var minimums = ReadMinimums();
        var machineOnly = args.Length == 1;
        if (!machineOnly && (args.Length != 5 || args[1] != "--streamlink-path" || args[3] != "--vlc-directory"))
            throw new ArgumentException("Dependency verification requires both --streamlink-path and --vlc-directory.");
        var streamlink = machineOnly
            ? Path.Combine(WindowsDependencyProbe.GetProgramFiles64Directory(), "Streamlink", "bin", "streamlink.exe")
            : Path.GetFullPath(args[2]);
        var vlc = machineOnly ? WindowsDependencyProbe.FindMachineVlcDirectory() : Path.GetFullPath(args[4]);
        var failures = new List<string>();
        Check("Streamlink", () => AssertMinimum("streamlink", WindowsDependencyProbe.ReadStreamlinkVersion(streamlink, 10_000), minimums), failures);
        Check("VLC", () =>
        {
            AssertMinimum("vlc", WindowsDependencyProbe.ReadVlcVersion(vlc), minimums);
            VerifyVlcRuntime(vlc);
        }, failures);
        Check("WebView2", () =>
        {
            AssertMinimum("webview2", WindowsDependencyProbe.ReadWebView2Version(machineOnly), minimums);
            // Start an isolated hidden browser and execute local JavaScript. Registry
            // entries and version strings alone cannot prove the browser payload works.
            AssertMinimum("webview2", WindowsDependencyProbe.NormalizeVersion(WebView2DependencyVerifier.Verify()), minimums);
        }, failures);
        Check("Skia", () =>
        {
            using var bitmap = new SKBitmap(1, 1);
            bitmap.SetPixel(0, 0, SKColors.White);
            if (bitmap.GetPixel(0, 0) != SKColors.White) throw new InvalidDataException("The native drawing runtime failed its pixel check.");
        }, failures);
        Check("HarfBuzz", () =>
        {
            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf8("Stream Studio");
            if (buffer.Length == 0) throw new InvalidDataException("The native text runtime failed to populate its buffer.");
        }, failures);
        foreach (var failure in failures) Console.Error.WriteLine(failure);
        return failures.Count == 0 ? 0 : 1;
    }

    private static Dictionary<string, Version> ReadMinimums()
    {
        using var stream = typeof(ExecutableResolver).Assembly.GetManifestResourceStream("StreamStudio.WindowsDependencies.json")
            ?? throw new InvalidDataException("The embedded Windows dependency manifest is missing.");
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("The embedded Windows dependency manifest is unsupported.");
        var dependencies = document.RootElement.GetProperty("dependencies");
        var result = new Dictionary<string, Version>(StringComparer.Ordinal);
        foreach (var name in new[] { "streamlink", "vlc", "webview2" })
        {
            var entry = dependencies.GetProperty(name);
            var value = entry.TryGetProperty("minimumVersion", out var minimum) ? minimum.GetString() : entry.GetProperty("version").GetString();
            var normalized = WindowsDependencyProbe.NormalizeVersion(value ?? string.Empty);
            if (!Version.TryParse(normalized, out var version) || version == new Version(0, 0, 0, 0))
                throw new InvalidDataException($"The embedded {name} dependency minimum is invalid.");
            result.Add(name, version);
        }
        return result;
    }

    private static void AssertMinimum(string name, string reported, IReadOnlyDictionary<string, Version> minimums)
    {
        if (!Version.TryParse(reported, out var installed) || installed < minimums[name])
            throw new InvalidDataException($"A working x64 {name} runtime {minimums[name]} or newer is required; detected '{reported}'.");
        Console.WriteLine($"Verified {name} {installed}.");
    }

    private static void Check(string name, Action verify, ICollection<string> failures)
    {
        try { verify(); Console.WriteLine($"{name}: ready."); }
        catch (Exception ex) { failures.Add($"{name}: {ex.Message}"); }
    }

    internal static void VerifyVlcRuntime(string directory)
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var verificationRoot = Path.Combine(temporaryRoot, "StreamStudio-dependency-check-vlc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(verificationRoot);
        try
        {
            // Use the same core selection and embedded resources as playback.
            // A stock libvlc_new alone does not test the app's bundled native code.
            var overlay = VlcOverlayBundledResourceExtractor.TryExtract(new DependencyLogger(), verificationRoot)
                ?? throw new InvalidDataException("The bundled VLC chat plugin and controller could not be extracted.");
            var plugin = VlcOverlayDirectoryResolver.GetPluginPath(overlay);
            var controller = VlcOverlayDirectoryResolver.GetControllerPath(overlay);
            if (!WindowsDependencyProbe.IsX64PortableExecutable(plugin) ||
                !WindowsDependencyProbe.IsX64PortableExecutable(controller))
                throw new InvalidDataException("The bundled chat plugin or controller is not a valid x64 executable.");
            var command = BoundedProcessRunner.CreateRedirectedStartInfo(controller, ["--help"]);
            command.WorkingDirectory = Path.GetDirectoryName(controller)!;
            var result = new BoundedProcessRunner().RunAsync(command, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            if (result.TimedOut || result.OutputWasTruncated || result.ExitCode != 0 ||
                !(result.StandardOutput + result.StandardError).Contains("vlc_chat_overlay --channel", StringComparison.Ordinal))
                throw new InvalidDataException("The bundled chat controller could not start and report its supported arguments.");

            var overlayPlugins = Path.Combine(verificationRoot, "plugins");
            Directory.CreateDirectory(Path.Combine(overlayPlugins, "spu"));
            File.Copy(plugin, Path.Combine(overlayPlugins, "spu", VlcOverlayDirectoryResolver.PluginFileName));
            var replayPlugins = VlcReplayPausePlugin.Prepare();
            LibVlcNative.ConfigureVlcDirectory(directory);
            if (LibVlcNative.SetEnvironmentVariable("VLC_PLUGIN_PATH",
                    string.Join(Path.PathSeparator, Path.Combine(directory, "plugins"), replayPlugins, overlayPlugins)) != 0)
                throw new InvalidDataException("The VLC dependency plugin search path could not be set.");
            // Disabling the cache forces DLL loading; stale plugins.dat entries
            // cannot conceal a corrupt module or a missing native dependency.
            var instance = LibVlcNative.CreateInstance(["--ignore-config", "--no-plugins-cache", "--no-one-instance", "--intf=dummy", "--quiet"]);
            if (instance == nint.Zero) throw new InvalidDataException("The VLC runtime could not initialize its plugins.");
            try
            {
                var modules = LibVlcNative.ReadModuleCapabilities();
                foreach (var module in new (string Name, string Capability)[] {
                    ("filesystem", "access"), ("http", "access"), ("adaptive", "demux"), ("mp4", "demux"),
                    ("ts", "demux"), ("avcodec", "video decoder"), ("avcodec", "audio decoder"),
                    ("directsound", "audio output"), ("gnutls", "tls client"), ("swscale", "video converter"),
                    ("adjust", "video filter"), ("direct3d11", "vout display"), ("wingdi", "vout display"),
                    ("myoverlay", "sub source"), ("studio_replay_pause", "demux_filter"), ("studio_adaptive", "demux") })
                    AssertVlcModule(modules, module.Name, module.Capability);
                Console.WriteLine(LibVlcNative.CoreSelectionDescription);
            }
            finally { LibVlcNative.libvlc_release(instance); }
        }
        finally
        {
            var resolved = Path.GetFullPath(verificationRoot);
            if (!resolved.StartsWith(Path.TrimEndingDirectorySeparator(temporaryRoot) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("StreamStudio-dependency-check-vlc-", StringComparison.Ordinal))
                throw new InvalidDataException("The VLC dependency check escaped its temporary directory.");
            try { Directory.Delete(resolved, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"The temporary VLC dependency check could not be removed: {ex.Message}");
            }
        }
    }

    internal static void AssertVlcModule(IReadOnlySet<(string Name, string Capability)> modules, string name, string capability)
    {
        // VLC's MP4 reader and writer share a name. A same-name module with a
        // different capability cannot establish that playback dependencies work.
        if (!modules.Contains((name, capability)))
            throw new InvalidDataException($"The required VLC module '{name}' with capability '{capability}' could not load.");
    }

    private sealed class DependencyLogger : IAppLogger
    {
        public event EventHandler<LogEntry>? EntryWritten { add { } remove { } }
        public void Write(AppLogLevel level, string source, string message, Exception? exception = null)
        {
            if (level >= AppLogLevel.Warning) Console.Error.WriteLine($"{source}: {message} {exception?.Message}");
        }
    }
}

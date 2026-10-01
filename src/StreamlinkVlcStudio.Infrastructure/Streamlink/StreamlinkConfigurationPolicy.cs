using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Infrastructure.Streamlink;

/// <summary>Keeps direct HTTP playback on Streamlink when it has custom configuration.</summary>
internal static class StreamlinkConfigurationPolicy
{
    private static readonly string[] HttpEnvironmentVariables =
        ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "REQUESTS_CA_BUNDLE", "CURL_CA_BUNDLE", "NETRC"];

    internal static string AppDataDirectory => Environment.GetEnvironmentVariable("APPDATA") ??
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    internal static bool HasDefaultHttpEnvironment()
    {
        // Python, managed HTTP clients and VLC do not share all proxy, trust and
        // authentication behavior. Preserve Streamlink's settings by using its resolver.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return HttpEnvironmentVariables.All(name => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))) &&
            !File.Exists(Path.Combine(profile, ".netrc")) &&
            !File.Exists(Path.Combine(profile, "_netrc"));
    }

    internal static bool HasDefaultConfiguration(string appData, PlatformKind platform)
    {
        var plugin = platform switch
        {
            PlatformKind.Twitch => "twitch",
            PlatformKind.Kick => "kick",
            _ => null
        };
        if (plugin is null) return false;

        try
        {
            var directory = Path.Combine(appData, "streamlink");
            if (Directory.Exists(Path.Combine(directory, "plugins"))) return false;
            foreach (var name in new[] { "config", "config." + plugin, "streamlinkrc", "streamlinkrc." + plugin })
            {
                var path = Path.Combine(directory, name);
                if (!File.Exists(path)) continue;
                if (new FileInfo(path).Length > 65536) return false;
                foreach (var line in File.ReadLines(path))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                    var separator = trimmed.IndexOf('=');
                    // The installer's ffmpeg location does not affect direct HLS playback.
                    if (separator < 0 || trimmed[..separator].Trim() != "ffmpeg-ffmpeg") return false;
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return false;
        }
    }
}

using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Infrastructure.Streamlink;

internal static class DirectVodResolutionPolicy
{
    internal static bool CanUse(StreamTransportRequest request) => CanUse(request,
        Environment.GetEnvironmentVariable("APPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    internal static bool CanUse(StreamTransportRequest request, string appData)
    {
        if (!request.Target.IsExplicitTwitchVod || request.CustomArguments.Count != 0 ||
            !QualityOption.Defaults.Any(option => option.Id == request.Quality) ||
            string.IsNullOrEmpty(request.Target.MediaId) || !request.Target.MediaId.All(char.IsAsciiDigit) ||
            !Uri.TryCreate(request.Target.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host is not ("www.twitch.tv" or "twitch.tv" or "m.twitch.tv") ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath.TrimEnd('/') != "/videos/" + request.Target.MediaId)
            return false;
        // Respect Streamlink authentication, proxies, plugin overrides and future options.
        // The installer-generated ffmpeg path does not affect a direct HLS URL lookup.
        try
        {
            var directory = Path.Combine(appData, "streamlink");
            if (Directory.Exists(Path.Combine(directory, "plugins"))) return false;
            foreach (var name in new[] { "config", "config.twitch", "streamlinkrc", "streamlinkrc.twitch" })
            {
                var path = Path.Combine(directory, name);
                if (!File.Exists(path)) continue;
                if (new FileInfo(path).Length > 65536) return false;
                foreach (var line in File.ReadLines(path))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                    var separator = trimmed.IndexOf('=');
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

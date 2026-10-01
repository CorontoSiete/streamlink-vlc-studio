using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Security;
using StreamlinkVlcStudio.Infrastructure.Vlc;

namespace StreamlinkVlcStudio.Infrastructure.Previews;

internal static class LivePreviewPolicy
{
    internal static bool CanResolve(StreamTransportRequest request) =>
        CanResolve(request, Environment.GetEnvironmentVariable("APPDATA") ??
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)) &&
        // Requests and libVLC do not share Python's custom trust/authentication settings.
        new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "REQUESTS_CA_BUNDLE", "CURL_CA_BUNDLE", "NETRC" }
            .All(name => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))) &&
        !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".netrc")) &&
        !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "_netrc"));

    internal static bool CanResolve(StreamTransportRequest request, string appData)
    {
        if (request.Target.Kind != StreamTargetKind.Live || request.CustomArguments.Count != 0 ||
            request.Quality != LibVlcLivePreview.QualityPreference || !File.Exists(request.StreamlinkPath) ||
            string.IsNullOrEmpty(request.Target.Channel) ||
            !request.Target.Channel.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-') ||
            !Uri.TryCreate(request.Target.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath.TrimEnd('/') != "/" + request.Target.Channel ||
            !(request.Target.Platform switch
            {
                PlatformKind.Twitch => uri.Host is "www.twitch.tv" or "twitch.tv" or "m.twitch.tv",
                PlatformKind.Kick => uri.Host is "kick.com" or "www.kick.com",
                _ => false
            })) return false;

        try
        {
            var directory = Path.Combine(appData, "streamlink");
            if (Directory.Exists(Path.Combine(directory, "plugins"))) return false;
            var plugin = request.Target.Platform == PlatformKind.Twitch ? "twitch" : "kick";
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
                    // The installer's ffmpeg location has no effect on video-only HLS.
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

    internal static bool IsAllowedUri(Uri uri, PlatformKind platform)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
        return ProviderUriPolicy.IsApprovedReplayUri(uri, platform) ||
            platform == PlatformKind.Kick && uri.IdnHost.EndsWith(".live-video.net", StringComparison.OrdinalIgnoreCase);
    }
}

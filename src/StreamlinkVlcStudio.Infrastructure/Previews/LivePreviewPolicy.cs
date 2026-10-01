using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Security;
using StreamlinkVlcStudio.Infrastructure.Streamlink;
using StreamlinkVlcStudio.Infrastructure.Vlc;

namespace StreamlinkVlcStudio.Infrastructure.Previews;

internal static class LivePreviewPolicy
{
    internal static bool CanResolve(StreamTransportRequest request) =>
        CanResolve(request, StreamlinkConfigurationPolicy.AppDataDirectory) &&
        StreamlinkConfigurationPolicy.HasDefaultHttpEnvironment();

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

        return StreamlinkConfigurationPolicy.HasDefaultConfiguration(appData, request.Target.Platform);
    }

    internal static bool IsAllowedUri(Uri uri, PlatformKind platform)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
        return ProviderUriPolicy.IsApprovedReplayUri(uri, platform) ||
            platform == PlatformKind.Kick && uri.IdnHost.EndsWith(".live-video.net", StringComparison.OrdinalIgnoreCase);
    }
}

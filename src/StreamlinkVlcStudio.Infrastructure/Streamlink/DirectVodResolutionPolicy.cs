using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Infrastructure.Streamlink;

internal static class DirectVodResolutionPolicy
{
    internal static bool CanUse(StreamTransportRequest request) =>
        CanUse(request, StreamlinkConfigurationPolicy.AppDataDirectory) &&
        StreamlinkConfigurationPolicy.HasDefaultHttpEnvironment();

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
        return StreamlinkConfigurationPolicy.HasDefaultConfiguration(appData, request.Target.Platform);
    }
}

using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

/// <summary>Gets a Kick app token when configured, then falls back to the usable user token.</summary>
internal static class KickAccessTokenResolver
{
    public static async Task<string?> ResolveAsync(
        ChatSettings settings,
        IAppLogger logger,
        CancellationToken cancellationToken)
    {
        var appToken = await KickOAuthService
            .TryGetAppAccessTokenAsync(settings, logger, cancellationToken)
            .ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(appToken)
            ? appToken
            : await KickOAuthService
                .GetUsableAccessTokenAsync(settings, logger, cancellationToken)
                .ConfigureAwait(false);
    }
}

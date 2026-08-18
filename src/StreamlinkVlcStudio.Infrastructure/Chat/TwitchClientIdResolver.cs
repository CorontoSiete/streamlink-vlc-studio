using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

/// <summary>
/// Resolves the Client ID validated as belonging to the OAuth token. A configured mismatch is
/// diagnostic only and is never sent to Twitch with that token.
/// </summary>
internal static class TwitchClientIdResolver
{
    public static async Task<string?> ResolveAsync(
        ChatSettings settings,
        HttpClient httpClient,
        string token,
        IAppLogger logger,
        string logCategory,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var validated = await TwitchClientIdCache.GetOrResolveAsync(
                httpClient,
                token,
                logger,
                logCategory,
                failureMessage,
                cancellationToken)
            .ConfigureAwait(false);
        WarnIfConfiguredMismatch(settings, validated, logger, logCategory);
        return validated;
    }

    public static bool WarnIfConfiguredMismatch(
        ChatSettings settings,
        string? validatedClientId,
        IAppLogger logger,
        string logCategory)
    {
        var configured = settings.TwitchClientId.Trim();
        if (string.IsNullOrWhiteSpace(configured) ||
            string.IsNullOrWhiteSpace(validatedClientId) ||
            string.Equals(configured, validatedClientId, StringComparison.Ordinal))
        {
            return false;
        }

        logger.Write(
            AppLogLevel.Warning,
            logCategory,
            $"Configured Twitch Client ID '{configured}' does not match the OAuth token's Client ID; using the validated token Client ID '{validatedClientId}'.");
        return true;
    }
}

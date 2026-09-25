using System.Globalization;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

/// <summary>Captures one acquisition's credentials and detects edits before publishing refreshed tokens.</summary>
internal sealed record KickCredentialSnapshot(
    string AccessToken,
    string RefreshToken,
    string ClientId,
    string ClientSecret,
    DateTimeOffset? ExpiresAtUtc)
{
    internal string CacheKey { get; } = OAuthTokenHelpers.CreateCredentialFingerprint(
        AccessToken, RefreshToken, ClientId, ClientSecret,
        ExpiresAtUtc?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "");

    internal static KickCredentialSnapshot Capture(ChatSettings settings) => new(
        settings.KickOAuthToken.Trim(), settings.KickRefreshToken.Trim(),
        settings.KickClientId.Trim(), settings.KickClientSecret.Trim(), settings.KickTokenExpiresAtUtc);

    internal bool Matches(ChatSettings settings) => this == Capture(settings);

    internal ChatSettings ToSettings() => new()
    {
        KickOAuthToken = AccessToken,
        KickRefreshToken = RefreshToken,
        KickClientId = ClientId,
        KickClientSecret = ClientSecret,
        KickTokenExpiresAtUtc = ExpiresAtUtc
    };
}

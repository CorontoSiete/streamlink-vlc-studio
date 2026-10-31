using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed partial class MainViewModel
{
    private Timer? accountSignInExpiryTimer;

    // Describe the saved account session, independently of chat connectivity and bonus sign-in.
    public bool IsTwitchSignedIn => HasUnexpiredAccountToken(
        Settings.Chat.TwitchOAuthToken, Settings.Chat.TwitchTokenExpiresAtUtc);

    public bool IsKickSignedIn => HasUnexpiredAccountToken(
        Settings.Chat.KickOAuthToken, Settings.Chat.KickTokenExpiresAtUtc) || CanRefreshKickSignIn;

    public string TwitchAccountSignInStatus => IsTwitchSignedIn ? "Signed in" : "Signed out";

    public string KickAccountSignInStatus => IsKickSignedIn ? "Signed in" : "Signed out";

    public string TwitchAccountSignInDescription => IsTwitchSignedIn
        ? "Your Twitch account is connected."
        : HasAccountToken(Settings.Chat.TwitchOAuthToken)
            ? "Your Twitch sign-in has expired. Connect Twitch to sign in again."
            : "Sign in in your browser to connect your Twitch account.";

    public string KickAccountSignInDescription => IsKickSignedIn
        ? "Your Kick account is connected."
        : HasAccountToken(Settings.Chat.KickOAuthToken) || !string.IsNullOrWhiteSpace(Settings.Chat.KickRefreshToken)
            ? "Your Kick sign-in needs to be renewed. Connect Kick to sign in again."
            : "Sign in in your browser to connect your Kick account.";

    private bool CanRefreshKickSignIn =>
        !string.IsNullOrWhiteSpace(Settings.Chat.KickRefreshToken) &&
        !string.IsNullOrWhiteSpace(Settings.Chat.KickClientId) &&
        (Settings.Chat.KickUseTokenService
            ? string.Equals(Settings.Chat.KickClientId.Trim(), AccountConnectionConfiguration.Current.KickClientId,
                StringComparison.Ordinal) && AccountConnectionConfiguration.Current.TryGetKickTokenServiceUri(out _)
            : !string.IsNullOrWhiteSpace(Settings.Chat.KickClientSecret));

    private static bool HasAccountToken(string token) => OAuthTokenHelpers.NormalizeBearerToken(token).Length > 0;

    private static bool HasUnexpiredAccountToken(string token, DateTimeOffset? expiresAt) =>
        HasAccountToken(token) && (expiresAt is null || expiresAt > DateTimeOffset.UtcNow);

    private static bool IsAccountSignInSetting(string? propertyName) => propertyName is null or "" or
        nameof(ChatSettings.TwitchOAuthToken) or nameof(ChatSettings.TwitchTokenExpiresAtUtc) or
        nameof(ChatSettings.KickOAuthToken) or nameof(ChatSettings.KickRefreshToken) or
        nameof(ChatSettings.KickTokenExpiresAtUtc) or nameof(ChatSettings.KickClientId) or
        nameof(ChatSettings.KickClientSecret) or nameof(ChatSettings.KickUseTokenService);

    private void RefreshAccountSignInProperties()
    {
        if (disposed) return;

        OnPropertyChanged(nameof(IsTwitchSignedIn));
        OnPropertyChanged(nameof(IsKickSignedIn));
        OnPropertyChanged(nameof(TwitchAccountSignInStatus));
        OnPropertyChanged(nameof(KickAccountSignInStatus));
        OnPropertyChanged(nameof(TwitchAccountSignInDescription));
        OnPropertyChanged(nameof(KickAccountSignInDescription));

        accountSignInExpiryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        if (!IsSettingsOpen || !IsAccountsSettingsSelected) return;

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? nextExpiry = null;
        if (HasAccountToken(Settings.Chat.TwitchOAuthToken) && Settings.Chat.TwitchTokenExpiresAtUtc > now)
            nextExpiry = Settings.Chat.TwitchTokenExpiresAtUtc;
        if (!CanRefreshKickSignIn && HasAccountToken(Settings.Chat.KickOAuthToken) &&
            Settings.Chat.KickTokenExpiresAtUtc > now &&
            (nextExpiry is null || Settings.Chat.KickTokenExpiresAtUtc < nextExpiry))
            nextExpiry = Settings.Chat.KickTokenExpiresAtUtc;

        if (nextExpiry is null) return;

        accountSignInExpiryTimer ??= new Timer(_ => dispatch(RefreshAccountSignInProperties));
        accountSignInExpiryTimer.Change(
            TimeSpan.FromMilliseconds(Math.Clamp((nextExpiry.Value - now).TotalMilliseconds, 1, int.MaxValue)),
            Timeout.InfiniteTimeSpan);
    }
}

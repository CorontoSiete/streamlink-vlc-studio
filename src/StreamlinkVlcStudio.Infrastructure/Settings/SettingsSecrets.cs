namespace StreamlinkVlcStudio.Infrastructure.Settings;

internal sealed class SettingsSecrets
{
    public string TwitchOAuthToken { get; set; } = "";
    public string KickOAuthToken { get; set; } = "";
    public string KickRefreshToken { get; set; } = "";
    public string KickClientSecret { get; set; } = "";
}

using System.Text.Json;
using System.Text.Json.Serialization;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

/// <summary>Public application registration data, shipped once by the app publisher.</summary>
public sealed record AccountConnectionConfiguration(
    string TwitchClientId = "",
    string KickClientId = "",
    string KickTokenServiceUrl = "")
{
    public static AccountConnectionConfiguration Current { get; } = Load();

    public string ResolveTwitchClientId(ChatSettings settings) =>
        string.IsNullOrWhiteSpace(settings.TwitchClientId) ? TwitchClientId.Trim() : settings.TwitchClientId.Trim();

    public bool CanConnectTwitch(ChatSettings settings) => ResolveTwitchClientId(settings).Length > 0;

    public bool CanConnectKick(ChatSettings settings) =>
        HasPersonalKickCredentials(settings) || CanUseKickTokenService(settings);

    internal bool CanUseKickTokenService(ChatSettings settings) =>
        !string.IsNullOrWhiteSpace(KickClientId) && TryGetKickTokenServiceUri(out _) &&
        (string.IsNullOrWhiteSpace(settings.KickClientId) ||
         string.Equals(settings.KickClientId.Trim(), KickClientId.Trim(), StringComparison.Ordinal));

    internal static bool HasPersonalKickCredentials(ChatSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.KickClientId) && !string.IsNullOrWhiteSpace(settings.KickClientSecret);

    internal Uri RequireKickTokenServiceUri(string clientId)
    {
        if (!string.Equals(clientId, KickClientId.Trim(), StringComparison.Ordinal) || !TryGetKickTokenServiceUri(out var uri))
            throw new InvalidOperationException("This build's Kick connection service is not configured for this account. Reconnect Kick in Settings > Accounts.");
        return uri!;
    }

    public bool TryGetKickTokenServiceUri(out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(KickTokenServiceUrl, UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps || candidate.IsLoopback ||
            candidate.UserInfo.Length > 0 || candidate.Query.Length > 0 || candidate.Fragment.Length > 0)
            return false;
        uri = candidate;
        return true;
    }

    public const string TwitchUnavailableMessage = "Twitch connection is not configured in this build. Contact the app publisher or use advanced account settings.";
    public const string KickUnavailableMessage = "Kick connection is not available in this build yet. The app publisher needs to configure its secure connection service. You can skip Kick and connect later.";

    private static AccountConnectionConfiguration Load()
    {
        using var stream = typeof(AccountConnectionConfiguration).Assembly
            .GetManifestResourceStream("StreamStudio.OAuthClients.json")
            ?? throw new InvalidOperationException("Application account configuration is missing.");
        var configuration = JsonSerializer.Deserialize<AccountConnectionConfiguration>(stream,
            new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidOperationException("Application account configuration is empty.");
        return new(configuration.TwitchClientId?.Trim() ?? "", configuration.KickClientId?.Trim() ?? "",
            configuration.KickTokenServiceUrl?.Trim() ?? "");
    }
}

using StreamlinkVlcStudio.Core.Models;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Core.Settings;

public sealed class ChatSettings : NotifyPropertyChangedObject
{
    public const double DefaultOpacity = 0.92;
    public const double MinimumFontSize = 8;
    public const double MaximumFontSize = 36;
    public const double DefaultFontSize = 13;
    public const double DefaultVlcOverlayFontSize = 15;
    public const double DefaultDockWidth = 340;
    public const double MinimumDockWidth = 220;
    public const double MaximumDockWidth = 1920;

    private ChatLayout layout = ChatLayout.Overlay;
    private bool connectAutomatically = true;
    private double opacity = DefaultOpacity;
    private double fontSize = DefaultFontSize;
    private double vlcOverlayFontSize = DefaultVlcOverlayFontSize;
    private double dockWidth = DefaultDockWidth;
    private string vlcOverlayDirectory = "";
    private string twitchUsername = "";
    private string twitchOAuthToken = "";
    private DateTimeOffset? twitchTokenExpiresAtUtc;
    private string twitchClientId = "";
    private List<string> twitchTokenScopes = [];
    private string kickUsername = "";
    private string kickOAuthToken = "";
    private string kickRefreshToken = "";
    private DateTimeOffset? kickTokenExpiresAtUtc;
    private string kickClientId = "";
    private string kickClientSecret = "";
    private bool kickSendAsBot;
    private bool kickWebhookListenerEnabled;
    private int kickWebhookListenerPort = 39180;
    private readonly KickIdentityStore kickIdentityStore = new();

    public ChatLayout Layout
    {
        get => layout;
        set => SetProperty(ref layout, Enum.IsDefined(value) ? value : ChatLayout.Overlay);
    }

    public bool ConnectAutomatically
    {
        get => connectAutomatically;
        set => SetProperty(ref connectAutomatically, value);
    }

    public double Opacity
    {
        get => opacity;
        set => SetProperty(ref opacity, double.IsFinite(value) ? Math.Clamp(value, 0, 1) : DefaultOpacity);
    }

    public double FontSize
    {
        get => fontSize;
        set => SetProperty(ref fontSize, NormalizeFontSize(value, DefaultFontSize));
    }

    public double VlcOverlayFontSize
    {
        get => vlcOverlayFontSize;
        set => SetProperty(ref vlcOverlayFontSize, NormalizeFontSize(value, DefaultVlcOverlayFontSize));
    }

    public double DockWidth
    {
        get => dockWidth;
        set => SetProperty(ref dockWidth, NormalizeDockWidth(value));
    }

    public string VlcOverlayDirectory
    {
        get => vlcOverlayDirectory;
        set => SetProperty(ref vlcOverlayDirectory, value ?? "");
    }

    public string TwitchUsername
    {
        get => twitchUsername;
        set => SetProperty(ref twitchUsername, value ?? "");
    }

    public string TwitchOAuthToken
    {
        get => twitchOAuthToken;
        set => SetProperty(ref twitchOAuthToken, value ?? "");
    }

    public DateTimeOffset? TwitchTokenExpiresAtUtc
    {
        get => twitchTokenExpiresAtUtc;
        set => SetProperty(ref twitchTokenExpiresAtUtc, value);
    }

    public string TwitchClientId
    {
        get => twitchClientId;
        set => SetProperty(ref twitchClientId, value ?? "");
    }

    public List<string> TwitchTokenScopes
    {
        get => twitchTokenScopes;
        set => SetProperty(ref twitchTokenScopes, NormalizeTokenScopes(value));
    }

    public string KickUsername
    {
        get => kickUsername;
        set => SetProperty(ref kickUsername, value ?? "");
    }

    public string KickOAuthToken
    {
        get => kickOAuthToken;
        set => SetProperty(ref kickOAuthToken, value ?? "");
    }

    public string KickRefreshToken
    {
        get => kickRefreshToken;
        set => SetProperty(ref kickRefreshToken, value ?? "");
    }

    public DateTimeOffset? KickTokenExpiresAtUtc
    {
        get => kickTokenExpiresAtUtc;
        set => SetProperty(ref kickTokenExpiresAtUtc, value);
    }

    public string KickClientId
    {
        get => kickClientId;
        set => SetProperty(ref kickClientId, value ?? "");
    }

    public string KickClientSecret
    {
        get => kickClientSecret;
        set => SetProperty(ref kickClientSecret, value ?? "");
    }

    public bool KickSendAsBot
    {
        get => kickSendAsBot;
        set => SetProperty(ref kickSendAsBot, value);
    }

    public bool KickWebhookListenerEnabled
    {
        get => kickWebhookListenerEnabled;
        set => SetProperty(ref kickWebhookListenerEnabled, value);
    }

    public int KickWebhookListenerPort
    {
        get => kickWebhookListenerPort;
        set => SetProperty(ref kickWebhookListenerPort, value <= 0 ? 39180 : Math.Clamp(value, 1024, 65535));
    }

    public Dictionary<string, string> KickChatroomIds
    {
        get => kickIdentityStore.GetChatroomIds();
        set
        {
            if (kickIdentityStore.ReplaceChatroomIds(value))
            {
                OnPropertyChanged();
            }
        }
    }

    public Dictionary<string, string> KickBroadcasterUserIds
    {
        get => kickIdentityStore.GetBroadcasterUserIds();
        set
        {
            if (kickIdentityStore.ReplaceBroadcasterUserIds(value))
            {
                OnPropertyChanged();
            }
        }
    }

    public bool TryGetKickChatroomId(string? channel, out string value) =>
        kickIdentityStore.TryGetChatroomId(channel, out value);

    public bool TryGetKickBroadcasterUserId(string? channel, out string value) =>
        kickIdentityStore.TryGetBroadcasterUserId(channel, out value);

    public bool SetKickChatroomId(string? channel, string? value)
    {
        if (!kickIdentityStore.SetChatroomId(channel, value))
        {
            return false;
        }

        OnPropertyChanged(nameof(KickChatroomIds));
        return true;
    }

    public bool SetKickBroadcasterUserId(string? channel, string? value)
    {
        if (!kickIdentityStore.SetBroadcasterUserId(channel, value))
        {
            return false;
        }

        OnPropertyChanged(nameof(KickBroadcasterUserIds));
        return true;
    }

    public static double NormalizeFontSize(double value, double fallback)
    {
        return double.IsFinite(value)
            ? Math.Clamp(value, MinimumFontSize, MaximumFontSize)
            : fallback;
    }

    public static double NormalizeDockWidth(double value)
    {
        return double.IsFinite(value)
            ? Math.Clamp(value, MinimumDockWidth, MaximumDockWidth)
            : DefaultDockWidth;
    }

    private static List<string> NormalizeTokenScopes(IEnumerable<string>? scopes)
    {
        if (scopes is null)
        {
            return [];
        }

        return scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

}

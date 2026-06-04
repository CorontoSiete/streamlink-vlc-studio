using System.ComponentModel;
using System.Runtime.CompilerServices;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;

namespace StreamlinkVlcStudio.Core.Settings;

public sealed class AppSettings : NotifyPropertyChangedObject
{
    private string? streamlinkPath;
    private string? vlcDirectory;
    private PlatformKind defaultPlatform = PlatformKind.Twitch;
    private string defaultQuality = "best";
    private bool lowLatency = true;
    private bool keepInactiveTabsRunning = true;
    private bool multiStreamEnabled;
    private string customStreamlinkArguments = "";
    private ChatSettings chat = new();
    private ReplaySettings replay = new();
    private FollowedChannelsSettings followedChannels = new();
    private List<RecentStreamSettings> recentStreams = [];
    private Dictionary<string, int> streamVolumes = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, double> streamVlcOverlayFontSizes = new(StringComparer.OrdinalIgnoreCase);
    private PictureInPictureWindowLocation? pictureInPictureWindowLocation;

    public string? StreamlinkPath
    {
        get => streamlinkPath;
        set => SetProperty(ref streamlinkPath, value);
    }

    public string? VlcDirectory
    {
        get => vlcDirectory;
        set => SetProperty(ref vlcDirectory, value);
    }

    public PlatformKind DefaultPlatform
    {
        get => defaultPlatform;
        set => SetProperty(ref defaultPlatform, value);
    }

    public string DefaultQuality
    {
        get => defaultQuality;
        set => SetProperty(ref defaultQuality, value ?? "best");
    }

    public bool LowLatency
    {
        get => lowLatency;
        set => SetProperty(ref lowLatency, value);
    }

    public bool KeepInactiveTabsRunning
    {
        get => keepInactiveTabsRunning;
        set => SetProperty(ref keepInactiveTabsRunning, value);
    }

    public bool MultiStreamEnabled
    {
        get => multiStreamEnabled;
        set => SetProperty(ref multiStreamEnabled, value);
    }

    public string CustomStreamlinkArguments
    {
        get => customStreamlinkArguments;
        set => SetProperty(ref customStreamlinkArguments, value ?? "");
    }

    public ChatSettings Chat
    {
        get => chat;
        set => SetProperty(ref chat, value ?? new());
    }

    public ReplaySettings Replay
    {
        get => replay;
        set => SetProperty(ref replay, value ?? new());
    }

    public FollowedChannelsSettings FollowedChannels
    {
        get => followedChannels;
        set => SetProperty(ref followedChannels, value ?? new());
    }

    public List<RecentStreamSettings> RecentStreams
    {
        get => recentStreams;
        set => SetProperty(ref recentStreams, NormalizeRecentStreams(value));
    }

    public Dictionary<string, int> StreamVolumes
    {
        get => streamVolumes;
        set => SetProperty(ref streamVolumes, NormalizeStreamVolumes(value));
    }

    public Dictionary<string, double> StreamVlcOverlayFontSizes
    {
        get => streamVlcOverlayFontSizes;
        set => SetProperty(ref streamVlcOverlayFontSizes, NormalizeStreamVlcOverlayFontSizes(value));
    }

    public PictureInPictureWindowLocation? PictureInPictureWindowLocation
    {
        get => pictureInPictureWindowLocation;
        set => SetProperty(ref pictureInPictureWindowLocation, NormalizePictureInPictureWindowLocation(value));
    }

    private static Dictionary<string, int> NormalizeStreamVolumes(Dictionary<string, int>? values)
    {
        var normalized = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (values is null)
        {
            return normalized;
        }

        foreach (var entry in values)
        {
            if (!string.IsNullOrWhiteSpace(entry.Key))
            {
                normalized[entry.Key.Trim()] = Math.Clamp(entry.Value, 0, 125);
            }
        }

        return normalized;
    }

    private static Dictionary<string, double> NormalizeStreamVlcOverlayFontSizes(Dictionary<string, double>? values)
    {
        var normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (values is null)
        {
            return normalized;
        }

        foreach (var entry in values)
        {
            if (!string.IsNullOrWhiteSpace(entry.Key) && double.IsFinite(entry.Value))
            {
                normalized[entry.Key.Trim()] = ChatSettings.NormalizeFontSize(entry.Value, ChatSettings.DefaultVlcOverlayFontSize);
            }
        }

        return normalized;
    }

    private static List<RecentStreamSettings> NormalizeRecentStreams(IEnumerable<RecentStreamSettings>? values)
    {
        if (values is null)
        {
            return [];
        }

        var recentStreams = new List<RecentStreamSettings>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values
            .OfType<RecentStreamSettings>()
            .OrderByDescending(value => value.LastWatchedAtUtc))
        {
            if (!Enum.IsDefined(value.Platform))
            {
                continue;
            }

            StreamTarget target;
            try
            {
                target = StreamInputParser.FromChannel(value.Platform, value.Channel);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (!seen.Add(target.StateKey))
            {
                continue;
            }

            var lastWatchedAtUtc = value.LastWatchedAtUtc == default
                ? DateTimeOffset.MinValue
                : value.LastWatchedAtUtc.ToUniversalTime();
            recentStreams.Add(new RecentStreamSettings
            {
                Platform = target.Platform,
                Channel = target.Channel,
                Url = target.Url,
                DisplayName = string.IsNullOrWhiteSpace(value.DisplayName)
                    ? target.Channel
                    : value.DisplayName.Trim(),
                ThumbnailUrl = NormalizeImageUrl(value.ThumbnailUrl),
                LastQuality = value.LastQuality?.Trim() ?? "",
                LastWatchedAtUtc = lastWatchedAtUtc
            });
        }

        return recentStreams;
    }

    private static string NormalizeImageUrl(string? url)
    {
        var trimmed = (url ?? "").Trim();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            ? "https:" + trimmed
            : trimmed;
    }

    private static PictureInPictureWindowLocation? NormalizePictureInPictureWindowLocation(PictureInPictureWindowLocation? location)
    {
        if (location is null ||
            !double.IsFinite(location.Left) ||
            !double.IsFinite(location.Top))
        {
            return null;
        }

        if (!double.IsFinite(location.Width) || location.Width < 0)
        {
            location.Width = 0;
        }

        if (!double.IsFinite(location.Height) || location.Height < 0)
        {
            location.Height = 0;
        }

        if (!Enum.IsDefined(location.FullscreenMode))
        {
            location.FullscreenMode = PictureInPictureFullscreenMode.StreamOnly;
        }

        location.FullscreenScreen = NormalizePictureInPictureFullscreenScreen(location.FullscreenScreen);
        return location;
    }

    private static PictureInPictureFullscreenScreen? NormalizePictureInPictureFullscreenScreen(
        PictureInPictureFullscreenScreen? screen)
    {
        if (screen is null ||
            !double.IsFinite(screen.Left) ||
            !double.IsFinite(screen.Top) ||
            !double.IsFinite(screen.Width) ||
            !double.IsFinite(screen.Height) ||
            screen.Width <= 0 ||
            screen.Height <= 0)
        {
            return null;
        }

        screen.DeviceName = screen.DeviceName?.Trim() ?? "";
        return screen;
    }
}

public sealed class PictureInPictureWindowLocation
{
    public PictureInPictureWindowLocation()
    {
    }

    public PictureInPictureWindowLocation(double left, double top, double width = 0, double height = 0)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool IsFullscreen { get; set; }

    public PictureInPictureFullscreenMode FullscreenMode { get; set; } = PictureInPictureFullscreenMode.StreamOnly;

    public PictureInPictureFullscreenScreen? FullscreenScreen { get; set; }
}

public enum PictureInPictureFullscreenMode
{
    StreamOnly,
    MultiView
}

public sealed class PictureInPictureFullscreenScreen
{
    public PictureInPictureFullscreenScreen()
    {
    }

    public PictureInPictureFullscreenScreen(string deviceName, double left, double top, double width, double height)
    {
        DeviceName = deviceName;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public string DeviceName { get; set; } = "";

    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }
}

public sealed class ChatSettings : NotifyPropertyChangedObject
{
    public const double MinimumFontSize = 8;
    public const double MaximumFontSize = 36;
    public const double DefaultFontSize = 13;
    public const double DefaultVlcOverlayFontSize = 15;

    private ChatLayout layout = ChatLayout.Overlay;
    private bool connectAutomatically = true;
    private double opacity = 0.92;
    private double fontSize = DefaultFontSize;
    private double vlcOverlayFontSize = DefaultVlcOverlayFontSize;
    private double dockWidth = 340;
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
    private Dictionary<string, string> kickChatroomIds = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> kickBroadcasterUserIds = new(StringComparer.OrdinalIgnoreCase);

    public ChatLayout Layout
    {
        get => layout;
        set => SetProperty(ref layout, value);
    }

    public bool ConnectAutomatically
    {
        get => connectAutomatically;
        set => SetProperty(ref connectAutomatically, value);
    }

    public double Opacity
    {
        get => opacity;
        set => SetProperty(ref opacity, value);
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
        set => SetProperty(ref dockWidth, value);
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

    public Dictionary<string, string> KickChatroomIds
    {
        get => kickChatroomIds;
        set => SetProperty(ref kickChatroomIds, value is null ? new(StringComparer.OrdinalIgnoreCase) : new(value, StringComparer.OrdinalIgnoreCase));
    }

    public Dictionary<string, string> KickBroadcasterUserIds
    {
        get => kickBroadcasterUserIds;
        set => SetProperty(ref kickBroadcasterUserIds, value is null ? new(StringComparer.OrdinalIgnoreCase) : new(value, StringComparer.OrdinalIgnoreCase));
    }

    public static double NormalizeFontSize(double value, double fallback)
    {
        return double.IsFinite(value)
            ? Math.Clamp(value, MinimumFontSize, MaximumFontSize)
            : fallback;
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

public sealed class FollowedChannelsSettings : NotifyPropertyChangedObject
{
    private List<string> kickChannelSlugs = [];

    public List<string> KickChannelSlugs
    {
        get => kickChannelSlugs;
        set => SetProperty(ref kickChannelSlugs, NormalizeChannelSlugs(value));
    }

    private static List<string> NormalizeChannelSlugs(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return [];
        }

        var slugs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var slug = (value ?? "").Trim().TrimStart('@').Trim('/');
            if (string.IsNullOrWhiteSpace(slug) || !seen.Add(slug))
            {
                continue;
            }

            slugs.Add(slug);
        }

        return slugs;
    }
}

public sealed class ReplaySettings : NotifyPropertyChangedObject
{
    private bool enabled = true;
    private bool attemptPrivateKickReplayResolution;

    public bool Enabled
    {
        get => enabled;
        set => SetProperty(ref enabled, value);
    }

    public bool AttemptPrivateKickReplayResolution
    {
        get => attemptPrivateKickReplayResolution;
        set => SetProperty(ref attemptPrivateKickReplayResolution, value);
    }
}

public sealed class RecentStreamSettings
{
    public PlatformKind Platform { get; set; }

    public string Channel { get; set; } = "";

    public string Url { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string ThumbnailUrl { get; set; } = "";

    public string LastQuality { get; set; } = "";

    public DateTimeOffset LastWatchedAtUtc { get; set; }
}

public abstract class NotifyPropertyChangedObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Core.Settings;

public sealed class AppSettings : NotifyPropertyChangedObject
{
    private string? streamlinkPath;
    private string? vlcDirectory;
    private PlatformKind defaultPlatform = PlatformKind.Twitch;
    private string defaultQuality = "best";
    private bool lowLatency = true;
    private bool keepInactiveTabsRunning;
    private VideoRendererMode videoRendererMode = VideoRendererMode.Automatic;
    private bool multiStreamEnabled;
    private bool keepHomeCardRightGap = true;
    private bool setupCompleted;
    private AppTheme theme = AppTheme.Dark;
    private WindowCloseBehavior closeBehavior = WindowCloseBehavior.Exit;
    private string customStreamlinkArguments = "";
    private ChatSettings chat = new();
    private ReplaySettings replay = new();
    private HotkeySettings hotkeys = new();
    private FollowedChannelsSettings followedChannels = new();
    private List<RecentStreamSettings> recentStreams = [];
    private Dictionary<string, int> streamVolumes = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, double> streamVlcOverlayFontSizes = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, bool> streamPictureInPictureTopBarVisibility = new(StringComparer.OrdinalIgnoreCase);
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
        set => SetProperty(ref defaultPlatform, Enum.IsDefined(value) ? value : PlatformKind.Twitch);
    }

    public string DefaultQuality
    {
        get => defaultQuality;
        set => SetProperty(ref defaultQuality, string.IsNullOrWhiteSpace(value) ? "best" : value.Trim());
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

    public VideoRendererMode VideoRendererMode
    {
        get => videoRendererMode;
        set => SetProperty(
            ref videoRendererMode,
            Enum.IsDefined(value) ? value : VideoRendererMode.Automatic);
    }

    public bool MultiStreamEnabled
    {
        get => multiStreamEnabled;
        set => SetProperty(ref multiStreamEnabled, value);
    }

    public bool KeepHomeCardRightGap
    {
        get => keepHomeCardRightGap;
        set => SetProperty(ref keepHomeCardRightGap, value);
    }

    public bool SetupCompleted
    {
        get => setupCompleted;
        set => SetProperty(ref setupCompleted, value);
    }

    public AppTheme Theme
    {
        get => theme;
        set => SetProperty(ref theme, Enum.IsDefined(value) ? value : AppTheme.Dark);
    }

    public WindowCloseBehavior CloseBehavior
    {
        get => closeBehavior;
        set => SetProperty(ref closeBehavior, Enum.IsDefined(value) ? value : WindowCloseBehavior.Exit);
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

    public HotkeySettings Hotkeys
    {
        get => hotkeys;
        set => SetProperty(ref hotkeys, value ?? new());
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

    public Dictionary<string, bool> StreamPictureInPictureTopBarVisibility
    {
        get => streamPictureInPictureTopBarVisibility;
        set => SetProperty(
            ref streamPictureInPictureTopBarVisibility,
            NormalizeStreamPictureInPictureTopBarVisibility(value));
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
                normalized[entry.Key.Trim()] = Math.Clamp(entry.Value, VolumeLimits.Min, VolumeLimits.Max);
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

    private static Dictionary<string, bool> NormalizeStreamPictureInPictureTopBarVisibility(
        IReadOnlyDictionary<string, bool>? values)
    {
        var normalized = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (values is null)
        {
            return normalized;
        }

        foreach (var entry in values)
        {
            if (!string.IsNullOrWhiteSpace(entry.Key))
            {
                normalized[entry.Key.Trim()] = entry.Value;
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
                CategoryName = value.CategoryName?.Trim() ?? "",
                ThumbnailUrl = NormalizeImageUrl(value.ThumbnailUrl),
                LastQuality = value.LastQuality?.Trim() ?? "",
                LastWatchedAtUtc = lastWatchedAtUtc
            });
        }

        return recentStreams;
    }

    private static PictureInPictureWindowLocation? NormalizePictureInPictureWindowLocation(PictureInPictureWindowLocation? location)
    {
        if (location is null ||
            !double.IsFinite(location.Left) ||
            !double.IsFinite(location.Top))
        {
            return null;
        }

        var width = double.IsFinite(location.Width) && location.Width >= 0
            ? location.Width
            : 0;
        var height = double.IsFinite(location.Height) && location.Height >= 0
            ? location.Height
            : 0;
        var fullscreenMode = Enum.IsDefined(location.FullscreenMode)
            ? location.FullscreenMode
            : PictureInPictureFullscreenMode.StreamOnly;
        return new PictureInPictureWindowLocation(location.Left, location.Top, width, height)
        {
            IsFullscreen = location.IsFullscreen,
            FullscreenMode = fullscreenMode,
            FullscreenScreen = NormalizePictureInPictureFullscreenScreen(location.FullscreenScreen)
        };
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

        return new PictureInPictureFullscreenScreen(
            screen.DeviceName?.Trim() ?? "",
            screen.Left,
            screen.Top,
            screen.Width,
            screen.Height);
    }
}

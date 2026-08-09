using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.Notifications;

/// <summary>
/// Shows desktop notifications when a followed channel goes live and reports
/// when the user activates one (clicks the toast or its action button).
/// </summary>
public interface ILiveNotificationService
{
    /// <summary>
    /// Raised when the user activates a live notification. May fire on a background
    /// thread, so subscribers must marshal to the UI thread before touching UI state.
    /// </summary>
    event Action<NotificationActivation>? Activated;

    /// <summary>
    /// Controls delivery of new notifications. Disabling delivery must also invalidate
    /// notifications that were queued but have not yet been shown.
    /// </summary>
    bool IsEnabled { get; set; }

    /// <summary>Shows a "channel is live" notification. Implementations must never throw.</summary>
    void NotifyChannelLive(LiveChannelNotification notification);
}

/// <summary>Data shown in a "followed channel is live" notification.</summary>
public sealed record LiveChannelNotification(
    PlatformKind Platform,
    string Channel,
    string DisplayName,
    string Title,
    string CategoryName,
    int? ViewerCount,
    string ThumbnailUrl);

/// <summary>Identifies the channel the user chose to open from a live notification.</summary>
public sealed record NotificationActivation(PlatformKind Platform, string Channel);

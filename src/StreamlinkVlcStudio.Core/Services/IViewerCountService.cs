using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Core.Services;

public interface IViewerCountService
{
    Task<ViewerCountResult> GetViewerCountAsync(
        StreamTarget target,
        AppSettings settings,
        CancellationToken cancellationToken = default);
}

public enum ViewerCountState
{
    Available,
    Offline,
    NotConfigured,
    Unavailable
}

/// <summary>
/// A live poll of the channel the tab is watching. <paramref name="CategoryName"/> and
/// <paramref name="StreamTitle"/> are the values the platform reports right now, so callers can
/// follow mid-stream metadata changes; they are only meaningful when <paramref name="State"/> is
/// <see cref="ViewerCountState.Available"/>. <paramref name="CategoryName"/> is empty when the
/// channel has no category set.
/// </summary>
public sealed record ViewerCountResult(
    ViewerCountState State,
    int? ViewerCount,
    string Message,
    string CategoryName = "",
    string StreamTitle = "");

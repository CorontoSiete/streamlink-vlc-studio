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

public sealed record ViewerCountResult(
    ViewerCountState State,
    int? ViewerCount,
    string Message);

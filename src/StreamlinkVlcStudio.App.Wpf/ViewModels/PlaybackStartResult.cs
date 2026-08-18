using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

/// <summary>
/// Describes the outcome of a playback startup independently from the tab's
/// current visibility state. A tab can start successfully and be paused by
/// the inactive-tab policy before the caller observes it.
/// </summary>
internal readonly record struct PlaybackStartResult(bool Succeeded, PlaybackStatus Status)
{
    public static PlaybackStartResult NotStarted => new(false, PlaybackStatus.Empty);
}

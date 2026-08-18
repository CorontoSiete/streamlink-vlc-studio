using StreamlinkVlcStudio.App.Wpf.Notifications;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

/// <summary>
/// Application composition for <see cref="MainViewModel"/>. Keeping the service graph in one
/// value makes production wiring explicit and gives tests a small, named seam.
/// </summary>
internal sealed record MainViewModelDependencies
{
    public required AppSettings Settings { get; init; }
    public required ISettingsService SettingsService { get; init; }
    public required IStreamlinkService StreamlinkService { get; init; }
    public required IPlaybackEngineFactory PlaybackFactory { get; init; }
    public required IChatClientFactory ChatFactory { get; init; }
    public required IAppLogger Logger { get; init; }
    public required Action<Action> Dispatch { get; init; }
    public IViewerCountService? ViewerCountService { get; init; }
    public IFollowedStreamsService? FollowedStreamsService { get; init; }
    public IStreamMetadataService? StreamMetadataService { get; init; }
    public IReplayResolver? ReplayResolver { get; init; }
    public IReplayChatProvider? ReplayChatProvider { get; init; }
    public TimeSpan? RecentThumbnailRefreshInterval { get; init; }
    public TimeSpan? StreamSearchDebounceInterval { get; init; }
    public ITwitchVodService? TwitchVodService { get; init; }
    public TimeSpan? TwitchVodSearchDebounceInterval { get; init; }
    public IBrowseService? BrowseService { get; init; }
    public TimeSpan? BrowseCategorySearchDebounceInterval { get; init; }
    public IKickChatHistoryProvider? KickChatHistoryProvider { get; init; }
    public TimeSpan? FollowedChannelsRefreshInterval { get; init; }
    public IStreamSearchService? StreamSearchService { get; init; }
    public IKickVodService? KickVodService { get; init; }
    public IKickEventSubscriptionService? KickEventSubscriptionService { get; init; }
    public ILiveNotificationService? LiveNotificationService { get; init; }
    public ITwitchSubOnlyVodResolver? TwitchSubOnlyVodResolver { get; init; }
    public ITwitchClipService? TwitchClipService { get; init; }
    public IAppUpdateService? AppUpdateService { get; init; }
    public Action<Uri>? OpenBrowser { get; init; }
    public Action? RequestShutdown { get; init; }
    public Func<Action, bool>? TryDispatch { get; init; }
}

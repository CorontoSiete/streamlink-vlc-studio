using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

/// <summary>Named composition for the reusable per-stream workspace boundary.</summary>
internal sealed record StreamTabViewModelDependencies
{
    public required StreamTarget Target { get; init; }
    public required string Quality { get; init; }
    public required IStreamlinkService StreamlinkService { get; init; }
    public required IPlaybackEngineFactory PlaybackFactory { get; init; }
    public required IChatClientFactory ChatFactory { get; init; }
    public required IAppLogger Logger { get; init; }
    public required Action<Action> Dispatch { get; init; }
    public int InitialVolume { get; init; } = StreamTabViewModel.DefaultVolume;
    public IViewerCountService? ViewerCountService { get; init; }
    public IReplayResolver? ReplayResolver { get; init; }
    public IReplayChatProvider? ReplayChatProvider { get; init; }
    public TimeSpan? TwitchLiveDvrPromotionPollInterval { get; init; }
    public IKickChatHistoryProvider? KickChatHistoryProvider { get; init; }
    public IKickEventSubscriptionService? KickEventSubscriptionService { get; init; }
    public ITwitchSubOnlyVodResolver? TwitchSubOnlyVodResolver { get; init; }
}

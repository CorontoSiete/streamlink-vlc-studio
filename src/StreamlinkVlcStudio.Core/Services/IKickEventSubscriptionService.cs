using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Core.Services;

public interface IKickEventSubscriptionService
{
    Task<KickEventSubscriptionEnsureResult> EnsureChatMessageSentSubscriptionAsync(
        StreamTarget target,
        ChatSettings settings,
        CancellationToken cancellationToken = default);
}

public sealed record KickEventSubscriptionEnsureResult(
    KickEventSubscriptionEnsureStatus Status,
    string Message,
    string SubscriptionId = "",
    long? BroadcasterUserId = null)
{
    public bool IsSuccess =>
        Status is KickEventSubscriptionEnsureStatus.AlreadySubscribed or
            KickEventSubscriptionEnsureStatus.Subscribed;
}

public enum KickEventSubscriptionEnsureStatus
{
    NotNeeded,
    AlreadySubscribed,
    Subscribed,
    MissingCredentials,
    MissingBroadcasterUserId,
    Unavailable
}

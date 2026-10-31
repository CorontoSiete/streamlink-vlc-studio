using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Core.Services;

public interface IFollowedStreamsService
{
    /// <summary>Loads channel status and cached artwork without waiting for optional banner/history requests.</summary>
    Task<FollowedLiveStreamsResult> GetLiveFollowedStreamsAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default);

    /// <summary>Publishes cumulative status as each platform finishes, identifying platforms still pending.</summary>
    async IAsyncEnumerable<FollowedStreamsUpdate> GetFollowedStreamUpdatesAsync(
        AppSettings settings,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new(await GetLiveFollowedStreamsAsync(settings, cancellationToken).ConfigureAwait(false), []);
    }

    /// <summary>Enriches an already displayed offline snapshot without changing channel membership or live status.</summary>
    Task<IReadOnlyList<FollowedChannel>> EnrichOfflineChannelsAsync(
        IReadOnlyList<FollowedChannel> channels,
        CancellationToken cancellationToken = default) => Task.FromResult(channels);
}

public sealed record FollowedStreamsUpdate(
    FollowedLiveStreamsResult Result,
    IReadOnlyList<PlatformKind> PendingPlatforms);

public sealed record FollowedLiveStreamsResult(
    IReadOnlyList<FollowedLiveStream> Streams,
    IReadOnlyList<string> Messages,
    IReadOnlyList<PlatformKind>? SucceededPlatforms = null,
    IReadOnlyList<FollowedChannel>? OfflineChannels = null,
    IReadOnlyList<string>? OfflineMessages = null);

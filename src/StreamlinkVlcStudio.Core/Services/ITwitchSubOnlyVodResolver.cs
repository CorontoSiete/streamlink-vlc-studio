namespace StreamlinkVlcStudio.Core.Services;

public interface ITwitchSubOnlyVodResolver
{
    Task<TwitchSubOnlyVodResolution> ResolveAsync(
        TwitchSubOnlyVodRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record TwitchSubOnlyVodRequest(string VodId, string Quality);

public sealed record TwitchSubOnlyVodResolution(
    Uri PlaybackUri,
    string QualityKey,
    string Message,
    TimeSpan MediaDuration = default,
    string OwnerLogin = "",
    DateTimeOffset? CreatedAtUtc = null);

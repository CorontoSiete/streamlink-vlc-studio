using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.Infrastructure.Replay;

public sealed class ReplayResolver : IReplayResolver
{
    private readonly TwitchReplayProvider twitch;
    private readonly KickReplayProvider kick;

    private static readonly HttpClient SharedHttpClient = HttpClientFactory.Create(
        TimeSpan.FromSeconds(12),
        includeUserAgent: true,
        acceptJson: true,
        allowAutoRedirect: false);

    public ReplayResolver(IAppLogger logger, IStreamlinkService streamlinkService)
        : this(logger, streamlinkService, SharedHttpClient)
    {
    }

    internal ReplayResolver(IAppLogger logger, IStreamlinkService streamlinkService, HttpClient httpClient)
        : this(logger, streamlinkService, httpClient, ReplayUrlSecurityValidator.Shared)
    {
    }

    internal ReplayResolver(
        IAppLogger logger,
        IStreamlinkService streamlinkService,
        HttpClient httpClient,
        ReplayUrlSecurityValidator replayUrlValidator)
        : this(
            logger,
            streamlinkService,
            httpClient,
            replayUrlValidator,
            KickTokenProvider.Shared)
    {
    }

    internal ReplayResolver(
        IAppLogger logger,
        IStreamlinkService streamlinkService,
        HttpClient httpClient,
        ReplayUrlSecurityValidator replayUrlValidator,
        IKickTokenProvider kickTokenProvider)
    {
        twitch = new TwitchReplayProvider(logger, httpClient, replayUrlValidator);
        kick = new KickReplayProvider(logger, streamlinkService, httpClient, replayUrlValidator, kickTokenProvider);
    }

    public Task<ReplaySessionInfo> ResolveCurrentReplayAsync(
        StreamTarget target,
        string quality,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Replay.Enabled)
        {
            return Task.FromResult(ReplaySessionInfo.Unavailable(
                target.Platform,
                target.Channel,
                "Replay seekbar is disabled in Settings."));
        }

        return target.Platform switch
        {
            PlatformKind.Twitch => twitch.ResolveTwitchReplayAsync(target, settings.Chat, cancellationToken),
            PlatformKind.Kick => kick.ResolveKickReplayAsync(target, quality, settings, cancellationToken),
            _ => Task.FromResult(ReplaySessionInfo.Unavailable(
                target.Platform,
                target.Channel,
                $"Replay seeking is not supported for {target.Platform}."))
        };
    }
}

public sealed record TwitchLiveStreamInfo(string UserId, string StreamId, DateTimeOffset StartedAtUtc);

public sealed record TwitchVodInfo(
    string Id,
    string StreamId,
    string Url,
    DateTimeOffset? CreatedAtUtc,
    TimeSpan Duration);

public sealed record KickLiveStreamInfo(string StreamId, DateTimeOffset? StartedAtUtc);

public sealed record KickReplayCandidate(string Url, string Id, TimeSpan Duration);

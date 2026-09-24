using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.Infrastructure.Vod;

/// <summary>
/// Routes a VOD chat fetch to the platform that owns it. All paging, retry, and frontier logic
/// lives in the per-platform fetchers; this type only picks one.
/// </summary>
public sealed class VodChatProvider : IVodChatProvider
{
    private static readonly HttpClient SharedHttpClient = HttpClientFactory.Create(
        TimeSpan.FromSeconds(20),
        includeUserAgent: true);

    private readonly TwitchVodChatFetcher twitchFetcher;
    private readonly KickVodChatFetcher kickFetcher;

    public VodChatProvider()
        : this(SharedHttpClient, NoOpAppLogger.Instance)
    {
    }

    public VodChatProvider(IAppLogger logger)
        : this(SharedHttpClient, logger)
    {
    }

    public VodChatProvider(HttpClient httpClient)
        : this(httpClient, NoOpAppLogger.Instance)
    {
    }

    public VodChatProvider(HttpClient httpClient, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        KickHttpHeaders.Configure(httpClient);
        twitchFetcher = new TwitchVodChatFetcher(httpClient);
        kickFetcher = new KickVodChatFetcher(httpClient, logger);
    }

    public async Task<VodChatFetchResult> FetchAsync(
        ReplaySessionInfo replay,
        AppSettings settings,
        TimeSpan fromOffset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(settings);

        return replay.Platform switch
        {
            PlatformKind.Twitch => await twitchFetcher
                .FetchAsync(replay, fromOffset, cancellationToken)
                .ConfigureAwait(false),
            PlatformKind.Kick => await kickFetcher
                .FetchAsync(replay, settings, fromOffset, cancellationToken)
                .ConfigureAwait(false),
            _ => VodChatFetchResult.Unsupported($"VOD chat is not supported for {replay.Platform}.")
        };
    }

    /// <summary>Lets the provider be constructed without a logger in tests and simple hosts.</summary>
    private sealed class NoOpAppLogger : IAppLogger
    {
        public static readonly NoOpAppLogger Instance = new();

        public event EventHandler<LogEntry>? EntryWritten
        {
            add { }
            remove { }
        }

        public void Write(AppLogLevel level, string source, string message, Exception? exception = null)
        {
        }
    }
}

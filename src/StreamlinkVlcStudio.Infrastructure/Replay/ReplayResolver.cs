using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Core.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Core.Time;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Twitch;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Replay;

public sealed partial class ReplayResolver : IReplayResolver
{
    // Public Twitch web Client-ID used by the installed Twitch VOD Downloader extension as its fallback.
    private const string TwitchVodDownloaderClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";
    private const string TwitchLiveDvrReplayIdPrefix = "live-dvr-";
    private const int TwitchGraphQlArchiveLimit = 100;
    private static readonly HttpClient SharedHttpClient = HttpClientFactory.Create(
        TimeSpan.FromSeconds(12),
        includeUserAgent: true,
        acceptJson: true,
        allowAutoRedirect: false);
    private static readonly TimeSpan TwitchVodStartTolerance = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan KickFallbackDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan TwitchDvrDiscoveryTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TwitchDvrRequestTimeout = TimeSpan.FromSeconds(5);
    private const int TwitchDvrMaximumConcurrency = 8;
    private static readonly int[] TwitchLiveDvrStartSecondOffsets = [0, 1, -1];
    private static readonly string[] TwitchDvrCloudFrontServers =
    [
        "d1g1f25tn8m2e6",
        "d1m7jfoe9zdc1j",
        "d1mhjrowxxagfy",
        "d1oca24q5dwo6d",
        "d1w2poirtb3as9",
        "d1xhnb4ptk05mw",
        "d1ymi26ma8va5x",
        "d2aba1wr3818hz",
        "d2dylwb3shzel1",
        "d2e2de1etea730",
        "d2nvs31859zcd8",
        "d2um2qdswy1tb0",
        "d2vjef5jvl6bfs",
        "d2xmjdvx03ij56",
        "d36nr0u3xmc4mm",
        "d3aqoihi2n8ty8",
        "d3c27h4odz752x",
        "d3vd9lfkzbru3h",
        "d6d4ismr40iw",
        "d6tizftlrpuof",
        "ddacn6pr5v0tl",
        "dgeft87wbj63p",
        "dqrpb9wgowsf5",
        "ds0h3roq6wcgc",
        "dykkng5hnh52u",
        "d3fi1amfgojobc",
        "d2v02itv0y9u9t",
        "d1mjs7qzzz669v"
    ];

    private readonly IAppLogger logger;
    private readonly IStreamlinkService streamlinkService;
    private readonly HttpClient httpClient;
    private readonly ReplayUrlSecurityValidator replayUrlValidator;
    private readonly IKickTokenProvider kickTokenProvider;
    private readonly TwitchGraphQlTransport twitchGraphQlTransport;
    private readonly KickWebsiteJsonReader kickWebsiteReader;

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
        this.logger = logger;
        this.streamlinkService = streamlinkService;
        this.httpClient = httpClient;
        this.replayUrlValidator = replayUrlValidator;
        this.kickTokenProvider = kickTokenProvider;
        twitchGraphQlTransport = new TwitchGraphQlTransport(httpClient);
        kickWebsiteReader = new KickWebsiteJsonReader(
            httpClient,
            logger,
            "Replay",
            TimeSpan.FromSeconds(18));
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
            PlatformKind.Twitch => ResolveTwitchReplayAsync(target, settings.Chat, cancellationToken),
            PlatformKind.Kick => ResolveKickReplayAsync(target, quality, settings, cancellationToken),
            _ => Task.FromResult(ReplaySessionInfo.Unavailable(
                target.Platform,
                target.Channel,
                $"Replay seeking is not supported for {target.Platform}."))
        };
    }

    private async Task<ReplaySessionInfo> ResolveTwitchReplayAsync(
        StreamTarget target,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        var token = TwitchOAuthService.NormalizeOAuthToken(settings.TwitchOAuthToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return ReplaySessionInfo.Unavailable(
                target.Platform,
                target.Channel,
                "Twitch replay lookup requires a Twitch OAuth token.");
        }

        var clientId = await TwitchClientIdResolver.ResolveAsync(
            settings,
            httpClient,
            token,
            logger,
            "Replay",
            "Could not resolve Twitch Client ID from the OAuth token.",
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return ReplaySessionInfo.Unavailable(
                target.Platform,
                target.Channel,
                "Twitch replay lookup requires a Twitch Client ID that matches the OAuth token.");
        }

        var liveStream = await GetTwitchLiveStreamAsync(target, token, clientId, cancellationToken).ConfigureAwait(false);
        if (liveStream is null)
        {
            return ReplaySessionInfo.Unavailable(
                target.Platform,
                target.Channel,
                "Twitch does not report this channel as live.");
        }

        var vods = await GetTwitchArchiveVodsAsync(liveStream.UserId, token, clientId, cancellationToken).ConfigureAwait(false);
        var vod = MatchTwitchVod(liveStream, vods);
        if (vod is not null)
        {
            return BuildTwitchVodReplaySession(target, liveStream, vod, null);
        }

        try
        {
            var graphQlVods = await GetTwitchGraphQlArchiveVodsAsync(target.Channel, cancellationToken).ConfigureAwait(false);
            var graphQlVod = MatchTwitchGraphQlVod(liveStream, graphQlVods);
            if (graphQlVod is not null)
            {
                var directDvr = await ProbeTwitchDvrPathCandidatesAsync(
                        graphQlVod.DvrPathCandidates,
                        cancellationToken)
                    .ConfigureAwait(false);
                return BuildTwitchVodReplaySession(
                    target,
                    liveStream,
                    graphQlVod.ToVodInfo(),
                    directDvr);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(
                AppLogLevel.Info,
                "Replay",
                $"Twitch GraphQL archive replay lookup failed for {target.DisplayName}; trying current-live DVR probing.",
                ex);
        }

        var currentDvr = await ProbeCurrentTwitchLiveDvrAsync(target.Channel, liveStream, cancellationToken).ConfigureAwait(false);
        if (currentDvr is not null)
        {
            var duration = currentDvr.Duration > TimeSpan.Zero
                ? currentDvr.Duration
                : DateTimeOffset.UtcNow - liveStream.StartedAtUtc;
            if (duration > TimeSpan.Zero)
            {
                return new ReplaySessionInfo(
                    target.Platform,
                    target.Channel,
                    currentDvr.Url,
                    TwitchLiveDvrReplayIdPrefix + liveStream.StreamId,
                    liveStream.StartedAtUtc,
                    duration,
                    true,
                    "",
                    "best",
                    ReplayMediaKind.CurrentLiveDvr,
                    liveStream.UserId);
            }
        }

        return ReplaySessionInfo.Unavailable(
            target.Platform,
            target.Channel,
            "No public Twitch archive VOD matched the current live stream, and current-live DVR probing did not find a valid Twitch DVR playlist.",
            liveStream.StartedAtUtc);
    }

    private async Task<TwitchLiveStreamInfo?> GetTwitchLiveStreamAsync(
        StreamTarget target,
        string token,
        string clientId,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.twitch.tv/helix/streams?user_login={Uri.EscapeDataString(target.Channel)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Client-Id", clientId);

        using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
        var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Replay",
                $"Twitch live stream lookup failed for {target.DisplayName}: {(int)response.StatusCode} {response.ReasonPhrase}. {ApiErrorMessage.Extract(responseBody)}");
            throw new InvalidOperationException("Twitch replay lookup failed. Check the Twitch Client ID and OAuth token.");
        }

        using var document = JsonDocument.Parse(responseBody);
        return ReadTwitchLiveStream(document.RootElement, target.Channel);
    }

    private async Task<IReadOnlyList<TwitchVodInfo>> GetTwitchArchiveVodsAsync(
        string userId,
        string token,
        string clientId,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.twitch.tv/helix/videos?user_id={Uri.EscapeDataString(userId)}&type=archive&first=100";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Client-Id", clientId);

        using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
        var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Replay",
                $"Twitch VOD lookup failed for user {userId}: {(int)response.StatusCode} {response.ReasonPhrase}. {ApiErrorMessage.Extract(responseBody)}");
            throw new InvalidOperationException("Twitch archive VOD lookup failed. Check the Twitch Client ID and OAuth token.");
        }

        using var document = JsonDocument.Parse(responseBody);
        return ReadTwitchArchiveVods(document.RootElement);
    }

    private async Task<IReadOnlyList<TwitchGraphQlVodCandidate>> GetTwitchGraphQlArchiveVodsAsync(
        string channel,
        CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = await twitchGraphQlTransport.SendAsync(
                BuildTwitchGraphQlArchiveVideosPayload(channel),
                TwitchVodDownloaderClientId,
                CreateTwitchGraphQlDeviceId(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (TwitchGraphQlHttpException ex)
        {
            throw new InvalidOperationException(
                $"Twitch GraphQL returned {(int)ex.StatusCode} {ex.ReasonPhrase}. {ApiErrorMessage.Extract(ex.ResponseBody)}".Trim(),
                ex);
        }
        catch (TwitchGraphQlRejectedException ex)
        {
            throw new InvalidOperationException($"Twitch GraphQL rejected archive lookup: {ex.GraphQlMessage}", ex);
        }

        using (document)
        {
            return ReadTwitchGraphQlVodCandidates(document.RootElement);
        }
    }

    private static ReplaySessionInfo BuildTwitchVodReplaySession(
        StreamTarget target,
        TwitchLiveStreamInfo liveStream,
        TwitchVodInfo vod,
        TwitchDvrProbeResult? directDvr)
    {
        var duration = directDvr?.Duration > TimeSpan.Zero
            ? directDvr.Duration
            : vod.Duration > TimeSpan.Zero
                ? vod.Duration
                : DateTimeOffset.UtcNow - liveStream.StartedAtUtc;
        if (duration <= TimeSpan.Zero)
        {
            return ReplaySessionInfo.Unavailable(
                target.Platform,
                target.Channel,
                "The matched Twitch VOD did not report a usable duration.",
                liveStream.StartedAtUtc);
        }

        var replayUrl = !string.IsNullOrWhiteSpace(directDvr?.Url)
            ? directDvr.Url
            : string.IsNullOrWhiteSpace(vod.Url)
                ? $"https://www.twitch.tv/videos/{vod.Id}"
                : vod.Url;
        return new ReplaySessionInfo(
            target.Platform,
            target.Channel,
            replayUrl,
            vod.Id,
            liveStream.StartedAtUtc,
            duration,
            true,
            "",
            directDvr is null ? "" : "best",
            ChatRoomId: liveStream.UserId);
    }

    private async Task<TwitchDvrProbeResult?> ProbeTwitchDvrPathCandidatesAsync(
        IReadOnlyList<TwitchDvrPathCandidate> pathCandidates,
        CancellationToken cancellationToken)
    {
        var urls = BuildTwitchDvrPlaylistUrls(pathCandidates)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (urls.Length == 0)
        {
            return null;
        }

        using var overallTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallTimeout.CancelAfter(TwitchDvrDiscoveryTimeout);
        using var stopWorkers = CancellationTokenSource.CreateLinkedTokenSource(overallTimeout.Token);
        TwitchDvrProbeResult? winner = null;
        var nextIndex = -1;

        async Task ProbeWorkerAsync()
        {
            while (!stopWorkers.IsCancellationRequested)
            {
                var index = Interlocked.Increment(ref nextIndex);
                if (index >= urls.Length)
                {
                    return;
                }

                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(stopWorkers.Token);
                requestTimeout.CancelAfter(TwitchDvrRequestTimeout);
                TwitchDvrProbeResult? result;
                try
                {
                    result = await ValidateTwitchDvrPlaylistAsync(urls[index], requestTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (overallTimeout.IsCancellationRequested || stopWorkers.IsCancellationRequested)
                    {
                        return;
                    }

                    continue;
                }

                if (result is not null && Interlocked.CompareExchange(ref winner, result, null) is null)
                {
                    stopWorkers.Cancel();
                    return;
                }
            }
        }

        var workers = Enumerable.Range(0, Math.Min(TwitchDvrMaximumConcurrency, urls.Length))
            .Select(_ => ProbeWorkerAsync())
            .ToArray();
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
        return winner;
    }

    private async Task<TwitchDvrProbeResult?> ProbeCurrentTwitchLiveDvrAsync(
        string channel,
        TwitchLiveStreamInfo liveStream,
        CancellationToken cancellationToken)
    {
        var channelLogin = NormalizeTwitchChannelLogin(channel);
        var startSeconds = liveStream.StartedAtUtc.ToUnixTimeSeconds();
        var pathCandidates = TwitchLiveDvrStartSecondOffsets
            .Select(offset =>
            {
                var seconds = startSeconds + offset;
                var hash = BuildTwitchDvrHash(channelLogin, liveStream.StreamId, seconds);
                return new TwitchDvrPathCandidate(hash, channelLogin, liveStream.StreamId, seconds, "");
            })
            .ToArray();

        return await ProbeTwitchDvrPathCandidatesAsync(pathCandidates, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TwitchDvrProbeResult?> ValidateTwitchDvrPlaylistAsync(
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await ValidatedReplayHttpClient.SendGetAsync(
                httpClient,
                replayUrlValidator,
                new Uri(url),
                PlatformKind.Twitch,
                static requestUri =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                    request.Headers.Accept.ParseAdd("application/vnd.apple.mpegurl");
                    request.Headers.Accept.ParseAdd("application/x-mpegURL");
                    request.Headers.Accept.ParseAdd("text/plain");
                    request.Headers.Accept.ParseAdd("*/*");
                    request.Headers.Referrer = new Uri("https://www.twitch.tv/");
                    return request;
                },
                cancellationToken).ConfigureAwait(false);
            var responseBody = await BoundedHttpContentReader.ReadPlaylistAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode ||
                !IsValidTwitchDvrPlaylist(responseBody))
            {
                return null;
            }

            _ = TryReadTwitchDvrTotalSeconds(responseBody, out var duration);
            return new TwitchDvrProbeResult(url, duration);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Info, "Replay", $"Twitch DVR playlist probe failed for {url}. {ex.Message}");
            return null;
        }
    }

    private async Task<ReplaySessionInfo> ResolveKickReplayAsync(
        StreamTarget target,
        string quality,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var accessToken = await kickTokenProvider
            .ResolveAsync(settings.Chat, logger, cancellationToken)
            .ConfigureAwait(false);
        KickLiveStreamInfo? liveStream = null;
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            try
            {
                liveStream = await GetKickLiveStreamAsync(target, accessToken, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Write(AppLogLevel.Warning, "Replay", $"Kick public API live lookup failed for {target.DisplayName}; falling back to website metadata.", ex);
            }
        }

        liveStream ??= await GetKickWebsiteLiveStreamAsync(target.Channel, cancellationToken).ConfigureAwait(false);
        if (liveStream is null)
        {
            return ReplaySessionInfo.Unavailable(
                target.Platform,
                target.Channel,
                "Kick does not report this channel as live.");
        }

        if (!settings.Replay.AttemptPrivateKickReplayResolution)
        {
            return ReplaySessionInfo.Unavailable(
                target.Platform,
                target.Channel,
                "Kick does not expose a stable public replay lookup API. Enable private Kick replay attempts in Settings to try best-effort website probing.",
                liveStream.StartedAtUtc);
        }

        var replayQuality = NormalizeKickReplayQuality(quality);
        var candidates = await GetKickPrivateReplayCandidatesAsync(target.Channel, liveStream, cancellationToken).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Uri.TryCreate(candidate.Url, UriKind.Absolute, out var candidateUri))
                {
                    continue;
                }

                await replayUrlValidator
                    .ValidateAsync(candidateUri, PlatformKind.Kick, cancellationToken)
                    .ConfigureAwait(false);
                var request = new StreamTransportRequest(
                    new StreamTarget(target.Platform, target.Channel, candidate.Url),
                    replayQuality,
                    settings.StreamlinkPath ?? "",
                    false,
                    CommandLineTokenizer.Tokenize(settings.CustomStreamlinkArguments));
                _ = await streamlinkService.ResolveStreamUrlAsync(request, cancellationToken).ConfigureAwait(false);
                return new ReplaySessionInfo(
                    target.Platform,
                    target.Channel,
                    candidate.Url,
                    candidate.Id,
                    liveStream.StartedAtUtc,
                    ResolveKickReplayDuration(candidate, liveStream),
                    true,
                    "",
                    replayQuality);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Write(AppLogLevel.Info, "Replay", $"Kick replay candidate failed Streamlink validation: {candidate.Url}. {ex.Message}");
            }
        }

        return ReplaySessionInfo.Unavailable(
            target.Platform,
            target.Channel,
            "Kick private replay probing did not find a Streamlink-playable replay URL.",
            liveStream.StartedAtUtc);
    }

    private static TimeSpan ResolveKickReplayDuration(
        KickReplayCandidate candidate,
        KickLiveStreamInfo liveStream)
    {
        if (candidate.Duration > TimeSpan.Zero)
        {
            return candidate.Duration;
        }

        if (liveStream.StartedAtUtc is { } startedAtUtc)
        {
            var elapsed = DateTimeOffset.UtcNow - startedAtUtc;
            if (elapsed > TimeSpan.Zero)
            {
                return elapsed;
            }
        }

        // A future or missing start timestamp is not a usable duration. Keep the
        // replay seekbar available with the same conservative fallback used when
        // Kick omits timing metadata entirely.
        return KickFallbackDuration;
    }

    private async Task<KickLiveStreamInfo?> GetKickLiveStreamAsync(
        StreamTarget target,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.kick.com/public/v1/channels?slug={Uri.EscapeDataString(target.Channel)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
        var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Replay",
                $"Kick live stream lookup failed for {target.DisplayName}: {(int)response.StatusCode} {response.ReasonPhrase}. {ApiErrorMessage.Extract(responseBody)}");
            throw new InvalidOperationException("Kick replay lookup failed. Check Kick API credentials.");
        }

        using var document = JsonDocument.Parse(responseBody);
        return ReadKickLiveStream(document.RootElement, target.Channel);
    }

    private async Task<KickLiveStreamInfo?> GetKickWebsiteLiveStreamAsync(
        string channel,
        CancellationToken cancellationToken)
    {
        var escapedChannel = Uri.EscapeDataString(channel);
        var url = $"https://kick.com/api/v2/channels/{escapedChannel}";
        var body = await GetKickWebsiteProbeBodyAsync(url, channel, expectsJson: true, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return ReadKickWebsiteLiveStream(document.RootElement, channel);
        }
        catch (JsonException ex)
        {
            logger.Write(AppLogLevel.Info, "Replay", $"Kick website live metadata returned invalid JSON for {channel}. {ex.Message}");
            return null;
        }
    }

    private async Task<IReadOnlyList<KickReplayCandidate>> GetKickPrivateReplayCandidatesAsync(
        string channel,
        KickLiveStreamInfo liveStream,
        CancellationToken cancellationToken)
    {
        var escapedChannel = Uri.EscapeDataString(channel);
        var urls = new[]
        {
            $"https://kick.com/api/v2/channels/{escapedChannel}/videos",
            $"https://kick.com/api/v1/channels/{escapedChannel}/videos",
            $"https://kick.com/{escapedChannel}/videos"
        };
        var candidates = new List<KickReplayCandidate>();
        foreach (var url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = await GetKickWebsiteProbeBodyAsync(
                url,
                channel,
                expectsJson: url.Contains("/api/", StringComparison.OrdinalIgnoreCase),
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            candidates.AddRange(ReadKickPrivateReplayCandidates(channel, body, liveStream));
        }

        return candidates
            .GroupBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private async Task<string?> GetKickWebsiteProbeBodyAsync(
        string url,
        string channel,
        bool expectsJson,
        CancellationToken cancellationToken)
    {
        return await kickWebsiteReader.ReadAsync(
                url,
                $"https://kick.com/{Uri.EscapeDataString(channel)}",
                cancellationToken,
                expectsJson ? KickWebsitePayloadKind.Json : KickWebsitePayloadKind.Html)
            .ConfigureAwait(false);
    }

    public static TwitchLiveStreamInfo? ReadTwitchLiveStream(JsonElement root, string channel)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var login = GetOptionalString(item, "user_login");
            if (!string.IsNullOrWhiteSpace(login) &&
                !string.Equals(login, channel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var userId = GetOptionalString(item, "user_id");
            var streamId = GetOptionalString(item, "id");
            if (string.IsNullOrWhiteSpace(userId) ||
                string.IsNullOrWhiteSpace(streamId) ||
                !TryGetDateTimeOffset(item, "started_at", out var startedAt))
            {
                return null;
            }

            return new TwitchLiveStreamInfo(userId, streamId, startedAt.ToUniversalTime());
        }

        return null;
    }

    public static IReadOnlyList<TwitchVodInfo> ReadTwitchArchiveVods(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var vods = new List<TwitchVodInfo>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = GetOptionalString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            _ = TryParseTwitchDuration(GetOptionalString(item, "duration"), out var duration);
            _ = TryGetDateTimeOffset(item, "created_at", out var createdAt) ||
                TryGetDateTimeOffset(item, "published_at", out createdAt);
            vods.Add(new TwitchVodInfo(
                id,
                GetOptionalString(item, "stream_id"),
                GetOptionalString(item, "url"),
                createdAt == default ? null : createdAt.ToUniversalTime(),
                duration));
        }

        return vods;
    }

    private static IReadOnlyList<TwitchGraphQlVodCandidate> ReadTwitchGraphQlVodCandidates(JsonElement root)
    {
        var candidates = new List<TwitchGraphQlVodCandidate>();
        foreach (var node in EnumerateTwitchGraphQlVideoNodes(root))
        {
            var id = GetOptionalString(node, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var broadcastType = GetOptionalString(node, "broadcastType");
            if (!string.IsNullOrWhiteSpace(broadcastType) &&
                !string.Equals(broadcastType, "ARCHIVE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DateTimeOffset? createdAt = null;
            if (TryGetDateTimeOffset(node, "publishedAt", out var parsedCreatedAt) ||
                TryGetDateTimeOffset(node, "createdAt", out parsedCreatedAt))
            {
                createdAt = parsedCreatedAt.ToUniversalTime();
            }

            var duration = TryReadTwitchLengthSeconds(node, out var parsedDuration)
                ? parsedDuration
                : TimeSpan.Zero;
            var previewUrls = ReadTwitchGraphQlPreviewUrls(node);
            var pathCandidates = ReadTwitchDvrPathCandidates(previewUrls);
            var streamId = pathCandidates
                .Select(candidate => candidate.StreamId)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
            candidates.Add(new TwitchGraphQlVodCandidate(
                id,
                GetOptionalString(node, "title"),
                streamId,
                string.IsNullOrWhiteSpace(id) ? "" : $"https://www.twitch.tv/videos/{id}",
                createdAt,
                duration,
                pathCandidates));
        }

        return candidates
            .GroupBy(candidate => candidate.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    public static TwitchVodInfo? MatchTwitchVod(TwitchLiveStreamInfo liveStream, IEnumerable<TwitchVodInfo> vods)
    {
        var vodList = vods.ToArray();
        var streamIdMatch = vodList.FirstOrDefault(vod =>
            !string.IsNullOrWhiteSpace(vod.StreamId) &&
            string.Equals(vod.StreamId, liveStream.StreamId, StringComparison.Ordinal));
        if (streamIdMatch is not null)
        {
            return streamIdMatch;
        }

        return vodList
            .Where(vod =>
                string.IsNullOrWhiteSpace(vod.StreamId) ||
                string.Equals(vod.StreamId, liveStream.StreamId, StringComparison.Ordinal))
            .Where(vod => vod.CreatedAtUtc is not null)
            .Select(vod => new
            {
                Vod = vod,
                Distance = (vod.CreatedAtUtc!.Value - liveStream.StartedAtUtc).Duration()
            })
            .Where(item => item.Distance <= TwitchVodStartTolerance)
            .OrderBy(item => item.Distance)
            .Select(item => item.Vod)
            .FirstOrDefault();
    }

    private static TwitchGraphQlVodCandidate? MatchTwitchGraphQlVod(
        TwitchLiveStreamInfo liveStream,
        IEnumerable<TwitchGraphQlVodCandidate> candidates)
    {
        var candidateList = candidates.ToArray();
        var match = MatchTwitchVod(
            liveStream,
            candidateList.Select(candidate => candidate.ToVodInfo()));
        if (match is null)
        {
            return null;
        }

        return candidateList.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, match.Id, StringComparison.Ordinal));
    }

    public static bool TryParseTwitchDuration(string value, out TimeSpan duration)
    {
        return DurationValues.TryParseHmsDuration(value, out duration);
    }

    public static bool TryReadTwitchDvrTotalSeconds(string playlist, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(playlist))
        {
            return false;
        }

        var match = TwitchDvrTotalSecondsPattern().Match(playlist);
        if (!match.Success ||
            !double.TryParse(match.Groups["seconds"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            seconds <= 0)
        {
            return false;
        }

        return DurationValues.TryCreatePositive(seconds, TimeSpan.TicksPerSecond, out duration);
    }

    public static bool IsValidTwitchDvrPlaylist(string playlist)
    {
        if (string.IsNullOrWhiteSpace(playlist) ||
            !playlist.Contains("#EXTM3U", StringComparison.Ordinal))
        {
            return false;
        }

        return playlist.Contains("#EXTINF", StringComparison.Ordinal) &&
            TwitchDvrMediaSegmentPattern().IsMatch(playlist);
    }

    private static IEnumerable<JsonElement> EnumerateTwitchGraphQlVideoNodes(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("videos", out var videos) &&
                    videos.ValueKind == JsonValueKind.Object)
                {
                    foreach (var node in EnumerateTwitchGraphQlVideoConnectionNodes(videos))
                    {
                        yield return node;
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    foreach (var node in EnumerateTwitchGraphQlVideoNodes(property.Value))
                    {
                        yield return node;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var node in EnumerateTwitchGraphQlVideoNodes(item))
                    {
                        yield return node;
                    }
                }

                break;
        }
    }

    private static IEnumerable<JsonElement> EnumerateTwitchGraphQlVideoConnectionNodes(JsonElement videos)
    {
        if (!videos.TryGetProperty("edges", out var edges) ||
            edges.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var edge in edges.EnumerateArray())
        {
            if (edge.ValueKind == JsonValueKind.Object &&
                edge.TryGetProperty("node", out var node) &&
                node.ValueKind == JsonValueKind.Object)
            {
                yield return node;
            }
        }
    }

    private static bool TryReadTwitchLengthSeconds(JsonElement node, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        return node.ValueKind == JsonValueKind.Object &&
            node.TryGetProperty("lengthSeconds", out var property) &&
            TryGetPositiveDuration(property, TimeSpan.TicksPerSecond, out duration);
    }

    private static IReadOnlyList<string> ReadTwitchGraphQlPreviewUrls(JsonElement node)
    {
        var urls = new List<string>();
        foreach (var propertyName in new[] { "animatedPreviewURL", "previewThumbnailURL", "thumbnailURL", "thumbnailUrl" })
        {
            var value = GetOptionalString(node, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                urls.Add(value);
            }
        }

        return urls;
    }

    private static IReadOnlyList<TwitchDvrPathCandidate> ReadTwitchDvrPathCandidates(IEnumerable<string> urls)
    {
        var candidates = new List<TwitchDvrPathCandidate>();
        foreach (var rawUrl in urls)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                continue;
            }

            var url = UnescapeJsonUrl(rawUrl);
            foreach (Match match in TwitchDvrPathPattern().Matches(url))
            {
                if (!long.TryParse(match.Groups["startSeconds"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var startSeconds))
                {
                    continue;
                }

                candidates.Add(new TwitchDvrPathCandidate(
                    match.Groups["hash"].Value,
                    match.Groups["channel"].Value,
                    match.Groups["streamId"].Value,
                    startSeconds,
                    TryReadTwitchDvrServer(url, match)));
            }
        }

        return candidates
            .GroupBy(candidate => $"{candidate.Server}|{candidate.DirectoryName}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static string TryReadTwitchDvrServer(string url, Match pathMatch)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Host.EndsWith(".cloudfront.net", StringComparison.OrdinalIgnoreCase))
        {
            return uri.Host[..^".cloudfront.net".Length];
        }

        var prefix = url[..pathMatch.Index];
        var match = TwitchStaticCdnVodServerPattern().Match(prefix);
        return match.Success ? match.Groups["server"].Value : "";
    }

    private static IEnumerable<string> BuildTwitchDvrPlaylistUrls(IEnumerable<TwitchDvrPathCandidate> pathCandidates)
    {
        foreach (var pathCandidate in pathCandidates)
        {
            var servers = string.IsNullOrWhiteSpace(pathCandidate.Server)
                ? TwitchDvrCloudFrontServers
                : new[] { pathCandidate.Server }
                    .Concat(TwitchDvrCloudFrontServers.Where(server =>
                        !string.Equals(server, pathCandidate.Server, StringComparison.OrdinalIgnoreCase)));

            foreach (var server in servers)
            {
                yield return $"https://{server}.cloudfront.net/{pathCandidate.DirectoryName}/chunked/index-dvr.m3u8";
            }
        }
    }

    private static string BuildTwitchGraphQlArchiveVideosPayload(string channel)
    {
        var payload = new[]
        {
            new
            {
                operationName = "FilterableVideoTower_Videos",
                variables = new
                {
                    login = NormalizeTwitchChannelLogin(channel),
                    limit = TwitchGraphQlArchiveLimit
                },
                query = """
                query FilterableVideoTower_Videos($login: String!, $limit: Int!) {
                  user(login: $login) {
                    id
                    login
                    displayName
                    videos(first: $limit, type: ARCHIVE, sort: TIME) {
                      edges {
                        node {
                          id
                          title
                          createdAt
                          publishedAt
                          lengthSeconds
                          broadcastType
                          animatedPreviewURL
                          previewThumbnailURL(width: 320, height: 180)
                          owner {
                            login
                            displayName
                          }
                        }
                      }
                    }
                  }
                }
                """
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string NormalizeTwitchChannelLogin(string channel) =>
        channel.Trim().ToLowerInvariant();

    private static string BuildTwitchDvrHash(string channelLogin, string streamId, long startSeconds)
    {
        var hashInput = $"{channelLogin}_{streamId}_{startSeconds}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(hashInput))).ToLowerInvariant()[..20];
    }

    private static string CreateTwitchGraphQlDeviceId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static KickLiveStreamInfo? ReadKickLiveStream(JsonElement root, string channel)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var slug = GetOptionalString(item, "slug");
            if (!string.IsNullOrWhiteSpace(slug) &&
                !string.Equals(slug, channel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.TryGetProperty("stream", out var stream) ||
                stream.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (TryGetBool(stream, "is_live") == false)
            {
                return null;
            }

            DateTimeOffset? startedAt = null;
            if (TryGetDateTimeOffset(stream, "started_at", out var started) ||
                TryGetDateTimeOffset(stream, "start_time", out started) ||
                TryGetDateTimeOffset(stream, "created_at", out started))
            {
                startedAt = started.ToUniversalTime();
            }

            return new KickLiveStreamInfo(
                GetOptionalString(stream, "id"),
                startedAt);
        }

        return null;
    }

    public static KickLiveStreamInfo? ReadKickWebsiteLiveStream(JsonElement root, string channel)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var slug = GetOptionalString(root, "slug");
        if (!string.IsNullOrWhiteSpace(slug) &&
            !string.Equals(slug, channel, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!root.TryGetProperty("livestream", out var livestream) ||
            livestream.ValueKind != JsonValueKind.Object ||
            TryGetBool(livestream, "is_live") == false)
        {
            return null;
        }

        DateTimeOffset? startedAt = null;
        if (TryGetDateTimeOffset(livestream, "started_at", out var started) ||
            TryGetDateTimeOffset(livestream, "start_time", out started) ||
            TryGetDateTimeOffset(livestream, "created_at", out started))
        {
            startedAt = started.ToUniversalTime();
        }

        return new KickLiveStreamInfo(
            GetOptionalString(livestream, "id"),
            startedAt);
    }

    public static IReadOnlyList<KickReplayCandidate> ReadKickPrivateReplayCandidates(
        string channel,
        string responseBody,
        KickLiveStreamInfo liveStream)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return [];
        }

        var candidates = new List<KickReplayCandidate>();
        var parsedJson = TryParseJson(responseBody, out var document);
        if (parsedJson)
        {
            using (document)
            {
                ReadKickPrivateReplayCandidatesFromJson(channel, document.RootElement, liveStream, candidates);
            }
        }

        if (!parsedJson && candidates.Count == 0)
        {
            foreach (Match match in KickHlsUrlPattern().Matches(responseBody))
            {
                var url = UnescapeJsonUrl(match.Value);
                candidates.Add(new KickReplayCandidate(url, BuildKickReplayCandidateId(url), KickFallbackDuration));
            }

            foreach (Match match in KickVideoPathPattern().Matches(responseBody))
            {
                var path = match.Groups["path"].Value.Trim('\\', '"');
                var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? path
                    : $"https://kick.com{path}";
                candidates.Add(new KickReplayCandidate(UnescapeJsonUrl(url), BuildKickReplayCandidateId(url), KickFallbackDuration));
            }
        }

        return candidates
            .Where(candidate =>
                Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri) &&
                ReplayUrlSecurityValidator.TryValidateProviderUri(uri, PlatformKind.Kick))
            .GroupBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static void ReadKickPrivateReplayCandidatesFromJson(
        string channel,
        JsonElement element,
        KickLiveStreamInfo liveStream,
        List<KickReplayCandidate> candidates)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var looksLikeCurrentReplay = LooksLikeReplayObject(element, liveStream);
                var url = FirstNonEmpty(
                    GetOptionalString(element, "source"),
                    GetOptionalString(element, "playback_url"),
                    GetOptionalString(element, "video_url"),
                    GetOptionalString(element, "hls"),
                    GetOptionalString(element, "url"));
                var id = FirstNonEmpty(
                    GetOptionalString(element, "uuid"),
                    GetNestedOptionalString(element, "video", "uuid"),
                    GetOptionalString(element, "id"),
                    BuildKickReplayCandidateId(url));
                var duration = TryReadKickDuration(element, out var parsedDuration)
                    ? parsedDuration
                    : TimeSpan.Zero;
                if (duration == TimeSpan.Zero &&
                    element.TryGetProperty("video", out var nestedVideo) &&
                    TryReadKickDuration(nestedVideo, out parsedDuration))
                {
                    duration = parsedDuration;
                }

                if (IsLikelyKickReplayUrl(url) && looksLikeCurrentReplay)
                {
                    candidates.Add(new KickReplayCandidate(UnescapeJsonUrl(url), id, duration));
                }

                var videoId = FirstNonEmpty(
                    GetOptionalString(element, "uuid"),
                    GetNestedOptionalString(element, "video", "uuid"));
                if (!string.IsNullOrWhiteSpace(videoId) && looksLikeCurrentReplay)
                {
                    candidates.Add(new KickReplayCandidate(
                        $"https://kick.com/{channel}/videos/{Uri.EscapeDataString(videoId)}",
                        videoId,
                        duration));
                }

                foreach (var property in element.EnumerateObject())
                {
                    ReadKickPrivateReplayCandidatesFromJson(channel, property.Value, liveStream, candidates);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ReadKickPrivateReplayCandidatesFromJson(channel, item, liveStream, candidates);
                }

                break;
        }
    }

    private static bool LooksLikeReplayObject(JsonElement element, KickLiveStreamInfo liveStream)
    {
        if (!string.IsNullOrWhiteSpace(liveStream.StreamId) &&
            ElementContainsKickLiveStreamId(element, liveStream.StreamId))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(liveStream.StreamId) &&
            ElementDeclaresDifferentKickLiveStreamId(element, liveStream.StreamId))
        {
            return false;
        }

        if (TryGetBool(element, "is_live") == true)
        {
            return true;
        }

        if (liveStream.StartedAtUtc is null)
        {
            return string.IsNullOrWhiteSpace(liveStream.StreamId);
        }

        if (!TryGetDateTimeOffset(element, "created_at", out var createdAt) &&
            !TryGetDateTimeOffset(element, "start_time", out createdAt) &&
            !TryGetDateTimeOffset(element, "published_at", out createdAt))
        {
            return string.IsNullOrWhiteSpace(liveStream.StreamId);
        }

        return (createdAt.ToUniversalTime() - liveStream.StartedAtUtc.Value).Duration() <= TimeSpan.FromHours(2);
    }

    private static bool ElementContainsKickLiveStreamId(JsonElement element, string liveStreamId)
    {
        foreach (var propertyName in new[] { "id", "live_stream_id", "livestream_id" })
        {
            if (string.Equals(GetOptionalString(element, propertyName), liveStreamId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (element.TryGetProperty("video", out var video) &&
            video.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "live_stream_id", "livestream_id" })
            {
                if (string.Equals(GetOptionalString(video, propertyName), liveStreamId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ElementDeclaresDifferentKickLiveStreamId(JsonElement element, string liveStreamId)
    {
        foreach (var propertyName in new[] { "live_stream_id", "livestream_id" })
        {
            var value = GetOptionalString(element, propertyName);
            if (!string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value, liveStreamId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        var topLevelId = GetOptionalString(element, "id");
        if (!string.IsNullOrWhiteSpace(topLevelId) &&
            element.TryGetProperty("source", out _) &&
            !string.Equals(topLevelId, liveStreamId, StringComparison.Ordinal))
        {
            return true;
        }

        if (element.TryGetProperty("video", out var video) &&
            video.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "live_stream_id", "livestream_id" })
            {
                var value = GetOptionalString(video, propertyName);
                if (!string.IsNullOrWhiteSpace(value) &&
                    !string.Equals(value, liveStreamId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadKickDuration(JsonElement element, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty("duration_seconds", out var secondsProperty) &&
            TryGetPositiveDuration(secondsProperty, TimeSpan.TicksPerSecond, out duration))
        {
            return true;
        }

        if (!element.TryGetProperty("duration", out var property))
        {
            return false;
        }

        return TryGetPositiveDuration(property, TimeSpan.TicksPerMillisecond, out duration);
    }

    private static bool IsLikelyKickReplayUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            (url.Contains("kick.com", StringComparison.OrdinalIgnoreCase) &&
                url.Contains("/videos/", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildKickReplayCandidateId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "kick-replay";
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()))).ToLowerInvariant()[..16];
    }

    private static bool TryParseJson(string value, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    // Twitch GraphQL replay chat messages are rebuilt by concatenating fragment text verbatim,
    // so this reader must NOT trim (trimming drops the spaces between fragments).
    private static string GetOptionalString(JsonElement element, string propertyName)
    {
        return JsonElementReader.GetOptionalString(element, propertyName, trimStrings: false);
    }

    private static string GetNestedOptionalString(JsonElement element, string objectPropertyName, string propertyName)
    {
        if (!element.TryGetProperty(objectPropertyName, out var nested) ||
            nested.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        return GetOptionalString(nested, propertyName);
    }

    private static string UnescapeJsonUrl(string value) =>
        value.Trim().Trim('"').Replace("\\/", "/", StringComparison.Ordinal);

    private static string NormalizeKickReplayQuality(string quality)
    {
        var normalized = string.IsNullOrWhiteSpace(quality) ? "best" : quality.Trim();
        return normalized.ToLowerInvariant() switch
        {
            "source" => "best",
            "1080p" => "1080p60",
            "720p" => "720p60",
            "audio_only" => "best",
            _ => normalized
        };
    }

    [GeneratedRegex(@"(?<hash>[A-Za-z0-9]{20})_(?<channel>[_A-Za-z0-9]+)_(?<streamId>\d+)_(?<startSeconds>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex TwitchDvrPathPattern();

    [GeneratedRegex(@"cf_vods/(?<server>[A-Za-z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TwitchStaticCdnVodServerPattern();

    [GeneratedRegex(@"(?im)^#EXT-X-TWITCH-TOTAL-SECS:(?<seconds>\d+(?:\.\d+)?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TwitchDvrTotalSecondsPattern();

    [GeneratedRegex(@"(?im)^[^#\r\n][^\r\n]*\.(?:ts|mp4)(?:[?#][^\r\n]*)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TwitchDvrMediaSegmentPattern();

    [GeneratedRegex(@"https?:\\?/\\?/[^""'\s<>]+?\.m3u8[^""'\s<>]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KickHlsUrlPattern();

    [GeneratedRegex(@"(?<path>(?:https?:\\?/\\?/[^""'\s<>]+)?/[^""'\s<>]*/videos/[^""'\s<>\\]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KickVideoPathPattern();

    private sealed record TwitchGraphQlVodCandidate(
        string Id,
        string Title,
        string StreamId,
        string Url,
        DateTimeOffset? CreatedAtUtc,
        TimeSpan Duration,
        IReadOnlyList<TwitchDvrPathCandidate> DvrPathCandidates)
    {
        public TwitchVodInfo ToVodInfo() => new(Id, StreamId, Url, CreatedAtUtc, Duration);
    }

    private sealed record TwitchDvrPathCandidate(
        string Hash,
        string Channel,
        string StreamId,
        long StartSeconds,
        string Server)
    {
        public string DirectoryName => $"{Hash}_{Channel}_{StreamId}_{StartSeconds}";
    }

    private sealed record TwitchDvrProbeResult(string Url, TimeSpan Duration);
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

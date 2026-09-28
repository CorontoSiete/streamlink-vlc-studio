using static StreamlinkVlcStudio.Infrastructure.Replay.ReplayPayloadReader;
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
using StreamlinkVlcStudio.Infrastructure.Viewers;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Replay;

internal sealed partial class TwitchReplayProvider
{
    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly ReplayUrlSecurityValidator replayUrlValidator;
    internal TwitchReplayProvider(IAppLogger logger, HttpClient httpClient, ReplayUrlSecurityValidator replayUrlValidator)
    {
        this.logger = logger;
        this.httpClient = httpClient;
        this.replayUrlValidator = replayUrlValidator;
        twitchGraphQlTransport = new TwitchGraphQlTransport(httpClient);
    }
    private const string TwitchLiveDvrReplayIdPrefix = "live-dvr-";
    private const int TwitchGraphQlArchiveLimit = 100;
    private static readonly TimeSpan TwitchVodStartTolerance = TimeSpan.FromMinutes(30);
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
    private readonly TwitchGraphQlTransport twitchGraphQlTransport;

    internal async Task<ReplaySessionInfo> ResolveTwitchReplayAsync(
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
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
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
        using var request = TwitchApiRequest.Create(HttpMethod.Get, url, token, clientId);

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
        using var request = TwitchApiRequest.Create(HttpMethod.Get, url, token, clientId);

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
                TwitchGraphQlTransport.PublicClientId,
                TwitchGraphQlTransport.CreateDeviceId(),
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
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.Write(AppLogLevel.Info, "Replay", $"Twitch DVR playlist probe failed for {url}. {ex.Message}");
            return null;
        }
    }

    public static TwitchLiveStreamInfo? ReadTwitchLiveStream(JsonElement root, string channel)
    {
        if (!JsonElementReader.TryGetArray(root, "data", out var data))
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var login = ReadReplayString(item, "user_login");
            if (!string.IsNullOrWhiteSpace(login) &&
                !string.Equals(login, channel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var userId = ReadReplayString(item, "user_id");
            var streamId = ReadReplayString(item, "id");
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
        if (!JsonElementReader.TryGetArray(root, "data", out var data))
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

            var id = ReadReplayString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            _ = TryParseTwitchDuration(ReadReplayString(item, "duration"), out var duration);
            _ = TryGetDateTimeOffset(item, "created_at", out var createdAt) ||
                TryGetDateTimeOffset(item, "published_at", out createdAt);
            vods.Add(new TwitchVodInfo(
                id,
                ReadReplayString(item, "stream_id"),
                ReadReplayString(item, "url"),
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
            var id = ReadReplayString(node, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var broadcastType = ReadReplayString(node, "broadcastType");
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
            var value = ReadReplayString(node, propertyName);
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

    [GeneratedRegex(@"(?<hash>[A-Za-z0-9]{20})_(?<channel>[_A-Za-z0-9]+)_(?<streamId>\d+)_(?<startSeconds>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex TwitchDvrPathPattern();

    [GeneratedRegex(@"cf_vods/(?<server>[A-Za-z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TwitchStaticCdnVodServerPattern();

    [GeneratedRegex(@"(?im)^#EXT-X-TWITCH-TOTAL-SECS:(?<seconds>\d+(?:\.\d+)?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TwitchDvrTotalSecondsPattern();

    [GeneratedRegex(@"(?im)^[^#\r\n][^\r\n]*\.(?:ts|mp4)(?:[?#][^\r\n]*)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TwitchDvrMediaSegmentPattern();

    private sealed record TwitchGraphQlVodCandidate(
        string Id,
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

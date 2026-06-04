using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;

namespace StreamlinkVlcStudio.Infrastructure.Replay;

public sealed partial class ReplayResolver : IReplayResolver
{
    private const string TwitchGraphQlEndpoint = "https://gql.twitch.tv/gql";
    // Public Twitch web Client-ID used by the installed Twitch VOD Downloader extension as its fallback.
    private const string TwitchVodDownloaderClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";
    private const string TwitchLiveDvrReplayIdPrefix = "live-dvr-";
    private const int TwitchGraphQlArchiveLimit = 100;
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly TimeSpan TwitchVodStartTolerance = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan KickFallbackDuration = TimeSpan.FromHours(12);
    private static readonly Dictionary<string, TwitchClientIdCacheEntry> TwitchClientIdCache = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim TwitchClientIdCacheGate = new(1, 1);
    private static readonly TimeSpan TwitchClientIdFallbackCacheLifetime = TimeSpan.FromHours(6);
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

    public ReplayResolver(IAppLogger logger, IStreamlinkService streamlinkService)
        : this(logger, streamlinkService, SharedHttpClient)
    {
    }

    public ReplayResolver(IAppLogger logger, IStreamlinkService streamlinkService, HttpClient httpClient)
    {
        this.logger = logger;
        this.streamlinkService = streamlinkService;
        this.httpClient = httpClient;
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

        var clientId = await ResolveTwitchClientIdAsync(settings, token, cancellationToken).ConfigureAwait(false);
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

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Replay",
                $"Twitch live stream lookup failed for {target.DisplayName}: {(int)response.StatusCode} {response.ReasonPhrase}. {ExtractApiMessage(responseBody)}");
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

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Replay",
                $"Twitch VOD lookup failed for user {userId}: {(int)response.StatusCode} {response.ReasonPhrase}. {ExtractApiMessage(responseBody)}");
            throw new InvalidOperationException("Twitch archive VOD lookup failed. Check the Twitch Client ID and OAuth token.");
        }

        using var document = JsonDocument.Parse(responseBody);
        return ReadTwitchArchiveVods(document.RootElement);
    }

    private async Task<IReadOnlyList<TwitchGraphQlVodCandidate>> GetTwitchGraphQlArchiveVodsAsync(
        string channel,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TwitchGraphQlEndpoint);
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.AcceptLanguage.ParseAdd("en-US");
        request.Headers.Referrer = new Uri("https://www.twitch.tv/");
        request.Headers.TryAddWithoutValidation("Client-Id", TwitchVodDownloaderClientId);
        request.Headers.TryAddWithoutValidation("X-Device-Id", CreateTwitchGraphQlDeviceId());
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
        request.Content = new StringContent(
            BuildTwitchGraphQlArchiveVideosPayload(channel),
            Encoding.UTF8,
            "text/plain");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Twitch GraphQL returned {(int)response.StatusCode} {response.ReasonPhrase}. {ExtractApiMessage(responseBody)}".Trim());
        }

        using var document = JsonDocument.Parse(responseBody);
        var graphQlError = ExtractTwitchGraphQlError(document.RootElement);
        if (!string.IsNullOrWhiteSpace(graphQlError))
        {
            throw new InvalidOperationException($"Twitch GraphQL rejected archive lookup: {graphQlError}");
        }

        return ReadTwitchGraphQlVodCandidates(document.RootElement);
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
        foreach (var url in BuildTwitchDvrPlaylistUrls(pathCandidates))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ValidateTwitchDvrPlaylistAsync(url, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
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
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/vnd.apple.mpegurl");
            request.Headers.Accept.ParseAdd("application/x-mpegURL");
            request.Headers.Accept.ParseAdd("text/plain");
            request.Headers.Accept.ParseAdd("*/*");
            request.Headers.Referrer = new Uri("https://www.twitch.tv/");

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<string?> ResolveTwitchClientIdAsync(
        ChatSettings settings,
        string token,
        CancellationToken cancellationToken)
    {
        var configured = settings.TwitchClientId.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var tokenKey = Fingerprint(token);
        await TwitchClientIdCacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TwitchClientIdCache.TryGetValue(tokenKey, out var cached) &&
                cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                return cached.ClientId;
            }

            TwitchTokenInfo tokenInfo;
            try
            {
                tokenInfo = await TwitchOAuthService.ValidateTokenAsync(httpClient, token, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Write(AppLogLevel.Warning, "Replay", "Could not resolve Twitch Client ID from the OAuth token.", ex);
                return null;
            }

            var expiresAt = tokenInfo.ExpiresAtUtc is { } tokenExpiry
                ? Min(tokenExpiry, DateTimeOffset.UtcNow.Add(TwitchClientIdFallbackCacheLifetime))
                : DateTimeOffset.UtcNow.Add(TwitchClientIdFallbackCacheLifetime);
            TwitchClientIdCache[tokenKey] = new TwitchClientIdCacheEntry(tokenInfo.ClientId, expiresAt);
            return tokenInfo.ClientId;
        }
        finally
        {
            TwitchClientIdCacheGate.Release();
        }
    }

    private async Task<ReplaySessionInfo> ResolveKickReplayAsync(
        StreamTarget target,
        string quality,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var accessToken = await ResolveKickAccessTokenAsync(settings.Chat, cancellationToken).ConfigureAwait(false);
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
                    candidate.Duration > TimeSpan.Zero
                        ? candidate.Duration
                        : liveStream.StartedAtUtc is { } startedAt ? DateTimeOffset.UtcNow - startedAt : KickFallbackDuration,
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

    private async Task<string?> ResolveKickAccessTokenAsync(ChatSettings settings, CancellationToken cancellationToken)
    {
        var appToken = await KickOAuthService.TryGetAppAccessTokenAsync(settings, logger, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(appToken))
        {
            return appToken;
        }

        return await KickOAuthService.GetUsableAccessTokenAsync(settings, logger, cancellationToken).ConfigureAwait(false);
    }

    private async Task<KickLiveStreamInfo?> GetKickLiveStreamAsync(
        StreamTarget target,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.kick.com/public/v1/channels?slug={Uri.EscapeDataString(target.Channel)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Replay",
                $"Kick live stream lookup failed for {target.DisplayName}: {(int)response.StatusCode} {response.ReasonPhrase}. {ExtractApiMessage(responseBody)}");
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
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyKickWebsiteHeaders(request, channel, expectsJson);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(body))
            {
                return body;
            }

            logger.Write(
                AppLogLevel.Info,
                "Replay",
                $"Kick website replay probe returned {(int)response.StatusCode} {response.ReasonPhrase} for {url}. {ExtractApiMessage(body)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Info, "Replay", $"Kick website replay probe failed for {url}. {ex.Message}");
        }

        return await GetKickWebsiteProbeBodyWithCurlAsync(url, channel, expectsJson, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> GetKickWebsiteProbeBodyWithCurlAsync(
        string url,
        string channel,
        bool expectsJson,
        CancellationToken cancellationToken)
    {
        var curlPath = ResolveCurlPath();
        if (string.IsNullOrWhiteSpace(curlPath))
        {
            logger.Write(AppLogLevel.Info, "Replay", "curl.exe was not found; Kick website replay probe fallback is unavailable.");
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = curlPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in BuildKickWebsiteCurlArguments(url, channel, expectsJson))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.Write(AppLogLevel.Info, "Replay", $"curl.exe could not start for Kick replay probe {url}. {ex.Message}");
            return null;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(18));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await KillProcessTreeAsync(process).ConfigureAwait(false);
            logger.Write(AppLogLevel.Info, "Replay", $"curl.exe timed out probing Kick replay metadata for {url}.");
            return null;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            return stdout;
        }

        logger.Write(AppLogLevel.Info, "Replay", $"curl.exe failed probing Kick replay metadata for {url}: {stderr.Trim()}");
        return null;
    }

    private static void ApplyKickWebsiteHeaders(HttpRequestMessage request, string channel, bool expectsJson)
    {
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
        request.Headers.Accept.ParseAdd(expectsJson
            ? "application/json, text/plain, */*"
            : "text/html, application/xhtml+xml, application/xml;q=0.9, */*;q=0.8");
        request.Headers.AcceptLanguage.ParseAdd("*");
        request.Headers.Referrer = new Uri($"https://kick.com/{Uri.EscapeDataString(channel)}");
    }

    private static IEnumerable<string> BuildKickWebsiteCurlArguments(string url, string channel, bool expectsJson)
    {
        yield return "--location";
        yield return "--silent";
        yield return "--show-error";
        yield return "--fail";
        yield return "--compressed";
        yield return "--max-time";
        yield return "15";
        yield return "--user-agent";
        yield return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";
        yield return "--header";
        yield return expectsJson
            ? "Accept: application/json,text/plain,*/*"
            : "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
        yield return "--header";
        yield return "Accept-Language: *";
        yield return "--referer";
        yield return $"https://kick.com/{Uri.EscapeDataString(channel)}";
        yield return url;
    }

    private static async Task KillProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
        }
    }

    public static TwitchLiveStreamInfo? ReadTwitchLiveStream(JsonElement root, string channel)
    {
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("user_login", out var login) &&
                !string.Equals(login.GetString(), channel, StringComparison.OrdinalIgnoreCase))
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
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var vods = new List<TwitchVodInfo>();
        foreach (var item in data.EnumerateArray())
        {
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

    public static IReadOnlyList<TwitchVodInfo> ReadTwitchGraphQlArchiveVods(JsonElement root)
    {
        return ReadTwitchGraphQlVodCandidates(root)
            .Select(candidate => candidate.ToVodInfo())
            .ToArray();
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
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = TwitchDurationPattern().Match(value.Trim());
        if (!match.Success)
        {
            return false;
        }

        var hours = ParseOptionalInt(match.Groups["h"].Value);
        var minutes = ParseOptionalInt(match.Groups["m"].Value);
        var seconds = ParseOptionalInt(match.Groups["s"].Value);
        if (hours == 0 && minutes == 0 && seconds == 0)
        {
            return false;
        }

        duration = new TimeSpan(hours, minutes, seconds);
        return true;
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

        duration = TimeSpan.FromSeconds(seconds);
        return true;
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
        if (!node.TryGetProperty("lengthSeconds", out var property))
        {
            return false;
        }

        double seconds;
        if (property.ValueKind == JsonValueKind.Number)
        {
            if (!property.TryGetDouble(out seconds))
            {
                return false;
            }
        }
        else if (property.ValueKind == JsonValueKind.String)
        {
            if (!double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        if (seconds <= 0)
        {
            return false;
        }

        duration = TimeSpan.FromSeconds(seconds);
        return true;
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

    private static string ExtractTwitchGraphQlError(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var error = ExtractTwitchGraphQlError(item);
                if (!string.IsNullOrWhiteSpace(error))
                {
                    return error;
                }
            }

            return "";
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("errors", out var errors) ||
            errors.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        return errors
            .EnumerateArray()
            .Select(error => GetOptionalString(error, "message"))
            .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message)) ?? "";
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
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("slug", out var slug) &&
                !string.Equals(slug.GetString(), channel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.TryGetProperty("stream", out var stream) ||
                stream.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

            if (stream.ValueKind != JsonValueKind.Object ||
                TryGetBool(stream, "is_live") == false)
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
        if (root.TryGetProperty("slug", out var slug) &&
            !string.Equals(slug.GetString(), channel, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!root.TryGetProperty("livestream", out var livestream) ||
            livestream.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
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
        DateTimeOffset? liveStartedAtUtc)
    {
        return ReadKickPrivateReplayCandidates(
            channel,
            responseBody,
            new KickLiveStreamInfo("", liveStartedAtUtc));
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
        if (TryParseJson(responseBody, out var document))
        {
            using (document)
            {
                ReadKickPrivateReplayCandidatesFromJson(channel, document.RootElement, liveStream, candidates);
            }
        }

        if (candidates.Count == 0)
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
            .Where(candidate => Uri.TryCreate(candidate.Url, UriKind.Absolute, out _))
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
                if (IsLikelyKickReplayUrl(url) && looksLikeCurrentReplay)
                {
                    candidates.Add(new KickReplayCandidate(UnescapeJsonUrl(url), id, duration));
                }

                if (duration == TimeSpan.Zero &&
                    element.TryGetProperty("video", out var nestedVideo) &&
                    TryReadKickDuration(nestedVideo, out parsedDuration))
                {
                    duration = parsedDuration;
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
        if (element.TryGetProperty("duration_seconds", out var secondsProperty))
        {
            return TryReadDurationValue(secondsProperty, value => TimeSpan.FromSeconds(Math.Max(0, value)), out duration);
        }

        if (!element.TryGetProperty("duration", out var property))
        {
            return false;
        }

        return TryReadDurationValue(property, value => TimeSpan.FromMilliseconds(Math.Max(0, value)), out duration);
    }

    private static bool TryReadDurationValue(
        JsonElement property,
        Func<double, TimeSpan> numericDurationFactory,
        out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value))
        {
            duration = numericDurationFactory(value);
            return duration > TimeSpan.Zero;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            var text = property.GetString();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                duration = numericDurationFactory(value);
                return duration > TimeSpan.Zero;
            }

            if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out duration))
            {
                return duration > TimeSpan.Zero;
            }
        }

        return false;
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

    private static bool? TryGetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static bool TryGetDateTimeOffset(JsonElement element, string propertyName, out DateTimeOffset value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return DateTimeOffset.TryParse(
            property.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);
    }

    private static string GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return "";
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? "",
            JsonValueKind.Number => property.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
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

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private static int ParseOptionalInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

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

    private static string ExtractApiMessage(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? "";
            }

            if (document.RootElement.TryGetProperty("error_description", out var errorDescription))
            {
                return errorDescription.GetString() ?? "";
            }

            if (document.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
        }

        return responseBody.Length <= 240 ? responseBody : responseBody[..240];
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamlinkVlcStudio/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static string? ResolveCurlPath()
    {
        var configured = Environment.GetEnvironmentVariable("STREAMLINK_KICK_CURL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        return "curl.exe";
    }

    private static string Fingerprint(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()))).ToLowerInvariant();
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right)
    {
        return left <= right ? left : right;
    }

    [GeneratedRegex(@"^(?:(?<h>\d+)h)?(?:(?<m>\d+)m)?(?:(?<s>\d+)s)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TwitchDurationPattern();

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

    private sealed record TwitchClientIdCacheEntry(string ClientId, DateTimeOffset ExpiresAtUtc);

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

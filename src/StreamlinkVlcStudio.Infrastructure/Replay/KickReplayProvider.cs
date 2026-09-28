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

internal sealed partial class KickReplayProvider
{
    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly ReplayUrlSecurityValidator replayUrlValidator;
    private readonly IStreamlinkService streamlinkService;
    internal KickReplayProvider(IAppLogger logger, IStreamlinkService streamlinkService, HttpClient httpClient, ReplayUrlSecurityValidator replayUrlValidator, IKickTokenProvider kickTokenProvider)
    {
        this.logger = logger;
        this.streamlinkService = streamlinkService;
        this.httpClient = httpClient;
        this.replayUrlValidator = replayUrlValidator;
        this.kickTokenProvider = kickTokenProvider;
        kickWebsiteReader = new KickWebsiteJsonReader(httpClient, logger, "Replay", TimeSpan.FromSeconds(18));
    }
    private static readonly TimeSpan KickFallbackDuration = TimeSpan.FromHours(12);
    private readonly IKickTokenProvider kickTokenProvider;
    private readonly KickWebsiteJsonReader kickWebsiteReader;

    internal async Task<ReplaySessionInfo> ResolveKickReplayAsync(
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
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
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
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
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

    public static KickLiveStreamInfo? ReadKickLiveStream(JsonElement root, string channel)
    {
        var payload = LiveChannelPayloadReader.Read(PlatformKind.Kick, channel, root);
        return payload.State == LiveChannelState.Available ? ReadKickLiveStreamInfo(payload.Stream) : null;
    }

    public static KickLiveStreamInfo? ReadKickWebsiteLiveStream(JsonElement root, string channel)
    {
        if (!TryGetNonEmptyString(root, "slug", out var slug) ||
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

        return ReadKickLiveStreamInfo(livestream);
    }

    private static KickLiveStreamInfo ReadKickLiveStreamInfo(JsonElement stream)
    {
        DateTimeOffset? startedAt = null;
        if (TryGetDateTimeOffset(stream, "started_at", out var started) ||
            TryGetDateTimeOffset(stream, "start_time", out started) ||
            TryGetDateTimeOffset(stream, "created_at", out started))
        {
            startedAt = started.ToUniversalTime();
        }

        return new KickLiveStreamInfo(
            ReadReplayString(stream, "id"),
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
                    ReadReplayString(element, "source"),
                    ReadReplayString(element, "playback_url"),
                    ReadReplayString(element, "video_url"),
                    ReadReplayString(element, "hls"),
                    ReadReplayString(element, "url"));
                var id = FirstNonEmpty(
                    ReadReplayString(element, "uuid"),
                    GetNestedOptionalString(element, "video", "uuid"),
                    ReadReplayString(element, "id"),
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
                    ReadReplayString(element, "uuid"),
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
            if (string.Equals(ReadReplayString(element, propertyName), liveStreamId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (element.TryGetProperty("video", out var video) &&
            video.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "live_stream_id", "livestream_id" })
            {
                if (string.Equals(ReadReplayString(video, propertyName), liveStreamId, StringComparison.Ordinal))
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
            var value = ReadReplayString(element, propertyName);
            if (!string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value, liveStreamId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        var topLevelId = ReadReplayString(element, "id");
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
                var value = ReadReplayString(video, propertyName);
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

    private static string GetNestedOptionalString(JsonElement element, string objectPropertyName, string propertyName)
    {
        if (!element.TryGetProperty(objectPropertyName, out var nested) ||
            nested.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        return ReadReplayString(nested, propertyName);
    }

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

    [GeneratedRegex(@"https?:\\?/\\?/[^""'\s<>]+?\.m3u8[^""'\s<>]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KickHlsUrlPattern();

    [GeneratedRegex(@"(?<path>(?:https?:\\?/\\?/[^""'\s<>]+)?/[^""'\s<>]*/videos/[^""'\s<>\\]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KickVideoPathPattern();
}

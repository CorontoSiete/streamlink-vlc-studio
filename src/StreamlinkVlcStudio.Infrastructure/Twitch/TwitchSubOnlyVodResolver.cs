using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Twitch;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Replay;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;

namespace StreamlinkVlcStudio.Infrastructure.Twitch;

/// <summary>
/// Resolves subscriber-only Twitch VODs to local HLS playlists by deriving the public
/// CloudFront segment URLs from the VOD's storyboard metadata — the same technique as the
/// TwitchNoSub browser extension (https://github.com/besuper/TwitchNoSub), reimplemented
/// for desktop playback. Used only as a fallback when Streamlink cannot resolve the VOD.
/// </summary>
public sealed partial class TwitchSubOnlyVodResolver : ITwitchSubOnlyVodResolver
{
    // Public Twitch web Client-ID, the same one ReplayResolver uses for archive lookups.
    private const string TwitchPublicClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";
    private const int PlaylistProbeByteLimit = 65535;
    private static readonly TimeSpan VariantProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StalePlaylistAge = TimeSpan.FromHours(24);
    // TwitchNoSub compares createdAt against this frozen cutoff instead of "now":
    // newer uploads do not expose the index-dvr layout, so they use the archive URL
    // shape (which simply yields no variants and a clean error).
    private static readonly DateTimeOffset TwitchUploadLayoutCutoff = new(2023, 2, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly HttpClient SharedHttpClient = HttpClientFactory.Create(
        TimeSpan.FromSeconds(20),
        allowAutoRedirect: false);
    private static readonly string DefaultPlaylistDirectory = Path.Combine(
        Path.GetTempPath(),
        "StreamlinkVlcStudio",
        "sub-only-vods");

    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly string playlistDirectory;
    private readonly ReplayUrlSecurityValidator replayUrlValidator;
    private readonly TwitchGraphQlTransport twitchGraphQlTransport;

    public TwitchSubOnlyVodResolver(IAppLogger logger)
        : this(logger, SharedHttpClient, DefaultPlaylistDirectory)
    {
    }

    internal TwitchSubOnlyVodResolver(IAppLogger logger, HttpClient httpClient, string playlistDirectory)
        : this(logger, httpClient, playlistDirectory, ReplayUrlSecurityValidator.Shared)
    {
    }

    internal TwitchSubOnlyVodResolver(
        IAppLogger logger,
        HttpClient httpClient,
        string playlistDirectory,
        ReplayUrlSecurityValidator replayUrlValidator)
    {
        this.logger = logger;
        this.httpClient = httpClient;
        this.playlistDirectory = playlistDirectory;
        this.replayUrlValidator = replayUrlValidator;
        twitchGraphQlTransport = new TwitchGraphQlTransport(httpClient);
        SweepStalePlaylists();
    }

    public async Task<TwitchSubOnlyVodResolution> ResolveAsync(
        TwitchSubOnlyVodRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var vodId = (request.VodId ?? "").Trim();
        if (!TwitchVodIdPattern().IsMatch(vodId))
        {
            throw new InvalidOperationException($"'{request.VodId}' is not a valid Twitch VOD id.");
        }

        var metadata = await FetchVideoMetadataAsync(vodId, cancellationToken).ConfigureAwait(false);
        if (!TwitchSubOnlyVodPlaylist.TryParseStoryboardLocation(metadata.SeekPreviewsUrl, out var host, out var specialId))
        {
            throw new InvalidOperationException(
                $"Could not derive the direct playlist location for VOD {vodId} from its storyboard URL.");
        }

        var candidates = new List<(string Key, string Url)>();
        foreach (var qualityKey in TwitchSubOnlyVodPlaylist.QualityKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = TwitchSubOnlyVodPlaylist.BuildVariantPlaylistUrl(
                metadata.BroadcastType,
                metadata.CreatedAtUtc ?? DateTimeOffset.MinValue,
                TwitchUploadLayoutCutoff,
                host,
                specialId,
                metadata.OwnerLogin,
                vodId,
                qualityKey);
            if (await ProbeVariantAsync(url, cancellationToken).ConfigureAwait(false))
            {
                candidates.Add((qualityKey, url));
            }
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No playable qualities were found for VOD {vodId}. Recently uploaded VODs are not supported by the sub-only fallback.");
        }

        var selectedKey = TwitchSubOnlyVodPlaylist.SelectQualityKey(
            candidates.Select(candidate => candidate.Key).ToArray(),
            request.Quality);
        var playlistUrl = candidates.First(candidate => candidate.Key == selectedKey).Url;
        var playlistContent = await FetchStringAsync(playlistUrl, ranged: false, cancellationToken).ConfigureAwait(false);
        var rewritten = TwitchSubOnlyVodPlaylist.RewriteMediaPlaylist(playlistContent, new Uri(playlistUrl));

        Directory.CreateDirectory(playlistDirectory);
        var playlistPath = Path.Combine(playlistDirectory, $"{vodId}-{selectedKey}.m3u8");
        // No Encoding.UTF8 here: it would prepend a BOM, and libVLC's HLS demuxer
        // refuses playlists that do not start with "#EXTM3U" (black screen).
        await WritePlaylistAtomicallyAsync(playlistPath, rewritten, cancellationToken).ConfigureAwait(false);
        logger.Write(
            AppLogLevel.Info,
            "SubOnlyVod",
            $"Resolved sub-only VOD {vodId} via direct CloudFront playlist ({selectedKey}): {playlistUrl}");
        return new TwitchSubOnlyVodResolution(
            new Uri(playlistPath),
            selectedKey,
            $"Resolved sub-only VOD via direct CloudFront playlist ({selectedKey}).",
            metadata.MediaDuration,
            metadata.OwnerLogin,
            metadata.CreatedAtUtc);
    }

    private async Task<TwitchVideoMetadata> FetchVideoMetadataAsync(string vodId, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = await twitchGraphQlTransport.SendAsync(
                BuildVideoQueryPayload(vodId),
                TwitchPublicClientId,
                CreateDeviceId(),
                cancellationToken,
                mediaType: "application/json").ConfigureAwait(false);
        }
        catch (TwitchGraphQlHttpException ex)
        {
            throw new InvalidOperationException(
                $"Twitch GraphQL returned {(int)ex.StatusCode} {ex.ReasonPhrase} for VOD {vodId}. {ApiErrorMessage.Extract(ex.ResponseBody, includeBodyFallback: false)}".Trim(),
                ex);
        }
        catch (TwitchGraphQlRejectedException ex)
        {
            throw new InvalidOperationException($"Twitch GraphQL rejected the VOD lookup: {ex.GraphQlMessage}", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("video", out var video) ||
                video.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"VOD {vodId} was not found or is not public.");
            }

            return new TwitchVideoMetadata(
                GetOptionalString(video, "broadcastType"),
                TryReadTwitchLengthSeconds(video),
                TryGetDateTimeOffset(video, "createdAt"),
                GetOptionalString(video, "seekPreviewsURL"),
                TryReadNestedString(video, "owner", "login"));
        }
    }

    private async Task<bool> ProbeVariantAsync(string url, CancellationToken cancellationToken)
    {
        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeTimeout.CancelAfter(VariantProbeTimeout);
        try
        {
            var content = await FetchStringAsync(url, ranged: true, probeTimeout.Token).ConfigureAwait(false);
            return content.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal);
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A slow variant is unavailable for selection; continue probing the remaining
            // qualities without waiting for the shared HttpClient's much longer timeout.
            return false;
        }
    }

    private async Task<string> FetchStringAsync(string url, bool ranged, CancellationToken cancellationToken)
    {
        using var response = await ValidatedReplayHttpClient.SendGetAsync(
            httpClient,
            replayUrlValidator,
            new Uri(url),
            PlatformKind.Twitch,
            requestUri =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                if (ranged)
                {
                    request.Headers.Range = new RangeHeaderValue(0, PlaylistProbeByteLimit);
                }

                return request;
            },
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GET {url} returned {(int)response.StatusCode}.");
        }

        return ranged
            ? await BoundedHttpContentReader.ReadRangeProbeAsync(response.Content, cancellationToken).ConfigureAwait(false)
            : await BoundedHttpContentReader.ReadPlaylistAsync(response.Content, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WritePlaylistAtomicallyAsync(
        string playlistPath,
        string content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{playlistPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            // Write without a BOM, then replace the destination in one filesystem
            // operation so VLC never opens a half-written playlist.
            await File.WriteAllTextAsync(
                    temporaryPath,
                    content,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, playlistPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A completed playlist is still usable if cleanup loses a race with
                // another resolver; stale files are swept on the next construction.
            }
        }
    }

    private void SweepStalePlaylists()
    {
        try
        {
            if (!Directory.Exists(playlistDirectory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - StalePlaylistAge;
            foreach (var path in Directory.EnumerateFiles(playlistDirectory, "*.m3u8"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.Write(AppLogLevel.Warning, "SubOnlyVod", $"Could not delete stale sub-only VOD playlist '{path}': {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Write(AppLogLevel.Warning, "SubOnlyVod", $"Could not sweep stale sub-only VOD playlists: {ex.Message}");
        }
    }

    private static string BuildVideoQueryPayload(string vodId)
    {
        var payload = new
        {
            query = $"query {{ video(id: \"{vodId}\") {{ broadcastType, createdAt, lengthSeconds, seekPreviewsURL, owner {{ login }} }} }}"
        };
        return JsonSerializer.Serialize(payload);
    }

    private static TimeSpan TryReadTwitchLengthSeconds(JsonElement video)
    {
        return video.ValueKind == JsonValueKind.Object &&
            video.TryGetProperty("lengthSeconds", out var property) &&
            TryGetPositiveDuration(property, TimeSpan.TicksPerSecond, out var duration)
            ? duration
            : TimeSpan.Zero;
    }

    private static string CreateDeviceId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    [GeneratedRegex("^[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TwitchVodIdPattern();

    private sealed record TwitchVideoMetadata(
        string BroadcastType,
        TimeSpan MediaDuration,
        DateTimeOffset? CreatedAtUtc,
        string SeekPreviewsUrl,
        string OwnerLogin);
}

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Twitch;
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
    private const string TwitchGraphQlEndpoint = "https://gql.twitch.tv/gql";
    // Public Twitch web Client-ID, the same one ReplayResolver uses for archive lookups.
    private const string TwitchPublicClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";
    private const int PlaylistProbeByteLimit = 65535;
    private static readonly TimeSpan StalePlaylistAge = TimeSpan.FromHours(24);
    // TwitchNoSub compares createdAt against this frozen cutoff instead of "now":
    // newer uploads do not expose the index-dvr layout, so they use the archive URL
    // shape (which simply yields no variants and a clean error).
    private static readonly DateTimeOffset TwitchUploadLayoutCutoff = new(2023, 2, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly string DefaultPlaylistDirectory = Path.Combine(
        Path.GetTempPath(),
        "StreamlinkVlcStudio",
        "sub-only-vods");

    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly string playlistDirectory;

    public TwitchSubOnlyVodResolver(IAppLogger logger)
        : this(logger, SharedHttpClient, DefaultPlaylistDirectory)
    {
    }

    public TwitchSubOnlyVodResolver(IAppLogger logger, HttpClient httpClient, string playlistDirectory)
    {
        this.logger = logger;
        this.httpClient = httpClient;
        this.playlistDirectory = playlistDirectory;
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
                metadata.CreatedAtUtc,
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
        await File.WriteAllTextAsync(playlistPath, rewritten, cancellationToken).ConfigureAwait(false);
        logger.Write(
            AppLogLevel.Info,
            "SubOnlyVod",
            $"Resolved sub-only VOD {vodId} via direct CloudFront playlist ({selectedKey}): {playlistUrl}");
        return new TwitchSubOnlyVodResolution(
            new Uri(playlistPath),
            selectedKey,
            $"Resolved sub-only VOD via direct CloudFront playlist ({selectedKey}).");
    }

    private async Task<TwitchVideoMetadata> FetchVideoMetadataAsync(string vodId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TwitchGraphQlEndpoint);
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.TryAddWithoutValidation("Client-Id", TwitchPublicClientId);
        request.Headers.TryAddWithoutValidation("X-Device-Id", CreateDeviceId());
        request.Content = new StringContent(BuildVideoQueryPayload(vodId), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Twitch GraphQL returned {(int)response.StatusCode} {response.ReasonPhrase} for VOD {vodId}. {ExtractApiMessage(body)}".Trim());
        }

        using var document = JsonDocument.Parse(body);
        var graphQlError = ExtractGraphQlError(document.RootElement);
        if (!string.IsNullOrWhiteSpace(graphQlError))
        {
            throw new InvalidOperationException($"Twitch GraphQL rejected the VOD lookup: {graphQlError}");
        }

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("video", out var video) ||
            video.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"VOD {vodId} was not found or is not public.");
        }

        return new TwitchVideoMetadata(
            GetOptionalString(video, "broadcastType"),
            TryGetDateTimeOffset(video, "createdAt") ?? DateTimeOffset.MinValue,
            GetOptionalString(video, "seekPreviewsURL"),
            TryReadNestedString(video, "owner", "login"));
    }

    private async Task<bool> ProbeVariantAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var content = await FetchStringAsync(url, ranged: true, cancellationToken).ConfigureAwait(false);
            return content.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal);
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The shared HttpClient timed out: treat the variant as unavailable
            // instead of aborting the remaining probes.
            return false;
        }
    }

    private async Task<string> FetchStringAsync(string url, bool ranged, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (ranged)
        {
            request.Headers.Range = new RangeHeaderValue(0, PlaylistProbeByteLimit);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GET {url} returned {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
            query = $"query {{ video(id: \"{vodId}\") {{ broadcastType, createdAt, seekPreviewsURL, owner {{ login }} }} }}"
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string ExtractGraphQlError(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array)
        {
            return errors
                .EnumerateArray()
                .Select(error => GetOptionalString(error, "message"))
                .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message)) ?? "";
        }

        return "";
    }

    private static string ExtractApiMessage(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var message = GetOptionalString(root, "message");
            return string.IsNullOrWhiteSpace(message) ? "" : message;
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string CreateDeviceId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    [GeneratedRegex("^[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TwitchVodIdPattern();

    private sealed record TwitchVideoMetadata(
        string BroadcastType,
        DateTimeOffset CreatedAtUtc,
        string SeekPreviewsUrl,
        string OwnerLogin);
}

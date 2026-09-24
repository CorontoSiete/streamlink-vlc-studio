using System.Globalization;
using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Security;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.Infrastructure.Replay;

/// <summary>Reads only the DVR segment covering a hover, never the continuous live transport.</summary>
internal sealed class LiveSeekPreviewClient
{
    private static readonly HttpClient SharedClient = HttpClientFactory.Create(
        TimeSpan.FromSeconds(8), allowAutoRedirect: false);
    private readonly HttpClient httpClient;
    private readonly ReplayUrlSecurityValidator validator;

    internal LiveSeekPreviewClient() : this(SharedClient, ReplayUrlSecurityValidator.Shared) { }

    internal LiveSeekPreviewClient(HttpClient httpClient, ReplayUrlSecurityValidator validator)
    {
        this.httpClient = httpClient;
        this.validator = validator;
    }

    internal async Task<LiveSeekPlaylist?> GetPlaylistAsync(Uri uri, PlatformKind platform,
        DateTimeOffset? startedAt, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(uri, platform, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await BoundedHttpContentReader.ReadStringAsync(response.Content, 2 * 1024 * 1024,
            cancellationToken).ConfigureAwait(false);
        return LiveSeekPlaylist.Parse(text, response.RequestMessage?.RequestUri ?? uri, platform, startedAt);
    }

    internal async Task<byte[]> GetSegmentAsync(Uri uri, PlatformKind platform, CancellationToken cancellationToken,
        bool initialization = false)
    {
        using var response = await GetAsync(uri, platform, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await BoundedByteReader.ReadOrThrowAsync(response.Content,
            initialization ? 1024 * 1024 : 16 * 1024 * 1024, cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<HttpResponseMessage> GetAsync(Uri uri, PlatformKind platform, CancellationToken token) =>
        ValidatedReplayHttpClient.SendGetAsync(httpClient, validator, uri, platform,
            static address => new HttpRequestMessage(HttpMethod.Get, address), token);
}

internal sealed record LiveSeekSegment(Uri Uri, double Start, double Duration, Uri? InitializationUri = null);

internal sealed partial record LiveSeekPlaylist(IReadOnlyList<LiveSeekSegment> Segments)
{
    internal LiveSeekSegment? GetSegment(double seconds) => double.IsFinite(seconds) && seconds >= 0
        ? Segments.FirstOrDefault(segment => seconds >= segment.Start && seconds < segment.Start + segment.Duration)
        : null;

    internal static LiveSeekPlaylist? Parse(string text, Uri uri, PlatformKind platform, DateTimeOffset? startedAt)
    {
        if (!text.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal) ||
            !ProviderUriPolicy.IsApprovedReplayUri(uri, platform)) return null;
        var segments = new List<LiveSeekSegment>();
        double? position = null;
        double? duration = null;
        var sequence = 0L;
        var encrypted = false;
        var gap = false;
        Uri? initializationUri = null;
        var initializationEncrypted = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                if (!long.TryParse(line[22..], NumberStyles.None, CultureInfo.InvariantCulture, out sequence)) return null;
            }
            else if (line.StartsWith("#EXT-X-TWITCH-ELAPSED-SECS:", StringComparison.Ordinal))
            {
                if (!TrySeconds(line[27..], out var elapsed)) return null;
                position = elapsed;
            }
            else if (line.StartsWith("#EXT-X-PROGRAM-DATE-TIME:", StringComparison.Ordinal) &&
                position is null && sequence != 0 && startedAt is { } start)
            {
                if (!DateTimeOffset.TryParse(line[25..], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var time)) return null;
                position = (time - start).TotalSeconds;
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                if (!TrySeconds(line[8..].Split(',')[0], out var length) || length is <= 0 or > 30) return null;
                duration = length;
            }
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                encrypted = !line[11..].Split(',').Contains("METHOD=NONE", StringComparer.Ordinal);
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                // A fragmented MP4 media segment needs the EXT-X-MAP which applies to it.
                // Keep that association per segment, including changes after discontinuities.
                var map = MapAttributes().Match(line[11..]);
                if (!map.Success || !ProviderUriPolicy.TryResolveReplayUri(map.Groups["uri"].Value,
                    uri, platform, out initializationUri)) return null;
                initializationEncrypted = encrypted;
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) return null;
            else if (line == "#EXT-X-GAP") gap = true;
            else if (line.Length > 0 && line[0] != '#')
            {
                if (duration is not { } length) return null;
                // Sequence numbers count segments, not seconds. A sliding playlist needs an
                // explicit clock; guessing sequence * target-duration shows the wrong frame.
                if (position is null && sequence != 0) return null;
                position ??= 0;
                if (!ProviderUriPolicy.TryResolveReplayUri(line, uri, platform, out var segmentUri)) return null;
                var isTransportStream = segmentUri.AbsolutePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);
                var isFragmentedMp4 = initializationUri is not null &&
                    (segmentUri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                     segmentUri.AbsolutePath.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase));
                if (!encrypted && !initializationEncrypted && !gap && (isTransportStream || isFragmentedMp4))
                    segments.Add(new(segmentUri, position.Value, length, isFragmentedMp4 ? initializationUri : null));
                position += length;
                duration = null;
                gap = false;
            }
        }
        return segments.Count > 0 ? new(segments) : null;
    }

    private static bool TrySeconds(string value, out double seconds) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) &&
        double.IsFinite(seconds) && seconds >= 0;

    // Byte-range maps require a separate ranged downloader; never treat a partial map as a whole file.
    [GeneratedRegex("^URI=\"(?<uri>[^\"\\r\\n]+)\"$", RegexOptions.CultureInvariant)]
    private static partial Regex MapAttributes();
}

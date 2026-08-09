using System.Text;

namespace StreamlinkVlcStudio.Core.Twitch;

/// <summary>
/// Pure helpers that build direct CloudFront playlist URLs for subscriber-only Twitch VODs
/// from the VOD's public storyboard (seek preview) metadata. This reimplements the technique
/// used by the TwitchNoSub browser extension (https://github.com/besuper/TwitchNoSub):
/// usher.ttvnw.net refuses sub-only VODs, but the segments on CloudFront need no token.
/// </summary>
public static class TwitchSubOnlyVodPlaylist
{
    // Ordered best-first; the same renditions TwitchNoSub probes for.
    public static IReadOnlyList<string> QualityKeys { get; } =
        ["chunked", "1080p60", "720p60", "480p30", "360p30", "160p30"];

    public static bool TryParseStoryboardLocation(
        string? seekPreviewsUrl,
        out string host,
        out string specialId)
    {
        host = "";
        specialId = "";

        if (string.IsNullOrWhiteSpace(seekPreviewsUrl) ||
            !Uri.TryCreate(seekPreviewsUrl, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var storyboardIndex = Array.FindIndex(
            segments,
            segment => segment.Contains("storyboards", StringComparison.OrdinalIgnoreCase));
        if (storyboardIndex < 1)
        {
            return false;
        }

        host = uri.Host;
        specialId = segments[storyboardIndex - 1];
        return specialId.Length > 0;
    }

    public static string BuildVariantPlaylistUrl(
        string broadcastType,
        DateTimeOffset createdAtUtc,
        DateTimeOffset nowUtc,
        string host,
        string specialId,
        string ownerLogin,
        string vodId,
        string qualityKey)
    {
        var type = (broadcastType ?? "").Trim().ToLowerInvariant();
        if (type == "highlight")
        {
            return $"https://{host}/{specialId}/{qualityKey}/highlight-{vodId}.m3u8";
        }

        if (type == "upload" && nowUtc - createdAtUtc > TimeSpan.FromDays(7))
        {
            return $"https://{host}/{ownerLogin}/{vodId}/{specialId}/{qualityKey}/index-dvr.m3u8";
        }

        return $"https://{host}/{specialId}/{qualityKey}/index-dvr.m3u8";
    }

    public static string SelectQualityKey(IReadOnlyList<string> availableKeys, string? requestedQuality)
    {
        if (availableKeys.Count == 0)
        {
            throw new ArgumentException("At least one available quality key is required.", nameof(availableKeys));
        }

        var requested = (requestedQuality ?? "").Trim().ToLowerInvariant();
        if (requested is "worst" or "audio_only")
        {
            return availableKeys[^1];
        }

        var preferred = requested switch
        {
            "1080p60" or "1080p" => "1080p60",
            "720p60" or "720p" => "720p60",
            "480p" => "480p30",
            _ => "chunked"
        };

        var preferredIndex = IndexOfQualityKey(preferred);
        string? bestKey = null;
        var bestDistance = int.MaxValue;
        var bestIndex = int.MaxValue;
        foreach (var key in availableKeys)
        {
            var index = IndexOfQualityKey(key);
            if (index < 0)
            {
                continue;
            }

            var distance = Math.Abs(index - preferredIndex);
            if (distance < bestDistance || (distance == bestDistance && index > bestIndex))
            {
                bestDistance = distance;
                bestIndex = index;
                bestKey = key;
            }
        }

        return bestKey ?? availableKeys[0];
    }

    public static string RewriteMediaPlaylist(string playlistContent, Uri playlistUri)
    {
        ArgumentNullException.ThrowIfNull(playlistContent);
        ArgumentNullException.ThrowIfNull(playlistUri);

        // Sub-only VODs 404 on the "-unmuted" segment names; "-muted" always exists.
        var mutedContent = playlistContent.Replace("-unmuted", "-muted", StringComparison.Ordinal);
        var lines = mutedContent.Split('\n');
        var builder = new StringBuilder(mutedContent.Length + 256);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            // Skip the artificial empty entry produced by a trailing newline.
            if (line.Length == 0 && i == lines.Length - 1)
            {
                break;
            }

            if (line.Length > 0)
            {
                builder.Append(line[0] == '#'
                    ? RewriteTagLine(line, playlistUri)
                    : AbsolutizeUri(line.Trim(), playlistUri));
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static int IndexOfQualityKey(string key)
    {
        for (var i = 0; i < QualityKeys.Count; i++)
        {
            if (string.Equals(QualityKeys[i], key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string RewriteTagLine(string line, Uri playlistUri)
    {
        const string marker = "URI=\"";
        var start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return line;
        }

        start += marker.Length;
        var end = line.IndexOf('"', start);
        if (end < 0)
        {
            return line;
        }

        var uri = line[start..end];
        return string.Concat(line[..start], AbsolutizeUri(uri, playlistUri), line[end..]);
    }

    private static string AbsolutizeUri(string uri, Uri playlistUri)
    {
        if (uri.Length == 0 ||
            uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        return new Uri(playlistUri, uri).ToString();
    }
}

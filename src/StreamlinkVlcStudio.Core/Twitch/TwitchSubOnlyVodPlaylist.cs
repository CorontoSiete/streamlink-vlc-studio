using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Security;

namespace StreamlinkVlcStudio.Core.Twitch;

/// <summary>
/// Pure helpers that build direct CloudFront playlist URLs for subscriber-only Twitch VODs
/// from the VOD's public storyboard (seek preview) metadata. This reimplements the technique
/// used by the TwitchNoSub browser extension (https://github.com/besuper/TwitchNoSub):
/// usher.ttvnw.net refuses sub-only VODs, but the segments on CloudFront need no token.
/// </summary>
public static partial class TwitchSubOnlyVodPlaylist
{
    // Ordered best-first; the same renditions TwitchNoSub probes for.
    public static IReadOnlyList<string> QualityKeys { get; } =
        Array.AsReadOnly<string>(["chunked", "1080p60", "720p60", "480p30", "360p30", "160p30"]);

    public static bool TryParseStoryboardLocation(
        string? seekPreviewsUrl,
        out string host,
        out string specialId)
    {
        host = "";
        specialId = "";

        if (string.IsNullOrWhiteSpace(seekPreviewsUrl) ||
            !Uri.TryCreate(seekPreviewsUrl, UriKind.Absolute, out var uri) ||
            !ProviderUriPolicy.IsApprovedReplayUri(uri, PlatformKind.Twitch))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var storyboardIndex = Array.FindIndex(
            segments,
            segment => segment.Equals("storyboards", StringComparison.OrdinalIgnoreCase));
        if (storyboardIndex < 1)
        {
            return false;
        }

        try
        {
            specialId = Uri.UnescapeDataString(segments[storyboardIndex - 1]);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (!StoryboardIdentifierPattern().IsMatch(specialId))
        {
            specialId = "";
            return false;
        }

        host = uri.IdnHost.TrimEnd('.');
        return true;
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
        var normalizedHost = NormalizeHost(host);
        var encodedSpecialId = EncodePathSegment(specialId);
        var encodedVodId = EncodePathSegment(vodId);
        var encodedQualityKey = EncodePathSegment(qualityKey);
        if (type == "highlight")
        {
            return $"https://{normalizedHost}/{encodedSpecialId}/{encodedQualityKey}/highlight-{encodedVodId}.m3u8";
        }

        // Missing createdAt metadata is common for partially populated GraphQL responses.
        // Treat it like an archive-style upload instead of subtracting DateTimeOffset.MinValue,
        // which can overflow TimeSpan and abort the fallback before probing any variants.
        var isOldUpload = type == "upload" &&
            createdAtUtc > DateTimeOffset.MinValue &&
            nowUtc >= createdAtUtc &&
            nowUtc - createdAtUtc > TimeSpan.FromDays(7);
        if (isOldUpload)
        {
            return $"https://{normalizedHost}/{EncodePathSegment(ownerLogin)}/{encodedVodId}/{encodedSpecialId}/{encodedQualityKey}/index-dvr.m3u8";
        }

        return $"https://{normalizedHost}/{encodedSpecialId}/{encodedQualityKey}/index-dvr.m3u8";
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
            "1080p" => "1080p60",
            "720p" => "720p60",
            "480p" => "480p30",
            _ => requested
        };

        // Accept the canonical rendition keys as well as the app's display aliases.
        // Unknown preferences (including best/source) keep the highest-quality fallback.
        var preferredIndex = Math.Max(0, IndexOfQualityKey(preferred));
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
        if (!ProviderUriPolicy.IsApprovedReplayUri(playlistUri, PlatformKind.Twitch))
        {
            throw new InvalidDataException("The Twitch playlist URL is not an approved HTTPS provider endpoint.");
        }

        // Rewrite only the media filename: directory names, signed queries, key/map URIs,
        // and tag metadata can contain the same text and must retain their original values.
        return TwitchPlaylistUriRewriter.Rewrite(playlistContent, playlistUri, static uri =>
        {
            var path = uri.AbsolutePath;
            var filenameStart = path.LastIndexOf('/') + 1;
            return uri.GetLeftPart(UriPartial.Authority) + path[..filenameStart] +
                path[filenameStart..].Replace("-unmuted", "-muted", StringComparison.Ordinal) + uri.Query;
        });
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

    private static string EncodePathSegment(string? value) =>
        Uri.EscapeDataString(value ?? "").Replace(".", "%2E", StringComparison.Ordinal);

    private static string NormalizeHost(string? host)
    {
        var candidate = (host ?? "").Trim().TrimEnd('.');
        if (candidate.Length == 0 ||
            !Uri.TryCreate($"https://{candidate}/", UriKind.Absolute, out var uri) ||
            !uri.IsDefaultPort ||
            uri.UserInfo.Length > 0 ||
            uri.Query.Length > 0 ||
            uri.Fragment.Length > 0 ||
            !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal) ||
            !string.Equals(uri.IdnHost, candidate, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The playlist host is not valid.", nameof(host));
        }

        return uri.IdnHost;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex StoryboardIdentifierPattern();
}

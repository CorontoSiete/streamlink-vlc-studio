using System.Globalization;
using System.Text;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Infrastructure.Hls;

namespace StreamlinkVlcStudio.Infrastructure.Previews;

internal sealed record LivePreviewPlaybackOptions(bool LowLatency, int LiveDelayMilliseconds);

/// <summary>Validates live TS and fragmented MP4; provider-specific handling stays with Streamlink.</summary>
internal static class LivePreviewPlaylist
{
    internal static LivePreviewPlaybackOptions GetPlaybackOptions(string validatedPlaylist, StreamTransportRequest request)
    {
        const string prefix = "#EXT-X-TARGETDURATION:";
        var line = HlsPlaylistPolicy.SplitLines(validatedPlaylist).First(value => value.StartsWith(prefix, StringComparison.Ordinal));
        var seconds = int.Parse(line[prefix.Length..], CultureInfo.InvariantCulture);
        var segments = request.LowLatency ? request.Target.Platform == PlatformKind.Twitch ? 2 : 4 : 3;
        // VLC already keeps one safety segment behind the edge. Derive the remaining
        // distance from the existing transport's policy instead of VLC's default 15s.
        // Native HLS still enforces its own minimum buffer for continuity.
        return new(request.LowLatency && request.Target.Platform == PlatformKind.Twitch, checked((segments - 1) * seconds * 1000));
    }

    internal static string Rewrite(string content, Uri origin, PlatformKind platform, out Uri? initializationUri)
    {
        initializationUri = null;
        var lines = HlsPlaylistPolicy.SplitLines(content);
        if (lines.Length == 0 || lines[0] != "#EXTM3U" || HlsPlaylistPolicy.HasSkippedSegments(lines))
            throw Unsupported();
        var output = new StringBuilder(content.Length);
        var pendingSegment = false;
        var segments = 0;
        var targetDuration = false;
        foreach (var line in lines)
        {
            if (line.Any(character => char.IsControl(character) && character != '\t')) throw Unsupported();

            // Streamlink filters Twitch ads. Never hand an ad playlist to the native
            // player, including when an ad first appears during a later refresh.
            if (platform == PlatformKind.Twitch &&
                (line.Contains("twitch-stitched-ad", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("stitched-ad-", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("X-TV-TWITCH-AD-", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("#EXTINF:", StringComparison.Ordinal) && line.Contains("Amazon", StringComparison.Ordinal)))
                throw Unsupported();

            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                // Accept a whole, unencrypted initialization file on the same approved
                // provider endpoints as segments. Ranges and changing maps stay on Streamlink.
                if (!HlsPlaylistPolicy.TryReadWholeMapUri(line[11..], out var mapUri) ||
                    !Uri.TryCreate(origin, mapUri, out var map) ||
                    !LivePreviewPolicy.IsAllowedUri(map, platform) ||
                    !map.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                    initializationUri is null && segments != 0 || initializationUri is not null && initializationUri != map)
                    throw Unsupported();
                initializationUri = map;
                output.Append("#EXT-X-MAP:URI=\"").Append(map.AbsoluteUri).AppendLine("\"");
                continue;
            }
            if (line.Contains("URI=", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-DEFINE:", StringComparison.Ordinal) || line == "#EXT-X-ENDLIST") throw Unsupported();

            // These provider extensions are not consumed by VLC 3's HLS parser.
            if (line.StartsWith("#EXT-X-TWITCH-PREFETCH", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-PREFETCH", StringComparison.Ordinal)) continue;
            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
            {
                if (!int.TryParse(line[22..], NumberStyles.None, CultureInfo.InvariantCulture, out var duration) ||
                    duration is <= 0 or > 3600 || targetDuration) throw Unsupported();
                targetDuration = true;
            }
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                if (pendingSegment || !HlsPlaylistPolicy.TryReadSegmentDuration(line.AsSpan(8), 3600, out _)) throw Unsupported();
                pendingSegment = true;
            }
            if (!line.StartsWith('#'))
            {
                if (!pendingSegment || !Uri.TryCreate(origin, line, out var segment) ||
                    !LivePreviewPolicy.IsAllowedUri(segment, platform)) throw Unsupported();
                var transportStream = initializationUri is null && segment.AbsolutePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);
                var fragmentedMp4 = initializationUri is not null &&
                    (segment.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                     segment.AbsolutePath.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase));
                if (!transportStream && !fragmentedMp4) throw Unsupported();
                pendingSegment = false;
                segments++;
                output.AppendLine(segment.AbsoluteUri);
            }
            else output.AppendLine(line);
        }
        if (!targetDuration || pendingSegment || segments == 0) throw Unsupported();
        return output.ToString();
    }

    private static InvalidDataException Unsupported() => new("The live playlist requires Streamlink transport.");

}

using System.Globalization;
using System.Text;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Infrastructure.Previews;

internal sealed record LivePreviewPlaybackOptions(bool LowLatency, int LiveDelayMilliseconds);

/// <summary>The fast path accepts ordinary live TS; provider-specific handling stays with Streamlink.</summary>
internal static class LivePreviewPlaylist
{
    internal static LivePreviewPlaybackOptions GetPlaybackOptions(string validatedPlaylist, StreamTransportRequest request)
    {
        const string prefix = "#EXT-X-TARGETDURATION:";
        var line = validatedPlaylist.Split('\n', StringSplitOptions.TrimEntries).First(value => value.StartsWith(prefix, StringComparison.Ordinal));
        var seconds = int.Parse(line[prefix.Length..], CultureInfo.InvariantCulture);
        var segments = request.LowLatency ? request.Target.Platform == PlatformKind.Twitch ? 2 : 4 : 3;
        // VLC already keeps one safety segment behind the edge. Derive the remaining
        // distance from the existing transport's policy instead of VLC's default 15s.
        // Native HLS still enforces its own minimum buffer for continuity.
        return new(request.LowLatency && request.Target.Platform == PlatformKind.Twitch, checked((segments - 1) * seconds * 1000));
    }

    internal static string Rewrite(string content, Uri origin, PlatformKind platform)
    {
        var lines = content.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || lines[0] != "#EXTM3U") throw Unsupported();
        var output = new StringBuilder(content.Length);
        var pendingSegment = false;
        var segments = 0;
        var targetDuration = false;
        foreach (var line in lines)
        {
            if (line.Any(character => char.IsControl(character) && character != '\t') ||
                line.Contains("URI=", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-DEFINE:", StringComparison.Ordinal) || line == "#EXT-X-ENDLIST") throw Unsupported();

            // Streamlink filters Twitch ads. Never hand an ad playlist to the native
            // player, including when an ad first appears during a later refresh.
            if (platform == PlatformKind.Twitch &&
                (line.Contains("twitch-stitched-ad", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("stitched-ad-", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("X-TV-TWITCH-AD-", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("#EXTINF:", StringComparison.Ordinal) && line.Contains("Amazon", StringComparison.Ordinal)))
                throw Unsupported();

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
                if (pendingSegment || !decimal.TryParse(line[8..].Split(',')[0], NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var duration) || duration <= 0 || duration > 3600) throw Unsupported();
                pendingSegment = true;
            }
            if (!line.StartsWith('#'))
            {
                if (!pendingSegment || !Uri.TryCreate(origin, line, out var segment) ||
                    !LivePreviewPolicy.IsAllowedUri(segment, platform) ||
                    !segment.AbsolutePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)) throw Unsupported();
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

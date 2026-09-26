using System.Globalization;

namespace StreamlinkVlcStudio.Infrastructure.Twitch;

/// <summary>Formats verified with VLC's FFmpeg HLS demuxer and the local segment transport.</summary>
internal static class TwitchVodReplayPolicy
{
    private static readonly Uri RelativeUriBase = new("https://example.invalid/");

    internal static TimeSpan GetPreroll(string playlist, Version? version)
    {
        // Other VLC builds may have different FFmpeg protocol/seek behavior. Keep their
        // existing adaptive path until they have the same native regression coverage.
        if (version is not { Major: 3, Minor: 0, Build: 23 }) return TimeSpan.Zero;
        var lines = playlist.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || lines[0].TrimStart('\uFEFF') != "#EXTM3U" || lines[^1] != "#EXT-X-ENDLIST") return TimeSpan.Zero;
        var pending = false;
        var segments = 0;
        var ended = false;
        var targetDuration = 0;
        decimal maximumDuration = 0;
        foreach (var line in lines)
        {
            if (ended) return TimeSpan.Zero;
            if (line == "#EXT-X-ENDLIST") { ended = true; continue; }
            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
            {
                if (targetDuration != 0 || !int.TryParse(line[22..], NumberStyles.None, CultureInfo.InvariantCulture,
                        out targetDuration) || targetDuration is <= 0 or > 30) return TimeSpan.Zero;
            }
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                if (!long.TryParse(line[22..], NumberStyles.None, CultureInfo.InvariantCulture, out _)) return TimeSpan.Zero;
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                if (pending || !decimal.TryParse(line[8..].Split(',')[0], NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var duration) || duration is <= 0 or > 30) return TimeSpan.Zero;
                maximumDuration = Math.Max(maximumDuration, duration);
                pending = true;
            }
            else if (!line.StartsWith('#') && line[0] != '\uFEFF')
            {
                if (!pending || !Uri.TryCreate(RelativeUriBase, line, out var uri) ||
                    !uri.AbsolutePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)) return TimeSpan.Zero;
                pending = false;
                segments++;
            }
            else if (line.StartsWith("#EXT-X-", StringComparison.Ordinal) &&
                !(line.StartsWith("#EXT-X-VERSION:", StringComparison.Ordinal) ||
                  line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal) ||
                  line.StartsWith("#EXT-X-TWITCH-", StringComparison.Ordinal) ||
                  line == "#EXT-X-INDEPENDENT-SEGMENTS")) return TimeSpan.Zero;
        }
        return segments > 0 && !pending && targetDuration > 0 && targetDuration >= Math.Round(maximumDuration, MidpointRounding.AwayFromZero)
            ? TimeSpan.FromTicks((long)(maximumDuration * TimeSpan.TicksPerSecond)) : TimeSpan.Zero;
    }
}

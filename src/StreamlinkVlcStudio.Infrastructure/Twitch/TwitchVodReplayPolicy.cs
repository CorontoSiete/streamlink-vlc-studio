using System.Globalization;
using StreamlinkVlcStudio.Infrastructure.Hls;

namespace StreamlinkVlcStudio.Infrastructure.Twitch;

/// <summary>Playlists verified with the bundled live HLS and completed-replay demuxers.</summary>
internal static class TwitchVodReplayPolicy
{
    private static readonly Uri RelativeUriBase = new("https://example.invalid/");

    internal static TimeSpan GetPreroll(string playlist, Version? version) =>
        GetSegmentDuration(playlist, version, completed: true);

    internal static TimeSpan GetLiveReplaySegmentDuration(string playlist, Version? version) =>
        GetSegmentDuration(playlist, version, completed: false);

    private static TimeSpan GetSegmentDuration(string playlist, Version? version, bool completed)
    {
        // The bundled demuxer uses VLC 3.0.23's ABI. Other releases and playlist
        // formats retain the installed modules, including encryption and fMP4 support.
        if (version is not { Major: 3, Minor: 0, Build: 23 }) return TimeSpan.Zero;
        var lines = HlsPlaylistPolicy.SplitLines(playlist);
        if (lines.Length == 0 || lines[0] != "#EXTM3U") return TimeSpan.Zero;
        if (completed ? lines[^1] != "#EXT-X-ENDLIST" :
            lines.Contains("#EXT-X-ENDLIST") || !lines.Contains("#EXT-X-PLAYLIST-TYPE:EVENT")) return TimeSpan.Zero;
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
                if (pending || !HlsPlaylistPolicy.TryReadSegmentDuration(line.AsSpan(8), 30, out var duration)) return TimeSpan.Zero;
                maximumDuration = Math.Max(maximumDuration, duration);
                pending = true;
            }
            else if (!line.StartsWith('#'))
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

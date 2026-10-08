using System.Globalization;

namespace StreamlinkVlcStudio.Infrastructure.Hls;

internal static class HlsPlaylistPolicy
{
    internal static string[] SplitLines(string content)
    {
        var lines = content.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        // UTF-8 decoded from a file can retain its BOM; normalize only the header.
        if (lines.Length > 0) lines[0] = lines[0].TrimStart('\uFEFF');
        return lines;
    }

    // Delta updates omit segment metadata, including durations and key/map state.
    // Standalone readers cannot interpret them without the previous full playlist.
    internal static bool HasSkippedSegments(IEnumerable<string> lines) =>
        lines.Any(static line => line == "#EXT-X-SKIP" ||
            line.StartsWith("#EXT-X-SKIP:", StringComparison.Ordinal));

    internal static bool TryReadSegmentDuration(ReadOnlySpan<char> value, decimal maximumSeconds, out decimal seconds)
    {
        seconds = 0;
        var comma = value.IndexOf(',');
        if (comma >= 0) value = value[..comma];
        foreach (var character in value)
            if (!char.IsAsciiDigit(character) && character != '.') return false;
        return decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds) &&
            seconds > 0 && seconds <= maximumSeconds;
    }

    // Byte-range maps need a ranged downloader. These readers accept only a whole,
    // quoted URI; provider and encryption policy remains with the caller.
    internal static bool TryReadWholeMapUri(string attributes, out string uri)
    {
        uri = "";
        if (!HlsAttributeList.TryParse(attributes, out var parsed) || parsed.Count != 1 ||
            !parsed.TryGetValue("URI", out var value) || !value.IsQuoted || value.Value.Length == 0) return false;
        uri = value.Value;
        return true;
    }
}

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
}

namespace StreamlinkVlcStudio.Core.Text;

/// <summary>Shared result-count text for provider discovery and playback probes.</summary>
public static class StreamSearchSummary
{
    public static string Format(string query, int live, int offline, int unavailable)
    {
        var total = live + offline + unavailable;
        if (total == 0) return $"No channels found for {query}.";

        var parts = new List<string>(3);
        if (live > 0) parts.Add($"{live} live");
        if (offline > 0) parts.Add($"{offline} offline");
        if (unavailable > 0) parts.Add($"{unavailable} unavailable");
        return $"{string.Join(", ", parts)} channel result{(total == 1 ? "" : "s")} found for {query}.";
    }
}

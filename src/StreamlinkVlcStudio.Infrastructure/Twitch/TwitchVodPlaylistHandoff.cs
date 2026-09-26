namespace StreamlinkVlcStudio.Infrastructure.Twitch;

/// <summary>
/// Passes a freshly validated, completed playlist from URL resolution to the playback
/// gateway. Entries are consumed once; this never replaces playback authorization.
/// </summary>
internal sealed class TwitchVodPlaylistHandoff(TimeProvider? timeProvider = null)
{
    internal static TwitchVodPlaylistHandoff Shared { get; } = new();
    internal const int MaximumCharacters = 4 * 1024 * 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<Uri, (string Content, long Timestamp)> entries = [];
    private int characters;

    internal void Offer(Uri uri, string content)
    {
        lock (gate)
        {
            Prune();
            Remove(uri);
            // Growing playlists must be read again: both duration and muted segments
            // can change between resolution and opening. Bound retained UTF-16 memory.
            if (content.Length > MaximumCharacters ||
                !content.Split('\n').Any(line => line.Trim() == "#EXT-X-ENDLIST")) return;
            while (entries.Count >= 8 || characters + content.Length > MaximumCharacters)
                Remove(entries.MinBy(entry => entry.Value.Timestamp).Key);
            entries[uri] = (content, clock.GetTimestamp());
            characters += content.Length;
        }
    }

    internal string? Take(Uri uri)
    {
        lock (gate)
        {
            Prune();
            return Remove(uri);
        }
    }

    private string? Remove(Uri uri)
    {
        if (!entries.Remove(uri, out var entry)) return null;
        characters -= entry.Content.Length;
        return entry.Content;
    }

    private void Prune()
    {
        foreach (var uri in entries.Where(entry => clock.GetElapsedTime(entry.Value.Timestamp) >= Lifetime)
            .Select(entry => entry.Key).ToArray()) Remove(uri);
    }
}

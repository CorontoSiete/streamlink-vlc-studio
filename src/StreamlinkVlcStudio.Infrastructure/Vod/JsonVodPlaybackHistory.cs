using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Io;

namespace StreamlinkVlcStudio.Infrastructure.Vod;

/// <summary>Small, bounded history independent of account settings and expiring playback URLs.</summary>
public sealed class JsonVodPlaybackHistory(string path, IAppLogger logger) : IVodPlaybackHistory
{
    private const int MaximumEntries = 1000;
    private const int MaximumBytes = 1024 * 1024;
    private readonly object gate = new();
    private readonly SemaphoreSlim fileGate = new(1, 1);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private bool loaded;
    private long version;
    private long savedVersion;

    public event Action<StreamTarget, VodPlaybackBookmark>? BookmarkChanged;

    public async Task<VodPlaybackBookmark?> GetAsync(StreamTarget target, CancellationToken cancellationToken = default)
    {
        if (Key(target) is not { } key) return null;
        await fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            lock (gate) return entries.TryGetValue(key, out var entry) ? entry.Bookmark : null;
        }
        finally { fileGate.Release(); }
    }

    public void Remember(StreamTarget target, VodPlaybackBookmark bookmark)
    {
        if (Key(target) is not { } key || !IsValid(bookmark)) return;
        lock (gate)
        {
            entries.TryGetValue(key, out var previous);
            if (previous is not null && previous.Bookmark.UpdatedAtUtc > bookmark.UpdatedAtUtc) return;
            // Finishing a VOD remains visible on its card even while rewatching it.
            // Completed only describes this playback pass, so resume still works on a rewatch.
            bookmark = bookmark with
            {
                HasBeenWatched = bookmark.HasBeenWatched || bookmark.Completed ||
                previous?.Bookmark.HasBeenWatched == true || previous?.Bookmark.Completed == true
            };
            // A stationary clock still confirms a newer sample. Keep that timestamp so
            // a delayed sample cannot move the bookmark backwards, including after reload.
            if (previous?.Bookmark == bookmark) return;
            entries[key] = new Entry(target.Platform, target.MediaId.Trim(), bookmark);
            TrimCore();
            version++;
        }
        BookmarkChanged?.Invoke(target, bookmark);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            Entry[] snapshot;
            long snapshotVersion;
            lock (gate)
            {
                if (version == savedVersion) return;
                snapshot = entries.Values.ToArray();
                snapshotVersion = version;
            }
            await AtomicFile.WriteAsync(path,
                (stream, token) => JsonSerializer.SerializeAsync(stream, snapshot, cancellationToken: token),
                cancellationToken, flushToDisk: true).ConfigureAwait(false);
            lock (gate) savedVersion = snapshotVersion;
        }
        finally { fileGate.Release(); }
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (loaded) return;
        try
        {
            if (File.Exists(path))
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous);
                if (stream.Length > MaximumBytes) throw new JsonException("VOD history exceeded its size limit.");
                var saved = await JsonSerializer.DeserializeAsync<Entry[]>(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false) ?? [];
                lock (gate)
                {
                    foreach (var entry in saved.Where(entry => entry is not null &&
                                 ValidMediaId(entry.MediaId) && IsValid(entry.Bookmark) &&
                                 entry.Platform is PlatformKind.Twitch or PlatformKind.Kick)
                                 .OrderByDescending(entry => entry.Bookmark.UpdatedAtUtc))
                    {
                        var key = $"{entry.Platform}:{entry.MediaId.Trim()}";
                        if (!entries.TryGetValue(key, out var current))
                        {
                            entries.Add(key, entry);
                            continue;
                        }

                        // Remember can run before or during this first read. Keep the newest
                        // position while carrying completion forward from either history.
                        var latest = current.Bookmark.UpdatedAtUtc >= entry.Bookmark.UpdatedAtUtc ? current : entry;
                        var merged = latest with
                        {
                            Bookmark = latest.Bookmark with
                            {
                                HasBeenWatched = current.Bookmark.HasBeenWatched || current.Bookmark.Completed ||
                                    entry.Bookmark.HasBeenWatched || entry.Bookmark.Completed
                            }
                        };
                        if (merged != current)
                        {
                            entries[key] = merged;
                            version++;
                        }
                    }
                    TrimCore();
                }
            }
        }
        catch (JsonException ex)
        {
            // Preserve the damaged file before allowing a fresh history to replace it.
            File.Move(path, $"{path}.invalid-{Guid.NewGuid():N}");
            logger.Write(AppLogLevel.Warning, "VOD resume", "Invalid VOD history was backed up; starting a new history.", ex);
        }
        loaded = true;
    }

    private void TrimCore()
    {
        if (entries.Count <= MaximumEntries) return;
        foreach (var key in entries.OrderByDescending(pair => pair.Value.Bookmark.UpdatedAtUtc)
                     .Skip(MaximumEntries).Select(pair => pair.Key).ToArray()) entries.Remove(key);
    }

    private static string? Key(StreamTarget target) => target.IsExplicitVod && ValidMediaId(target.MediaId)
        ? $"{target.Platform}:{target.MediaId.Trim()}" : null;

    private static bool ValidMediaId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 256 &&
        id.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool IsValid(VodPlaybackBookmark? bookmark) => bookmark is not null &&
        bookmark.Duration > TimeSpan.Zero && bookmark.Duration <= TimeSpan.FromDays(14) &&
        bookmark.Position >= TimeSpan.Zero && bookmark.Position <= bookmark.Duration &&
        bookmark.UpdatedAtUtc != default;

    private sealed record Entry(PlatformKind Platform, string MediaId, VodPlaybackBookmark Bookmark);
}

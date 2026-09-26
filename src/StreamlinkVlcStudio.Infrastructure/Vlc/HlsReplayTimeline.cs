using System.Globalization;
using System.Text;
using StreamlinkVlcStudio.Core;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Limits;
using StreamlinkVlcStudio.Infrastructure.Replay;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

/// <summary>
/// VLC 3's adaptive demuxer interprets a large MPEG-TS seek as a 33-bit timestamp
/// rollover. Start completed TS replays on a segment near the requested position,
/// keeping their original timeline in the engine instead of exposing that jump to VLC.
/// </summary>
internal static class HlsReplayTimeline
{
    // Below half of the 33-bit / 90 kHz timestamp period (13h 15m 21.859s).
    internal static readonly TimeSpan RebaseThreshold = TimeSpan.FromHours(12);
    private static readonly HttpClient Client = HttpClientFactory.Create(TimeSpan.FromSeconds(15), allowAutoRedirect: false);

    internal static bool IsPlaylist(Uri uri) => uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    internal static async Task<PlaybackMediaSource> PrepareAsync(
        PlaybackMediaSource source, TimeSpan position, CancellationToken cancellationToken,
        HttpClient? httpClient = null)
    {
        if (position < RebaseThreshold || !IsPlaylist(source.PlaybackUri)) return source;
        var uri = source.PlaybackUri;
        string content;
        if (uri.IsFile)
        {
            var bytes = await BoundedByteReader.ReadFileAsync(uri.LocalPath, PayloadLimits.PlaylistBytes, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("The replay playlist is missing or too large.");
            content = new UTF8Encoding(false, true).GetString(bytes);
        }
        else if (uri.IsLoopback)
        {
            // An already owned media source (the muted-segment proxy or a local transport).
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await BoundedHttpResponseSender.SendAsync(httpClient ?? Client, request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            content = await BoundedHttpContentReader.ReadPlaylistAsync(response.Content, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var platform = ReplayUrlSecurityValidator.TryValidateProviderUri(uri, PlatformKind.Twitch)
                ? PlatformKind.Twitch : PlatformKind.Kick;
            (content, uri) = await ValidatedReplayHttpClient.ReadPlaylistAsync(httpClient ?? Client,
                ReplayUrlSecurityValidator.Shared, uri, platform, cancellationToken).ConfigureAwait(false);
        }

        var timeline = Rebase(content, uri, position);
        if (timeline is null) return source;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppIdentity.ProductDirectoryName, "ReplayTimelines", Guid.NewGuid().ToString("N") + ".m3u8");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await File.WriteAllTextAsync(path, timeline.Value.Playlist, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return new PlaybackMediaSource(new Uri(path), new TimelineLease(path, source), timeline.Value.Offset,
                source.UseAvformatDemuxer, source.ReplaySeekPreroll);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    internal static (string Playlist, TimeSpan Offset)? Rebase(string content, Uri uri, TimeSpan position)
    {
        var lines = content.Split('\n').Select(line => line.Trim().TrimStart('\uFEFF')).ToArray();
        // Growing playlists must retain their refresh URL; fMP4 and encrypted/byte-range
        // playlists have different initialization semantics and do not use this TS adapter.
        if (lines.FirstOrDefault() != "#EXTM3U" || !lines.Contains("#EXT-X-ENDLIST") ||
            lines.Any(line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal) ||
                (line.StartsWith('#') && line.Contains("URI=", StringComparison.OrdinalIgnoreCase)))) return null;

        var segments = new List<(int Start, int End, TimeSpan Time)>();
        var elapsed = TimeSpan.Zero;
        var pendingStart = -1;
        var duration = TimeSpan.Zero;
        long sequence = 0, discontinuitySequence = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                sequence = long.Parse(line[22..], CultureInfo.InvariantCulture);
            else if (line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE:", StringComparison.Ordinal))
                discontinuitySequence = long.Parse(line[30..], CultureInfo.InvariantCulture);
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                if (pendingStart >= 0) throw new InvalidDataException("Replay playlist has a missing media segment.");
                if (!decimal.TryParse(line[8..].Split(',')[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                    out var seconds) || seconds <= 0 || seconds > 3600)
                    throw new InvalidDataException("Replay playlist has an invalid segment duration.");
                duration = TimeSpan.FromTicks(checked((long)(seconds * TimeSpan.TicksPerSecond)));
                pendingStart = i;
            }
            else if (line.Length > 0 && line[0] != '#')
            {
                if (pendingStart < 0) throw new InvalidDataException("Replay playlist has an unpaired media segment.");
                var segmentUri = new Uri(uri, line);
                if (!segmentUri.AbsolutePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)) return null;
                ValidateSegment(uri, segmentUri);
                lines[i] = segmentUri.AbsoluteUri;
                segments.Add((pendingStart, i, elapsed));
                elapsed += duration;
                pendingStart = -1;
            }
        }
        if (pendingStart >= 0) throw new InvalidDataException("Replay playlist has a missing media segment.");
        if (segments.Count == 0) return null;
        // Keep one preceding segment for decoder preroll, including exact segment boundaries.
        var selected = segments.FindLastIndex(segment => segment.Time <= position);
        selected = Math.Max(0, selected - 1);
        if (selected == 0) return null;
        var first = segments[selected];
        var boundary = segments[selected - 1].End + 1;
        discontinuitySequence = checked(discontinuitySequence + lines.Take(boundary).Count(line => line == "#EXT-X-DISCONTINUITY"));
        var result = new StringBuilder("#EXTM3U\n");
        foreach (var line in lines.Take(segments[0].Start))
        {
            if (line.StartsWith("#EXT-X-VERSION:", StringComparison.Ordinal) ||
                line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal) || line == "#EXT-X-INDEPENDENT-SEGMENTS")
                result.AppendLine(line);
        }
        result.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");
        result.AppendLine("#EXT-X-MEDIA-SEQUENCE:" + checked(sequence + selected).ToString(CultureInfo.InvariantCulture));
        result.AppendLine("#EXT-X-DISCONTINUITY-SEQUENCE:" + discontinuitySequence.ToString(CultureInfo.InvariantCulture));
        foreach (var line in lines.Skip(boundary)) result.AppendLine(line);
        return (result.ToString(), first.Time);
    }

    private static void ValidateSegment(Uri playlist, Uri segment)
    {
        if (ReplayUrlSecurityValidator.TryValidateProviderUri(segment, PlatformKind.Twitch) ||
            ReplayUrlSecurityValidator.TryValidateProviderUri(segment, PlatformKind.Kick)) return;
        if (playlist.IsLoopback && !playlist.IsFile && segment.Scheme == playlist.Scheme &&
            segment.Authority == playlist.Authority) return;
        if (playlist.IsFile && segment.IsFile && string.Equals(Path.GetDirectoryName(playlist.LocalPath),
            Path.GetDirectoryName(segment.LocalPath), StringComparison.OrdinalIgnoreCase)) return;
        throw new InvalidDataException("Replay playlist contains an unapproved media URL.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class TimelineLease(string path, PlaybackMediaSource source) : IDisposable
    {
        public void Dispose() { TryDelete(path); source.Dispose(); }
    }
}

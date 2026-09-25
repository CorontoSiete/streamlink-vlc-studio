using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Security;

namespace StreamlinkVlcStudio.Core.Twitch;

/// <summary>What a Twitch VOD playlist says about muted segments.</summary>
/// <param name="IsMediaPlaylist">The playlist lists media segments (it is not a master playlist).</param>
/// <param name="IsComplete">The playlist ended (<c>#EXT-X-ENDLIST</c>); otherwise it is still growing.</param>
/// <param name="MutedSegments">Muted MPEG-TS segments, which the repair handles.</param>
/// <param name="UnsupportedMutedSegments">
/// Muted segments in another container (for example fragmented MP4), which the repair cannot handle.
/// </param>
public readonly record struct TwitchMutedVodPlaylistInspection(
    bool IsMediaPlaylist,
    bool IsComplete,
    int MutedSegments,
    int UnsupportedMutedSegments);

/// <summary>
/// Pure helpers for Twitch VOD playlists that contain muted segments (<c>N-muted.ts</c>). Those
/// segments freeze libVLC releases before 3.0.18 (see <see cref="TwitchMutedSegmentSanitizer"/>),
/// so playback routes them -- and only them -- through a local repair proxy.
/// </summary>
public static class TwitchMutedVodPlaylist
{
    private const string MutedSegmentMarker = "-muted.";
    private const string MutedSegmentSuffix = MutedSegmentMarker + "ts";
    private const string SegmentTag = "#EXTINF";
    private const string VariantStreamTag = "#EXT-X-STREAM-INF";
    private const string EndListTag = "#EXT-X-ENDLIST";

    public static TwitchMutedVodPlaylistInspection Inspect(string? playlistContent)
    {
        if (string.IsNullOrEmpty(playlistContent))
        {
            return default;
        }

        var hasSegments = false;
        var hasVariantStreams = false;
        var isComplete = false;
        var mutedSegments = 0;
        var unsupportedMutedSegments = 0;
        foreach (var rawLine in playlistContent.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim().TrimStart('﻿');
            if (line.IsEmpty)
            {
                continue;
            }

            if (line[0] == '#')
            {
                hasSegments |= line.StartsWith(SegmentTag, StringComparison.Ordinal);
                hasVariantStreams |= line.StartsWith(VariantStreamTag, StringComparison.Ordinal);
                isComplete |= line.StartsWith(EndListTag, StringComparison.Ordinal);
                continue;
            }

            var pathEnd = line.IndexOfAny('?', '#');
            var path = pathEnd < 0 ? line : line[..pathEnd];
            if (path.EndsWith(MutedSegmentSuffix, StringComparison.OrdinalIgnoreCase))
            {
                mutedSegments++;
            }
            else if (FileName(path).Contains(MutedSegmentMarker, StringComparison.OrdinalIgnoreCase))
            {
                unsupportedMutedSegments++;
            }
        }

        var isMediaPlaylist = hasSegments && !hasVariantStreams;
        return isMediaPlaylist
            ? new TwitchMutedVodPlaylistInspection(true, isComplete, mutedSegments, unsupportedMutedSegments)
            : default;
    }

    public static bool IsMutedSegment(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsAbsoluteUri &&
            uri.AbsolutePath.EndsWith(MutedSegmentSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rewrites a media playlist so every muted segment is fetched from the URI returned by
    /// <paramref name="selectRepairedSegmentUri"/> and everything else directly from Twitch.
    /// </summary>
    /// <param name="playlistUri">
    /// Where the playlist came from: an approved Twitch HTTPS endpoint, or a local file written by
    /// the subscriber-only VOD fallback (whose entries are already absolute).
    /// </param>
    /// <exception cref="InvalidDataException">
    /// The playlist location or one of its URIs is not an approved Twitch HTTPS endpoint.
    /// </exception>
    public static string RewriteForRepair(
        string playlistContent,
        Uri playlistUri,
        Func<Uri, string> selectRepairedSegmentUri)
    {
        ArgumentNullException.ThrowIfNull(playlistContent);
        ArgumentNullException.ThrowIfNull(playlistUri);
        ArgumentNullException.ThrowIfNull(selectRepairedSegmentUri);
        if (!IsSupportedPlaylistLocation(playlistUri))
        {
            throw new InvalidDataException("The Twitch playlist location is not an approved HTTPS provider endpoint or a local file.");
        }

        return TwitchPlaylistUriRewriter.Rewrite(
            playlistContent,
            playlistUri,
            uri => IsMutedSegment(uri) ? selectRepairedSegmentUri(uri) : uri.AbsoluteUri);
    }

    /// <summary>
    /// Whether a playlist at <paramref name="playlistUri"/> may be inspected and repaired.
    /// </summary>
    public static bool IsSupportedPlaylistLocation(Uri? playlistUri) =>
        playlistUri is { IsAbsoluteUri: true } &&
        (playlistUri.IsFile || ProviderUriPolicy.IsApprovedReplayUri(playlistUri, PlatformKind.Twitch));

    private static ReadOnlySpan<char> FileName(ReadOnlySpan<char> path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }
}

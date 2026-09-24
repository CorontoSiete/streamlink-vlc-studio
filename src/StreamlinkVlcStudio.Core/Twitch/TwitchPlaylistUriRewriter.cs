using System.Text;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Security;

namespace StreamlinkVlcStudio.Core.Twitch;

/// <summary>
/// Rewrites every URI of a Twitch HLS media playlist. Each URI is resolved against the playlist
/// location and must be an approved Twitch HTTPS endpoint, so a rewritten playlist can never point
/// the player at a host the provider policy does not allow.
/// </summary>
internal static class TwitchPlaylistUriRewriter
{
    /// <param name="selectMediaUri">
    /// Chooses the text written for each media segment line from its validated absolute URI.
    /// URIs inside tag attributes (keys, initialization maps) are always written as absolute URIs.
    /// </param>
    internal static string Rewrite(string playlistContent, Uri playlistUri, Func<Uri, string> selectMediaUri)
    {
        var lines = playlistContent.Split('\n');
        var builder = new StringBuilder(playlistContent.Length + 256);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (i == 0)
            {
                line = line.TrimStart('﻿');
            }

            // Skip the artificial empty entry produced by a trailing newline.
            if (line.Length == 0 && i == lines.Length - 1)
            {
                break;
            }

            if (line.Length > 0)
            {
                builder.Append(line[0] == '#'
                    ? RewriteTagLine(line, playlistUri)
                    : selectMediaUri(ResolveApprovedUri(line.Trim(), playlistUri)));
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string RewriteTagLine(string line, Uri playlistUri)
    {
        const string marker = "URI=";
        var attributeStart = FindExactAttribute(line, marker);
        if (attributeStart < 0)
        {
            return line;
        }

        var quoteIndex = attributeStart + marker.Length;
        if (quoteIndex >= line.Length || line[quoteIndex] != '"')
        {
            throw new InvalidDataException("The playlist contained a malformed URI attribute.");
        }

        var start = quoteIndex + 1;
        var end = line.IndexOf('"', start);
        if (end < 0)
        {
            throw new InvalidDataException("The playlist contained an unterminated URI attribute.");
        }

        var uri = line[start..end];
        return string.Concat(line[..start], ResolveApprovedUri(uri, playlistUri).AbsoluteUri, line[end..]);
    }

    private static int FindExactAttribute(string line, string marker)
    {
        var searchIndex = 0;
        while (searchIndex < line.Length)
        {
            var index = line.IndexOf(marker, searchIndex, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return -1;
            }

            if (index == 0 || line[index - 1] is ':' or ',')
            {
                return index;
            }

            searchIndex = index + marker.Length;
        }

        return -1;
    }

    private static Uri ResolveApprovedUri(string uri, Uri playlistUri)
    {
        if (!ProviderUriPolicy.TryResolveReplayUri(
                uri,
                playlistUri,
                PlatformKind.Twitch,
                out var resolved))
        {
            throw new InvalidDataException("The playlist contained an unapproved media URI.");
        }

        return resolved;
    }
}

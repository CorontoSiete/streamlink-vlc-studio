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
        var colon = line.IndexOf(':');
        if (colon < 0 || !line.StartsWith("#EXT-X-", StringComparison.OrdinalIgnoreCase))
        {
            return line;
        }

        StringBuilder? rewritten = null;
        var copiedThrough = 0;
        var foundUri = false;
        for (var attributeStart = colon + 1; attributeStart < line.Length;)
        {
            // Commas and URI= inside a quoted value are metadata, not new attributes.
            var attributeEnd = attributeStart;
            var quoted = false;
            for (; attributeEnd < line.Length; attributeEnd++)
            {
                var character = line[attributeEnd];
                if (character == '"')
                {
                    quoted = !quoted;
                }
                else if (character == ',' && !quoted)
                {
                    break;
                }
            }

            if (quoted)
            {
                throw new InvalidDataException("The playlist contained an unterminated quoted attribute.");
            }

            var attribute = line.AsSpan(attributeStart, attributeEnd - attributeStart);
            var equals = attribute.IndexOf('=');
            if (equals >= 0 && attribute[..equals].Trim().Equals("URI", StringComparison.OrdinalIgnoreCase))
            {
                if (foundUri)
                {
                    throw new InvalidDataException("The playlist contained duplicate URI attributes.");
                }

                foundUri = true;
                var value = attribute[(equals + 1)..].Trim();
                if (value.Length < 2 || value[0] != '"' || value[^1] != '"' || value[1..^1].Contains('"'))
                {
                    throw new InvalidDataException("The playlist contained a malformed URI attribute.");
                }

                var resolved = ResolveApprovedUri(value[1..^1].ToString(), playlistUri);
                var valueStart = attributeStart + equals + 1;
                rewritten ??= new StringBuilder(line.Length + 128);
                rewritten.Append(line.AsSpan(copiedThrough, valueStart - copiedThrough));
                rewritten.Append('"').Append(resolved.AbsoluteUri).Append('"');
                copiedThrough = attributeEnd;
            }

            attributeStart = attributeEnd + 1;
        }

        return rewritten is null
            ? line
            : rewritten.Append(line.AsSpan(copiedThrough)).ToString();
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

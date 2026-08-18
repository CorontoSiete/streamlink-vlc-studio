using System.Globalization;
using StreamlinkVlcStudio.Core.Models;
using System.Text;

namespace StreamlinkVlcStudio.Core.Parsing;

public static class TwitchIrcParser
{
    private const int MaxMessageEmotes = 64;

    public static ChatMessage? TryParsePrivMsg(string? rawLine, string channel)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return null;
        }

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var line = rawLine;
        if (line.StartsWith('@'))
        {
            var tagEnd = line.IndexOf(' ');
            if (tagEnd <= 1)
            {
                return null;
            }

            foreach (var pair in line[1..tagEnd].Split(';'))
            {
                var split = pair.IndexOf('=');
                if (split > 0)
                {
                    tags[pair[..split]] = UnescapeTag(pair[(split + 1)..]);
                }
            }

            line = line[(tagEnd + 1)..];
        }

        var prefixSeparator = line.IndexOf(' ');
        const string privMsgCommand = "PRIVMSG ";
        var commandStart = prefixSeparator + 1;
        if (prefixSeparator <= 1 ||
            line[0] != ':' ||
            !line.AsSpan(commandStart).StartsWith(privMsgCommand, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var messageIndex = line.IndexOf(
            " :",
            commandStart + privMsgCommand.Length,
            StringComparison.Ordinal);
        if (messageIndex < 0)
        {
            return null;
        }

        var userSeparator = line.IndexOf('!');
        var fallbackUser = userSeparator > 1 && userSeparator < prefixSeparator
            ? line[1..userSeparator]
            : "unknown";

        var username = tags.TryGetValue("display-name", out var displayName) && !string.IsNullOrWhiteSpace(displayName)
            ? displayName
            : fallbackUser;

        var color = tags.TryGetValue("color", out var parsedColor) ? parsedColor : null;
        var badges = ParseBadges(tags);
        var messageText = NormalizeActionText(line[(messageIndex + 2)..]);
        var emotes = tags.TryGetValue("emotes", out var rawEmotes) && rawEmotes.Length > 0
            ? ParseEmotesTag(messageText, rawEmotes)
            : Array.Empty<ChatEmote>();
        var roomId = tags.TryGetValue("room-id", out var parsedRoomId) && !string.IsNullOrWhiteSpace(parsedRoomId)
            ? parsedRoomId
            : null;
        var messageId = tags.TryGetValue("id", out var parsedMessageId) && !string.IsNullOrWhiteSpace(parsedMessageId)
            ? parsedMessageId.Trim()
            : null;
        var timestamp = TryReadTmiSentTimestamp(tags, out var sentAt)
            ? sentAt
            : DateTimeOffset.UtcNow;

        return new ChatMessage(
            PlatformKind.Twitch,
            channel,
            username,
            messageText,
            timestamp,
            color,
            badges,
            emotes,
            roomId,
            messageId);
    }

    private static bool TryReadTmiSentTimestamp(Dictionary<string, string> tags, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!tags.TryGetValue("tmi-sent-ts", out var rawTimestamp) ||
            !long.TryParse(rawTimestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
        {
            return false;
        }

        try
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            timestamp = default;
            return false;
        }
    }

    private static ChatBadge[] ParseBadges(Dictionary<string, string> tags)
    {
        var badges = new List<ChatBadge>();
        if (tags.TryGetValue("badges", out var rawBadges) && rawBadges.Length > 0)
        {
            foreach (var rawBadge in rawBadges.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var split = rawBadge.IndexOf('/');
                var id = split >= 0 ? rawBadge[..split] : rawBadge;
                var version = split >= 0 && split + 1 < rawBadge.Length ? rawBadge[(split + 1)..] : null;
                AddBadge(badges, id, version, TwitchBadgeValues.ResolveTitle(id));
            }
        }

        if ((tags.TryGetValue("mod", out var mod) && string.Equals(mod, "1", StringComparison.Ordinal)) ||
            (tags.TryGetValue("user-type", out var userType) && string.Equals(userType, "mod", StringComparison.OrdinalIgnoreCase)))
        {
            AddBadge(badges, "moderator", "1", TwitchBadgeValues.ResolveTitle("moderator"));
        }

        return badges.Count > 0 ? badges.ToArray() : [];
    }

    private static void AddBadge(List<ChatBadge> badges, string? id, string? version, string? title, string? imageUrl = null)
    {
        id = TwitchBadgeValues.NormalizeId(id);
        if (string.IsNullOrWhiteSpace(id) ||
            badges.Any(existing => string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        badges.Add(new ChatBadge(id, string.IsNullOrWhiteSpace(version) ? null : version.Trim(), title, imageUrl));
    }

    private static ChatEmote[] ParseEmotesTag(string message, string emotesTag)
    {
        if (string.IsNullOrEmpty(message) || string.IsNullOrWhiteSpace(emotesTag))
        {
            return [];
        }

        var emotes = new List<ChatEmote>();
        foreach (var emoteGroup in emotesTag.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = emoteGroup.IndexOf(':');
            if (split <= 0 || split == emoteGroup.Length - 1)
            {
                continue;
            }

            var id = emoteGroup[..split];
            foreach (var rangeText in emoteGroup[(split + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var rangeSplit = rangeText.IndexOf('-');
                if (rangeSplit <= 0 ||
                    !int.TryParse(rangeText[..rangeSplit], NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
                    !int.TryParse(rangeText[(rangeSplit + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var end) ||
                    end < start ||
                    end == int.MaxValue)
                {
                    continue;
                }

                if (!TryGetStringRangeFromCodepointRange(message, start, end, out var startIndex, out var endIndex) &&
                    !TryGetStringRangeFromStringIndexes(message, start, end + 1, out startIndex, out endIndex))
                {
                    continue;
                }

                if (startIndex < 0 || endIndex <= startIndex || endIndex > message.Length)
                {
                    continue;
                }

                var code = message[startIndex..endIndex];
                if (!IsPlausibleEmoteCode(code))
                {
                    continue;
                }

                emotes.Add(new ChatEmote(
                    startIndex,
                    endIndex,
                    code,
                    BuildTwitchEmoteImageUrl(id)));

                if (emotes.Count >= MaxMessageEmotes)
                {
                    return emotes
                        .OrderBy(emote => emote.StartIndex)
                        .ThenBy(emote => emote.EndIndex)
                        .ToArray();
                }
            }
        }

        return emotes
            .OrderBy(emote => emote.StartIndex)
            .ThenBy(emote => emote.EndIndex)
            .ToArray();
    }

    private static string BuildTwitchEmoteImageUrl(string id)
    {
        // Twitch's documented CDN template uses an explicit format. Static PNGs are
        // available for normal emotes and avoid relying on the legacy `default` format;
        // AnimatedEmoteImage can fall back to the animated format for animated-only emotes.
        return $"https://static-cdn.jtvnw.net/emoticons/v2/{Uri.EscapeDataString(id)}/static/light/2.0";
    }

    private static bool TryGetStringRangeFromCodepointRange(
        string message,
        int startCodepoint,
        int endCodepointInclusive,
        out int startIndex,
        out int endIndex)
    {
        startIndex = -1;
        endIndex = -1;
        if (startCodepoint < 0 || endCodepointInclusive < startCodepoint)
        {
            return false;
        }

        var codepoint = 0;
        for (var index = 0; index < message.Length;)
        {
            if (codepoint == startCodepoint)
            {
                startIndex = index;
            }

            var step = char.IsHighSurrogate(message[index]) &&
                index + 1 < message.Length &&
                char.IsLowSurrogate(message[index + 1])
                    ? 2
                    : 1;

            if (codepoint == endCodepointInclusive)
            {
                endIndex = index + step;
                return startIndex >= 0 && endIndex > startIndex;
            }

            index += step;
            codepoint++;
        }

        return false;
    }

    private static bool TryGetStringRangeFromStringIndexes(
        string message,
        int startIndex,
        int endIndex,
        out int validStartIndex,
        out int validEndIndex)
    {
        validStartIndex = startIndex;
        validEndIndex = endIndex;
        return startIndex >= 0 && endIndex > startIndex && endIndex <= message.Length;
    }

    private static bool IsPlausibleEmoteCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length >= 96)
        {
            return false;
        }

        return code.All(character => !char.IsControl(character) && !char.IsWhiteSpace(character));
    }

    private static string NormalizeActionText(string text)
    {
        const string actionPrefix = "\u0001ACTION ";
        if (!text.StartsWith(actionPrefix, StringComparison.Ordinal))
        {
            return text;
        }

        var normalized = text[actionPrefix.Length..];
        return normalized.EndsWith('\u0001') ? normalized[..^1] : normalized;
    }

    private static string UnescapeTag(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\')
            {
                result.Append(value[index]);
                continue;
            }

            // IRCv3 specifies that a trailing backslash produces no output.
            if (index == value.Length - 1)
            {
                break;
            }

            var escaped = value[++index];
            switch (escaped)
            {
                case 's':
                    result.Append(' ');
                    break;
                case ':':
                    result.Append(';');
                    break;
                case '\\':
                    result.Append('\\');
                    break;
                case 'r':
                    result.Append('\r');
                    break;
                case 'n':
                    result.Append('\n');
                    break;
                default:
                    // Invalid escape sequences keep the escaped character but drop
                    // the backslash, as required by the IRCv3 message-tags grammar.
                    result.Append(escaped);
                    break;
            }
        }

        return result.ToString();
    }
}

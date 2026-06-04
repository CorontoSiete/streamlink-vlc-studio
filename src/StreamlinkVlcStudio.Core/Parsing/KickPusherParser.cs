using System.Globalization;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Parsing;

public static class KickPusherParser
{
    private const int MaxMessageEmotes = 64;

    public static ChatMessage? TryParse(string websocketPayload, string channel)
    {
        try
        {
            using var document = JsonDocument.Parse(websocketPayload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!root.TryGetProperty("event", out var eventElement))
            {
                return null;
            }

            var eventName = eventElement.ValueKind == JsonValueKind.String
                ? eventElement.GetString() ?? ""
                : eventElement.ToString();
            if (!eventName.Contains("ChatMessage", StringComparison.OrdinalIgnoreCase) &&
                !eventName.Contains("chat.message", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!root.TryGetProperty("data", out var dataElement))
            {
                return null;
            }

            using var dataDocument = JsonDocument.Parse(dataElement.ValueKind == JsonValueKind.String ? dataElement.GetString() ?? "{}" : dataElement.GetRawText());
            var data = dataDocument.RootElement;

            return TryParseMessageData(data, channel);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ChatMessage? TryParseMessageData(JsonElement data, string channel)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var content = GetString(data, "content") ?? GetString(data, "message") ?? "";
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var messageId = GetMessageId(data);
        var username = "unknown";
        string? color = null;
        IReadOnlyList<ChatBadge> badges = Array.Empty<ChatBadge>();
        if (data.TryGetProperty("sender", out var sender))
        {
            username = GetString(sender, "username") ?? GetString(sender, "slug") ?? username;
            color = GetString(sender, "identity", "color") ?? GetString(sender, "color");
            badges = ParseBadges(sender);
        }
        else if (data.TryGetProperty("user", out var user))
        {
            username = GetString(user, "username") ?? GetString(user, "name") ?? username;
            color = GetString(user, "color");
            badges = ParseBadges(user);
        }

        if (badges.Count == 0)
        {
            badges = ParseBadges(data);
        }

        var timestamp = TryReadMessageTimestamp(data, out var parsedTimestamp)
            ? parsedTimestamp
            : DateTimeOffset.Now;

        return new ChatMessage(
            PlatformKind.Kick,
            channel,
            username,
            content,
            timestamp,
            color,
            badges,
            Emotes: ParseEmoteMarkers(content),
            MessageId: messageId);
    }

    private static IReadOnlyList<ChatBadge> ParseBadges(JsonElement element)
    {
        var badges = new List<ParsedKickBadge>();
        if (element.TryGetProperty("identity", out var identity))
        {
            AddKickBadgesFromProperty(badges, identity, "badges_v2");
            AddKickBadgesFromProperty(badges, identity, "badges");
        }

        AddKickBadgesFromProperty(badges, element, "badges_v2");
        AddKickBadgesFromProperty(badges, element, "badges");
        return badges.Count > 0
            ? badges
                .OrderBy(badge => badge.SortOrder)
                .ThenBy(badge => badge.Index)
                .Select(badge => badge.Badge)
                .ToArray()
            : Array.Empty<ChatBadge>();
    }

    private static void AddKickBadgesFromProperty(List<ParsedKickBadge> badges, JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var badgeArray) ||
            badgeArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var badge in badgeArray.EnumerateArray())
        {
            if (badge.ValueKind == JsonValueKind.String)
            {
                AddBadge(badges, badge.GetString(), null, null, null, badges.Count, badges.Count);
                continue;
            }

            if (badge.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = GetString(badge, "type") ??
                GetString(badge, "id") ??
                GetString(badge, "name") ??
                GetString(badge, "slug") ??
                GetString(badge, "badge_type") ??
                GetString(badge, "text");
            var version = FirstNonEmpty(
                GetString(badge, "count"),
                GetString(badge, "months"),
                GetString(badge, "tier"),
                GetString(badge, "metadata", "level"),
                GetString(badge, "metadata", "count"),
                GetString(badge, "metadata", "months"),
                GetString(badge, "metadata", "tier"),
                GetString(badge, "info"),
                GetString(badge, "metadata", "info"));
            var explicitTitle = FirstNonEmpty(
                GetString(badge, "text"),
                GetString(badge, "title"),
                GetString(badge, "label"),
                GetString(badge, "metadata", "title"),
                GetString(badge, "metadata", "label"),
                GetString(badge, "info"),
                GetString(badge, "metadata", "info"));
            var title = ResolveBadgeTitle(id, version, explicitTitle, GetString(badge, "name"));
            var imageUrl = GetBadgeImageUrl(badge);
            AddBadge(badges, id, version, title, imageUrl, TryGetInt(badge, "sort_order", badges.Count), badges.Count);
        }
    }

    private static void AddBadge(
        List<ParsedKickBadge> badges,
        string? id,
        string? version,
        string? title,
        string? imageUrl,
        int sortOrder,
        int index)
    {
        id = NormalizeBadgeId(id);
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var badge = new ChatBadge(
            id,
            string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
            string.IsNullOrWhiteSpace(title) ? ResolveBadgeTitle(id) : title.Trim(),
            string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl.Trim());

        var existingIndex = badges.FindIndex(existing =>
            string.Equals(existing.Badge.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            var existing = badges[existingIndex];
            if (string.IsNullOrWhiteSpace(existing.Badge.ImageUrl) &&
                !string.IsNullOrWhiteSpace(badge.ImageUrl))
            {
                badges[existingIndex] = new ParsedKickBadge(
                    badge,
                    Math.Min(existing.SortOrder, sortOrder),
                    existing.Index);
            }

            return;
        }

        badges.Add(new ParsedKickBadge(badge, sortOrder, index));
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string NormalizeBadgeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "";
        }

        var normalized = id.Trim().ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            "gift_sub" => "sub_gifter",
            "gift_subs" => "sub_gifter",
            "gift_subscriber" => "sub_gifter",
            "gift_subscription" => "sub_gifter",
            "gifted_sub" => "sub_gifter",
            "gifted_subs" => "sub_gifter",
            "gifted_subscriber" => "sub_gifter",
            "gifted_subscription" => "sub_gifter",
            "gifter" => "sub_gifter",
            "subgifter" => "sub_gifter",
            "sub_gift" => "sub_gifter",
            "sub_gifter_badge" => "sub_gifter",
            "sub_gifts" => "sub_gifter",
            "subscriber_gifter" => "sub_gifter",
            "subscription_gift" => "sub_gifter",
            "subscription_gifts" => "sub_gifter",
            "subscription" => "subscriber",
            "sub" => "subscriber",
            _ => normalized
        };
    }

    private static string ResolveBadgeTitle(string? id)
    {
        return ResolveBadgeTitle(id, null, null, null);
    }

    private static string ResolveBadgeTitle(string? id, string? version, string? explicitTitle, string? name)
    {
        var normalized = NormalizeBadgeId(id);
        if (normalized == "level" && !string.IsNullOrWhiteSpace(version))
        {
            return $"Level {version.Trim()}";
        }

        if (normalized == "subscriber" &&
            int.TryParse(version, out var subscriberMonths) &&
            subscriberMonths > 0)
        {
            return $"{subscriberMonths}-Month Subscriber";
        }

        if (!string.IsNullOrWhiteSpace(explicitTitle))
        {
            return explicitTitle.Trim();
        }

        if (!string.IsNullOrWhiteSpace(name) &&
            !string.Equals(NormalizeBadgeId(name), normalized, StringComparison.OrdinalIgnoreCase))
        {
            return name.Trim();
        }

        return normalized switch
        {
            "broadcaster" => "Broadcaster",
            "founder" => "Founder",
            "level" => "Level",
            "moderator" => "Moderator",
            "og" => "OG",
            "staff" => "Staff",
            "sub_gifter" => "Sub Gifter",
            "subgifter" => "Sub Gifter",
            "subscriber" => "Subscriber",
            "verified" => "Verified",
            "vip" => "VIP",
            var badgeId when badgeId.Length > 0 => ToTitle(badgeId),
            _ => "Badge"
        };
    }

    private static string ToTitle(string value)
    {
        return string.Join(
            ' ',
            value.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static string? GetBadgeImageUrl(JsonElement badge)
    {
        foreach (var propertyName in new[]
        {
            "imageUrl",
            "image_url",
            "image",
            "iconUrl",
            "icon_url",
            "icon",
            "url",
            "src"
        })
        {
            if (TryGetImageLikeString(badge, propertyName, out var imageUrl))
            {
                return imageUrl;
            }
        }

        return TryFindImageLikeString(badge, out var nestedImageUrl) ? nestedImageUrl : null;
    }

    private static bool TryFindImageLikeString(JsonElement element, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (IsImageLikePropertyName(property.Name) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    value = property.Value.GetString() ?? "";
                    if (LooksLikeImageUrl(value))
                    {
                        return true;
                    }
                }

                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array &&
                    TryFindImageLikeString(property.Value, out value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindImageLikeString(item, out value))
                {
                    return true;
                }
            }
        }

        value = "";
        return false;
    }

    private static bool TryGetImageLikeString(JsonElement item, string propertyName, out string value)
    {
        if (GetString(item, propertyName) is { Length: > 0 } parsed && LooksLikeImageUrl(parsed))
        {
            value = parsed;
            return true;
        }

        value = "";
        return false;
    }

    private static bool IsImageLikePropertyName(string propertyName)
    {
        return propertyName.Contains("image", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Contains("icon", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "url", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "src", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeImageUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith("/", StringComparison.Ordinal);
    }

    private static int TryGetInt(JsonElement item, string propertyName, int fallback)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty(propertyName, out var property))
        {
            return fallback;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => fallback
        };
    }

    private static bool TryReadMessageTimestamp(JsonElement data, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var propertyName in new[] { "created_at", "createdAt", "timestamp", "sent_at", "sentAt" })
        {
            if (data.TryGetProperty(propertyName, out var property) &&
                TryReadTimestampValue(property, out timestamp))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadTimestampValue(JsonElement property, out DateTimeOffset timestamp)
    {
        timestamp = default;
        switch (property.ValueKind)
        {
            case JsonValueKind.String:
                var value = property.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                value = value.Trim();
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integerTimestamp) &&
                    TryReadUnixTimestamp(integerTimestamp, out timestamp))
                {
                    return true;
                }

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatingTimestamp) &&
                    TryReadUnixTimestamp(floatingTimestamp, out timestamp))
                {
                    return true;
                }

                return DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out timestamp);
            case JsonValueKind.Number:
                if (property.TryGetInt64(out var numericTimestamp) &&
                    TryReadUnixTimestamp(numericTimestamp, out timestamp))
                {
                    return true;
                }

                if (property.TryGetDouble(out var numericTimestampDouble) &&
                    TryReadUnixTimestamp(numericTimestampDouble, out timestamp))
                {
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private static bool TryReadUnixTimestamp(long value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        try
        {
            var absoluteValue = Math.Abs(value);
            timestamp = absoluteValue switch
            {
                >= 1_000_000_000_000_000_000 => DateTimeOffset.FromUnixTimeMilliseconds(value / 1_000_000),
                >= 1_000_000_000_000_000 => DateTimeOffset.FromUnixTimeMilliseconds(value / 1_000),
                >= 1_000_000_000_000 => DateTimeOffset.FromUnixTimeMilliseconds(value),
                _ => DateTimeOffset.FromUnixTimeSeconds(value)
            };
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            timestamp = default;
            return false;
        }
        catch (OverflowException)
        {
            timestamp = default;
            return false;
        }
    }

    private static bool TryReadUnixTimestamp(double value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return false;
        }

        var milliseconds = Math.Abs(value) >= 1_000_000_000_000
            ? value
            : value * 1000;
        if (milliseconds < long.MinValue || milliseconds > long.MaxValue)
        {
            return false;
        }

        try
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(milliseconds));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            timestamp = default;
            return false;
        }
    }

    private static IReadOnlyList<ChatEmote> ParseEmoteMarkers(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return Array.Empty<ChatEmote>();
        }

        var emotes = new List<ChatEmote>();
        var searchIndex = 0;
        while (searchIndex < message.Length && emotes.Count < MaxMessageEmotes)
        {
            var markerStart = message.IndexOf("[emote:", searchIndex, StringComparison.Ordinal);
            if (markerStart < 0)
            {
                break;
            }

            var idStart = markerStart + "[emote:".Length;
            var idEnd = idStart;
            while (idEnd < message.Length && char.IsDigit(message[idEnd]))
            {
                idEnd++;
            }

            if (idEnd == idStart || idEnd >= message.Length || message[idEnd] != ':')
            {
                searchIndex = markerStart + 1;
                continue;
            }

            var nameStart = idEnd + 1;
            var nameEnd = message.IndexOf(']', nameStart);
            if (nameEnd < 0)
            {
                break;
            }

            var name = message[nameStart..nameEnd];
            if (IsPlausibleKickEmoteName(name))
            {
                var id = message[idStart..idEnd];
                emotes.Add(new ChatEmote(
                    markerStart,
                    nameEnd + 1,
                    name,
                    $"https://files.kick.com/emotes/{Uri.EscapeDataString(id)}/fullsize"));
            }

            searchIndex = nameEnd + 1;
        }

        return emotes
            .OrderBy(emote => emote.StartIndex)
            .ThenBy(emote => emote.EndIndex)
            .ToArray();
    }

    private static bool IsPlausibleKickEmoteName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length >= 96)
        {
            return false;
        }

        return name.All(character =>
            !char.IsControl(character) &&
            !char.IsWhiteSpace(character) &&
            character is not '[' and not ']' and not ':');
    }

    private static string? GetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var part in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
    }

    private static string? GetMessageId(JsonElement data)
    {
        foreach (var propertyName in new[] { "id", "message_id", "messageId", "uuid" })
        {
            var value = GetString(data, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private sealed record ParsedKickBadge(ChatBadge Badge, int SortOrder, int Index);
}

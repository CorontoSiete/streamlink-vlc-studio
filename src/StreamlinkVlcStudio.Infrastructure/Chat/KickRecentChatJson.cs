using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

internal static class KickRecentChatJson
{
    public static ChatMessage[] ReadMessages(JsonElement root, string channel)
    {
        if (!TryGetMessageArray(root, out var messagesElement))
        {
            return [];
        }

        return messagesElement.EnumerateArray()
            .Select(item => KickPusherParser.TryParseMessageData(item, channel))
            .OfType<ChatMessage>()
            .OrderBy(message => message.Timestamp)
            .ThenBy(message => message.MessageId, StringComparer.Ordinal)
            .ToArray();
    }

    public static bool TryGetMessageArray(JsonElement root, out JsonElement messages)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("data", out var data))
            {
                if (data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("messages", out messages) &&
                    messages.ValueKind == JsonValueKind.Array)
                {
                    return true;
                }

                if (data.ValueKind == JsonValueKind.Array)
                {
                    messages = data;
                    return true;
                }
            }

            if (root.TryGetProperty("messages", out messages) &&
                messages.ValueKind == JsonValueKind.Array)
            {
                return true;
            }
        }

        messages = default;
        return false;
    }

    public static string? ReadCursor(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            TryReadNonEmptyString(data, "cursor", out var dataCursor))
        {
            return dataCursor;
        }

        return TryReadNonEmptyString(root, "cursor", out var rootCursor)
            ? rootCursor
            : null;
    }

    private static bool TryReadNonEmptyString(JsonElement element, string propertyName, out string value)
    {
        value = "";
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        value = property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : property.ToString();
        value = value.Trim();
        return !string.IsNullOrWhiteSpace(value);
    }
}

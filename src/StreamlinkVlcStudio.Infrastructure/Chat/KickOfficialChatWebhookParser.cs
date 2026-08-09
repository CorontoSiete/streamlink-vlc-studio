using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public static class KickOfficialChatWebhookParser
{
    public const string ChatMessageSentEventType = "chat.message.sent";

    public static bool TryParseChatMessage(string body, out ChatMessage message, out string error)
    {
        message = default!;
        error = "";
        if (string.IsNullOrWhiteSpace(body))
        {
            error = "Webhook body was empty.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Webhook body was not a JSON object.";
                return false;
            }

            var channel = ReadBroadcasterChannel(root);
            if (string.IsNullOrWhiteSpace(channel))
            {
                error = "Webhook body did not include broadcaster.channel_slug.";
                return false;
            }

            var parsed = KickPusherParser.TryParseMessageData(root, channel);
            if (parsed is null)
            {
                error = "Webhook body did not contain a usable chat message.";
                return false;
            }

            message = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Webhook body was not valid JSON: {ex.Message}";
            return false;
        }
    }

    private static string ReadBroadcasterChannel(JsonElement root)
    {
        if (!root.TryGetProperty("broadcaster", out var broadcaster) ||
            broadcaster.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        return FirstNonEmpty(
            GetOptionalString(broadcaster, "channel_slug"),
            GetOptionalString(broadcaster, "slug"),
            GetOptionalString(broadcaster, "username"));
    }

}

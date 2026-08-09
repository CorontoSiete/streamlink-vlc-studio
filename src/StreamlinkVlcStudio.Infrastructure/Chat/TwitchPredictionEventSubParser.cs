using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public sealed class TwitchPredictionEventSubParser
{
    private const int MaxSeenMessageIds = 512;
    private readonly Queue<string> seenMessageIds = [];
    private readonly HashSet<string> seenMessageIdSet = new(StringComparer.Ordinal);

    public bool TryParse(string json, out TwitchEventSubMessage message)
    {
        message = TwitchEventSubMessage.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("metadata", out var metadata) ||
                metadata.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var messageId = TwitchPredictionJson.GetOptionalString(metadata, "message_id");
            var messageType = TwitchPredictionJson.GetOptionalString(metadata, "message_type");
            var duplicate = !string.IsNullOrWhiteSpace(messageId) && IsDuplicate(messageId);
            var payload = root.TryGetProperty("payload", out var payloadElement) && payloadElement.ValueKind == JsonValueKind.Object
                ? payloadElement
                : default;

            string? sessionId = null;
            int? keepaliveTimeoutSeconds = null;
            string? reconnectUrl = null;
            string? revocationStatus = null;
            TwitchPrediction? prediction = null;

            if (messageType.Equals("session_welcome", StringComparison.OrdinalIgnoreCase) &&
                payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("session", out var session))
            {
                sessionId = TwitchPredictionJson.GetOptionalString(session, "id");
                reconnectUrl = NullIfEmpty(TwitchPredictionJson.GetOptionalString(session, "reconnect_url"));
                var keepalive = TwitchPredictionJson.GetOptionalInt32(session, "keepalive_timeout_seconds");
                keepaliveTimeoutSeconds = keepalive > 0 ? keepalive : null;
            }
            else if (messageType.Equals("session_reconnect", StringComparison.OrdinalIgnoreCase) &&
                payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("session", out var reconnectSession))
            {
                reconnectUrl = NullIfEmpty(TwitchPredictionJson.GetOptionalString(reconnectSession, "reconnect_url"));
            }
            else if (messageType.Equals("revocation", StringComparison.OrdinalIgnoreCase) &&
                payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("subscription", out var revokedSubscription))
            {
                revocationStatus = NullIfEmpty(TwitchPredictionJson.GetOptionalString(revokedSubscription, "status"));
            }
            else if (messageType.Equals("notification", StringComparison.OrdinalIgnoreCase) &&
                !duplicate &&
                payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("subscription", out var subscription) &&
                payload.TryGetProperty("event", out var eventElement))
            {
                var subscriptionType = TwitchPredictionJson.GetOptionalString(subscription, "type");
                if (IsPredictionSubscription(subscriptionType))
                {
                    prediction = TwitchPredictionJson.ReadPrediction(eventElement, subscriptionType);
                }
            }

            message = new TwitchEventSubMessage(
                messageId,
                messageType,
                duplicate,
                sessionId,
                keepaliveTimeoutSeconds,
                reconnectUrl,
                revocationStatus,
                prediction);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool IsDuplicate(string messageId)
    {
        if (!seenMessageIdSet.Add(messageId))
        {
            return true;
        }

        seenMessageIds.Enqueue(messageId);
        while (seenMessageIds.Count > MaxSeenMessageIds)
        {
            seenMessageIdSet.Remove(seenMessageIds.Dequeue());
        }

        return false;
    }

    private static bool IsPredictionSubscription(string subscriptionType)
    {
        return subscriptionType is
            "channel.prediction.begin" or
            "channel.prediction.progress" or
            "channel.prediction.lock" or
            "channel.prediction.end";
    }
}

public sealed record TwitchEventSubMessage(
    string MessageId,
    string MessageType,
    bool IsDuplicate,
    string? SessionId,
    int? KeepaliveTimeoutSeconds,
    string? ReconnectUrl,
    string? RevocationStatus,
    TwitchPrediction? Prediction)
{
    public static TwitchEventSubMessage Empty { get; } = new("", "", false, null, null, null, null, null);
}

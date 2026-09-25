using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public sealed class TwitchPredictionEventSubParser
{
    private const int MaxSeenMessageIds = 512;
    private const int MaxMessageIdLength = 256;
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
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("metadata", out var metadata) ||
                !TryGetNonEmptyString(metadata, "message_id", out var messageId) ||
                string.IsNullOrWhiteSpace(messageId) || messageId.Length > MaxMessageIdLength ||
                !TryGetNonEmptyString(metadata, "message_type", out var messageType) ||
                !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string? sessionId = null;
            int? keepaliveTimeoutSeconds = null;
            string? reconnectUrl = null;
            string? revocationStatus = null;
            TwitchPrediction? prediction = null;

            if (messageType.Equals("session_welcome", StringComparison.OrdinalIgnoreCase))
            {
                if (!payload.TryGetProperty("session", out var session) ||
                    !TryGetNonEmptyString(session, "id", out sessionId) || string.IsNullOrWhiteSpace(sessionId))
                    return false;
                var keepalive = TwitchPredictionJson.GetOptionalInt32(session, "keepalive_timeout_seconds");
                keepaliveTimeoutSeconds = keepalive > 0 ? keepalive : null;
            }
            else if (messageType.Equals("session_reconnect", StringComparison.OrdinalIgnoreCase))
            {
                if (!payload.TryGetProperty("session", out var session) ||
                    !TryGetNonEmptyString(session, "reconnect_url", out reconnectUrl) || string.IsNullOrWhiteSpace(reconnectUrl))
                    return false;
            }
            else if (messageType.Equals("revocation", StringComparison.OrdinalIgnoreCase))
            {
                if (!payload.TryGetProperty("subscription", out var subscription) ||
                    !TryGetNonEmptyString(subscription, "status", out revocationStatus) || string.IsNullOrWhiteSpace(revocationStatus))
                    return false;
            }
            else if (messageType.Equals("notification", StringComparison.OrdinalIgnoreCase))
            {
                if (!payload.TryGetProperty("subscription", out var subscription) ||
                    !TryGetNonEmptyString(subscription, "type", out var subscriptionType) ||
                    !IsPredictionSubscription(subscriptionType) ||
                    !payload.TryGetProperty("event", out var eventElement) ||
                    (prediction = TwitchPredictionJson.ReadPrediction(eventElement, subscriptionType)) is null)
                    return false;
            }
            else if (!messageType.Equals("session_keepalive", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Only accepted messages consume deduplication IDs. A malformed delivery must
            // not hide a later usable notification with the same ID.
            var duplicate = IsDuplicate(messageId);
            message = new TwitchEventSubMessage(
                messageId,
                messageType,
                duplicate,
                sessionId,
                keepaliveTimeoutSeconds,
                reconnectUrl,
                revocationStatus,
                duplicate ? null : prediction);
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

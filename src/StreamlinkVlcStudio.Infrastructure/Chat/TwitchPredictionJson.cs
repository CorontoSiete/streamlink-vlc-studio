using System.Globalization;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

internal static class TwitchPredictionJson
{
    public static TwitchPrediction ReadPrediction(JsonElement element, string eventType = "")
    {
        var startedAt = GetOptionalTimestamp(element, "created_at") ??
            GetOptionalTimestamp(element, "started_at");
        var locksAt = GetOptionalTimestamp(element, "locked_at") ??
            GetOptionalTimestamp(element, "locks_at");
        var endedAt = GetOptionalTimestamp(element, "ended_at");
        var predictionWindowSeconds = GetOptionalInt32(element, "prediction_window");
        if (predictionWindowSeconds <= 0 &&
            startedAt is { } started &&
            locksAt is { } locks &&
            locks > started)
        {
            predictionWindowSeconds = (int)Math.Round((locks - started).TotalSeconds);
        }

        return new TwitchPrediction(
            GetOptionalString(element, "id"),
            FirstNonEmpty(GetOptionalString(element, "broadcaster_id"), GetOptionalString(element, "broadcaster_user_id")),
            FirstNonEmpty(GetOptionalString(element, "broadcaster_login"), GetOptionalString(element, "broadcaster_user_login")),
            FirstNonEmpty(GetOptionalString(element, "broadcaster_name"), GetOptionalString(element, "broadcaster_user_name")),
            GetOptionalString(element, "title"),
            NullIfEmpty(GetOptionalString(element, "winning_outcome_id")),
            ReadOutcomes(element),
            Math.Max(0, predictionWindowSeconds),
            ReadStatus(GetOptionalString(element, "status"), eventType),
            startedAt,
            locksAt,
            endedAt);
    }

    public static TwitchPrediction? ReadFirstPrediction(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            return ReadPrediction(item);
        }

        return null;
    }

    public static string GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return "";
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? "",
            JsonValueKind.Number => property.GetRawText(),
            _ => ""
        };
    }

    public static int GetOptionalInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return GetOptionalInt32(property);
    }

    public static DateTimeOffset? GetOptionalTimestamp(JsonElement element, string propertyName)
    {
        var value = GetOptionalString(element, propertyName);
        return TryParseTimestamp(value, out var timestamp) ? timestamp : null;
    }

    public static bool TryParseTimestamp(string value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (DateTimeOffset.TryParse(
            normalized,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp))
        {
            return true;
        }

        var zIndex = normalized.IndexOf('Z', StringComparison.OrdinalIgnoreCase);
        var dotIndex = normalized.IndexOf('.');
        if (dotIndex >= 0 && zIndex > dotIndex)
        {
            var fractionalLength = zIndex - dotIndex - 1;
            if (fractionalLength > 7)
            {
                normalized = normalized[..(dotIndex + 1 + 7)] + normalized[zIndex..];
            }
        }

        return DateTimeOffset.TryParse(
            normalized,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    private static IReadOnlyList<TwitchPredictionOutcome> ReadOutcomes(JsonElement element)
    {
        if (!element.TryGetProperty("outcomes", out var outcomesElement) ||
            outcomesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var outcomes = new List<TwitchPredictionOutcome>();
        foreach (var outcomeElement in outcomesElement.EnumerateArray())
        {
            outcomes.Add(new TwitchPredictionOutcome(
                GetOptionalString(outcomeElement, "id"),
                GetOptionalString(outcomeElement, "title"),
                GetOptionalString(outcomeElement, "color"),
                GetOptionalInt32(outcomeElement, "users"),
                GetOptionalInt32(outcomeElement, "channel_points"),
                ReadTopPredictors(outcomeElement)));
        }

        return outcomes;
    }

    private static IReadOnlyList<TwitchPredictionTopPredictor> ReadTopPredictors(JsonElement outcomeElement)
    {
        if (!outcomeElement.TryGetProperty("top_predictors", out var predictorsElement) ||
            predictorsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var predictors = new List<TwitchPredictionTopPredictor>();
        foreach (var predictorElement in predictorsElement.EnumerateArray())
        {
            predictors.Add(new TwitchPredictionTopPredictor(
                GetOptionalString(predictorElement, "user_id"),
                GetOptionalString(predictorElement, "user_login"),
                GetOptionalString(predictorElement, "user_name"),
                GetOptionalInt32(predictorElement, "channel_points_used"),
                predictorElement.TryGetProperty("channel_points_won", out var wonElement) &&
                    wonElement.ValueKind != JsonValueKind.Null
                    ? GetOptionalInt32(wonElement)
                    : null));
        }

        return predictors;
    }

    private static int GetOptionalInt32(JsonElement property)
    {
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.Number when property.TryGetInt64(out var longValue) => longValue > int.MaxValue ? int.MaxValue : (int)longValue,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => 0
        };
    }

    private static TwitchPredictionStatus ReadStatus(string status, string eventType)
    {
        if (status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            return TwitchPredictionStatus.Active;
        }

        if (status.Equals("LOCKED", StringComparison.OrdinalIgnoreCase))
        {
            return TwitchPredictionStatus.Locked;
        }

        if (status.Equals("RESOLVED", StringComparison.OrdinalIgnoreCase))
        {
            return TwitchPredictionStatus.Resolved;
        }

        if (status.Equals("CANCELED", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return TwitchPredictionStatus.Canceled;
        }

        return eventType switch
        {
            "channel.prediction.begin" or "channel.prediction.progress" => TwitchPredictionStatus.Active,
            "channel.prediction.lock" => TwitchPredictionStatus.Locked,
            _ => TwitchPredictionStatus.Unknown
        };
    }
}

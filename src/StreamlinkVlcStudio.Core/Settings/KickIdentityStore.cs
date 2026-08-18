namespace StreamlinkVlcStudio.Core.Settings;

/// <summary>
/// Owns the two Kick channel-identity maps as one atomically published copy-on-write snapshot.
/// Published dictionaries are never mutated, so readers and settings serialization can safely
/// enumerate them while a subscription or channel lookup publishes an update.
/// </summary>
internal sealed class KickIdentityStore
{
    private readonly object gate = new();
    private Snapshot current = Snapshot.Empty;

    internal Dictionary<string, string> GetChatroomIds() =>
        Clone(Volatile.Read(ref current).ChatroomIds);

    internal Dictionary<string, string> GetBroadcasterUserIds() =>
        Clone(Volatile.Read(ref current).BroadcasterUserIds);

    internal bool TryGetChatroomId(string? channel, out string value) =>
        TryGet(Volatile.Read(ref current).ChatroomIds, channel, out value);

    internal bool TryGetBroadcasterUserId(string? channel, out string value) =>
        TryGet(Volatile.Read(ref current).BroadcasterUserIds, channel, out value);

    internal bool ReplaceChatroomIds(IReadOnlyDictionary<string, string>? values) =>
        Update(values, broadcaster: false);

    internal bool ReplaceBroadcasterUserIds(IReadOnlyDictionary<string, string>? values) =>
        Update(values, broadcaster: true);

    internal bool SetChatroomId(string? channel, string? value) =>
        Set(channel, value, broadcaster: false);

    internal bool SetBroadcasterUserId(string? channel, string? value) =>
        Set(channel, value, broadcaster: true);

    private bool Update(IReadOnlyDictionary<string, string>? values, bool broadcaster)
    {
        var normalized = NormalizeEntries(values);
        lock (gate)
        {
            var snapshot = current;
            var existing = broadcaster ? snapshot.BroadcasterUserIds : snapshot.ChatroomIds;
            if (DictionaryEquals(existing, normalized))
            {
                return false;
            }

            Volatile.Write(
                ref current,
                broadcaster
                    ? snapshot with { BroadcasterUserIds = normalized }
                    : snapshot with { ChatroomIds = normalized });
            return true;
        }
    }

    private bool Set(string? channel, string? value, bool broadcaster)
    {
        var normalizedChannel = NormalizeText(channel);
        if (normalizedChannel.Length == 0)
        {
            return false;
        }

        var normalizedValue = NormalizeText(value);
        lock (gate)
        {
            var snapshot = current;
            var source = broadcaster ? snapshot.BroadcasterUserIds : snapshot.ChatroomIds;
            Dictionary<string, string> replacement;
            if (normalizedValue.Length == 0)
            {
                if (!source.ContainsKey(normalizedChannel))
                {
                    return false;
                }

                replacement = Clone(source);
                replacement.Remove(normalizedChannel);
            }
            else
            {
                if (source.TryGetValue(normalizedChannel, out var existing) &&
                    string.Equals(existing, normalizedValue, StringComparison.Ordinal))
                {
                    return false;
                }

                replacement = Clone(source);
                replacement[normalizedChannel] = normalizedValue;
            }

            Volatile.Write(
                ref current,
                broadcaster
                    ? snapshot with { BroadcasterUserIds = replacement }
                    : snapshot with { ChatroomIds = replacement });
            return true;
        }
    }

    private static bool TryGet(
        Dictionary<string, string> values,
        string? channel,
        out string value)
    {
        var normalizedChannel = NormalizeText(channel);
        if (normalizedChannel.Length > 0 && values.TryGetValue(normalizedChannel, out var found))
        {
            value = found;
            return true;
        }

        value = "";
        return false;
    }

    private static Dictionary<string, string> NormalizeEntries(IReadOnlyDictionary<string, string>? values)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values is null)
        {
            return normalized;
        }

        foreach (var pair in values)
        {
            var key = NormalizeText(pair.Key);
            var value = NormalizeText(pair.Value);
            if (key.Length > 0 && value.Length > 0)
            {
                normalized[key] = value;
            }
        }

        return normalized;
    }

    private static string NormalizeText(string? value) => value?.Trim() ?? "";

    private static Dictionary<string, string> Clone(Dictionary<string, string> source) =>
        new(source, StringComparer.OrdinalIgnoreCase);

    private static bool DictionaryEquals(
        Dictionary<string, string> left,
        Dictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out var value) &&
            string.Equals(pair.Value, value, StringComparison.Ordinal));

    private sealed record Snapshot(
        Dictionary<string, string> ChatroomIds,
        Dictionary<string, string> BroadcasterUserIds)
    {
        internal static Snapshot Empty { get; } = new(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }
}

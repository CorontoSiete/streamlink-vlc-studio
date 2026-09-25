using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

internal enum LiveChannelState { Unavailable, Offline, Available }

// Elements remain owned by the caller's JsonDocument and are consumed before it is disposed.
internal readonly record struct LiveChannelPayload(
    LiveChannelState State, JsonElement Channel = default, JsonElement Stream = default);

/// <summary>Identifies one channel and separates provider errors from an explicit offline result.</summary>
internal static class LiveChannelPayloadReader
{
    internal static LiveChannelPayload Read(PlatformKind platform, string channel, JsonElement root)
    {
        if (!TryGetArray(root, "data", out var data)) return default;

        var identityProperty = platform == PlatformKind.Twitch ? "user_login" : "slug";
        var malformed = false;
        foreach (var item in data.EnumerateArray())
        {
            if (!TryGetNonEmptyString(item, identityProperty, out var identity) ||
                string.IsNullOrWhiteSpace(identity))
            {
                malformed = true;
                continue;
            }

            if (!string.Equals(identity, channel, StringComparison.OrdinalIgnoreCase)) continue;

            if (platform == PlatformKind.Twitch)
            {
                return new(LiveChannelState.Available, item, item);
            }

            return ReadKickChannel(item);
        }

        // An empty result (or only other named channels) means offline. Unidentified rows
        // could instead contain the requested stream, so they cannot establish that fact.
        return new(malformed ? LiveChannelState.Unavailable : LiveChannelState.Offline);
    }

    internal static LiveChannelPayload ReadKickChannel(JsonElement channel)
    {
        if (channel.ValueKind != JsonValueKind.Object ||
            !channel.TryGetProperty("stream", out var stream)) return default;
        if (stream.ValueKind == JsonValueKind.Null) return new(LiveChannelState.Offline);
        if (stream.ValueKind != JsonValueKind.Object) return default;
        return TryGetBool(stream, "is_live") == false
            ? new(LiveChannelState.Offline)
            : new(LiveChannelState.Available, channel, stream);
    }
}

using System.Text.Json;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public static class KickChannelInfoJson
{
    public static KickChannelInfo Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new KickChannelInfo(null, null, null);
        }

        var data = GetObjectProperty(root, "data");
        var chatroom = GetObjectProperty(root, "chatroom");
        var dataChatroom = GetObjectProperty(data, "chatroom");
        var user = GetObjectProperty(root, "user");
        var dataUser = GetObjectProperty(data, "user");
        var broadcaster = GetObjectProperty(root, "broadcaster");
        var dataBroadcaster = GetObjectProperty(data, "broadcaster");
        var channelId = FirstNonEmpty(
            GetOptionalString(root, "id"),
            GetOptionalString(root, "channel_id"),
            GetOptionalString(data, "id"),
            GetOptionalString(data, "channel_id"));
        var chatroomId = FirstNonEmpty(
            GetOptionalString(chatroom, "id"),
            GetOptionalString(dataChatroom, "id"),
            GetOptionalString(root, "chatroom_id"));
        var broadcasterUserId = TryGetPositiveInt64(user, "id") ??
            TryGetPositiveInt64(dataUser, "id") ??
            TryGetPositiveInt64(broadcaster, "user_id") ??
            TryGetPositiveInt64(broadcaster, "id") ??
            TryGetPositiveInt64(dataBroadcaster, "user_id") ??
            TryGetPositiveInt64(dataBroadcaster, "id") ??
            TryGetPositiveInt64(root, "broadcaster_user_id") ??
            TryGetPositiveInt64(data, "broadcaster_user_id") ??
            TryGetPositiveInt64(root, "user_id") ??
            TryGetPositiveInt64(data, "user_id");

        return new KickChannelInfo(
            NormalizeNumericId(channelId),
            NormalizeNumericId(chatroomId),
            broadcasterUserId);
    }

    public static string? NormalizeNumericId(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return normalized is not null && normalized.All(static character => character is >= '0' and <= '9')
            ? normalized
            : null;
    }

    private static JsonElement GetObjectProperty(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Object
            ? property
            : default;
    }

    private static long? TryGetPositiveInt64(JsonElement element, string propertyName)
    {
        var value = TryGetInt64(element, propertyName);
        return value is > 0 ? value : null;
    }
}

public sealed record KickChannelInfo(string? ChannelId, string? ChatroomId, long? BroadcasterUserId);

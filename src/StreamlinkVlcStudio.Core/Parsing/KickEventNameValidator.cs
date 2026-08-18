namespace StreamlinkVlcStudio.Core.Parsing;

public static class KickEventNameValidator
{
    public const string ChatMessageSent = "chat.message.sent";
    public const string LegacyChatMessage = "App\\Events\\ChatMessageEvent";

    public static bool IsChatMessageEvent(string? eventName) => eventName is not null &&
        (eventName.Equals(LegacyChatMessage, StringComparison.Ordinal) ||
            eventName.Equals("App\\Events\\ChatMessageSentEvent", StringComparison.Ordinal) ||
            eventName.Equals("chat.message", StringComparison.Ordinal) ||
            eventName.Equals(ChatMessageSent, StringComparison.Ordinal));
}

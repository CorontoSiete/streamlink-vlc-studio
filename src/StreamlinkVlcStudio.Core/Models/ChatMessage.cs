namespace StreamlinkVlcStudio.Core.Models;

public sealed record ChatMessage(
    PlatformKind Platform,
    string Channel,
    string Username,
    string Message,
    DateTimeOffset Timestamp,
    string? Color = null,
    IReadOnlyList<ChatBadge>? Badges = null,
    IReadOnlyList<ChatEmote>? Emotes = null,
    string? RoomId = null,
    string? MessageId = null);

public sealed record ChatBadge(
    string Id,
    string? Version = null,
    string? Title = null,
    string? ImageUrl = null);

public sealed record ChatEmote(
    int StartIndex,
    int EndIndex,
    string Code,
    string? ImageUrl = null);

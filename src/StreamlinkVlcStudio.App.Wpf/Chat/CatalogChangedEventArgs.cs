using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

/// <summary>The lookup scope whose decorations changed; null parts match the whole platform.</summary>
internal readonly record struct CatalogChangeScope
{
    private CatalogChangeScope(PlatformKind platform, string? channel, string? roomId)
    {
        Platform = platform;
        Channel = Normalize(channel);
        RoomId = Normalize(roomId);
    }

    private PlatformKind Platform { get; }
    private string? Channel { get; }
    private string? RoomId { get; }

    internal static CatalogChangeScope ForChannel(PlatformKind platform, string? channel = null) =>
        new(platform, channel, null);

    internal static CatalogChangeScope ForTwitchRoom(string? roomId) =>
        new(PlatformKind.Twitch, null, roomId);

    internal bool Affects(ChatMessage message) =>
        Platform == message.Platform && Matches(Channel, message.Channel) && Matches(RoomId, message.RoomId);

    internal bool MayAffect(StreamTarget target) =>
        Platform == target.Platform && Matches(Channel, target.Channel) &&
        // Live targets can learn their room ID only through incoming messages. Keep their
        // overlays eligible until identity is known; row lookups always use the message ID.
        (string.IsNullOrWhiteSpace(target.BroadcasterId) || Matches(RoomId, target.BroadcasterId));

    private static bool Matches(string? expected, string? actual) =>
        expected is null || expected.AsSpan().Equals(actual.AsSpan().Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}

internal sealed class CatalogChangedEventArgs(CatalogChangeScope[] scopes) : EventArgs
{
    internal bool Affects(ChatMessage? message)
    {
        if (message is null) return false;
        foreach (var scope in scopes)
            if (scope.Affects(message)) return true;
        return false;
    }

    internal bool MayAffect(StreamTarget target)
    {
        foreach (var scope in scopes)
            if (scope.MayAffect(target)) return true;
        return false;
    }
}

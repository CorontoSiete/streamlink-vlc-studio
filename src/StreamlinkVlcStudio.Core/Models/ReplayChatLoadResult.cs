namespace StreamlinkVlcStudio.Core.Models;

public sealed record ReplayChatLoadResult(
    bool IsAvailable,
    IReadOnlyList<ReplayChatMessage> Messages,
    string UnavailableReason,
    TimeSpan? LoadedFromOffset = null,
    TimeSpan? LoadedThroughOffset = null)
{
    public static ReplayChatLoadResult Available(
        IReadOnlyList<ReplayChatMessage>? messages,
        TimeSpan? loadedFromOffset = null,
        TimeSpan? loadedThroughOffset = null) =>
        new(true, messages ?? [], "", loadedFromOffset, loadedThroughOffset);

    public static ReplayChatLoadResult Unavailable(string reason) =>
        new(false, [], string.IsNullOrWhiteSpace(reason) ? "Replay chat is unavailable." : reason.Trim());
}

public sealed record ReplayChatMessage(TimeSpan Offset, ChatMessage Message);

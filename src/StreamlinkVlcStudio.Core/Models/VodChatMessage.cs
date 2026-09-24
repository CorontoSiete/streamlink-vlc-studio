namespace StreamlinkVlcStudio.Core.Models;

/// <summary>A chat message anchored to a position on a VOD's playback timeline.</summary>
public sealed record VodChatMessage(TimeSpan Offset, ChatMessage Message);

/// <summary>Why a single VOD chat fetch stopped.</summary>
public enum VodChatFetchOutcome
{
    /// <summary>
    /// A chunk was fetched. <see cref="VodChatFetchResult.CoveredThroughOffset"/> is the new
    /// frontier and is always greater than the requested offset, so polling always advances.
    /// </summary>
    Loaded,

    /// <summary>The source has no further chat for this VOD. Stop polling.</summary>
    Completed,

    /// <summary>
    /// This VOD can never serve chat from the network (unsupported platform, missing VOD id,
    /// missing stream start time). Stop polling; any locally captured chat still applies.
    /// </summary>
    Unsupported,

    /// <summary>A transient failure. The caller may retry the same offset later.</summary>
    Failed
}

/// <summary>The result of one bounded, forward VOD chat fetch.</summary>
public sealed record VodChatFetchResult(
    VodChatFetchOutcome Outcome,
    IReadOnlyList<VodChatMessage> Messages,
    TimeSpan CoveredThroughOffset,
    string Reason)
{
    public static VodChatFetchResult Loaded(
        IReadOnlyList<VodChatMessage>? messages,
        TimeSpan coveredThroughOffset) =>
        new(VodChatFetchOutcome.Loaded, messages ?? [], coveredThroughOffset, "");

    public static VodChatFetchResult Completed(
        IReadOnlyList<VodChatMessage>? messages,
        TimeSpan coveredThroughOffset) =>
        new(VodChatFetchOutcome.Completed, messages ?? [], coveredThroughOffset, "");

    public static VodChatFetchResult Unsupported(string reason) =>
        new(
            VodChatFetchOutcome.Unsupported,
            [],
            TimeSpan.Zero,
            Describe(reason, "VOD chat is not available for this video."));

    public static VodChatFetchResult Failed(string reason) =>
        new(
            VodChatFetchOutcome.Failed,
            [],
            TimeSpan.Zero,
            Describe(reason, "VOD chat could not be loaded."));

    private static string Describe(string reason, string fallback) =>
        string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim();
}

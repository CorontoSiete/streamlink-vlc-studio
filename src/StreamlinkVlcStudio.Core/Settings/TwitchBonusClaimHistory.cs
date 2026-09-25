namespace StreamlinkVlcStudio.Core.Settings;

/// <summary>Local totals, shared across website accounts. Recent IDs suppress duplicate responses after retries.</summary>
public sealed record TwitchBonusClaimHistory
{
    public long Count { get; init; }
    public List<string> RecentClaimIds { get; init; } = [];

    public TwitchBonusClaimHistory Record(string claimId) => new()
    {
        Count = Count == long.MaxValue ? Count : Count + 1,
        RecentClaimIds = [.. RecentClaimIds.TakeLast(63), claimId]
    };
}

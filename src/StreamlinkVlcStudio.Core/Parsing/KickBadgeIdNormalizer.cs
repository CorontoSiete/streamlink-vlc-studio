namespace StreamlinkVlcStudio.Core.Parsing;

public static class KickBadgeIdNormalizer
{
    public static string Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "";
        }

        var normalized = id.Trim().ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            "channel_host" or "creator" => "broadcaster",
            "gift_sub" or
            "gift_subs" or
            "gift_subscriber" or
            "gift_subscription" or
            "gifted_sub" or
            "gifted_subs" or
            "gifted_subscriber" or
            "gifted_subscription" or
            "gifter" or
            "subgift" or
            "subgifter" or
            "sub_gift" or
            "sub_gifter_badge" or
            "sub_gifts" or
            "subscriber_gifter" or
            "subscription_gift" or
            "subscription_gifts" => "sub_gifter",
            "sub" or "subscription" or "subscriptions" => "subscriber",
            _ => normalized
        };
    }
}

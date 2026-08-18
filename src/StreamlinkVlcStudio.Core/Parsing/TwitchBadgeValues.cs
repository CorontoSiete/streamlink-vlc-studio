using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Core.Parsing;

/// <summary>
/// Shared Twitch badge normalization and display-title rules used by live and replay chat.
/// </summary>
public static class TwitchBadgeValues
{
    public static string NormalizeId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? "" : id.Trim().ToLowerInvariant();

    public static string ResolveTitle(string? id)
    {
        return NormalizeId(id) switch
        {
            "admin" => "Admin",
            "artist-badge" => "Artist",
            "bits" => "Bits",
            "broadcaster" => "Broadcaster",
            "founder" => "Founder",
            "moderator" => "Moderator",
            "partner" => "Partner",
            "premium" => "Prime Gaming",
            "staff" => "Staff",
            "subscriber" => "Subscriber",
            "turbo" => "Turbo",
            "vip" => "VIP",
            var normalized when normalized.Length > 0 => HumanizeIdentifier(normalized),
            _ => "Badge"
        };
    }
}

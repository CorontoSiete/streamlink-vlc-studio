namespace StreamlinkVlcStudio.Infrastructure.Chat;

/// <summary>
/// Shared helpers for Kick's public chat REST endpoints. Consolidates the recent-messages URL
/// construction that was previously duplicated between the live chat client and the chat history
/// provider.
/// </summary>
internal static class KickChatApi
{
    /// <summary>
    /// Builds the kick.com recent-messages URL for a channel and optional backward cursor.
    /// </summary>
    public static string BuildRecentMessagesUrl(
        string escapedMessagesChannelId,
        string? cursor)
    {
        var url = $"https://kick.com/api/v2/channels/{escapedMessagesChannelId}/messages";
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            return $"{url}?cursor={Uri.EscapeDataString(cursor)}";
        }

        return url;
    }
}

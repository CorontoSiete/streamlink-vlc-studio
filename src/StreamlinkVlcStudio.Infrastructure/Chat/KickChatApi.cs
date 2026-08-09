using System.Globalization;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

/// <summary>
/// Shared helpers for Kick's public chat REST endpoints. Consolidates the recent-messages URL
/// construction that was previously duplicated between the live chat client and the chat history
/// provider.
/// </summary>
internal static class KickChatApi
{
    /// <summary>
    /// Builds the kick.com recent-messages URL for a channel; <paramref name="startTimeUtc"/>
    /// takes precedence over <paramref name="cursor"/> when both are provided.
    /// </summary>
    public static string BuildRecentMessagesUrl(
        string escapedMessagesChannelId,
        string? cursor,
        DateTimeOffset? startTimeUtc)
    {
        var url = $"https://kick.com/api/v2/channels/{escapedMessagesChannelId}/messages";
        if (startTimeUtc is { } startTime)
        {
            return $"{url}?start_time={Uri.EscapeDataString(FormatStartTime(startTime))}";
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            return $"{url}?cursor={Uri.EscapeDataString(cursor)}";
        }

        return url;
    }

    private static string FormatStartTime(DateTimeOffset timestampUtc)
    {
        return timestampUtc
            .ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }
}

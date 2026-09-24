namespace StreamlinkVlcStudio.Infrastructure.Twitch;

/// <summary>Log conventions shared by the muted-VOD repair components.</summary>
internal static class TwitchMutedVodRepairLog
{
    internal const string Source = "MutedVodRepair";

    /// <summary>
    /// A media location that is safe to write to the log: query strings and fragments can carry
    /// signed playback parameters, so only the path is kept.
    /// </summary>
    internal static string Describe(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            return uri.OriginalString;
        }

        return uri.IsFile ? uri.LocalPath : uri.GetLeftPart(UriPartial.Path);
    }
}

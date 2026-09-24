using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Core.Services;

/// <summary>
/// Streams a VOD's chat forward in bounded chunks.
/// </summary>
/// <remarks>
/// Both supported sources are paged and return only a few seconds of a busy chat per request, so
/// there is no "load the whole window" operation: one call returns the chat for a short span that
/// begins at <c>fromOffset</c> and reports how far it covered. Callers poll repeatedly to stay a
/// little ahead of playback, which keeps one slow page from stalling the whole feed.
/// </remarks>
public interface IVodChatProvider
{
    /// <param name="replay">The replay session being played.</param>
    /// <param name="settings">Current settings, used to resolve platform identifiers.</param>
    /// <param name="fromOffset">The playback offset to continue from.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    Task<VodChatFetchResult> FetchAsync(
        ReplaySessionInfo replay,
        AppSettings settings,
        TimeSpan fromOffset,
        CancellationToken cancellationToken = default);
}

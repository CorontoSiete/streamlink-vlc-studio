using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Core.Services;

public interface IStreamSearchService
{
    Task<StreamSearchResult> SearchAsync(
        StreamSearchRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default);
}

namespace StreamlinkVlcStudio.Core.Services;

public interface IKickFollowedChannelsImporter
{
    // Null means the user closed the sign-in/import window without importing.
    Task<IReadOnlyList<string>?> ImportAsync(CancellationToken cancellationToken = default);
}

namespace StreamlinkVlcStudio.Core;

public static class AppIdentity
{
    // IncludeAllContentForSelfExtract makes AppContext.BaseDirectory point at the
    // runtime extraction cache. Installation ownership and staged helpers use the apphost.
    public static string ExecutableDirectory => Path.GetDirectoryName(Environment.ProcessPath)
        ?? throw new InvalidOperationException("The running application directory is unavailable.");

    public const string DisplayName = "Stream Studio";
    public const string ProductDirectoryName = "StreamStudio";
    public const string LegacyProductDirectoryName = "StreamlinkVlcStudio";
    // Keep completion records and staged updates readable across the display-name change.
    public const string UpdateDirectoryName = LegacyProductDirectoryName;
    // Published 1.7.0 helpers verify and restart this exact managed-install filename.
    public const string ManagedExecutableName = "StreamlinkVlcStudio.exe";
    // Protocol 1 asset names are stable even when the display name changes.
    public const string SetupAssetName = "StreamlinkVlcStudio-Setup.exe";
    public const string ZipAssetName = "StreamlinkVlcStudio-release.zip";
    public const string OwnerFileName = ".stream-studio-owner.json";
    public const string ManifestFileName = ".stream-studio-files.json";
    public const string LegacyOwnerFileName = ".streamlink-vlc-studio-owner.json";
}

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal static class LiveThumbnailCacheVersion
{
    private static long nextVersion;
    internal static long Next() => Interlocked.Increment(ref nextVersion);
}

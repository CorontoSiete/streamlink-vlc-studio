namespace StreamlinkVlcStudio.Core.Security;

/// <summary>Images may come from HTTPS or a local file path.</summary>
public static class ImageUriPolicy
{
    public static bool IsSupported(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            IsLocalFile(uri));

    public static bool IsLocalFile(Uri? uri) =>
        uri is { IsAbsoluteUri: true, IsFile: true, IsUnc: false } &&
        // Encoded separators can hide a network path from Uri.IsUnc.
        uri.LocalPath is not ['/' or '\\', '/' or '\\', ..];
}

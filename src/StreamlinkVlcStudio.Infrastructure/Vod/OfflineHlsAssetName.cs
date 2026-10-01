namespace StreamlinkVlcStudio.Infrastructure.Vod;

internal static class OfflineHlsAssetName
{
    private const string Prefix = "asset-";
    private const int IndexDigits = 6;
    private static readonly string[] Extensions = [".ts", ".m4s", ".mp4", ".aac", ".mp3", ".vtt", ".key", ".bin"];

    internal static string Create(Uri uri, int index, bool isKey)
    {
        var extension = isKey ? ".key" : Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (!Extensions.Contains(extension, StringComparer.Ordinal)) extension = ".bin";
        var muted = extension == ".ts" && uri.AbsolutePath.EndsWith("-muted.ts", StringComparison.OrdinalIgnoreCase)
            ? "-muted" : "";
        return $"{Prefix}{index:D6}{muted}{extension}";
    }

    internal static bool IsValid(string? name)
    {
        var suffixOffset = Prefix.Length + IndexDigits;
        if (name is null || name.Length < suffixOffset + 3 || name.Length > suffixOffset + 9 ||
            !name.StartsWith(Prefix, StringComparison.Ordinal) ||
            name.AsSpan(Prefix.Length, IndexDigits).IndexOfAnyExceptInRange('0', '9') >= 0) return false;
        var suffix = name[suffixOffset..];
        return suffix == "-muted.ts" || Extensions.Contains(suffix, StringComparer.Ordinal);
    }
}

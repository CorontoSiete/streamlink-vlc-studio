using System.Security.Cryptography;

namespace StreamlinkVlcStudio.Infrastructure.Io;

internal static class FileHash
{
    internal static bool MatchesSha256(string path, string expectedHash) =>
        File.Exists(path) && string.Equals(GetSha256(path), expectedHash, StringComparison.OrdinalIgnoreCase);

    internal static string GetSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

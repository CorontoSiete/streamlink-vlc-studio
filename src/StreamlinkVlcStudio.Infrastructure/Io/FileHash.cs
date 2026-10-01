using System.Security.Cryptography;

namespace StreamlinkVlcStudio.Infrastructure.Io;

internal static class FileHash
{
    internal static string GetSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

using System.Security.Cryptography;
using StreamlinkVlcStudio.Infrastructure.Io;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

internal sealed record LibVlcCoreSelection(string Path, string Sha256, bool IsBundledAddressWaitBuild);

/// <summary>Selects the matching source-built core only for the verified VLC 3.0.23 DLL pair.</summary>
internal static class BundledVlcCoreRuntime
{
    private const string ResourceName =
        "StreamlinkVlcStudio.Infrastructure.Vlc.BundledVlcCore.libvlccore.dll";
    private const string ReferenceLibVlcSha256 =
        "8ae9f16a72441f43fb4ae8f72c843736726e067ea4a8def2646748631cc4e872";
    private const string ReferenceCoreSha256 =
        "d3475b834dd3eb77910f37f71b0341d358bcbdda5b9f04cc4a3a8e2be1bc8e35";
    private const string BundledCoreSha256 =
        "6efb3c92094ddfe9aca032910a4fadf1a5ce53488488720ca84bba56722291a2";

    internal static LibVlcCoreSelection Select(string vlcDirectory)
    {
        var installedCorePath = Path.Combine(vlcDirectory, "libvlccore.dll");
        var installedLibVlcPath = Path.Combine(vlcDirectory, "libvlc.dll");
        if (!File.Exists(installedCorePath))
            throw new FileNotFoundException("The selected VLC core library was not found.", installedCorePath);
        if (!File.Exists(installedLibVlcPath))
            throw new FileNotFoundException("The selected libVLC library was not found.", installedLibVlcPath);

        var installedCoreSha256 = FileHash.GetSha256(installedCorePath);
        var installedLibVlcSha256 = FileHash.GetSha256(installedLibVlcPath);
        if (!installedCoreSha256.Equals(ReferenceCoreSha256, StringComparison.OrdinalIgnoreCase) ||
            !installedLibVlcSha256.Equals(ReferenceLibVlcSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new LibVlcCoreSelection(installedCorePath, installedCoreSha256, IsBundledAddressWaitBuild: false);
        }

        var bundledPath = ExtractVerifiedCore();
        return new LibVlcCoreSelection(bundledPath, BundledCoreSha256, IsBundledAddressWaitBuild: true);
    }

    private static string ExtractVerifiedCore()
    {
        using var resource = typeof(BundledVlcCoreRuntime).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException("The verified VLC address-wait core resource is missing.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var resourceHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!resourceHash.Equals(BundledCoreSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The bundled VLC core failed its SHA-256 check.");

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamStudio",
            "vlc-native-core",
            BundledCoreSha256);
        var output = Path.Combine(directory, "libvlccore.dll");
        Directory.CreateDirectory(directory);
        if (File.Exists(output) && FileHash.GetSha256(output).Equals(BundledCoreSha256, StringComparison.OrdinalIgnoreCase))
            return output;

        var temporary = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            try { File.Move(temporary, output, overwrite: true); }
            catch (IOException) when (File.Exists(output) &&
                FileHash.GetSha256(output).Equals(BundledCoreSha256, StringComparison.OrdinalIgnoreCase))
            {
                // Another process extracted the same immutable build first.
            }
        }
        finally
        {
            File.Delete(temporary);
        }

        if (!File.Exists(output) || !FileHash.GetSha256(output).Equals(BundledCoreSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The verified VLC address-wait core could not be extracted.");
        return output;
    }

}

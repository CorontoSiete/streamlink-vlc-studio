using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

/// <summary>
/// Identifies the libVLC build that was actually loaded, so workarounds for bugs of specific
/// releases only run where those bugs exist.
/// </summary>
internal static partial class LibVlcVersion
{
    /// <summary>
    /// The version of the loaded libVLC, or <see langword="null"/> when it cannot be determined.
    /// Call only after the VLC directory has been registered as the native search path.
    /// </summary>
    internal static Version? TryReadLoaded()
    {
        try
        {
            return TryParse(Marshal.PtrToStringUTF8(LibVlcNative.libvlc_get_version()));
        }
        catch (Exception)
        {
            // The version only selects workarounds. Whatever goes wrong while reading it must not
            // fail the playback engine that asked; callers treat an unknown release as affected.
            return null;
        }
    }

    /// <summary>Parses libVLC version text such as <c>3.0.12 Vetinari</c> or <c>4.0.0-dev Otto Chriek</c>.</summary>
    internal static Version? TryParse(string? versionText) =>
        versionText is not null &&
        LeadingVersionPattern().Match(versionText) is { Success: true } match &&
        Version.TryParse(match.Groups[1].Value, out var version)
            ? version
            : null;

    [GeneratedRegex(@"^\s*(\d{1,9}(?:\.\d{1,9}){1,3})(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingVersionPattern();
}

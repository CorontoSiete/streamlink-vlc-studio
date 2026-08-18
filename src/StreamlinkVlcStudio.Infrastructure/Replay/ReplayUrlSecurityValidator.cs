using System.Net;
using System.Net.Sockets;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Security;

namespace StreamlinkVlcStudio.Infrastructure.Replay;

/// <summary>Validates replay endpoints before any network client or subprocess can reach them.</summary>
internal sealed class ReplayUrlSecurityValidator
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolveHostAsync;

    internal static ReplayUrlSecurityValidator Shared { get; } = new(
        static (host, cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken));

    internal ReplayUrlSecurityValidator(
        Func<string, CancellationToken, Task<IPAddress[]>> resolveHostAsync)
    {
        this.resolveHostAsync = resolveHostAsync ?? throw new ArgumentNullException(nameof(resolveHostAsync));
    }

    internal async Task<Uri> ValidateAsync(
        Uri uri,
        PlatformKind platform,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateProviderUri(uri, platform))
        {
            throw new InvalidDataException("Replay URL is not an approved public HTTPS provider endpoint.");
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.IdnHost, out var literalAddress))
        {
            addresses = [literalAddress];
        }
        else
        {
            try
            {
                addresses = await resolveHostAsync(uri.IdnHost, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                throw new InvalidDataException("Replay provider host could not be resolved safely.", ex);
            }
        }

        if (addresses.Length == 0 || addresses.Any(static address => !IsPublicAddress(address)))
        {
            throw new InvalidDataException("Replay provider host resolved to a non-public address.");
        }

        return uri;
    }

    internal static bool TryValidateProviderUri(Uri? uri, PlatformKind platform)
    {
        return ProviderUriPolicy.IsApprovedReplayUri(uri, platform);
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublicAddress(address.MapToIPv4());
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                0 or 10 or 127 => false,
                100 when bytes[1] is >= 64 and <= 127 => false,
                169 when bytes[1] == 254 => false,
                172 when bytes[1] is >= 16 and <= 31 => false,
                192 when bytes[1] == 168 => false,
                192 when bytes[1] == 0 => false,
                198 when bytes[1] is 18 or 19 => false,
                198 when bytes[1] == 51 && bytes[2] == 100 => false,
                203 when bytes[1] == 0 && bytes[2] == 113 => false,
                >= 224 => false,
                _ => true
            };
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        // Unique-local fc00::/7, link-local fe80::/10, multicast ff00::/8,
        // and the documentation range 2001:db8::/32 are not public endpoints.
        return (bytes[0] & 0xFE) != 0xFC &&
            !(bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) &&
            !(bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0xC0) &&
            bytes[0] != 0xFF &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8);
    }
}

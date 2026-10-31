using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

public sealed record LocalWebInterface(string Id, string Name, IPAddress Address, int PrefixLength)
{
    public override string ToString() => $"{Name} — {Address}/{PrefixLength}";

    public static IReadOnlyList<LocalWebInterface> GetAvailable() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up &&
            n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses
            .Where(a => IsPrivateV4(a.Address) && a.PrefixLength is >= 8 and <= 30)
            .Select(a => new LocalWebInterface(n.Id, n.Name, a.Address, a.PrefixLength)))
        .ToArray();

    internal bool IsCurrent() => GetAvailable().Any(n => n == this);

    internal bool Allows(IPAddress? peer)
    {
        if (peer is null) return false;
        if (peer.IsIPv4MappedToIPv6) peer = peer.MapToIPv4();
        // Loopback is only used by the in-process integration tests; it is never offered in the UI.
        if (IPAddress.IsLoopback(Address)) return peer.Equals(Address);
        if (!IsPrivateV4(Address) || !IsPrivateV4(peer) || PrefixLength is < 8 or > 30) return false;
        var local = Address.GetAddressBytes();
        var remote = peer.GetAddressBytes();
        for (var i = 0; i < 4; i++)
        {
            var bits = Math.Clamp(PrefixLength - i * 8, 0, 8);
            var mask = (byte)(0xff << (8 - bits));
            if ((local[i] & mask) != (remote[i] & mask)) return false;
        }
        // Do not accept subnet network/broadcast addresses as clients.
        var hostBits = 32 - PrefixLength;
        var hostMask = (1u << hostBits) - 1;
        var value = ((uint)remote[0] << 24) | ((uint)remote[1] << 16) | ((uint)remote[2] << 8) | remote[3];
        return (value & hostMask) != 0 && (value & hostMask) != hostMask;
    }

    internal static bool IsPrivateV4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168;
    }
}

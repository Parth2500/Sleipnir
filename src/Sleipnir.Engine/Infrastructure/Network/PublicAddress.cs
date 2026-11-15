using System.Net;
using System.Net.Sockets;

namespace Sleipnir.Infrastructure.Network;

/// <summary>Whether an address is on the public internet, as opposed to this machine, the home network or a special range.</summary>
public static class PublicAddress
{
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0                                   // 0.0.0.0/8 "this network"
                || b[0] == 10                                    // RFC 1918
                || (b[0] == 100 && b[1] is >= 64 and <= 127)     // 100.64.0.0/10 carrier-grade NAT
                || b[0] == 127                                   // loopback
                || (b[0] == 169 && b[1] == 254)                  // link-local, including cloud metadata
                || (b[0] == 172 && b[1] is >= 16 and <= 31)      // RFC 1918
                || (b[0] == 192 && b[1] == 168)                  // RFC 1918
                || b[0] >= 224);                                 // multicast, reserved and broadcast
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback)
                || (b[0] & 0xFE) == 0xFC                         // fc00::/7 unique local
                || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80)       // fe80::/10 link-local
                || (b[0] == 0xFE && (b[1] & 0xC0) == 0xC0)       // fec0::/10 site-local (deprecated)
                || b[0] == 0xFF);                                // multicast
        }

        return false;
    }
}

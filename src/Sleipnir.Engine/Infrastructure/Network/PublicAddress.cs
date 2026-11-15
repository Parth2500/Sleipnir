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
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicIPv4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicIPv6(address.GetAddressBytes()),
            _ => false,
        };
    }

    private static bool IsPublicIPv4(byte[] b) =>
        !(b[0] == 0                                   // 0.0.0.0/8 "this network"
        || b[0] == 10                                 // RFC 1918
        || (b[0] == 100 && b[1] is >= 64 and <= 127)  // 100.64.0.0/10 carrier-grade NAT
        || b[0] == 127                                // loopback
        || (b[0] == 169 && b[1] == 254)               // link-local, including cloud metadata
        || (b[0] == 172 && b[1] is >= 16 and <= 31)   // RFC 1918
        || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) // IETF protocol assignments, documentation
        || (b[0] == 192 && b[1] == 168)               // RFC 1918
        || (b[0] == 198 && b[1] is 18 or 19)          // benchmarking
        || (b[0] == 198 && b[1] == 51 && b[2] == 100) // documentation
        || (b[0] == 203 && b[1] == 0 && b[2] == 113)  // documentation
        || b[0] >= 224);                              // multicast, reserved and broadcast

    /// <summary>
    /// Global unicast (2000::/3) only, minus the ranges that are not really the internet. Forms that
    /// carry an IPv4 address are judged by that address (6to4) or refused (Teredo hides it).
    /// Everything else (loopback, unique local, link-local, multicast, NAT64, IPv4-compatible) is outside 2000::/3.
    /// </summary>
    private static bool IsPublicIPv6(byte[] b)
    {
        if ((b[0] & 0xE0) != 0x20) return false;                              // not 2000::/3
        if (b[0] == 0x20 && b[1] == 0x02) return IsPublicIPv4(b[2..6]);       // 2002::/16 6to4
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return false; // 2001::/32 Teredo
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false; // 2001:db8::/32 documentation
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && (b[3] & 0xF0) == 0x10) return false; // 2001:10::/28 ORCHID
        if (b[0] == 0x3F && b[1] == 0xFF && (b[2] & 0xF0) == 0x00) return false; // 3fff::/20 documentation
        return true;
    }
}

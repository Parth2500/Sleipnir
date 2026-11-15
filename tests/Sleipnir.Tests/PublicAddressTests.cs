using System.Net;
using Sleipnir.Infrastructure.Network;

namespace Sleipnir.Tests;

public class PublicAddressTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.0.9")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::c0a8:101")]               // IPv4-compatible 192.168.1.1
    [InlineData("2002:c0a8:101::1")]         // 6to4 wrapping 192.168.1.1
    [InlineData("2002:7f00:1::1")]           // 6to4 wrapping 127.0.0.1
    [InlineData("64:ff9b::c0a8:101")]        // NAT64 wrapping 192.168.1.1
    [InlineData("64:ff9b:1::1")]             // local-use NAT64
    [InlineData("2001:0:4136:e378::1")]      // Teredo: the IPv4 inside is obscured
    [InlineData("2001:db8::1")]              // documentation
    [InlineData("100::1")]                   // discard-only
    [InlineData("3fff::1")]                  // documentation (RFC 9637)
    [InlineData("192.0.2.1")]                // documentation
    [InlineData("198.18.0.1")]               // benchmarking
    public void Home_network_and_special_addresses_are_not_public(string address)
    {
        Assert.False(PublicAddress.IsPublic(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:1.1.1.1")]
    [InlineData("2002:101:101::1")]          // 6to4 wrapping 1.1.1.1
    public void Internet_addresses_are_public(string address)
    {
        Assert.True(PublicAddress.IsPublic(IPAddress.Parse(address)));
    }
}

using DesktopMcp.Core.Config;
using DesktopMcp.Core.Net;

namespace DesktopMcp.Core.Tests;

public class IpNetworkTests
{
    [Fact]
    public void ParsesAddressesAndNetworksAndClearsHostBits()
    {
        var networks = IpNetwork.ParseAllowlist(["10.1.2.3/8", " 192.168.1.5 ", "", "2001:db8::1/32", "::1"]);
        Assert.Equal(["10.0.0.0/8", "192.168.1.5/32", "2001:db8::/32", "::1/128"], networks.Select(n => n.ToString()));
    }

    [Fact]
    public void ReportsEveryBadEntryWithPythonsMessages()
    {
        var error = Assert.Throws<FormatException>(() => IpNetwork.ParseAllowlist(["abc", "10.0.0.0/33", " 010.0.0.1", "1.2.3"]));
        Assert.Equal(
            "Invalid IP allowlist entries: " +
            "'abc': 'abc' does not appear to be an IPv4 or IPv6 address; " +
            "'10.0.0.0/33': '10.0.0.0/33' does not appear to be an IPv4 or IPv6 network; " +
            "' 010.0.0.1': '010.0.0.1' does not appear to be an IPv4 or IPv6 address; " +
            "'1.2.3': '1.2.3' does not appear to be an IPv4 or IPv6 address",
            error.Message);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.5.6.7", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("example.com", false)]
    [InlineData("[::1]", false)]
    public void IsLoopbackHostMatchesUpstream(string host, bool expected) => Assert.Equal(expected, IpAddressText.IsLoopbackHost(host));
}

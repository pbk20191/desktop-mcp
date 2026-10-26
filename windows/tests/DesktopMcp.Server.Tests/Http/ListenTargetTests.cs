using System.Net;
using DesktopMcp.Core.Config;
using DesktopMcp.Server.Http;

namespace DesktopMcp.Server.Tests.Http;

/// <summary>Decisions 7: what server.host binds.</summary>
public class ListenTargetTests
{
    private static IPAddress[] NoDns(string _) => throw new InvalidOperationException("no DNS expected");

    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    public void LocalhostIsKestrelsLocalhost(string host) =>
        Assert.IsType<ListenTarget.Localhost>(Assert.Single(ListenTarget.Resolve(host, NoDns)));

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("::1", "::1")]
    [InlineData("[::1]", "::1")]
    [InlineData("0.0.0.0", "0.0.0.0")]
    [InlineData("::", "::")]
    [InlineData("", "::")]
    [InlineData("192.168.1.5", "192.168.1.5")]
    public void AddressesBindThemselves(string host, string expected) =>
        Assert.Equal(IPAddress.Parse(expected), Assert.IsType<ListenTarget.Address>(Assert.Single(ListenTarget.Resolve(host, NoDns))).Ip);

    [Fact]
    public void ANameBindsEveryResolvedAddress() =>
        Assert.Equal(2, ListenTarget.Resolve("myhost", _ => [IPAddress.Parse("10.0.0.2"), IPAddress.Parse("fe80::2")]).Count);

    [Fact]
    public void AnUnresolvableNameIsAConfigError() =>
        Assert.Equal("Could not resolve server.host 'nowhere'.",
            Assert.Throws<ConfigException>(() => ListenTarget.Resolve("nowhere", _ => [])).Message);
}

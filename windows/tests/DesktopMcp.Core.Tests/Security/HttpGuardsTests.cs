using System.Net;
using DesktopMcp.Core.Config;
using DesktopMcp.Core.Security;

namespace DesktopMcp.Core.Tests.Security;

public class HttpGuardsTests
{
    private static readonly string[] Loopback = ["localhost", "127.0.0.1", "[::1]"];

    [Theory]
    [InlineData("localhost", true)] [InlineData("LOCALHOST", true)] [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", true)] [InlineData("attacker.example", false)] [InlineData("127.0.0.2", false)]
    [InlineData("", false)] [InlineData(null, false)]
    [InlineData("[localhost]", false)] [InlineData("127.0.0.1]", false)] [InlineData("[127.0.0.1", false)] [InlineData("localhost.", false)]
    public void TrustedHostTable(string? host, bool expected) => Assert.Equal(expected, HttpGuards.IsTrustedHost(host, Loopback));

    [Fact]
    public void ASpecificIPv6BindTrustsItsBracketedHost() => Assert.True(HttpGuards.IsTrustedHost("[fe80::5]", ["fe80::5"]));

    [Fact]
    public void NoListTrustsEverything() => Assert.True(HttpGuards.IsTrustedHost("anything", null));

    [Theory]
    [InlineData(null, true)]
    [InlineData("http://localhost:6274", true)] [InlineData("https://127.0.0.1", true)] [InlineData("http://[::1]:3000", true)]
    [InlineData("https://client.example.com", true)]                 // listed
    [InlineData("https://attacker.example", false)] [InlineData("null", false)]
    [InlineData("http://127.0.0.1@evil.example", false)] [InlineData("http://localhost.", false)]
    [InlineData("HTTPS://CLIENT.EXAMPLE.COM", true)]
    [InlineData("file://localhost", false)] [InlineData("http://localhost.attacker.example", false)]
    public void OriginTable(string? origin, bool expected) =>
        Assert.Equal(expected, HttpGuards.IsAllowedOrigin(origin, ["https://client.example.com"]));

    [Fact]
    public void ConfiguredOriginsMatchCanonically() =>
        Assert.True(HttpGuards.IsAllowedOrigin("https://client.example.com", ["HTTPS://Client.Example.com/"]));

    [Fact]
    public void AnAllowedIPv6ClientPasses() =>
        Assert.Null(HttpGuards.AllowlistRejection(IPAddress.Parse("2001:db8::7"), IpNetwork.ParseAllowlist(["2001:db8::/32"])));

    [Fact]
    public void DistinctLoneSurrogatesDiffer() => Assert.False(HttpGuards.FixedTimeEquals("\ud800", "\ud801"));

    [Fact]
    public void AnEmptyAllowlistAllowsEveryone() => Assert.Null(HttpGuards.AllowlistRejection(IPAddress.Parse("8.8.8.8"), []));

    [Fact]
    public void AMappedClientMatchesAnIPv4Entry() =>
        Assert.Null(HttpGuards.AllowlistRejection(IPAddress.Parse("::ffff:10.1.2.3"), IpNetwork.ParseAllowlist(["10.0.0.0/8"])));

    [Fact]
    public void AnUnlistedClientIsNamedInIPv4Form() =>
        Assert.Equal("Forbidden: 192.168.0.9 is not in the IP allowlist",
            HttpGuards.AllowlistRejection(IPAddress.Parse("::ffff:192.168.0.9"), IpNetwork.ParseAllowlist(["10.0.0.0/8"])));

    [Fact]
    public void AMissingClientIsRejected() =>
        Assert.Equal("Forbidden: missing client address", HttpGuards.AllowlistRejection(null, IpNetwork.ParseAllowlist(["::1"])));

    [Theory]
    [InlineData("/health", true)] [InlineData("/oauth/token", true)] [InlineData("/.well-known/oauth-authorization-server", true)]
    [InlineData("/Health", false)] [InlineData("/health/", false)] [InlineData("/mcp", false)]
    public void PublicPathsAreExact(string path, bool expected) => Assert.Equal(expected, HttpGuards.IsPublicPath(path));

    [Theory]
    [InlineData("abc", "abc", true)] [InlineData("abc", "abd", false)] [InlineData("abc", "abcd", false)] [InlineData("", "", true)]
    public void FixedTimeEqualsCompares(string a, string b, bool expected) => Assert.Equal(expected, HttpGuards.FixedTimeEquals(a, b));

    [Fact]
    public void BodiesAreCompactPythonJson()
    {
        Assert.Equal("""{"error":"Forbidden: origin 'https://a.example' is not allowed"}""", SecurityJson.Error("Forbidden: origin 'https://a.example' is not allowed"));
        Assert.Equal("""{"error":"Missing or invalid Authorization header. Expected: Bearer <token>"}""", SecurityJson.Error("Missing or invalid Authorization header. Expected: Bearer <token>"));
        Assert.Equal("""{"error":"invalid_grant","error_description":"unknown code"}""", SecurityJson.OAuthError("invalid_grant", "unknown code"));
        Assert.Equal("""{"status":"ok"}""", SecurityJson.Health);
    }
}

using DesktopMcp.Core.Scrape;

namespace DesktopMcp.Core.Tests;

// Expected values checked against CPython 3.13.15's urllib.parse, whose urlsplit matches 3.14's.
public class UrlPartsTests
{
    [Theory]
    [InlineData("https://Example.COM/x", "https", "example.com")]
    [InlineData("HTTP://h", "http", "h")]
    [InlineData("ftp://x/y", "ftp", "x")]
    [InlineData("example.com", "", null)]
    [InlineData("localhost:8000", "localhost", null)]
    [InlineData("1http://x", "", null)]
    [InlineData("http:///path", "http", null)]
    [InlineData("http://[::1]:80/", "http", "::1")]
    [InlineData("  http://h/ \t", "http", "h")]
    [InlineData("\0http://h", "http", "h")]
    [InlineData("http://h \t", "http", "h ")]
    [InlineData("http://x?y@z", "http", "x")]
    [InlineData("http://x#a@b", "http", "x")]
    [InlineData("http://H%41/", "http", "h%41")]
    [InlineData(@"http:\\127.0.0.1/", "http", null)]
    [InlineData(@"http:/\127.0.0.1/", "http", null)]
    [InlineData(@"http://evil.com\@127.0.0.1/", "http", "127.0.0.1")]
    [InlineData("javascript:alert(1)", "javascript", null)]
    public void SchemeAndHostnameFollowUrlsplit(string url, string scheme, string? hostname)
    {
        var parts = UrlParts.Parse(url);
        Assert.Equal(scheme, parts.Scheme);
        Assert.Equal(hostname, parts.Hostname);
    }

    [Fact]
    public void Credentials()
    {
        var parts = UrlParts.Parse("http://user:pw@host:81/");
        Assert.Equal(("user", "pw", "host", 81), (parts.Username, parts.Password, parts.Hostname, parts.ReadPort()));
        Assert.Equal("", UrlParts.Parse("http://@host/").Username);
        Assert.Null(UrlParts.Parse("http://host/").Username);
        Assert.Equal(("u@a", null, "b"), (UrlParts.Parse("http://u@a@b/").Username, UrlParts.Parse("http://u@a@b/").Password, UrlParts.Parse("http://u@a@b/").Hostname));
    }

    [Theory]
    [InlineData("http://[::1/", "Invalid IPv6 URL")]
    [InlineData("http://::1]/", "Invalid IPv6 URL")]
    [InlineData("http://a[::1]/", "Invalid IPv6 URL")]
    [InlineData("http://[::1]x/", "Invalid IPv6 URL")]
    [InlineData("http://[1.2.3.4]/", "An IPv4 address cannot be in brackets")]
    [InlineData("http://[zz]/", "'zz' does not appear to be an IPv4 or IPv6 address")]
    [InlineData("http://[::1]@host/", "'host' does not appear to be an IPv4 or IPv6 address")]
    [InlineData("http://[vz]/", "IPvFuture address is invalid")]
    public void BadBracketedNetlocsThrowPythonsMessages(string url, string message) =>
        Assert.Equal(message, Assert.Throws<FormatException>(() => UrlParts.Parse(url)).Message);

    [Theory]
    [InlineData("http://[v1.abc]/", "v1.abc", null)]
    [InlineData("http://[FE80::1%Eth0]:8/", "fe80::1%Eth0", 8)]
    [InlineData("http://[::ffff:127.0.0.1]/", "::ffff:127.0.0.1", null)]
    [InlineData("http://[::1]:/", "::1", null)]
    public void BracketedHosts(string url, string hostname, int? port)
    {
        var parts = UrlParts.Parse(url);
        Assert.Equal((hostname, port), (parts.Hostname, parts.ReadPort()));
    }

    [Theory]
    [InlineData("http://a\u2100b/", "a\u2100b")]
    [InlineData("http://evil.com\uFF03@127.0.0.1/", "evil.com\uFF03@127.0.0.1")]
    public void NetlocsThatNfkcNormaliseToDelimitersThrow(string url, string netloc) =>
        Assert.Equal($"netloc '{netloc}' contains invalid characters under NFKC normalization",
            Assert.Throws<FormatException>(() => UrlParts.Parse(url)).Message);

    [Fact]
    public void OtherNonAsciiNetlocsPass() => Assert.Equal("a\uFF0Eb", UrlParts.Parse("http://a\uFF0Eb/").Hostname);

    [Theory]
    [InlineData("http://h:abc/", "Port could not be cast to integer value as 'abc'")]
    [InlineData("http://h:-1/", "Port could not be cast to integer value as '-1'")]
    [InlineData("http://a:b:c/", "Port could not be cast to integer value as 'b:c'")]
    [InlineData("http://h:99999/", "Port out of range 0-65535")]
    [InlineData("http://h:99999999999999999999/", "Port out of range 0-65535")]
    public void BadPortsUsePythonsMessages(string url, string message) =>
        Assert.Equal(message, Assert.Throws<FormatException>(() => UrlParts.Parse(url).ReadPort()).Message);

    [Fact]
    public void AnEmptyPortIsNone() => Assert.Null(UrlParts.Parse("http://h:/").ReadPort());

    [Fact]
    public void LeadingZerosInAPortAreAccepted() => Assert.Equal(80, UrlParts.Parse("http://h:0080/").ReadPort());
}

using System.Net;
using System.Net.Sockets;
using DesktopMcp.Core.Net;
using DesktopMcp.Core.Scrape;

namespace DesktopMcp.Core.Tests;

public class ScrapeClientTests
{
    // Test resolver: IP literals as themselves, site.test as 127.0.0.1, fake.test as a Fake-IP answer.
    private static Task<IPAddress[]> Resolve(string host, CancellationToken _)
    {
        if (IPAddress.TryParse(host, out var literal)) return Task.FromResult(new[] { literal });
        if (host == "site.test") return Task.FromResult(new[] { IPAddress.Loopback });
        if (host == "fake.test") return Task.FromResult(new[] { IPAddress.Parse("198.18.0.5") });
        throw new SocketException(11001);
    }

    // The test seam: only the listed addresses are blocked, so the loopback test server is reachable.
    private static ScrapeClient Permissive(params string[] blocked) => new(address => blocked.Contains(address.ToString()), Resolve, _ => null);

    [Fact]
    public async Task ProductionBlocksLoopbackBeforeConnecting()
    {
        await using var server = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response(200, "OK", "<p>secret</p>"));
        var error = await Assert.ThrowsAsync<ScrapeException>(() => new ScrapeClient().FetchMarkdownAsync($"http://127.0.0.1:{server.Port}/", CancellationToken.None));
        Assert.Equal("Private, loopback, link-local, multicast, and reserved addresses are blocked: 127.0.0.1", error.Message);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task ProductionBlocksLocalhost()
    {
        await using var server = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response(200, "OK", "<p>secret</p>"));
        var error = await Assert.ThrowsAsync<ScrapeException>(() => new ScrapeClient().FetchMarkdownAsync($"http://localhost:{server.Port}/", CancellationToken.None));
        Assert.StartsWith("Private, loopback, link-local, multicast, and reserved addresses are blocked: ", error.Message);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("ftp://example.com/", "URL scheme 'ftp' is not allowed; use http or https.")]
    [InlineData("example.com", "URL scheme '' is not allowed; use http or https.")]
    [InlineData("http:///path", "URL has no hostname: http:///path")]
    [InlineData("http://user:pw@example.com/", "URLs with embedded credentials are not allowed.")]
    [InlineData("http://[::1/", "Invalid URL: http://[::1/")]
    [InlineData("http://example.com:abc/", "Port could not be cast to integer value as 'abc'")]
    [InlineData("http://198.18.0.5/", "Private, loopback, link-local, multicast, and reserved addresses are blocked: 198.18.0.5")]
    [InlineData("http://[::ffff:10.0.0.1]/", "Private, loopback, link-local, multicast, and reserved addresses are blocked: ::ffff:10.0.0.1")]
    public async Task UrlRulesUseUpstreamMessages(string url, string message) =>
        Assert.Equal(message, (await Assert.ThrowsAsync<ScrapeException>(() => new ScrapeClient().FetchMarkdownAsync(url, CancellationToken.None))).Message);

    [Fact]
    public async Task AFakeIpDnsAnswerGetsItsOwnMessage()
    {
        var client = new ScrapeClient(AddressPolicy.IsBlocked, Resolve, _ => null);
        var error = await Assert.ThrowsAsync<ScrapeException>(() => client.FetchMarkdownAsync("http://fake.test/", CancellationToken.None));
        Assert.Equal(
            "Hostname 'fake.test' resolved to 198.18.0.5, which is in a benchmarking range commonly used by proxy or VPN Fake-IP DNS. " +
            "The address remains blocked by SSRF protection. Configure the proxy/VPN to return the hostname's real public address or " +
            "exclude this hostname from Fake-IP mode.",
            error.Message);
    }

    // Dns.GetHostAddressesAsync throws ArgumentException (not SocketException) for "0"; a literal never reaches DNS.
    [Theory]
    [InlineData("http://0/", "Could not resolve hostname '0': ")]
    [InlineData("http://0.0.0.0/", "Private, loopback, link-local, multicast, and reserved addresses are blocked: 0.0.0.0")]
    public async Task TheUnspecifiedAddressIsBlockedOrUnresolvable(string url, string message) =>
        Assert.StartsWith(message, (await Assert.ThrowsAsync<ScrapeException>(() => new ScrapeClient().FetchMarkdownAsync(url, CancellationToken.None))).Message);

    [Fact]
    public async Task AnUnresolvableHostIsReported() =>
        Assert.StartsWith("Could not resolve hostname 'nowhere.test': ",
            (await Assert.ThrowsAsync<ScrapeException>(() => Permissive().FetchMarkdownAsync("http://nowhere.test/", CancellationToken.None))).Message);

    [Fact]
    public async Task FetchesThroughThePinnedAddressAndConverts()
    {
        await using var server = new LoopbackHttpServer((_, _) =>
            LoopbackHttpServer.Response(200, "OK", "<html><head><title>T</title></head><body><h1>Hello</h1><p>World</p></body></html>"));
        Assert.Equal("# Hello\n\nWorld", await Permissive().FetchMarkdownAsync($"http://site.test:{server.Port}/page", CancellationToken.None));
        var request = Assert.Single(server.Requests);
        Assert.Equal(("/page", $"site.test:{server.Port}"), request);
    }

    [Fact]
    public async Task FollowsRedirects()
    {
        await using var server = new LoopbackHttpServer((path, _) => path == "/a"
            ? LoopbackHttpServer.Response(302, "Found", "", location: "/b")
            : LoopbackHttpServer.Response(200, "OK", "<p>B</p>"));
        Assert.Equal("B", await Permissive().FetchMarkdownAsync($"http://site.test:{server.Port}/a", CancellationToken.None));
        Assert.Equal(["/a", "/b"], server.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task RedirectTargetsAreValidatedAgain()
    {
        await using var server = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response(302, "Found", "", location: "http://127.0.0.2/"));
        var error = await Assert.ThrowsAsync<ScrapeException>(() => Permissive("127.0.0.2").FetchMarkdownAsync($"http://site.test:{server.Port}/a", CancellationToken.None));
        Assert.Equal("Private, loopback, link-local, multicast, and reserved addresses are blocked: 127.0.0.2", error.Message);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task StopsAfterFiveRequests()
    {
        await using var server = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response(301, "Moved Permanently", "", location: "/loop"));
        var error = await Assert.ThrowsAsync<ScrapeException>(() => Permissive().FetchMarkdownAsync($"http://site.test:{server.Port}/", CancellationToken.None));
        Assert.Equal("Too many redirects while fetching URL", error.Message);
        Assert.Equal(5, server.Requests.Count);
    }

    [Theory]
    [InlineData(404, "Not Found", "Client")]
    [InlineData(503, "Service Unavailable", "Server")]
    public async Task HttpErrorsUseRequestsText(int status, string reason, string kind)
    {
        await using var server = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response(status, reason, "<p>no</p>"));
        var url = $"http://site.test:{server.Port}/missing";
        var error = await Assert.ThrowsAsync<ScrapeException>(() => Permissive().FetchMarkdownAsync(url, CancellationToken.None));
        Assert.Equal($"HTTP error for {url}: {status} {kind} Error: {reason} for url: {url}", error.Message);
    }

    [Fact]
    public async Task CharsetComesFromTheHeaderThenTheMetaTag()
    {
        await using var server = new LoopbackHttpServer((path, _) => path == "/meta"
            ? LoopbackHttpServer.Response(200, "OK", "<meta charset=\"utf-8\"><p>héllo</p>", contentType: "text/html")
            : LoopbackHttpServer.Response(200, "OK", "<p>é</p>", contentType: "text/html; charset=iso-8859-1"));
        Assert.Equal("héllo", await Permissive().FetchMarkdownAsync($"http://site.test:{server.Port}/meta", CancellationToken.None));
        Assert.Equal("Ã©", await Permissive().FetchMarkdownAsync($"http://site.test:{server.Port}/header", CancellationToken.None));
    }

    [Fact]
    public async Task AConnectionFailureIsReported()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var url = $"http://site.test:{port}/";
        var error = await Assert.ThrowsAsync<ScrapeException>(() => Permissive().FetchMarkdownAsync(url, CancellationToken.None));
        Assert.StartsWith($"Failed to connect to {url}: ", error.Message);
    }

    [Fact]
    public async Task CancellationIsNotAnError()
    {
        await using var server = new LoopbackHttpServer((_, _) =>
        {
            Thread.Sleep(3000);
            return LoopbackHttpServer.Response(200, "OK", "<p>late</p>");
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Permissive().FetchMarkdownAsync($"http://site.test:{server.Port}/", cancellation.Token));
    }

    [Fact]
    public async Task AProxyReceivesTheAbsoluteUrl()
    {
        await using var proxy = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response(200, "OK", "<p>via proxy</p>"));
        var client = new ScrapeClient(_ => false, Resolve, _ => new Uri($"http://127.0.0.1:{proxy.Port}/"));
        Assert.Equal("via proxy", await client.FetchMarkdownAsync("http://site.test:8080/page", CancellationToken.None));
        var request = Assert.Single(proxy.Requests);
        Assert.Equal("http://site.test:8080/page", request.Path);
    }

    [Fact]
    public async Task AProxyDoesNotSkipValidation()
    {
        await using var proxy = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response(200, "OK", "<p>secret</p>"));
        var client = new ScrapeClient(AddressPolicy.IsBlocked, Resolve, _ => new Uri($"http://127.0.0.1:{proxy.Port}/"));
        var error = await Assert.ThrowsAsync<ScrapeException>(() => client.FetchMarkdownAsync("http://site.test/", CancellationToken.None));
        Assert.Equal("Private, loopback, link-local, multicast, and reserved addresses are blocked: 127.0.0.1", error.Message);
        Assert.Empty(proxy.Requests);
    }

    // urlsplit reads the host of "http://0x7f.1/" as "0x7f.1" (resolved here to a public address); System.Uri reads it as
    // 127.0.0.1 and asks the proxy for that host. Without a pin the proxy would connect there, so that host is validated too.
    [Fact]
    public async Task AProxyValidatesTheHostHttpClientWillUse()
    {
        await using var proxy = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response(200, "OK", "<p>secret</p>"));
        var client = new ScrapeClient(AddressPolicy.IsBlocked, PublicResolve, _ => new Uri($"http://127.0.0.1:{proxy.Port}/"));
        var error = await Assert.ThrowsAsync<ScrapeException>(() => client.FetchMarkdownAsync("http://0x7f.1/", CancellationToken.None));
        Assert.Equal("Private, loopback, link-local, multicast, and reserved addresses are blocked: 127.0.0.1", error.Message);
        Assert.Empty(proxy.Requests);
    }

    // The redirect target is joined with System.Uri, so its text already names 127.0.0.1.
    [Fact]
    public async Task AProxiedRedirectIsValidatedAgain()
    {
        await using var proxy = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response(302, "Found", "", location: "http://0x7f.1/"));
        var client = new ScrapeClient(AddressPolicy.IsBlocked, PublicResolve, _ => new Uri($"http://127.0.0.1:{proxy.Port}/"));
        var error = await Assert.ThrowsAsync<ScrapeException>(() => client.FetchMarkdownAsync("http://public.test/a", CancellationToken.None));
        Assert.Equal("Private, loopback, link-local, multicast, and reserved addresses are blocked: 127.0.0.1", error.Message);
        Assert.Equal(["http://public.test/a"], proxy.Requests.Select(r => r.Path));
    }

    // Every name resolves to a public address (never dialled: these tests go through the loopback proxy), even "0x7f.1", which
    // IPAddress.TryParse would read as 127.0.0.1 but Python's ip_address does not.
    private static Task<IPAddress[]> PublicResolve(string host, CancellationToken _) =>
        Task.FromResult(new[] { IpAddressText.TryParse(host, out var literal) ? literal : IPAddress.Parse("93.184.216.34") });
}

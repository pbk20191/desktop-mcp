using System.Collections.Concurrent;
using System.Net;
using DesktopMcp.Core.Config;
using DesktopMcp.Server.Http;
using DesktopMcp.Server.Tools;
using ModelContextProtocol.Client;

namespace DesktopMcp.Server.Tests.Http;

/// <summary>HTTPS from PEM files with HTTP/2 over TLS, and no cleartext HTTP/2 (spec §8 TLS; Decisions 11).</summary>
[Collection(HttpHostCollection.Name)]
public class TlsTests
{
    [Fact]
    public async Task HttpsOffersHttp2AndHttp11()
    {
        using var pem = TestCertificate.Create();
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http", SslCertfile = pem.CertPath, SslKeyfile = pem.KeyPath });
        Assert.Equal("https", rig.Base.Scheme);
        using var http = HttpRig.PinnedClient(pem.Thumbprint);
        foreach (var version in new[] { HttpVersion.Version20, HttpVersion.Version11 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(rig.Base, "/health")) { Version = version, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using var response = await http.SendAsync(request);
            Assert.Equal(version, response.Version);
        }
    }

    [Fact]
    public async Task McpWorksOverHttp2()
    {
        using var pem = TestCertificate.Create();
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http", SslCertfile = pem.CertPath, SslKeyfile = pem.KeyPath });
        // The SDK builds its own requests, so the pinned client forces HTTP/2 on each and records what was negotiated.
        var negotiated = new ConcurrentQueue<Version>();
        await using var client = await rig.ConnectAsync(HttpTransportMode.StreamableHttp, http: HttpRig.PinnedClient(pem.Thumbprint, HttpVersion.Version20, negotiated));
        Assert.Equal(21, (await client.ListToolsAsync()).Count);
        Assert.NotEmpty(negotiated);
        Assert.All(negotiated, version => Assert.Equal(HttpVersion.Version20, version));
    }

    [Fact]
    public async Task PlainHttpOffersNoH2c()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http" });
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(rig.Base, "/health")) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        await Assert.ThrowsAsync<HttpRequestException>(() => rig.Http.SendAsync(request));
    }

    [Fact]
    public async Task ABadCertificateStopsServeWithOneError()
    {
        using var a = TestCertificate.Create();
        using var b = TestCertificate.Create();
        var error = new StringWriter();
        var plan = HttpRig.Plan(new ServeArguments { Transport = "streamable-http", Host = "127.0.0.1", Port = 0, SslCertfile = a.CertPath, SslKeyfile = b.KeyPath });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(1, await HttpServer.RunAsync(plan, ToolServices.CreateDefault(), timeout.Token, announce: error));
        Assert.StartsWith("Error: Could not load the TLS certificate", error.ToString());
        Assert.Single(error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>A certificate Kestrel would refuse at start (client-auth-only EKU) is the one Error: line, not a crash.</summary>
    [Fact]
    public async Task AClientAuthOnlyCertificateStopsServeWithOneError()
    {
        using var pem = TestCertificate.Create(enhancedKeyUsage: "1.3.6.1.5.5.7.3.2");
        var error = new StringWriter();
        var plan = HttpRig.Plan(new ServeArguments { Transport = "streamable-http", Host = "127.0.0.1", Port = 0, SslCertfile = pem.CertPath, SslKeyfile = pem.KeyPath });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(1, await HttpServer.RunAsync(plan, ToolServices.CreateDefault(), timeout.Token, announce: error));
        Assert.StartsWith("Error: Could not load the TLS certificate", error.ToString());
        Assert.Contains("not valid for server authentication", error.ToString());
        Assert.Single(error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }
}

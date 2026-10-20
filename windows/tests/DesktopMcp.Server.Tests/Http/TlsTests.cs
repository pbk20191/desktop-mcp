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
        await using var client = await rig.ConnectAsync(HttpTransportMode.StreamableHttp, http: HttpRig.PinnedClient(pem.Thumbprint, HttpVersion.Version20));
        Assert.Equal(21, (await client.ListToolsAsync()).Count);
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
}

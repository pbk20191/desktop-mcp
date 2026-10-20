using System.Net;
using DesktopMcp.Core.Config;
using ModelContextProtocol.Client;

namespace DesktopMcp.Server.Tests.Http;

/// <summary>The legacy SSE transport: <c>GET /sse</c> and <c>POST /message</c> only (spec §8 Transports; Decisions 4).</summary>
[Collection(HttpHostCollection.Name)]
public class LegacySseTests
{
    [Fact]
    public async Task SseAnnouncesTheMessageEndpointAndServesTools()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "sse" });
        Assert.Matches(@"^desktop-mcp: serving sse on http://127\.0\.0\.1:\d+/sse$", rig.Announced);
        var endpoint = await rig.ReadSseEndpointEventAsync();          // first event of GET /sse
        Assert.StartsWith("/message?sessionId=", endpoint);
        await using var client = await rig.ConnectAsync(HttpTransportMode.Sse);
        Assert.Equal(21, (await client.ListToolsAsync()).Count);
    }

    [Fact]
    public async Task SseMapsNoStreamableEndpoint()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "sse" });
        using var post = await rig.Http.SendAsync(HttpRig.InitializeRequest(rig.Base));
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        using var mcp = await rig.Http.SendAsync(HttpRig.InitializeRequest(new Uri(rig.Base, "/mcp")));
        Assert.Equal(HttpStatusCode.NotFound, mcp.StatusCode);
        using var mcpSlash = await rig.Http.SendAsync(HttpRig.InitializeRequest(new Uri(rig.Base, "/mcp/")));
        Assert.Equal(HttpStatusCode.NotFound, mcpSlash.StatusCode);
    }

    [Fact]
    public async Task StatelessHttpIsIgnoredForSse()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "sse", StatelessHttp = true });
        Assert.StartsWith("/message?sessionId=", await rig.ReadSseEndpointEventAsync());
    }

    /// <summary>The switch is read per host (preflight F6), so a later host without SSE maps no <c>/sse</c>.</summary>
    [Fact]
    public async Task HostsAfterAnSseHostMapNoSse()
    {
        await (await HttpRig.StartAsync(new ServeArguments { Transport = "sse" })).DisposeAsync();
        await using var stateless = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http", StatelessHttp = true });
        using var sse = await stateless.Http.GetAsync(new Uri(stateless.Base, "/sse"));
        Assert.Equal(HttpStatusCode.NotFound, sse.StatusCode);
    }
}

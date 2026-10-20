using System.Net;
using System.Net.Sockets;
using DesktopMcp.Core.Config;
using DesktopMcp.Server.Http;
using DesktopMcp.Server.Tools;
using ModelContextProtocol.Client;

namespace DesktopMcp.Server.Tests.Http;

/// <summary>The HTTP host and Streamable HTTP (spec §8 Transports; Decisions 4, 7, 8).</summary>
[Collection(HttpHostCollection.Name)]
public class HttpServerTests
{
    [Fact]
    public async Task StreamableHttpServesInitializeAndTheContract()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http" });
        Assert.Matches(@"^desktop-mcp: serving streamable-http on http://127\.0\.0\.1:\d+/mcp$", rig.Announced);
        await using var client = await rig.ConnectAsync(HttpTransportMode.StreamableHttp);
        Assert.Equal("desktop-mcp", client.ServerInfo.Name);
        var tools = await client.ListToolsAsync();
        Assert.Equal(21, tools.Count);          // ContractTests compares the JSON; here the transport carries it
        var wait = await client.CallToolAsync("Wait", new Dictionary<string, object?> { ["duration"] = 0 });
        Assert.False(wait.IsError ?? false);
    }

    [Fact]
    public async Task StatefulResponsesCarryASessionId()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http" });
        using var response = await rig.Http.SendAsync(HttpRig.InitializeRequest(rig.Endpoint));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Mcp-Session-Id"));
    }

    [Fact]
    public async Task StatelessResponsesCarryNoSessionIdAndGetIs405()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http", StatelessHttp = true });
        using var response = await rig.Http.SendAsync(HttpRig.InitializeRequest(rig.Endpoint));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));
        using var get = await rig.Http.GetAsync(rig.Endpoint);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
    }

    [Fact]
    public async Task TheTrailingSlashPathIsTheSameEndpoint()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http" });
        using var response = await rig.Http.SendAsync(HttpRig.InitializeRequest(new Uri(rig.Base, "/mcp/")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StreamableHttpMapsNoLegacySse()
    {
        await using var rig = await HttpRig.StartAsync(new ServeArguments { Transport = "streamable-http" });
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Http.GetAsync(new Uri(rig.Base, "/sse"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Http.PostAsync(new Uri(rig.Base, "/message"), new StringContent("{}"))).StatusCode);
    }

    [Fact]
    public async Task APortInUseExitsWithAnError()
    {
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        var port = ((IPEndPoint)holder.LocalEndpoint).Port;
        var error = new StringWriter();
        var plan = HttpRig.Plan(new ServeArguments { Transport = "streamable-http", Host = "127.0.0.1", Port = port });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(1, await HttpServer.RunAsync(plan, ToolServices.CreateDefault(), timeout.Token, announce: error));
        Assert.StartsWith($"Error: Could not listen on 127.0.0.1:{port}: ", error.ToString());
        Assert.Single(error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// preflight F2: Kestrel refuses a dynamic port on localhost, so <c>localhost</c> with port 0 binds 127.0.0.1 only;
    /// the announcement still names localhost.
    /// </summary>
    [Fact]
    public async Task LocalhostOnPortZeroBindsTheIPv4Loopback()
    {
        var plan = HttpRig.Plan(new ServeArguments { Transport = "streamable-http", Host = "localhost", Port = 0 });
        var announce = new StringWriter();
        var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(() => HttpServer.RunAsync(plan, ToolServices.CreateDefault(), stop.Token, uri => listening.TrySetResult(uri), announce));
        try
        {
            var first = await Task.WhenAny(listening.Task, serving).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(first == listening.Task, announce.ToString());
            var announced = await listening.Task;
            Assert.Equal("localhost", announced.Host);
            Assert.Matches(@"^desktop-mcp: serving streamable-http on http://localhost:\d+/mcp\r?\n$", announce.ToString());
            using var http = HttpRig.CreateClient();
            using var response = await http.SendAsync(HttpRig.InitializeRequest(new Uri($"http://127.0.0.1:{announced.Port}/mcp")));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await stop.CancelAsync();
        }
        Assert.Equal(0, await serving.WaitAsync(TimeSpan.FromSeconds(30)));
    }
}

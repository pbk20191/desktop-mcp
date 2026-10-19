using System.IO.Pipelines;
using DesktopMcp.Core.Config;
using DesktopMcp.Server.Hosting;
using DesktopMcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DesktopMcp.Server.Tests;

/// <summary>The serve host's MCP setup over in-memory pipes in place of stdin and stdout (spec §9).</summary>
public sealed class StdioServerTests
{
    [Fact]
    public async Task InitializeListsTheContractToolsAndCallsWait()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var plan = new ServePlan { Transport = "stdio", Host = "localhost", Port = 8000, Control = new ControlSettings(true, "relaxed", 10.0, 500, 500) };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = StdioServer.RunAsync(plan,
            builder => builder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()), stop.Token);

        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()), cancellationToken: stop.Token))
        {
            Assert.Equal("desktop-mcp", client.ServerInfo.Name);

            var tools = await client.ListToolsAsync(cancellationToken: stop.Token);
            // The SDK does not keep registration order, so compare the names as sets.
            Assert.Equal(21, tools.Count);
            Assert.Equal(ToolSpecCatalog.LoadEmbedded().Names.Order(StringComparer.Ordinal), tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));

            var result = await client.CallToolAsync("Wait", new Dictionary<string, object?> { ["duration"] = 0 }, cancellationToken: stop.Token);
            Assert.False(result.IsError ?? false);
            Assert.Equal("Waited for 0 seconds.", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        }

        await stop.CancelAsync();
        Assert.Equal(0, await serving);
    }
}

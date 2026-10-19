using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopMcp.Server.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tests;

/// <summary>
/// spec/windows/tools.json is this port's tool contract. The tools the server registers must
/// serialise to exactly those JSON values, so any change to a tool's name, parameters or
/// description is a deliberate change to the contract file.
/// </summary>
public class ContractTests
{
    [Fact]
    public void RegisteredToolsMatchTheContract()
    {
        var spec = JsonNode.Parse(File.ReadAllText(RepoFile.Find("spec/windows/tools.json")))!["tools"]!.AsArray();
        var expected = spec.ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!);

        var tools = ToolRegistry.CreateAll(ToolSpecCatalog.LoadEmbedded());
        var actual = tools.ToDictionary(t => t.ProtocolTool.Name, t => JsonSerializer.SerializeToNode(t.ProtocolTool, ToolSpecCatalog.ToolTypeInfo)!);

        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (name, tool) in expected)
        {
            Assert.True(JsonNode.DeepEquals(tool, actual[name]),
                $"{name} differs from spec/windows/tools.json\nspec:   {tool.ToJsonString()}\nserver: {actual[name].ToJsonString()}");
        }
    }

    [Fact]
    public async Task PendingToolReturnsAnErrorResult()
    {
        var tool = ToolRegistry.CreateAll(ToolSpecCatalog.LoadEmbedded()).Single(t => t.ProtocolTool.Name == "Wait");
        await using var server = McpServer.Create(new StreamServerTransport(Stream.Null, Stream.Null), new McpServerOptions());
        var request = new RequestContext<CallToolRequestParams>(server, new JsonRpcRequest { Method = "tools/call" }, new CallToolRequestParams { Name = "Wait" });
        var result = await tool.InvokeAsync(request);
        Assert.True(result.IsError);
        Assert.Equal("Error calling tool 'Wait': not available in this build yet", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }
}

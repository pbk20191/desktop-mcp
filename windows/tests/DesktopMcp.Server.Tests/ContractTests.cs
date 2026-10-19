using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopMcp.Server.Tools;

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
}

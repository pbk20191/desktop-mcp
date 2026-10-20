using System.Text.Json;
using DesktopMcp.Server.Control;
using DesktopMcp.Server.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tests;

/// <summary>Calls a tool the way the SDK does, through <see cref="SpecTool"/>'s InvokeAsync.</summary>
internal static class ToolCalls
{
    public static McpServerTool Create(string name, ToolServices? services = null) =>
        ToolRegistry.Create(ToolSpecCatalog.LoadEmbedded().Get(name), services ?? ToolServices.CreateDefault());

    public static async Task<CallToolResult> CallAsync(McpServerTool tool, string argumentsJson, CancellationToken cancellationToken = default)
    {
        Dictionary<string, JsonElement> arguments;
        using (var document = JsonDocument.Parse(argumentsJson))
            arguments = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        await using var server = McpServer.Create(new StreamServerTransport(Stream.Null, Stream.Null), new McpServerOptions());
        var request = new RequestContext<CallToolRequestParams>(server, new JsonRpcRequest { Method = "tools/call" },
            new CallToolRequestParams { Name = tool.ProtocolTool.Name, Arguments = arguments });
        return await tool.InvokeAsync(request, cancellationToken);
    }

    /// <summary>Calls a tool through <paramref name="gate"/>, as the server's call-tool filter does.</summary>
    public static Task<CallToolResult> GatedCallAsync(ControlGate gate, McpServerTool tool, string argumentsJson, CancellationToken cancellationToken = default)
    {
        Dictionary<string, JsonElement> arguments;
        using (var document = JsonDocument.Parse(argumentsJson))
            arguments = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        return gate.InvokeAsync(tool.ProtocolTool.Name, arguments, ct => new ValueTask<CallToolResult>(CallAsync(tool, argumentsJson, ct)), cancellationToken)
            .AsTask();
    }

    public static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    /// <summary>The text of a result that must not be an error.</summary>
    public static string OkText(CallToolResult result)
    {
        var text = Text(result);
        Assert.False(result.IsError == true, text);
        return text;
    }
}

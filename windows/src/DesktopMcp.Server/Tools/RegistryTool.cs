using DesktopMcp.Core.WindowsRegistry;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

/// <summary>upstream tools/registry.py:20-51.</summary>
public sealed class RegistryTool(Tool protocolTool, RegistryService registry) : SpecTool(protocolTool)
{
    protected override async ValueTask<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var arguments = ToolArguments.From(request);
        var mode = arguments.RequiredString("mode");
        var path = arguments.RequiredString("path");
        var name = arguments.OptionalString("name");
        var value = arguments.OptionalString("value");
        var type = arguments.String("type", "String");
        var text = await Task.Run(() => mode switch
        {
            "get" => name is null ? "Error: name parameter is required for get mode." : registry.GetValue(path, name),
            "set" => name is null ? "Error: name parameter is required for set mode."
                : value is null ? "Error: value parameter is required for set mode."
                : registry.SetValue(path, name, value, type),
            "delete" => registry.DeleteEntry(path, name),
            "list" => registry.ListKey(path),
            _ => "Error: mode must be \"get\", \"set\", \"delete\", or \"list\".",
        }, cancellationToken).ConfigureAwait(false);
        return ToolResults.Text(text);
    }
}

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

public static class ToolRegistry
{
    /// <summary>One tool per contract entry, in contract order.</summary>
    public static IReadOnlyList<McpServerTool> CreateAll(ToolSpecCatalog catalog, ToolServices? services = null)
    {
        services ??= ToolServices.CreateDefault();
        return catalog.Names.Select(name => Create(catalog.Get(name), services)).ToArray();
    }

    /// <summary>The implementation for a contract entry; entries of later phases stay <see cref="PendingTool"/>.</summary>
    public static McpServerTool Create(Tool protocolTool, ToolServices services) => protocolTool.Name switch
    {
        "Wait" => new WaitTool(protocolTool),
        _ => new PendingTool(protocolTool),
    };
}

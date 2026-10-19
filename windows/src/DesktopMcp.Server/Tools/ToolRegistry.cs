using ModelContextProtocol.Server;

namespace DesktopMcp.Server.Tools;

public static class ToolRegistry
{
    /// <summary>One tool per contract entry, in contract order.</summary>
    public static IReadOnlyList<McpServerTool> CreateAll(ToolSpecCatalog catalog) =>
        catalog.Names.Select(name => (McpServerTool)new PendingTool(catalog.Get(name))).ToArray();
}

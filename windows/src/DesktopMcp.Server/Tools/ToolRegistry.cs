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
        "PowerShell" => new PowerShellTool(protocolTool, services.PowerShell),
        "FileSystem" => new FileSystemTool(protocolTool, services.FileSystem),
        "Registry" => new RegistryTool(protocolTool, services.Registry),
        "Process" => new ProcessTool(protocolTool, services.Processes),
        "Clipboard" => new ClipboardTool(protocolTool, services.Clipboard),
        "Notification" => new NotificationTool(protocolTool, services.Toasts),
        "Scrape" => new ScrapeTool(protocolTool, services.Scrape),
        "DisplayInventory" => new DisplayInventoryTool(protocolTool, services.Displays),
        "Snapshot" => new SnapshotTool(protocolTool, services.Snapshots),
        "Screenshot" => new ScreenshotTool(protocolTool, services.Snapshots),
        "Click" => new ClickTool(protocolTool, services),
        "Move" => new MoveTool(protocolTool, services),
        "Scroll" => new ScrollTool(protocolTool, services),
        _ => new PendingTool(protocolTool),
    };
}

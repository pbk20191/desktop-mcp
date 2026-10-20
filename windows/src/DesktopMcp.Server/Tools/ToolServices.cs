using DesktopMcp.Core.FileSystem;
using DesktopMcp.Core.PowerShell;
using DesktopMcp.Core.Processes;
using DesktopMcp.Core.Scrape;
using DesktopMcp.Core.WindowsRegistry;
using DesktopMcp.Desktop.ClipboardAccess;
using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Snapshots;
using DesktopMcp.Desktop.Toasts;

namespace DesktopMcp.Server.Tools;

/// <summary>
/// The services the tools call. Tests replace the ones with side effects (clipboard, toasts) with fakes through
/// <c>with</c>. Creating the defaults touches nothing: no Windows App SDK, no desktop.
/// </summary>
public sealed record ToolServices
{
    public required PowerShellRunner PowerShell { get; init; }
    public required FileSystemService FileSystem { get; init; }
    public required RegistryService Registry { get; init; }
    public required ProcessService Processes { get; init; }
    public required IClipboard Clipboard { get; init; }
    public required IToastSender Toasts { get; init; }
    public required ScrapeClient Scrape { get; init; }
    public required IDisplayProvider Displays { get; init; }
    public required IDesktopSnapshots Snapshots { get; init; }

    public static ToolServices CreateDefault() => new()
    {
        PowerShell = new PowerShellRunner(),
        FileSystem = new FileSystemService(),
        Registry = new RegistryService(),
        Processes = new ProcessService(),
        Clipboard = new SystemClipboard(),
        Toasts = new ToastSender(),
        Scrape = new ScrapeClient(),
        Displays = new Win32Displays(),
        Snapshots = new SnapshotService(),
    };
}

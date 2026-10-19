using DesktopMcp.Core.FileSystem;
using DesktopMcp.Core.PowerShell;
using DesktopMcp.Core.WindowsRegistry;

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

    public static ToolServices CreateDefault() => new()
    {
        PowerShell = new PowerShellRunner(),
        FileSystem = new FileSystemService(),
        Registry = new RegistryService(),
    };
}

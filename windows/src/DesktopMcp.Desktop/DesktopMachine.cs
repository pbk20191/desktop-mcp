using DesktopMcp.Desktop.Displays;
using DesktopMcp.Desktop.Tree;
using DesktopMcp.Desktop.Uia;
using DesktopMcp.Desktop.VirtualDesktops;

namespace DesktopMcp.Desktop;

/// <summary>
/// The stateful desktop services that Snapshot and the act tools share (spec §5: UIA jobs one at a time): one UIA worker,
/// one hang block list, one window catalog, one virtual desktop service. The server creates one for its lifetime.
/// </summary>
public sealed class DesktopMachine
{
    private DesktopMachine(UiaWorker<UiaSession> uia, HangBlockList blocks, IWindowSystem windows, VirtualDesktopService desktops,
        IDisplayProvider displays)
    {
        Uia = uia;
        Blocks = blocks;
        Windows = windows;
        Desktops = desktops;
        Displays = displays;
        Catalog = new WindowCatalog(windows, new UiaWindowProbe(uia), desktops, blocks, Environment.ProcessId, Task.Delay);
        Tree = new TreeService(windows, new UiaWindowWalker(uia), blocks, Environment.GetEnvironmentVariable, Task.Delay);
    }

    internal UiaWorker<UiaSession> Uia { get; }
    internal HangBlockList Blocks { get; }
    internal IWindowSystem Windows { get; }
    internal VirtualDesktopService Desktops { get; }
    internal IDisplayProvider Displays { get; }
    internal WindowCatalog Catalog { get; }
    internal TreeService Tree { get; }

    /// <summary>
    /// The real machine. Cheap: it starts no thread and touches no COM, UIA, desktop or Windows App SDK (the UIA worker
    /// starts its thread on the first job).
    /// </summary>
    public static DesktopMachine CreateDefault() => new(
        new UiaWorker<UiaSession>(UiaSession.Create),
        new HangBlockList(TimeProvider.System, HangBlockList.DefaultDuration),
        new Win32WindowSystem(),
        new VirtualDesktopService(),
        new Win32Displays());
}
